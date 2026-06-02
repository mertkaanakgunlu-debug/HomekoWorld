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
    private readonly WalkToMobEngine      _wtm;
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
    private const int DeadBlacklistRadiusPx = 64; // kayan ceset YOLO kutusunu da kapsasın (#4)

    // TrackAndCombat'in son bilinen hedef merkezi — kill sonrası ölü kara listesi için.
    private PointF _lastEngagedCenter;

    // ── Decoupled YOLO tespit thread'i (Sorun 2) ─────────────────────────────
    // Inference kendi thread'inde sürekli döner; scanning/combat döngüleri bu anlık
    // görüntüyü (snapshot) okur, asla inference beklemez → daima en taze tespit
    // (bayat pozisyon takibi = hedef etrafında dönme biter).
    private volatile DetectionSnapshot? _latestDetections;
    private volatile Detection?         _currentTargetForOverlay; // combat'te saldırılan hedef (overlay vurgusu)
    private Thread?                     _detThread;

    private sealed record DetectionSnapshot(
        IReadOnlyList<Detection> Dets, int W, int H, long CaptureMs,
        bool? TargetAliveHsv);

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
    public HpBarPresenceClassifier? HpClassifier { get; set; }

    public FarmEngine(
        TransportRouter    router,
        ComboEngine        combo,
        WalkToMobEngine    wtm,
        GlobalKeyboardHook keyHook,
        GlobalMouseHook    mouseHook,
        MobLibrary         mobLibrary,
        AppState           appState)
    {
        _router     = router;
        _combo      = combo;
        _wtm        = wtm;
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

        _wtm.PauseForFarm = true;

        _keyHook.KeyDown += OnKeyDown;
        _cts = new CancellationTokenSource();
        Telemetry.Reset();
        _paused = false;
        _idleWatch.Restart();
        _wayIndex = 0;
        _deadBlacklist.Clear();
        _latestDetections        = null;
        _currentTargetForOverlay = null;

        // YOLO inference ayrı thread'de sürekli döner (combat döngüsünü bloke etmez).
        _detThread = new Thread(() => DetectionLoop(_cts.Token))
            { IsBackground = true, Name = "FarmDetection" };
        _detThread.Start();

        _ = Task.Run(() => FarmLoopAsync(_cts.Token));
        SetState(FarmState.Scanning, "Taranıyor…");
    }

    public void Stop()
    {
        _keyHook.KeyDown -= OnKeyDown;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _combo.CancelAll();
        _wtm.PauseForFarm = false;
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
}
