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

        // Bu oyunda Town TP = sabit UI butonu → kalibre edilmişse ona sol-tık.
        // Değilse eski kısayol (TownTpKey) fallback; o da yoksa farm'a dön.
        if (s.IsTownTpButtonCalibrated)
        {
            var p = ResolutionMapper.Map(s.TownTpButtonX, s.TownTpButtonY, 1, 1);
            Log($"Town butonu tıklanıyor ({p.X},{p.Y})", "event");
            await _transport.MoveAbsAsync(p.X, p.Y, ct);
            await Task.Delay(120, ct);
            await _transport.ClickAsync(MouseButton.Left, ct);
        }
        else if (!string.IsNullOrWhiteSpace(s.TownTpKey))
        {
            Log($"Town TP tuşu: '{s.TownTpKey}' basılıyor (buton kalibre değil)", "event");
            await _transport.KeyDownAsync(s.TownTpKey, ct);
            await Task.Delay(80, ct);
            await _transport.KeyUpAsync(s.TownTpKey, CancellationToken.None);
        }
        else
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
    /// Portal konumunda: ekran merkezi + offset'e tıkla → PortalInteractDelayMs bekle
    /// → onay tuşu (Enter / PortalConfirmKey) bas → yükleme bekle → NavToFarmSpot.
    /// </summary>
    private async Task UsingPortalAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;

        // WorldNavigator portal koordinatına geldi; portal kapısı ekranın önünde,
        // yaklaşık merkez + Y-offset (kapı tepesi biraz yukarıda).
        int screenW = GetSystemMetrics(0);
        int screenH = GetSystemMetrics(1);
        int cx = screenW / 2 + s.PortalClickOffsetX;
        int cy = screenH / 2 + s.PortalClickOffsetY;

        Log($"Portal tıklanıyor ({cx},{cy})", "event");
        await _transport.MoveAbsAsync(cx, cy, ct);
        await Task.Delay(120, ct);
        await _transport.ClickAsync(MouseButton.Left, ct);

        await Task.Delay(s.PortalInteractDelayMs, ct); // onay diyaloğu belirmesi için bekle

        string confirmKey = string.IsNullOrWhiteSpace(s.PortalConfirmKey)
            ? "Return" : s.PortalConfirmKey;
        Log($"Portal onaylandı: '{confirmKey}'", "event");
        await _transport.KeyDownAsync(confirmKey, ct);
        await Task.Delay(80, ct);
        await _transport.KeyUpAsync(confirmKey, CancellationToken.None);

        StatusChanged?.Invoke(this, $"Portal yükleniyor ({s.PortalWaitMs / 1000} sn)…");
        await Task.Delay(s.PortalWaitMs, ct);

        Log("Farm alanına gelindi — farm noktasına yürünüyor", "event");
        SetState(AutoPlayerState.NavToFarmSpot, "Farm noktasına yürünüyor…");
    }
}
