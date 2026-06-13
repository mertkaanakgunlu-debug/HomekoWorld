using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using HomekoWorld.Hardware;
using HomekoWorld.Hooks;
using HomekoWorld.Models;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Farm;
using HomekoWorld.Services.Telemetry;
using HomekoWorld.Services.Vision;
using HomekoWorld.Services.Yolo;

namespace HomekoWorld.Engine;

/// <summary>
/// Faz 17 — Oto Farm state machine.
/// YOLO tespiti → hedef kilitleme → takip + kombo (TrackAndCombatAsync) →
/// loot → roam → tekrar.
/// </summary>
public sealed partial class FarmEngine
{
    // ── Bağımlılıklar ──────────────────────────────────────────────────────────
    private readonly TransportRouter      _router;
    private readonly ComboEngine          _combo;
    private readonly GlobalKeyboardHook   _keyHook;
    private readonly GlobalMouseHook      _mouseHook;
    private readonly MobLibrary           _mobLibrary;
    private          AppState             _appState;

    // ── Durum ─────────────────────────────────────────────────────────────────
    private FarmState _state = FarmState.Idle;
    private CancellationTokenSource? _cts;
    private Detection? _lastTarget;
    private int        _wayIndex;
    private int        _cameraRotateDir = 1; // +1 = D, -1 = A; her başarısız targeting'de değişir
    private readonly System.Diagnostics.Stopwatch _idleWatch = new();
    private int        _scanAttempts; // kamera tarama adım sayacı

    // Koruma mobu kara listesi: (merkez koordinatı, süre sonu ms)
    private readonly List<(PointF pos, long expireAt)> _guardianBlacklist = new();
    private const int GuardianBlacklistRadiusPx = 60;
    private const int GuardianBlacklistDurationMs = 30_000;

    // Ölü/seçilemeyen mob kara listesi (kısa ömürlü): ceset hâlâ YOLO'da görünür;
    // "en yakın" sıralaması onu tekrar seçmesin diye o konum DeadBlacklistMs süre atlanır.
    private readonly List<(PointF pos, long expireAt)> _deadBlacklist = new();
    private const int DeadBlacklistRadiusPx = 90; // F2: 64→90, kayan/düşen ceset YOLO kutusunu kapsasın
    // F2: mob ölünce cesedi ayakta-merkezden ekranda AŞAĞI düşer (~100px; log'da @(987,650) standing →
    // @(986,758) corpse). Kill anında merkez + bu kadar aşağısı ayrı blacklist'lenir ki düşen kutu da kapsansın.
    private const int CorpseFallOffsetPx = 85;
    // Tık ıskaladı + tespit o an kayboldu (mobStillThere=False) → aynı noktaya kilitlenip sonsuz re-pick
    // döngüsüne girmesin diye KISA süre atla (statik false-positive ya da kaymış mob). Onaylanan ceset
    // blacklist'inden (DeadBlacklistMs) çok daha kısa: gerçekten kaydıysa mob kısa sürede geri alınır.
    private const int MissReselectSkipMs = 2500;

    // TrackAndCombat'in son bilinen hedef merkezi — kill sonrası ölü kara listesi için.
    private PointF _lastEngagedCenter;

    // ── Decoupled YOLO tespit thread'i (Sorun 2) ─────────────────────────────
    // Inference kendi thread'inde sürekli döner; scanning/combat döngüleri bu anlık
    // görüntüyü (snapshot) okur, asla inference beklemez → daima en taze tespit
    // (bayat pozisyon takibi = hedef etrafında dönme biter).
    private volatile DetectionSnapshot? _latestDetections;
    private volatile Detection?         _currentTargetForOverlay; // combat'te saldırılan hedef (overlay vurgusu)
    private Thread?                     _detThread;
    private volatile ReplayRecorder?    _replay; // Faz B: açıkken kayıt; null = kapalı (DetectionLoop okur)
    // P2: pipelined inference — üretici (capture+preprocess) ∥ tüketici (GPU Run+post) ayrı thread.
    private Thread?                     _captureThread;            // üretici (yalnız pipelined modda)
    private readonly object             _pipeLock   = new();
    private readonly Stack<int>         _freeSlots  = new();       // boş tensor slot havuzu (triple-buffer)
    private PipeItem?                   _pending;                  // latest-wins mailbox (≤1)
    private readonly AutoResetEvent     _pipeSignal = new(false);  // tüketici uyandırma

    // Faz B (frame identity): her inference'a monotonik FrameId + gerçek zaman damgaları.
    //  • FrameId       : tespit thread'inin ürettiği kare sayacı (replay/eşleştirme için).
    //  • CapturedAtMs  : ekran yakalamasının BAŞLADIĞI an → "frame age at click" = NowMs - CapturedAtMs
    //                    (capture+inference+publish+karar gecikmesinin tamamı; bayat-kutu tıklama ölçümü).
    //  • PublishedAtMs : snapshot'ın yayınlandığı an (inference bitişi). Combat "snapshot yaşı" için kullanır.
    private sealed record DetectionSnapshot(
        long FrameId, IReadOnlyList<Detection> Dets, int W, int H,
        long CapturedAtMs, long PublishedAtMs, bool? TargetAliveHsv);

    // P2: üreticiden tüketiciye geçen iş birimi. Yalnız TENSOR slot + meta geçer; capture Bitmap üreticide
    // KALIR (recorder klonu + HSV onun thread'inde). ReplayClone: replay açıksa üretici klonladı (tüketici yazar).
    // P3: RoiX/Y — ROI-yerel tespitleri ekran-uzayına çevirmek için ofset. 0,0 = tam-ekran.
    //     FullW/H — gerçek fiziksel ekran boyutu (snapshot + overlay ölçekleme için).
    private sealed record PipeItem(
        int Slot, long FrameId, long CapturedAtMs, float PadX, float PadY, float Scale,
        int W, int H, bool? TargetAliveHsv, double CapMs, double PrepMs, Bitmap? ReplayClone,
        int RoiX = 0, int RoiY = 0, int FullW = 0, int FullH = 0);

    // ms cinsinden monotonik "şimdi" (Stopwatch tabanlı).
    private static long NowMs() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);

    // p noktası, listedeki herhangi bir kara liste girdisinin radius'u içinde mi?
    private static bool NearAny(PointF p, List<(PointF pos, long expireAt)> list, int radius)
    {
        for (int i = 0; i < list.Count; i++)
        {
            float dx = p.X - list[i].pos.X, dy = p.Y - list[i].pos.Y;
            if (dx * dx + dy * dy <= radius * radius) return true;
        }
        return false;
    }

    // Karakter daima ekranın FİZİKSEL merkezindedir (#1: kalibrasyon kaldırıldı). DIP'li
    // SystemParameters yerine GetSystemMetrics → yüksek DPI'da tespit koordinatlarıyla hizalı.
    private static PointF ScreenCenter()
    {
        var (w, h) = ScreenCapture.PhysicalSize();
        return new PointF(w / 2f, h / 2f);
    }

    /// <summary>Farm döngüsü şu an aktif mi (Idle veya KillSwitched değil).</summary>
    public bool IsRunning => _state != FarmState.Idle && _state != FarmState.KillSwitched;

    // ── Duraklat ────────────────────────────────────────────────────────────────
    // Session/sayaçlar korunur; tarama ve combat in-place beklemeye geçer.
    private volatile bool _paused;
    public bool IsPaused => _paused;
    public void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
        {
            _combo.CancelAll();
            SetState(_state, "⏸ Duraklatıldı");
        }
        else
        {
            SetState(_state, "▶ Devam ediliyor…");
        }
    }

    // ── Olaylar ───────────────────────────────────────────────────────────────
    public event EventHandler<string>?       StatusChanged;
    public event EventHandler<FarmTelemetry>? TelemetryUpdated;
    /// <summary>Gönderilen her tuş/aktivite — Log HUD canlı akışı için.</summary>
    public event EventHandler<Models.Farm.ActivityEntry>? KeyLogged;

    /// <summary>Her tick'te seçili mob tespitleri + saldırılan hedef — Tespit overlay'i için.
    /// Abone yokken yayınlama maliyeti ≈sıfır; overlay yalnızca görünürken bağlanır.</summary>
    public event EventHandler<DetectionFrame>? DetectionsUpdated;

    public FarmTelemetry Telemetry { get; } = new();

    public IYoloInferrer?            Inferrer   { get; set; }

    public FarmEngine(
        TransportRouter    router,
        ComboEngine        combo,
        GlobalKeyboardHook keyHook,
        GlobalMouseHook    mouseHook,
        MobLibrary         mobLibrary,
        AppState           appState)
    {
        _router     = router;
        _combo      = combo;
        _keyHook    = keyHook;
        _mouseHook  = mouseHook;
        _mobLibrary = mobLibrary;
        _appState   = appState;
    }

    // ── Yaşam döngüsü ─────────────────────────────────────────────────────────

    public void Start()
    {
        if (_state != FarmState.Idle) return;

        bool hpBarCalibrated   = _appState.Wtm.IsHpBarLocated;
        if (!hpBarCalibrated)
        {
            SetState(FarmState.Idle, "⚠ Farm kalibrasyonu eksik — 4. adımı tamamlayın (hedef HP bar)");
            return;
        }

        _keyHook.KeyDown += OnKeyDown;
        _cts = new CancellationTokenSource();
        Telemetry.Reset();
        _paused = false;
        _idleWatch.Restart();
        _wayIndex = 0;
        _deadBlacklist.Clear();
        _latestDetections        = null;
        _currentTargetForOverlay = null;

        // Faz B: replay recorder (yalnız açıksa) — DetectionLoop başlamadan ÖNCE kurulmalı.
        _replay = null;
        if (_appState.Farm.ReplayEnabled)
        {
            try
            {
                _replay = CreateReplayRecorder();
                Program.Log($"[Farm] Replay kaydı açık → {_replay.SessionDir}");
            }
            catch (Exception ex)
            {
                Program.Log($"[Farm] Replay recorder başlatılamadı: {ex.Message}");
                _replay = null;
            }
        }

        // YOLO inference ayrı thread'de sürekli döner (combat döngüsünü bloke etmez).
        // P2: pipelined ise 2 thread (üretici capture+prep ∥ tüketici GPU+post) + slot havuzu; değilse seri tek thread.
        if (_appState.Farm.PipelinedInference)
        {
            lock (_pipeLock)
            {
                _freeSlots.Clear();
                for (int i = 0; i < OnnxYoloInferrer.SlotCount; i++) _freeSlots.Push(i);
                if (_pending is { } p) p.ReplayClone?.Dispose();
                _pending = null;
            }
            _captureThread = new Thread(() => CaptureProduceLoop(_cts.Token))
                { IsBackground = true, Name = "FarmCapture" };
            _detThread = new Thread(() => InferConsumeLoop(_cts.Token))
                { IsBackground = true, Name = "FarmInference" };
            _captureThread.Start();
            _detThread.Start();
        }
        else
        {
            _detThread = new Thread(() => DetectionLoop(_cts.Token))
                { IsBackground = true, Name = "FarmDetection" };
            _detThread.Start();
        }

        _ = Task.Run(() => FarmLoopAsync(_cts.Token));
        SetState(FarmState.Scanning, "Taranıyor…");
    }

    public void Stop()
    {
        _keyHook.KeyDown -= OnKeyDown;

        // A4: önce iptal → detection thread'i Join et → SONRA CTS'i dispose et. Eski sıra
        // (Cancel hemen ardından Dispose) thread hâlâ token okurken dispose ediyordu (broad-catch
        // yutuyordu ama kirli; ileride daha ağır runtime'da risk büyür). Join, FarmLoopAsync'in de
        // token henüz canlıyken temiz iptalini garanti eder.
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        _pipeSignal.Set(); // P2: tüketiciyi WaitOne'dan uyandır → hızlı çıkış

        // P2: pipelined modda üretici + tüketici iki thread → ikisini de Join et.
        if (_captureThread is not null && _captureThread.IsAlive && !_captureThread.Join(1000))
            Program.Log("[Farm] Capture thread 1sn içinde kapanmadı.");
        _captureThread = null;

        if (_detThread is not null && _detThread.IsAlive && !_detThread.Join(1000))
            Program.Log("[Farm] Detection thread 1sn içinde kapanmadı.");
        _detThread = null;

        // P2: kalan pending klonu temizle (her iki thread durdu → güvenli).
        lock (_pipeLock) { if (_pending is { } p) p.ReplayClone?.Dispose(); _pending = null; _freeSlots.Clear(); }

        // Faz B: replay recorder'ı kapat — detection thread durdu (yeni Offer gelmez); kuyruğu yaz + kapat.
        var replay = _replay;
        _replay = null;
        if (replay is not null)
        {
            try
            {
                replay.Dispose();
                Program.Log($"[Farm] Replay kaydı bitti — {replay.FramesWritten} kare → {replay.SessionDir}");
            }
            catch (Exception ex) { Program.Log($"[Farm] Replay kapatma hatası: {ex.Message}"); }
        }

        cts?.Dispose();

        _combo.CancelAll();
        _paused = false;
        _currentTargetForOverlay = null; // detection thread CT ile durur
        SetState(FarmState.Idle, "Pasif");
        // Tespit overlay'ini temizle — aksi halde son kareler ekranda donar kalır.
        DetectionsUpdated?.Invoke(this, new DetectionFrame(Array.Empty<Detection>(), null, 0, 0));
    }

    // ── Kill-switch ────────────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, HookKeyEventArgs e)
    {
        var killKey = _appState.Farm.KillSwitchKey;
        if (e.Key.ToString().Equals(killKey, StringComparison.OrdinalIgnoreCase))
        {
            SetState(FarmState.KillSwitched, "⛔ Kill-switch — durduruldu");
            Stop();
        }
    }

    /// <summary>
    /// Faz B: bu farm oturumu için replay recorder kurar. session.json model/runtime bağlamını taşır
    /// (offline benchmark hangi model/conf/iou/EP ile kaydedildiğini bilsin).
    /// </summary>
    private ReplayRecorder CreateReplayRecorder()
    {
        var s      = _appState.Farm;
        var (w, h) = ScreenCapture.PhysicalSize();
        string ep  = (Inferrer as OnnxYoloInferrer)?.ActiveEp ?? "?";
        var gpu    = OrtEpFactory.DetectPrimaryGpu();
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            createdAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            modelPath = s.ModelPath,
            imgsz     = s.ModelInputSize,
            conf      = s.ConfidenceThreshold,
            iou       = s.IouThreshold,
            backend   = s.InferenceBackend.ToString(),
            activeEp  = ep,
            gpu       = gpu.name,
            screenW   = w,
            screenH   = h,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return new ReplayRecorder(AppContext.BaseDirectory, s.ReplayMaxFrames, jpegQuality: 75, json);
    }
}
