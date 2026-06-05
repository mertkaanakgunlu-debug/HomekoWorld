using System.Drawing;
using System.Windows.Input;
using HomekoWorld.Hardware;
using HomekoWorld.Hooks;
using HomekoWorld.Models;
using HomekoWorld.Services;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Vision;

namespace HomekoWorld.Engine;

/// <summary>
/// Walk-to-Mob engine (Phase 3 — keyboard walk approach).
///
/// On mouse-click or Z-key trigger:
///   1. Confirms target selected (HP-bar pixel check).
///   2. Holds W (walk forward) and taps R once (face target in KO).
///      → Character runs straight toward the selected mob.
///   3. Every PollIntervalMs scans a small window around the calibrated
///      CharacterCenter for green ring pixels (IsRingNearCharacterCenter).
///      When the ring enters that window the character is adjacent.
///   4. Releases W, fires the combo.
///   5. Loops while the target HP-bar remains visible (mob alive).
///   Safety: auto-cancels after 30 s to prevent runaway on miscalibration.
/// </summary>
public sealed class WalkToMobEngine : IDisposable
{
    private readonly ComboEngine        _engine;
    private readonly GlobalMouseHook    _mouseHook;
    private readonly GlobalKeyboardHook _keyHook;
    private readonly AppState           _state;
    private readonly IKeyDeviceTransport _transport;

    private WtmSettings Settings => _state.Wtm;

    public event EventHandler<string>? StatusChanged;

    // Set to true during calibration so click/key triggers are ignored.
    public bool PauseForCalibration { get; set; }

    // Set to true while FarmEngine is running — WtM yields control to FarmEngine.
    private bool _pauseForFarm;
    public bool PauseForFarm
    {
        get => _pauseForFarm;
        set
        {
            _pauseForFarm = value;
            if (value) CancelEngagement(); // immediately abort any in-progress walk
        }
    }

    private readonly object _stateLock = new();
    private CancellationTokenSource? _engageCts;

    private TaskCompletionSource<bool>? _comboTcs;
    private string?                     _waitingComboId;

    public WalkToMobEngine(
        ComboEngine         engine,
        GlobalMouseHook     mouseHook,
        GlobalKeyboardHook  keyHook,
        AppState            state,
        IKeyDeviceTransport transport)
    {
        _engine    = engine;
        _mouseHook = mouseHook;
        _keyHook   = keyHook;
        _state     = state;
        _transport = transport;

        _engine.ComboFired += OnComboFired;
    }

    // ---- enable / disable ----

    public void Start()
    {
        _mouseHook.MouseDown += OnMouseDown;
        _keyHook.KeyDown     += OnKeyDown;
    }

    public void Stop()
    {
        _mouseHook.MouseDown -= OnMouseDown;
        _keyHook.KeyDown     -= OnKeyDown;
        CancelEngagement();
        _engine.CancelAll(); // abort any combo fired by this engine
        Emit("Pasif");
    }

    public void Dispose()
    {
        Stop();
        _engine.ComboFired -= OnComboFired;
    }

    // ---- trigger handlers ----

    private void OnMouseDown(object? sender, Point p)
    {
        if (!Settings.Enabled || PauseForCalibration || _pauseForFarm) return;
        if (!_state.Active) return;
        Trigger(300);
    }

    private void OnKeyDown(object? sender, HookKeyEventArgs e)
    {
        if (!Settings.Enabled || PauseForCalibration || _pauseForFarm) return;
        if (!_state.Active) return;
        if (e.Key == Key.Z)
            Trigger(300); // Z selects nearest mob in KO; let it pass through, then check
    }

    private void OnComboFired(object? sender, ComboFiredEventArgs e)
    {
        if (e.ComboId == _waitingComboId)
            _comboTcs?.TrySetResult(true);
    }

    // ---- trigger → engage ----

    private void Trigger(int confirmDelayMs)
    {
        CancellationTokenSource newCts;
        lock (_stateLock)
        {
            _engageCts?.Cancel();
            newCts     = new CancellationTokenSource();
            _engageCts = newCts;
        }
        _ = Task.Run(() => RunEngagementAsync(confirmDelayMs, newCts.Token));
    }

    private void CancelEngagement()
    {
        lock (_stateLock)
        {
            _engageCts?.Cancel();
            _engageCts = null;
        }
    }

    private async Task RunEngagementAsync(int confirmDelayMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(confirmDelayMs, ct);

            // Race-safety: user may have toggled Active or Farm started during delay
            if (!_state.Active || _pauseForFarm) return;

            if (!WtmVision.IsTargetAlive(Settings)) return;

            Emit("Hedef tespit edildi");
            await EngageLoopAsync(ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            Emit("Pasif");
        }
    }

    // Outer engagement loop: walk → fire → repeat while target alive.
    private async Task EngageLoopAsync(CancellationToken ct)
    {
        var combo = FindCombo();
        if (combo is null)
        {
            Emit("⚠ WtM combo seçilmedi");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            if (!WtmVision.IsTargetAlive(Settings)) return;

            Emit("Yürüyor…");
            bool adjacent = await WalkAndWaitForAdjacencyAsync(ct);
            if (!adjacent) return;

            Emit("Bitişik — kombo ateşleniyor");

            if (combo.IsLoop)
            {
                _engine.FireAsync(combo);
                // Monitor until mob dies, then cancel the loop
                while (!ct.IsCancellationRequested && WtmVision.IsTargetAlive(Settings))
                    await Task.Delay(Settings.PollIntervalMs, ct);
                _engine.CancelAll();
                return;
            }
            else
            {
                _comboTcs       = new TaskCompletionSource<bool>();
                _waitingComboId = combo.Id;
                _engine.FireAsync(combo);

                // Wait for ComboFired or safety timeout
                int safetyMs = EstimateComboDurationMs(combo) + 3000;
                await Task.WhenAny(_comboTcs.Task, Task.Delay(safetyMs, ct));
                _waitingComboId = null;

                // Target still alive? → re-walk (outer loop continues)
            }
        }
    }

    /// <summary>
    /// Holds the W key (walk forward) and taps R once (face target in KO).
    /// Polls IsRingNearCharacterCenter every PollIntervalMs; when the green
    /// ring enters the CenterWindowRadius around the calibrated character
    /// center the character is adjacent and W is released.
    /// Always releases W in the finally block even on cancellation or error.
    /// Returns false if the target disappears or the 30-s safety fires.
    /// </summary>
    private async Task<bool> WalkAndWaitForAdjacencyAsync(CancellationToken ct)
    {
        // Safety: stop after 30 s to avoid infinite walk on miscalibration.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var tct = timeout.Token;

        // Faz 30: W/R artık aktif transport üstünden (RP2040 / Local / phone).
        // Sentetik keybd_event baypası kaldırıldı → yaklaşma da gerçek HID'den.
        await _transport.KeyDownAsync(Settings.WalkKey, tct);
        try
        {
            // Tap direction key once so the character immediately faces the mob.
            await Task.Delay(100, tct);
            await _transport.KeyDownAsync(Settings.DirectionKey, tct);
            await Task.Delay(30, tct);
            await _transport.KeyUpAsync(Settings.DirectionKey, tct);

            while (!tct.IsCancellationRequested)
            {
                if (!WtmVision.IsTargetAlive(Settings)) return false;

                using var bmp = ScreenCapture.CaptureScreen();
                if (WtmVision.IsRingNearCharacterCenter(bmp, Settings)) return true;

                await Task.Delay(Settings.PollIntervalMs, tct);
            }
        }
        finally
        {
            // Always release W — critical to avoid stuck-walk if anything throws.
            // CancellationToken.None: iptal olsa bile W mutlaka bırakılmalı.
            await _transport.KeyUpAsync(Settings.WalkKey, CancellationToken.None);
        }

        return false;
    }

    // ---- helpers ----

    private Combo? FindCombo() =>
        string.IsNullOrEmpty(Settings.ComboId)
            ? null
            : _state.Combos.FirstOrDefault(c => c.Id == Settings.ComboId);

    private static int EstimateComboDurationMs(Combo combo) =>
        combo.Steps.Sum(s => s.HoldMs + s.DelayMs) + 200;

    private void Emit(string status) => StatusChanged?.Invoke(this, status);
}
