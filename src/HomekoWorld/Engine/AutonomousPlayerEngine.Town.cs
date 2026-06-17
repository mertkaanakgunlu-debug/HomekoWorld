using System.Runtime.InteropServices;
using HomekoWorld.Hardware;
using HomekoWorld.Models.Autonomous;
using HomekoWorld.Services.Capture;

namespace HomekoWorld.Engine;

/// <summary>
/// Faz 35/40 — Town TP, satış, tamir, portal etkileşimi.
/// GoingToTownAsync: Town TP butonu/tuşu → yükleme bekle → NavToMerchant.
/// SellingTickAsync: MerchantTrader.TradeAsync → (RepairEnabled ? Repairing : NavToPortal).
/// RepairingTickAsync: MerchantTrader.RepairAsync (seçili ekipman slotlarına tık) → NavToPortal.
/// UsingPortalAsync: portal konumunda sol-tık → onay tuşu → yükleme bekle → NavToFarmSpot.
/// </summary>
public sealed partial class AutonomousPlayerEngine
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // ── Town TP ───────────────────────────────────────────────────────────────────

    private async Task GoingToTownAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;

        if (!await PressTownTpAsync(ct))
        {
            Log("⚠ Town butonu/tuşu ayarlanmamış", "event");
            SetState(AutoPlayerState.Farming, "Town TP eksik — farm'a dönüldü");
            _farm.Start();
            return;
        }

        StatusChanged?.Invoke(this, $"Town yükleniyor ({s.TownTpWaitMs / 1000} sn)…");
        await Task.Delay(s.TownTpWaitMs, ct);

        Log("Town'a gelindi — merchant'a yürünüyor", "event");
        SetState(AutoPlayerState.NavToMerchant, "Merchant'a yürünüyor…");
    }

    /// <summary>
    /// Town TP aksiyonu: UI butonu kalibre ise ona sol-tık, değilse <see cref="AutonomousSettings.TownTpKey"/>.
    /// Hiçbiri ayarlı değilse false (çağıran karar verir). GoingToTown + bağımsız test bunu paylaşır.
    /// </summary>
    private async Task<bool> PressTownTpAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;
        // Bu oyunda Town TP = sabit UI butonu → kalibre edilmişse ona sol-tık; değilse kısayol fallback.
        if (s.IsTownTpButtonCalibrated)
        {
            var p = ResolutionMapper.Map(s.TownTpButtonX, s.TownTpButtonY, 1, 1);
            Log($"Town butonu tıklanıyor ({p.X},{p.Y})", "event");
            await _transport.MoveAbsAsync(p.X, p.Y, ct);
            await Task.Delay(120, ct);
            await _transport.ClickAsync(MouseButton.Left, ct);
            return true;
        }
        if (!string.IsNullOrWhiteSpace(s.TownTpKey))
        {
            Log($"Town TP tuşu: '{s.TownTpKey}' basılıyor (buton kalibre değil)", "event");
            await _transport.KeyDownAsync(s.TownTpKey, ct);
            await Task.Delay(80, ct);
            await _transport.KeyUpAsync(s.TownTpKey, CancellationToken.None);
            return true;
        }
        return false;
    }

    /// <summary>Test: Otonom modu/loop BAŞLATMADAN yalnız Town TP aksiyonunu yapar (kalibrasyon doğrulama).</summary>
    public async Task TestTownTpAsync(CancellationToken ct)
    {
        if (!await PressTownTpAsync(ct))
            throw new InvalidOperationException("Town butonu/tuşu ayarlanmamış");
    }

    // ── Satış (Faz 36: MerchantTrader) ──────────────────────────────────────────

    private async Task SellingTickAsync(CancellationToken ct)
    {
        Log("Merchant ile etkileşime giriliyor…", "event");
        _merchantTrader.StatusChanged += OnMerchantStatus;
        try
        {
            int sold = await _merchantTrader.TradeAsync(ct);
            if (sold > 0) Telemetry.ItemsSold += sold;
            string soldStr = sold < 0 ? "tümü satıldı" : $"{sold} yuva satıldı";
            Log($"Satış tamamlandı ({soldStr}, toplam: {Telemetry.ItemsSold}) — portala yürünüyor", "event");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"Satış hatası: {ex.Message} — portala yine de yürünüyor", "event");
        }
        finally { _merchantTrader.StatusChanged -= OnMerchantStatus; }

        if (_state.Autonomous.RepairEnabled)
            SetState(AutoPlayerState.Repairing, "Eşyalar tamir ediliyor…");
        else
            SetState(AutoPlayerState.NavToPortal, "Portala yürünüyor…");
    }

    private void OnMerchantStatus(object? sender, string msg)
    {
        StatusChanged?.Invoke(this, msg);
        Log(msg, "event");
    }

    // ── Tamir (Faz 40: MerchantTrader.RepairAsync) ───────────────────────────────

    private async Task RepairingTickAsync(CancellationToken ct)
    {
        Log("Tamir için merchant ile etkileşime giriliyor…", "event");
        _merchantTrader.StatusChanged += OnMerchantStatus;
        try
        {
            int repaired = await _merchantTrader.RepairAsync(ct);
            if (repaired > 0) Telemetry.RepairsDone++;
            Log(repaired > 0
                ? $"Tamir tamamlandı ({repaired} ekipman, tur: {Telemetry.RepairsDone}) — portala yürünüyor"
                : "Tamir atlandı (kalibrasyon/slot eksik) — portala yürünüyor", "event");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"Tamir hatası: {ex.Message} — portala yine de yürünüyor", "event");
        }
        finally { _merchantTrader.StatusChanged -= OnMerchantStatus; }

        SetState(AutoPlayerState.NavToPortal, "Portala yürünüyor…");
    }

    // ── Portal etkileşimi ─────────────────────────────────────────────────────────

    /// <summary>
    /// Portal etkileşimini yapar → yükleme bekle → NavToFarmSpot.
    /// </summary>
    private async Task UsingPortalAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;

        await DoPortalInteractAsync(s, ct);

        StatusChanged?.Invoke(this, $"Portal yükleniyor ({s.PortalWaitMs / 1000} sn)…");
        await Task.Delay(s.PortalWaitMs, ct);

        Log("Farm alanına gelindi — farm noktasına yürünüyor", "event");
        SetState(AutoPlayerState.NavToFarmSpot, "Farm noktasına yürünüyor…");
    }

    /// <summary>
    /// Portal oyun mekaniği (merchant NPC ile aynı tık modeli): portal ekran-konumuna
    /// sol-tık (seç) → sağ-tık (menü) → açılan menüde kalibre edilmiş "manuel slot"
    /// öğesine sol-tık → anında ışınlanır (onay penceresi yok).
    /// "manuel slot" kalibre değilse eski onay-tuşu (Enter / <see cref="AutonomousSettings.PortalConfirmKey"/>)
    /// davranışına düşülür. Otonom akış + bağımsız test bunu paylaşır.
    /// </summary>
    private async Task DoPortalInteractAsync(AutonomousSettings s, CancellationToken ct)
    {
        // WorldNavigator portal koordinatına geldi; portal kapısı ekranın önünde,
        // yaklaşık merkez + Y-offset (kapı tepesi biraz yukarıda).
        int screenW = GetSystemMetrics(0);
        int screenH = GetSystemMetrics(1);
        int cx = screenW / 2 + s.PortalClickOffsetX;
        int cy = screenH / 2 + s.PortalClickOffsetY;

        // 1-2. Portala NPC gibi tıkla: sol-tık (seç) → sağ-tık (menü aç)
        Log($"Portala tıklanıyor ({cx},{cy})", "event");
        await _transport.MoveAbsAsync(cx, cy, ct);
        await Task.Delay(120, ct);
        await _transport.ClickAsync(MouseButton.Left, ct);
        await Task.Delay(150, ct);
        await _transport.ClickAsync(MouseButton.Right, ct);
        await Task.Delay(s.PortalInteractDelayMs, ct); // menü belirmesi için bekle

        // 3. Açılan menüde "manuel slot" öğesi → anında ışınlanır
        if (s.IsPortalMenuSlotCalibrated)
        {
            var p = ResolutionMapper.Map(s.PortalMenuSlotX, s.PortalMenuSlotY, 1, 1);
            Log($"'Manuel slot' seçiliyor ({p.X},{p.Y})", "event");
            await _transport.MoveAbsAsync(p.X, p.Y, ct);
            await Task.Delay(120, ct);
            await _transport.ClickAsync(MouseButton.Left, ct);
        }
        else
        {
            string confirmKey = string.IsNullOrWhiteSpace(s.PortalConfirmKey) ? "Return" : s.PortalConfirmKey;
            Log($"⚠ 'Manuel slot' kalibre değil — onay tuşu '{confirmKey}' (eski davranış)", "event");
            await _transport.KeyDownAsync(confirmKey, ct);
            await Task.Delay(80, ct);
            await _transport.KeyUpAsync(confirmKey, CancellationToken.None);
        }
    }

    /// <summary>Test: Otonom akış/loop BAŞLATMADAN yalnız portal etkileşimini yapar (kalibrasyon doğrulama).</summary>
    public Task TestUsePortalAsync(CancellationToken ct) => DoPortalInteractAsync(_state.Autonomous, ct);
}
