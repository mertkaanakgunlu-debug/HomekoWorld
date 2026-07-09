using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using HomekoWorld.Models.Farm;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HomekoWorld.Services.Yolo;

/// <summary>
/// YOLOv8/YOLO11 ONNX modelini GPU (CUDA/DirectML) veya CPU ile çalıştıran inferrer.
/// Output shape: (1, 4+nc, numAnchors) — raw YOLO export formatı. numAnchors imgsz'e göre
/// değişir (640 → 8400, 960 → 18900); kod bunu output.Dimensions[2]'den DİNAMİK okur.
/// IYoloInferrer implement eder; FarmEngine.Inferrer property'sine atanır.
/// </summary>
public sealed class OnnxYoloInferrer : IYoloInferrer, IDisposable
{
    private InferenceSession?  _session;
    private string[]           _classNames = Array.Empty<string>();

    // ── GPU/OBS dayanıklılığı (A4): TDR / device-removed kurtarma + CPU fallback ──
    // OBS kaydı + oyun + DirectML inference aynı GPU'da yarışınca GPU TDR (timeout) olabilir;
    // onnxruntime/DirectML bunu native exception olarak fırlatır. Eskiden DetectionLoop bunu
    // yutup dönse de session kalıcı bozulabiliyordu. Artık: hatada session yeniden kurulur,
    // GPU üst üste başarısız olursa CPU EP'ye KALICI düşülür → uygulama asla çökmez/donmaz.
    private string _onnxPath   = "";
    private InferenceBackend _backend = InferenceBackend.Auto; // kullanıcı tercihi (Auto/DirectML/Cpu)
    private bool   _forcedCpu;          // GPU üst üste hata verince kalıcı CPU'ya kilitlenir
    private string _epUsed     = "CPU"; // gerçekte kurulu EP (DirectML/TensorRT/CUDA/CPU)
    private int    _failStreak;         // üst üste inference hatası sayacı
    private const int GpuFailLimit = 2;

    /// <summary>Aktif EP GPU mu (CPU değil)? Tanılama/UI için.</summary>
    public bool   IsUsingGpu => !string.Equals(_epUsed, "CPU", StringComparison.OrdinalIgnoreCase);
    /// <summary>Aktif execution provider adı (DirectML / TensorRT / CUDA / CPU).</summary>
    public string ActiveEp   => _epUsed;

    // ── P0: son inference'ın alt-zamanlamaları (yüksek çözünürlüklü ms) ──
    // "Inf ms" üç aşamayı birden topluyordu (preprocess CPU + GPU Run + postprocess CPU). Darboğazın
    // GPU'da mı yoksa CPU'da mı olduğunu görmek için ayrı ölçülür → TensorRT/FP16 vs preprocess kararı.
    private double _lastPrepMs, _lastRunMs, _lastPostMs;
    /// <summary>Son <see cref="Infer"/>'ın preprocess / GPU Run / postprocess süreleri (ms).</summary>
    public (double Preprocess, double Run, double Postprocess) LastTimings => (_lastPrepMs, _lastRunMs, _lastPostMs);
    private static double Ms(long fromTs, long toTs) => (toTs - fromTs) * 1000.0 / Stopwatch.Frequency;

    // ── Inference parametreleri ───────────────────────────────────────────────
    // Model giriş boyutu (imgsz). Eğitim/export hangi imgsz ile yapıldıysa onunla eşleşmeli.
    // const DEĞİL: Load() içinde FarmSettings.ModelInputSize'dan atanır (640'ta export → 640, 960 → 960).
    private       int   _inputSize  = 640;
    private       float _confThresh = 0.35f;
    private       float _iouThresh  = 0.45f;

    /// <summary>
    /// Güven eşiği (0-1). Bu skorun altındaki tespitler elenir. FarmSettings.ConfidenceThreshold'dan
    /// beslenir (MainViewModel model yüklerken + kaydırıcı değişince atar). 0.05-0.95'e kıstırılır.
    /// Eskiden yalnız sabit 0.35 kullanılıyordu → ağaç yanlış-pozitifleri buradan geçiyordu.
    /// </summary>
    public float ConfThreshold
    {
        get => _confThresh;
        set => _confThresh = Math.Clamp(value, 0.05f, 0.95f);
    }

    /// <summary>
    /// NMS IoU eşiği (0.20-0.80). Yüksek = dip-dibe moblarda iki kutu birbirini daha az bastırır
    /// (ama çift-kutu riski artar); düşük = daha agresif birleştirme. FarmSettings.IouThreshold'dan beslenir.
    /// </summary>
    public float IouThreshold
    {
        get => _iouThresh;
        set => _iouThresh = Math.Clamp(value, 0.20f, 0.80f);
    }

    // ── P2: çok-slot tensor havuzu (pipelining) ───────────────────────────────
    // Eskiden tek _tensorBuf/_tensor (seri). Pipeline'da üretici (PreprocessInto) ve tüketici (InferSlot)
    // FARKLI slotlarda eşzamanlı çalışır → çakışma yok. Slot sahipliğini FarmEngine yönetir (free-pool +
    // latest-wins mailbox). Load() tüm slotları _inputSize'a göre kurar.
    public const int SlotCount = 3; // triple-buffer: üretici-yazan + mailbox + tüketici-okuyan
    private float[][]            _tensorBufs = Array.Empty<float[]>();
    private DenseTensor<float>[] _tensors    = Array.Empty<DenseTensor<float>>();

    // ── Thread-safety (A1c → P2): ReaderWriterLockSlim ────────────────────────
    // PreprocessInto/InferSlot/WarmUp = READ (eşzamanlı OK; üretici+tüketici farklı slot). Load/Dispose =
    // WRITE (exclusive: session dispose + buffer realloc). Load nadir (model değişimi). Read kilidi
    // recursive DEĞİL → metotlar iç içe kilit almaz (serial Infer ardışık çağırır, nested değil).
    private readonly ReaderWriterLockSlim _rwLock = new();

    // ── Yükleme ───────────────────────────────────────────────────────────────

    /// <summary>
    /// ONNX modelini yükler.
    /// classNames: mobs.json'dan gelen sıralı isimler — inference sonucunda className alanını doldurur.
    /// </summary>
    public void Load(string onnxPath, IReadOnlyList<string>? classNames = null, int inputSize = 640,
                     InferenceBackend backend = InferenceBackend.Auto)
    {
        // P2: write kilidi — uçuştaki tüm PreprocessInto/InferSlot/WarmUp bitene kadar bekler → torn
        // session swap / buffer realloc yarışı olmaz.
        _rwLock.EnterWriteLock();
        try
        {
            _onnxPath   = onnxPath;
            _classNames = classNames?.ToArray() ?? Array.Empty<string>();
            _backend    = backend;
            _forcedCpu  = false;
            _failStreak = 0;

            // Model giriş boyutu (imgsz) — eğitim/export ile eşleşmeli. Tüm slot buffer'ları bu boyuta göre kurulur.
            _inputSize  = Math.Clamp(inputSize, 320, 1920);
            int len     = 1 * 3 * _inputSize * _inputSize;
            _tensorBufs = new float[SlotCount][];
            _tensors    = new DenseTensor<float>[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                _tensorBufs[i] = new float[len];
                _tensors[i]    = new DenseTensor<float>(_tensorBufs[i], new[] { 1, 3, _inputSize, _inputSize });
            }

            BuildSession();
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>Session'ı tercih edilen EP ile (yeniden) kurar; gerçekleşen EP <see cref="_epUsed"/>'a yazılır.</summary>
    private void BuildSession()
    {
        _session?.Dispose();
        _session = null;

        // GPU üst üste hata verdiyse (_forcedCpu) tercih ne olursa olsun CPU.
        var backend = _forcedCpu ? InferenceBackend.Cpu : _backend;

        // A1b: OrtEpFactory.Create EP eklemede fırlamaz, ama `new InferenceSession` GPU EP ile
        // KURULURKEN native fırlatabilir (sürücü/sürüm uyumsuzluğu). Bunu yakalayıp CPU'ya düşmezsek
        // model yüklemesi tamamen başarısız olur. → GPU kurulumu patlarsa kalıcı CPU'ya düş, yeniden kur.
        try
        {
            var opts = OrtEpFactory.Create(backend, out _epUsed);
            _session = new InferenceSession(_onnxPath, opts);
            HomekoWorld.Program.Log($"[YOLO] Execution provider: {_epUsed}");
            LogModelDiag();
        }
        catch (Exception ex) when (backend != InferenceBackend.Cpu)
        {
            HomekoWorld.Program.Log($"[YOLO] GPU session kurulamadı ({_epUsed}): {ex.Message} — CPU'ya düşülüyor.");
            _forcedCpu = true;
            var cpuOpts = OrtEpFactory.Create(InferenceBackend.Cpu, out _epUsed);
            _session = new InferenceSession(_onnxPath, cpuOpts);
            HomekoWorld.Program.Log($"[YOLO] Execution provider: {_epUsed}");
            LogModelDiag();
        }
    }

    /// <summary>Model yükleme teşhisi (9.tur): FPS kıyasları oturumlar arası atfedilebilir olsun diye
    /// model dosyası + imgsz + EP + ONNX girdi/çıktı şekilleri tek satırda; modelin export imgsz'i ile
    /// ayarlanan imgsz uyuşmuyorsa açık uyarı (doğruluk/FPS sessizce sapmasın).</summary>
    private void LogModelDiag()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var im = session.InputMetadata.First();
            var om = session.OutputMetadata.First();
            HomekoWorld.Program.Log(
                $"[YOLO] model={System.IO.Path.GetFileName(_onnxPath)} imgsz={_inputSize} ep={_epUsed} " +
                $"girdi={im.Key}:[{string.Join("x", im.Value.Dimensions)}] " +
                $"çıktı={om.Key}:[{string.Join("x", om.Value.Dimensions)}]");
            var d = im.Value.Dimensions; // NCHW beklenir; -1 = dinamik boyut → uyarı atlanır
            if (d.Length == 4 && ((d[2] > 0 && d[2] != _inputSize) || (d[3] > 0 && d[3] != _inputSize)))
                HomekoWorld.Program.Log(
                    $"[YOLO] ⚠ imgsz UYUMSUZ: model girdisi {d[2]}x{d[3]} ≠ ayarlanan {_inputSize} — " +
                    "FarmSettings.ModelInputSize modelin export imgsz'ine eşitlenmeli");
        }
        catch (Exception ex) { HomekoWorld.Program.Log($"[YOLO] model-teşhis okunamadı: {ex.Message}"); }
    }

    /// <summary>
    /// Inference hatası sonrası kurtarma: GPU'da üst üste hatada CPU'ya kalıcı geçer,
    /// aksi halde aynı EP ile session'ı yeniden kurar. Hiçbir koşulda fırlatmaz.
    /// </summary>
    private void RecoverAfterFailure()
    {
        _failStreak++;
        try
        {
            if (IsUsingGpu)
            {
                bool toCpu = _failStreak >= GpuFailLimit;
                if (toCpu)
                {
                    HomekoWorld.Program.Log($"[YOLO] GPU ({_epUsed}) kararsız — CPU'ya geçiliyor (kalıcı).");
                    _forcedCpu = true;
                }
                BuildSession();
                if (toCpu) _failStreak = 0;
            }
            else if (_failStreak <= 3)
            {
                // CPU'da: yalnız ilk birkaç hatada yeniden kur, sonra bırak (yeniden-kurma thrash'ini önle).
                BuildSession();
            }
        }
        catch (Exception ex)
        {
            HomekoWorld.Program.Log($"[YOLO] Session yeniden kurulamadı: {ex.Message}");
            // _session null kalabilir → Infer başta guard'lar; uygulama çökmez.
        }
    }

    // ── IYoloInferrer ─────────────────────────────────────────────────────────

    /// <summary>
    /// SERİ yol (fallback / PipelinedInference=false): preprocess + GPU Run + parse tek çağrıda (slot 0).
    /// frame Dispose'u çağıranın. Pipeline yolu PreprocessInto + InferSlot kullanır (eşzamanlı, farklı slot).
    /// </summary>
    public IReadOnlyList<Detection> Infer(Bitmap frame)
    {
        if (_session is null) return Array.Empty<Detection>();
        var (padX, padY, scale) = PreprocessInto(frame, 0);
        return InferSlot(0, padX, padY, scale, frame.Width, frame.Height);
    }

    /// <summary>
    /// P2 ÜRETİCİ aşaması: frame'i letterbox+normalize ile slot'un tensor buffer'ına yazar; (padX,padY,scale)
    /// döner. GPU'ya DOKUNMAZ → tüketicinin InferSlot'uyla (farklı slot) EŞZAMANLI çalışır. _lastPrepMs set.
    /// </summary>
    public (float padX, float padY, float scale) PreprocessInto(Bitmap frame, int slot)
    {
        _rwLock.EnterReadLock();
        try
        {
            if ((uint)slot >= (uint)_tensorBufs.Length) return (0f, 0f, 1f);
            long t0 = Stopwatch.GetTimestamp();
            var r = PreprocessSlot(frame, slot);
            _lastPrepMs = Ms(t0, Stopwatch.GetTimestamp());   // P0: preprocess (CPU)
            return r;
        }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>
    /// P2 TÜKETİCİ aşaması: slot'un (önce PreprocessInto edilmiş) tensor'ını GPU'da çalıştırır + parse + NMS.
    /// _lastRunMs/_lastPostMs set. origW/origH = kaynak kare boyutu (kutu clamp). Upgradeable kilit: GPU
    /// hatasında write'a yükselip session'ı kurtarır (A4).
    /// </summary>
    public IReadOnlyList<Detection> InferSlot(int slot, float padX, float padY, float scale, int origW, int origH)
    {
        _rwLock.EnterUpgradeableReadLock();
        try
        {
            var session = _session;
            if (session is null || (uint)slot >= (uint)_tensors.Length) return Array.Empty<Detection>();
            var tensor = _tensors[slot];

            long tRunStart = Stopwatch.GetTimestamp();
            string inputName = session.InputMetadata.Keys.First();
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
            try
            {
                results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
            }
            catch (Exception ex)
            {
                HomekoWorld.Program.Log($"[YOLO] Inference hatası (EP={_epUsed}): {ex.Message}");
                _rwLock.EnterWriteLock();
                try { RecoverAfterFailure(); } finally { _rwLock.ExitWriteLock(); }
                _lastRunMs = _lastPostMs = 0; // atlanan kare
                return Array.Empty<Detection>();
            }
            _failStreak = 0; // başarılı inference → hata sayacı sıfır
            long tRunEnd = Stopwatch.GetTimestamp();
            _lastRunMs = Ms(tRunStart, tRunEnd);   // P0: GPU Run (TensorRT/FP16 yalnız BUNU hızlandırır)

            using var _resultsScope = results;
            var result = ParseAndNms(results, padX, padY, scale, origW, origH);
            _lastPostMs = Ms(tRunEnd, Stopwatch.GetTimestamp());   // P0: postprocess (CPU parse+NMS)
            return result;
        }
        finally { _rwLock.ExitUpgradeableReadLock(); }
    }

    // ── Output parse + NMS ────────────────────────────────────────────────────
    //    BBox modeli: (1, 4+nc, numAnchors)   numAnchors imgsz'e göre: 640→8400, 960→18900
    private List<Detection> ParseAndNms(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        float padX, float padY, float scale, int origW, int origH)
    {
        var output      = results.First().AsTensor<float>();
        int numAnchors  = output.Dimensions[2];
        int numChannels = output.Dimensions[1];

        // A1a: _classNames modelin kanal sayısından FAZLA olabilir ("tek↔çok sınıf taşması") → nc'yi
        // (numChannels-4) ile KISITLA, yoksa output[0, 4+c, a] aralık-dışı → çökme.
        int maxClasses = Math.Max(0, numChannels - 4);
        int nc, nKpts;
        if (_classNames.Length > 0)
        {
            nc         = Math.Min(_classNames.Length, maxClasses);
            int extra  = numChannels - 4 - nc;
            nKpts      = (extra > 0 && extra % 3 == 0) ? extra / 3 : 0;
        }
        else { nc = maxClasses; nKpts = 0; }
        if (nc <= 0) return new List<Detection>(); // dejenere/uyumsuz model çıkışı

        var raw = new List<Detection>(64);
        for (int a = 0; a < numAnchors; a++)
        {
            float maxScore = 0f; int bestCls = 0;
            for (int c = 0; c < nc; c++)
            {
                float s = output[0, 4 + c, a];
                if (s > maxScore) { maxScore = s; bestCls = c; }
            }
            if (maxScore < _confThresh) continue;

            float cx = output[0, 0, a]; float cy = output[0, 1, a];
            float bw = output[0, 2, a]; float bh = output[0, 3, a];

            float x1 = Math.Clamp((cx - bw * 0.5f - padX) / scale, 0, origW);
            float y1 = Math.Clamp((cy - bh * 0.5f - padY) / scale, 0, origH);
            float x2 = Math.Clamp((cx + bw * 0.5f - padX) / scale, 0, origW);
            float y2 = Math.Clamp((cy + bh * 0.5f - padY) / scale, 0, origH);
            if (x2 <= x1 || y2 <= y1) continue; // dejenere bbox

            string className = (bestCls < _classNames.Length)
                ? _classNames[bestCls]
                : bestCls.ToString(System.Globalization.CultureInfo.InvariantCulture);

            PointF? kp = null;
            if (nKpts > 0) // yalnız pose modeli
            {
                int kpBase = 4 + nc;
                float kpx = output[0, kpBase, a]; float kpy = output[0, kpBase + 1, a];
                float kpConf = output[0, kpBase + 2, a];
                if (kpConf > 0.3f)
                {
                    float kpxOrig = Math.Clamp((kpx - padX) / scale, 0, origW);
                    float kpyOrig = Math.Clamp((kpy - padY) / scale, 0, origH);
                    kp = new PointF(kpxOrig, kpyOrig);
                }
            }

            raw.Add(new Detection(bestCls, className,
                new RectangleF(x1, y1, x2 - x1, y2 - y1), maxScore, kp));
        }

        return ApplyNms(raw, _iouThresh);
    }

    // ── Preprocess (P1): tek-geçiş doğrudan bilinear → slot'un NCHW tensor buffer'ı ─────────────
    // GDI+ DrawImage (full-screen bilinear ~34ms) KALDIRILDI; yakalanan 32bpp BGRA kareyi DOĞRUDAN
    // bilinear örnekle+letterbox+normalize. Bilinear → model eğitim dağılımıyla uyumlu (accuracy korunur).
    // P2: slot parametresi → çok-slot havuza yazar (üretici/tüketici eşzamanlılığı).
    private (float padX, float padY, float scale) PreprocessSlot(Bitmap src, int slot)
    {
        float scale   = Math.Min((float)_inputSize / src.Width, (float)_inputSize / src.Height);
        int   scaledW = (int)Math.Round(src.Width  * scale);
        int   scaledH = (int)Math.Round(src.Height * scale);
        float padX    = (_inputSize - scaledW) * 0.5f;
        float padY    = (_inputSize - scaledH) * 0.5f;

        const float inv255 = 1f / 255f;
        const float padVal = 114f * inv255;      // letterbox dolgu (gri)
        int   plane = _inputSize * _inputSize;    // R:0, G:plane, B:2*plane
        int   sw = src.Width, sh = src.Height;
        int   maxX = sw - 1, maxY = sh - 1;
        float[] buf = _tensorBufs[slot];

        var srcData = src.LockBits(new Rectangle(0, 0, sw, sh),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* sp     = (byte*)srcData.Scan0;
                int   stride = srcData.Stride;
                fixed (float* tb = buf)
                {
                    for (int oy = 0; oy < _inputSize; oy++)
                    {
                        int   o   = oy * _inputSize;
                        float syf = (oy - padY) / scale;            // kaynak y (float)
                        bool  yIn = syf >= 0f && syf <= maxY;
                        int   y0 = 0; float fy = 0f, fy1 = 1f;
                        byte* rowY0 = sp, rowY1 = sp;
                        if (yIn)
                        {
                            y0  = (int)syf;
                            int y1 = y0 < maxY ? y0 + 1 : y0;
                            fy  = syf - y0; fy1 = 1f - fy;
                            rowY0 = sp + (long)y0 * stride;
                            rowY1 = sp + (long)y1 * stride;
                        }
                        for (int ox = 0; ox < _inputSize; ox++)
                        {
                            float sxf = (ox - padX) / scale;        // kaynak x (float)
                            if (!yIn || sxf < 0f || sxf > maxX)
                            {
                                tb[o + ox] = tb[plane + o + ox] = tb[2 * plane + o + ox] = padVal;
                                continue;
                            }
                            int   x0 = (int)sxf; int x1 = x0 < maxX ? x0 + 1 : x0;
                            float fx = sxf - x0; float fx1 = 1f - fx;
                            byte* pa = rowY0 + (long)x0 * 4; byte* pb = rowY0 + (long)x1 * 4;
                            byte* pc = rowY1 + (long)x0 * 4; byte* pd = rowY1 + (long)x1 * 4;
                            float w00 = fx1 * fy1, w01 = fx * fy1, w10 = fx1 * fy, w11 = fx * fy;
                            // 32bpp BGRA: [0]=B [1]=G [2]=R
                            float rr = pa[2] * w00 + pb[2] * w01 + pc[2] * w10 + pd[2] * w11;
                            float gg = pa[1] * w00 + pb[1] * w01 + pc[1] * w10 + pd[1] * w11;
                            float bb = pa[0] * w00 + pb[0] * w01 + pc[0] * w10 + pd[0] * w11;
                            tb[o + ox]             = rr * inv255; // R
                            tb[plane + o + ox]     = gg * inv255; // G
                            tb[2 * plane + o + ox] = bb * inv255; // B
                        }
                    }
                }
            }
        }
        finally
        {
            src.UnlockBits(srcData);
        }

        return (padX, padY, scale);
    }

    // ── Non-Maximum Suppression ───────────────────────────────────────────────

    private static List<Detection> ApplyNms(List<Detection> dets, float iouThresh)
    {
        if (dets.Count <= 1) return dets;

        var result = new List<Detection>(dets.Count);

        // Class başına ayrı NMS — farklı mob türleri birbirini elemez
        foreach (var group in dets.GroupBy(d => d.ClassId))
        {
            var sorted = group.OrderByDescending(d => d.Confidence).ToList();
            while (sorted.Count > 0)
            {
                var best = sorted[0];
                result.Add(best);
                sorted.RemoveAt(0);
                sorted.RemoveAll(d => Iou(best.BBox, d.BBox) >= iouThresh);
            }
        }

        return result;
    }

    private static float Iou(RectangleF a, RectangleF b)
    {
        float interX1 = Math.Max(a.Left,   b.Left);
        float interY1 = Math.Max(a.Top,    b.Top);
        float interX2 = Math.Min(a.Right,  b.Right);
        float interY2 = Math.Min(a.Bottom, b.Bottom);

        float interW = Math.Max(0f, interX2 - interX1);
        float interH = Math.Max(0f, interY2 - interY1);
        float inter  = interW * interH;
        if (inter <= 0f) return 0f;

        float aArea = a.Width * a.Height;
        float bArea = b.Width * b.Height;
        float union = aArea + bArea - inter;
        return union <= 0f ? 0f : inter / union;
    }

    // ── Warmup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// TensorRT motorunun ilk açılışta (uygulama boşta iken) derlenmesini sağlar.
    /// Böylece kullanıcı Başlat'a bastığında FPS 0'da donmaz.
    /// </summary>
    public async Task WarmUpAsync(Action<string>? onStatusChanged = null)
    {
        if (_session is null) return;

        await Task.Run(() =>
        {
            try
            {
                if (_epUsed == "TensorRT")
                {
                    onStatusChanged?.Invoke("⚙️ Yapay Zeka Optimize Ediliyor (İlk Kullanıma Özel, Lütfen Bekleyin...)");
                }

                // Boş bir tensor ile ilk inference'ı tetikle — P2: read kilidi (Load/Dispose write ile yarışmaz).
                _rwLock.EnterReadLock();
                try
                {
                    var session = _session;
                    if (session is null || _tensors.Length == 0) return;
                    string inputName = session.InputMetadata.Keys.First();
                    using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, _tensors[0]) });
                }
                finally { _rwLock.ExitReadLock(); }

                if (_epUsed == "TensorRT" || _epUsed == "CUDA")
                    onStatusChanged?.Invoke($"🚀 {_epUsed} Hazır");
                else
                    onStatusChanged?.Invoke($"🐢 {_epUsed} Modu (Yavaş)");
            }
            catch (Exception ex)
            {
                HomekoWorld.Program.Log($"[YOLO] Warmup hatası: {ex.Message}");
                onStatusChanged?.Invoke($"⚠ Hata (CPU Modu)");
            }
        });
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _rwLock.EnterWriteLock();
        try
        {
            _session?.Dispose();
            _session    = null;
            _tensorBufs = Array.Empty<float[]>();
            _tensors    = Array.Empty<DenseTensor<float>>();
        }
        finally { _rwLock.ExitWriteLock(); }
        _rwLock.Dispose();
    }
}
