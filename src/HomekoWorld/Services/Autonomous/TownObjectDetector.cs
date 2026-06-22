using HomekoWorld.Models;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Yolo;

namespace HomekoWorld.Services.Autonomous;

/// <summary>
/// Faz 41 — NPC + Portal görsel tespiti. Mob YOLO'sundan AYRI, 2 sınıflı (npc, portal) küçük model.
/// Navigasyon merchant/portal koordinatına varınca birkaç sn ekranı tarayıp NPC/portal kutusunu bulur;
/// kör tık (ekran merkezi + offset) yerine kutunun MERKEZİNE tıklanır → kamera açısı NPC/portal'ı
/// merkezden kaydırınca ıskalamaz. Modeli <see cref="AutonomousSettings.NpcPortalModelPath"/>'ten yükler;
/// yol değişince yeniden yükler. Model yoksa <see cref="IsReady"/>=false → çağıran eski kör-tık
/// fallback'ine düşer (bozulmadan derlenir/çalışır).
/// Model 2 sınıfla eğitilmeli — sınıf 0 = "npc", sınıf 1 = "portal" (bkz. <see cref="ClassNames"/>).
/// </summary>
public sealed class TownObjectDetector
{
    public const string NpcClass    = "npc";
    public const string PortalClass = "portal";
    /// <summary>Eğitimdeki sınıf SIRASI (0=npc, 1=portal) — modelin sınıf indekslerini isme çevirir.</summary>
    public static readonly string[] ClassNames = { NpcClass, PortalClass };

    private readonly AppState _state;
    private readonly object   _gate = new();   // EnsureLoaded/Dispose'u serialize eder (engine thread + VM Test thread)

    private OnnxYoloInferrer? _inferrer;
    private string            _loadedPath = "";
    private string            _loadError  = "model seçilmedi";

    public TownObjectDetector(AppState state) => _state = state;

    public event EventHandler<string>? StatusChanged;

    /// <summary>Model yüklü + hazır mı (yol set + dosya var + yüklenebildi).</summary>
    public bool   IsReady   => EnsureLoaded() is not null;
    public string LoadError => _loadError;

    /// <summary>Modeli önceden yükler (town turuna girerken çağrılır; farm durmuş, GPU boş → ısınma maliyeti
    /// merchant'a varmadan önce ödenir). Yükleme hatası sessizdir (IsReady false kalır).</summary>
    public void Preload() => EnsureLoaded();

    /// <summary>
    /// timeoutMs boyunca ekranı tarar; verilen sınıfın EN YÜKSEK güvenli tespitini döndürür (ekran-uzayı kutu).
    /// Bulununca hemen döner; süre dolarsa null. Güven eşiği ayardan uygulanır (zayıf tespitler elenir).
    /// </summary>
    public async Task<Detection?> DetectAsync(string className, int timeoutMs, CancellationToken ct)
    {
        var inf = EnsureLoaded();
        if (inf is null) { Status($"⚠ NPC/Portal modeli hazır değil ({_loadError})"); return null; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        while (sw.ElapsedMilliseconds < Math.Max(300, timeoutMs))
        {
            ct.ThrowIfCancellationRequested();
            Detection? hit = null;
            try
            {
                using var frame = ScreenCapture.CaptureScreen();
                foreach (var d in inf.Infer(frame))
                {
                    if (!string.Equals(d.ClassName, className, StringComparison.OrdinalIgnoreCase)) continue;
                    if (hit is null || d.Confidence > hit.Confidence) hit = d;
                }
                frames++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Status($"Tespit hatası: {ex.Message}"); }

            if (hit is not null)
            {
                Status($"{className} bulundu (%{(int)(hit.Confidence * 100)}) @ {(int)hit.Center.X},{(int)hit.Center.Y}");
                return hit;
            }
            await Task.Delay(120, ct);
        }
        Status($"{className} bulunamadı ({frames} kare, {timeoutMs}ms)");
        return null;
    }

    /// <summary>Test: npc + portal için tek-seferlik tespit özeti (UI "Test NPC/Portal Tespiti" butonu).</summary>
    public async Task<string> TestDetectAsync(int timeoutMs, CancellationToken ct)
    {
        if (EnsureLoaded() is null) return $"⚠ Model hazır değil: {_loadError}";
        var npc    = await DetectAsync(NpcClass, timeoutMs, ct);
        var portal = await DetectAsync(PortalClass, timeoutMs, ct);
        string n = npc    is null ? "NPC: ✗ bulunamadı"
                                  : $"NPC: ✓ %{(int)(npc.Confidence * 100)} @ {(int)npc.Center.X},{(int)npc.Center.Y}";
        string p = portal is null ? "Portal: ✗ bulunamadı"
                                  : $"Portal: ✓ %{(int)(portal.Confidence * 100)} @ {(int)portal.Center.X},{(int)portal.Center.Y}";
        return $"{n}   |   {p}";
    }

    /// <summary>Model yüklü mü; yol değiştiyse yeniden yükler. Hata/eksik → null + <see cref="LoadError"/> set.
    /// Engine thread'i (Preload/DetectAsync) ile VM Test thread'i (Task.Run) aynı anda çağırabildiğinden _gate ile serialize edilir.</summary>
    private OnnxYoloInferrer? EnsureLoaded()
    {
        var a    = _state.Autonomous;
        var path = a.NpcPortalModelPath;

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                _loadError = string.IsNullOrWhiteSpace(path) ? "model seçilmedi" : "model dosyası bulunamadı";
                DisposeInferrerLocked();
                return null;
            }
            if (_inferrer is not null && _loadedPath == path) return _inferrer;

            DisposeInferrerLocked();
            try
            {
                var inf = new OnnxYoloInferrer();
                inf.Load(path, ClassNames, a.NpcPortalInputSize, _state.Farm.InferenceBackend);
                inf.ConfThreshold = (float)a.NpcPortalConfThreshold;
                _inferrer   = inf;
                _loadedPath = path;
                _loadError  = "";
                Status($"NPC/Portal modeli yüklendi ({System.IO.Path.GetFileName(path)})");
                return _inferrer;
            }
            catch (Exception ex)
            {
                _loadError  = ex.Message;
                _inferrer   = null;
                _loadedPath = "";
                return null;
            }
        }
    }

    private void DisposeInferrerLocked()
    {
        try { _inferrer?.Dispose(); } catch { /* yoksay */ }
        _inferrer   = null;
        _loadedPath = "";
    }

    private void Status(string msg) => StatusChanged?.Invoke(this, msg);
}
