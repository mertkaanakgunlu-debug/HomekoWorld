using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Models.Autonomous;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services.Autonomous;

namespace HomekoWorld.Engine;

/// <summary>
/// Faz 31/34/35 — Otonom Oyuncu orkestratörü.
/// FarmEngine/ComboEngine'i DEĞİŞTİRMEDEN üstlerine kompozisyonla oturan bir FSM. Kendi
/// thread'inde döner (AutoPotService deseni) ve oto-farm modunu programatik açıp kapatır.
/// Faz 34: WorldNavigator (NavToMerchant/NavToPortal/NavToFarmSpot).
/// Faz 35: Town TP (GoingToTown) + portal etkileşimi (UsingPortal) + Selling stub.
/// </summary>
public sealed partial class AutonomousPlayerEngine
{
    private readonly FarmEngine          _farm;
    private readonly AppState            _state;
    private readonly WorldNavigator      _navigator;
    private readonly IKeyDeviceTransport _transport;
    private readonly InventoryReader     _inventoryReader;
    private readonly MerchantTrader      _merchantTrader;

    private AutoPlayerState _fsm = AutoPlayerState.Idle;
    private CancellationTokenSource? _cts;

    // Faz 37: dayanıklılık
    private int            _consecutiveFailures;
    private AutoPlayerState _lastFailedState = AutoPlayerState.Idle;

    public AutonomousPlayerEngine(
        FarmEngine          farm,
        AppState            state,
        WorldNavigator      navigator,
        IKeyDeviceTransport transport,
        InventoryReader     inventoryReader,
        MerchantTrader      merchantTrader)
    {
        _farm            = farm;
        _state           = state;
        _navigator       = navigator;
        _transport       = transport;
        _inventoryReader = inventoryReader;
        _merchantTrader  = merchantTrader;

        _farm.TelemetryUpdated += OnFarmTelemetryUpdated;
    }

    // ── Durum ─────────────────────────────────────────────────────────────────
    /// <summary>Otonom döngü aktif mi (Idle/Stopped değil).</summary>
    public bool IsRunning => _fsm is not (AutoPlayerState.Idle or AutoPlayerState.Stopped);
    public AutoPlayerState CurrentState => _fsm;
    public AutonomousTelemetry Telemetry { get; } = new();

    // ── Olaylar (FarmEngine deseni; düşük frekans → UI coalescing gerekmez) ──────
    public event EventHandler<string>?         StatusChanged;
    public event EventHandler<AutoPlayerState>? StateChanged;
    /// <summary>Canlı aktivite akışı — Otonom Oyuncu sayfasındaki log için.</summary>
    public event EventHandler<ActivityEntry>?  ActivityLogged;

    // ── Yaşam döngüsü ───────────────────────────────────────────────────────────

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        Telemetry.Reset();
        _killsAtLastCheck    = 0;
        _consecutiveFailures = 0;
        _lastFailedState     = AutoPlayerState.Idle;
        SetState(AutoPlayerState.Farming, "Otonom mod başladı — farm");
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        cts?.Dispose();
        // Otonom mod farm'ı yönetiyor → kapanınca farm'ı da durdur.
        try { if (_farm.IsRunning) _farm.Stop(); } catch { /* yoksay */ }
        SetState(AutoPlayerState.Idle, "Otonom mod durduruldu");
    }

    // ── Ana döngü ────────────────────────────────────────────────────────────────

    private async Task LoopAsync(CancellationToken ct)
    {
        // Farmı bir kez başlat. Önkoşullar (Active + model) VM'de doğrulandı; HP-bar
        // kalibrasyonu eksikse FarmEngine.Start() sessizce Idle kalır → aşağıda yakalanır.
        _farm.Start();
        try { await Task.Delay(200, ct); } catch { return; }

        if (!_farm.IsRunning)
        {
            SetState(AutoPlayerState.Idle, "⚠ Farm başlatılamadı — HP bar kalibrasyonunu kontrol edin");
            _cts = null;
            return;
        }
        Log("Farm başlatıldı", "event");

        while (!ct.IsCancellationRequested)
        {
            // Farm dışarıdan durduysa (F12 kill-switch) otonom modu sonlandır.
            // Town/merchant/portal durumlarında farm kasıtlı durdurulur — sadece Farming'de kontrol et.
            if (_fsm == AutoPlayerState.Farming && !_farm.IsRunning)
            {
                SetState(AutoPlayerState.Idle, "Farm durdu — otonom mod sonlandı");
                _cts = null;
                return;
            }

            try
            {
                await TickAsync(ct);
                await Task.Delay(_state.Autonomous.TickMs, ct);
                // Başarılı adımda art-arda hata sayacını sıfırla
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Telemetry.Failures++;
                _consecutiveFailures++;
                _lastFailedState = _fsm;
                int maxFail = _state.Autonomous.MaxConsecutiveFailures;
                Log($"Hata ({_consecutiveFailures}/{maxFail}) [{_fsm}]: {ex.Message}", "event");

                if (_consecutiveFailures >= maxFail)
                {
                    SetState(AutoPlayerState.Idle,
                        $"Art arda {maxFail} hata — otonom mod durduruldu");
                    _cts = null;
                    return;
                }
                SetState(AutoPlayerState.Recovering,
                    $"Hata ({_consecutiveFailures}/{maxFail}) — kurtarılıyor…");
                try { await Task.Delay(2000, ct); } catch { return; }
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        switch (_fsm)
        {
            case AutoPlayerState.Farming:
                // FarmEngine kendi döngüsünü sürdürür.
                // Faz 33: kill sayacı TelemetryUpdated'den InventoryCheck'e geçiş yapar.
                break;

            case AutoPlayerState.InventoryCheck:
                await InventoryCheckTickAsync(ct);
                break;

            case AutoPlayerState.GoingToTown:
                await GoingToTownAsync(ct);
                break;

            case AutoPlayerState.NavToMerchant:
                await NavToMerchantAsync(ct);
                break;

            case AutoPlayerState.Selling:
                await SellingTickAsync(ct);
                break;

            case AutoPlayerState.Repairing:
                await RepairingTickAsync(ct);
                break;

            case AutoPlayerState.NavToPortal:
                await NavToPortalAsync(ct);
                break;

            case AutoPlayerState.UsingPortal:
                await UsingPortalAsync(ct);
                break;

            case AutoPlayerState.NavToFarmSpot:
                await NavToFarmSpotAsync(ct);
                break;

            case AutoPlayerState.Recovering:
                await RecoveringTickAsync(ct);
                break;
        }
    }

    // ── Manuel tetikleyiciler (test + Faz 33 köprüsü) ──────────────────────────

    /// <summary>
    /// Farm duruyorken manuel olarak town yolculuğunu başlatır.
    /// Faz 33 tamamlandığında bu, kill sayacı tarafından çağrılır.
    /// </summary>
    public void RequestGoToTown()
    {
        if (!IsRunning) return;
        if (_farm.IsRunning) _farm.Stop();
        SetState(AutoPlayerState.GoingToTown, "Town yolculuğu başlatıldı");
    }

    // ── Yardımcılar ──────────────────────────────────────────────────────────────

    private void SetState(AutoPlayerState s, string status)
    {
        _fsm = s;
        StateChanged?.Invoke(this, s);
        StatusChanged?.Invoke(this, status);
        Log(status, "event");
    }

    private void Log(string text, string kind) =>
        ActivityLogged?.Invoke(this, new ActivityEntry(
            DateTime.Now.ToString("HH:mm:ss"), text, kind));
}
