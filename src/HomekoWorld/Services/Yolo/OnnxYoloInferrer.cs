using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using HomekoWorld.Models.Farm;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HomekoWorld.Services.Yolo;

/// <summary>
/// YOLOv8 ONNX modelini DirectML (GPU) ile çalıştıran inferrer.
/// Output shape: (1, 4+nc, 8400) — YOLOv8 export formatı.
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

    // ── Reusable letterbox buffer (garbage azaltmak için) ─────────────────────
    private Bitmap? _letterbox;

    // ── Reusable tensor buffer — her inference'ta allocation önlenir ──
    // Load() içinde _inputSize'a göre kurulur (field initializer instance alanına bağlanamaz).
    private          float[]           _tensorBuf = Array.Empty<float>();
    private          DenseTensor<float>? _tensor;

    // ── Yükleme ───────────────────────────────────────────────────────────────

    /// <summary>
    /// ONNX modelini yükler.
    /// classNames: mobs.json'dan gelen sıralı isimler — inference sonucunda className alanını doldurur.
    /// </summary>
    public void Load(string onnxPath, IReadOnlyList<string>? classNames = null, int inputSize = 640,
                     InferenceBackend backend = InferenceBackend.Auto)
    {
        _onnxPath   = onnxPath;
        _classNames = classNames?.ToArray() ?? Array.Empty<string>();
        _backend    = backend;
        _forcedCpu  = false;
        _failStreak = 0;

        // Model giriş boyutu (imgsz) — eğitim/export ile eşleşmeli. 960'ta export edilen model 640 ile
        // beslenirse doğruluk kazancı kaybolur. Buffer'lar bu boyuta göre (yeniden) kurulur.
        _inputSize  = Math.Clamp(inputSize, 320, 1920);
        _tensorBuf  = new float[1 * 3 * _inputSize * _inputSize];
        _tensor     = null;        // yeni boyutla yeniden kurulur (Preprocess'te lazy)
        _letterbox?.Dispose();
        _letterbox  = null;

        BuildSession();
    }

    /// <summary>Session'ı tercih edilen EP ile (yeniden) kurar; gerçekleşen EP <see cref="_epUsed"/>'a yazılır.</summary>
    private void BuildSession()
    {
        _session?.Dispose();
        _session = null;

        // GPU üst üste hata verdiyse (_forcedCpu) tercih ne olursa olsun CPU.
        var backend = _forcedCpu ? InferenceBackend.Cpu : _backend;
        var opts    = OrtEpFactory.Create(backend, out _epUsed);
        _session    = new InferenceSession(_onnxPath, opts);
        HomekoWorld.Program.Log($"[YOLO] Execution provider: {_epUsed}");
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
    /// Verilen frame üzerinde inference yapar ve tespit listesi döndürür.
    /// frame'in Dispose() sorumluluğu çağıranın — bu metod Dispose etmez.
    /// </summary>
    public IReadOnlyList<Detection> Infer(Bitmap frame)
    {
        if (_session is null) return Array.Empty<Detection>();

        int origW = frame.Width;
        int origH = frame.Height;

        // 1. Letterbox + tensor
        (var tensor, float padX, float padY, float scale) = Preprocess(frame);

        // 2. Inference — GPU TDR / device-removed'a karşı korumalı (A4)
        string inputName = _session.InputMetadata.Keys.First();
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
        try
        {
            results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        }
        catch (Exception ex)
        {
            HomekoWorld.Program.Log($"[YOLO] Inference hatası (EP={_epUsed}): {ex.Message}");
            RecoverAfterFailure();
            return Array.Empty<Detection>(); // bu kare atlanır; sonraki kare kurtarılmış session'ı kullanır
        }
        _failStreak = 0; // başarılı inference → hata sayacı sıfır

        using var _resultsScope = results; // metod sonunda dispose

        // 3. Output parse
        //    BBox  modeli: (1, 4+nc,       8400)
        //    Pose  modeli: (1, 4+nc+nk*3,  8400)  nk = keypoint sayısı (bizde 1)
        var output      = results.First().AsTensor<float>();
        int numAnchors  = output.Dimensions[2];
        int numChannels = output.Dimensions[1];

        // nc: sınıf sayısını _classNames'ten al (yüklüyse); yoksa eski davranış.
        int nc, nKpts;
        if (_classNames.Length > 0)
        {
            nc         = _classNames.Length;
            int extra  = numChannels - 4 - nc;
            nKpts      = (extra > 0 && extra % 3 == 0) ? extra / 3 : 0;
        }
        else
        {
            nc    = numChannels - 4; // BBox modeli varsayımı
            nKpts = 0;
        }

        var raw = new List<Detection>(64);

        for (int a = 0; a < numAnchors; a++)
        {
            // En yüksek sınıf skorunu bul
            float maxScore = 0f;
            int   bestCls  = 0;
            for (int c = 0; c < nc; c++)
            {
                float s = output[0, 4 + c, a];
                if (s > maxScore) { maxScore = s; bestCls = c; }
            }
            if (maxScore < _confThresh) continue;

            // Bounding box — 640-uzayında cx,cy,w,h
            float cx = output[0, 0, a];
            float cy = output[0, 1, a];
            float bw = output[0, 2, a];
            float bh = output[0, 3, a];

            // Letterbox offset kaldır, orijinal piksel uzayına dönüştür
            float x1 = Math.Clamp((cx - bw * 0.5f - padX) / scale, 0, origW);
            float y1 = Math.Clamp((cy - bh * 0.5f - padY) / scale, 0, origH);
            float x2 = Math.Clamp((cx + bw * 0.5f - padX) / scale, 0, origW);
            float y2 = Math.Clamp((cy + bh * 0.5f - padY) / scale, 0, origH);

            if (x2 <= x1 || y2 <= y1) continue; // dejenere bbox

            string className = (bestCls < _classNames.Length)
                ? _classNames[bestCls]
                : bestCls.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Keypoint parse — sadece pose modeli (nKpts > 0)
            // İlk keypoint: isim etiketi tıklama noktası
            PointF? kp = null;
            if (nKpts > 0)
            {
                int   kpBase = 4 + nc;                      // ilk kp'nin kanal indexi
                float kpx    = output[0, kpBase,     a];
                float kpy    = output[0, kpBase + 1, a];
                float kpConf = output[0, kpBase + 2, a];    // visibility (0-1)

                if (kpConf > 0.3f) // düşük güvenilir keypoint'leri atla
                {
                    float kpxOrig = Math.Clamp((kpx - padX) / scale, 0, origW);
                    float kpyOrig = Math.Clamp((kpy - padY) / scale, 0, origH);
                    kp = new System.Drawing.PointF(kpxOrig, kpyOrig);
                }
            }

            raw.Add(new Detection(
                bestCls,
                className,
                new RectangleF(x1, y1, x2 - x1, y2 - y1),
                maxScore,
                kp));
        }

        // 4. NMS
        return ApplyNms(raw, _iouThresh);
    }

    // ── Preprocess: letterbox → DenseTensor NCHW ─────────────────────────────

    private (DenseTensor<float> tensor, float padX, float padY, float scale)
        Preprocess(Bitmap src)
    {
        float scaleX = (float)_inputSize / src.Width;
        float scaleY = (float)_inputSize / src.Height;
        float scale  = Math.Min(scaleX, scaleY);

        int scaledW = (int)Math.Round(src.Width  * scale);
        int scaledH = (int)Math.Round(src.Height * scale);

        float padX = (_inputSize - scaledW) * 0.5f;
        float padY = (_inputSize - scaledH) * 0.5f;

        // Letterbox bitmap'i yeniden kullan (allocation azalt)
        if (_letterbox is null)
            _letterbox = new Bitmap(_inputSize, _inputSize, PixelFormat.Format24bppRgb);

        using (var g = Graphics.FromImage(_letterbox))
        {
            g.Clear(Color.FromArgb(114, 114, 114));
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(src, padX, padY, scaledW, scaledH);
        }

        // Pixel → NCHW float tensor — önceden allocate edilmiş buffer'ı yeniden kullan
        _tensor ??= new DenseTensor<float>(_tensorBuf, new[] { 1, 3, _inputSize, _inputSize });
        var tensor = _tensor;
        var bmpData = _letterbox.LockBits(
            new Rectangle(0, 0, _inputSize, _inputSize),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        try
        {
            // Doğrudan backing buffer'a yaz (NCHW planar offset) — yavaş DenseTensor
            // indexer'ı (kare başına ~1.2M çağrı) yerine pinned pointer → 3-6× hızlı
            // preprocess, tespit temposu yükselir (Sorun 2).
            int plane = _inputSize * _inputSize; // R: 0, G: plane, B: 2*plane
            unsafe
            {
                byte* ptr    = (byte*)bmpData.Scan0;
                int   stride = bmpData.Stride;

                fixed (float* tb = _tensorBuf)
                {
                    for (int y = 0; y < _inputSize; y++)
                    {
                        byte* row = ptr + y * stride;
                        int   o   = y * _inputSize;
                        for (int x = 0; x < _inputSize; x++)
                        {
                            // Format24bppRgb byte order: B G R
                            int b3 = x * 3;
                            tb[o + x]             = row[b3 + 2] / 255f; // R
                            tb[plane + o + x]     = row[b3 + 1] / 255f; // G
                            tb[2 * plane + o + x] = row[b3 + 0] / 255f; // B
                        }
                    }
                }
            }
        }
        finally
        {
            _letterbox.UnlockBits(bmpData);
        }

        return (tensor, padX, padY, scale);
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

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _letterbox?.Dispose();
        _letterbox = null;
    }
}
