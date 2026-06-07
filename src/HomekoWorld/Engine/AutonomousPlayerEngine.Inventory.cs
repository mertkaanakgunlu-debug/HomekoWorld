using HomekoWorld.Models.Autonomous;
using HomekoWorld.Models.Farm;

namespace HomekoWorld.Engine;

/// <summary>
/// Faz 33 — Envanter doluluğu kontrolü.
/// FarmEngine.TelemetryUpdated ile kill sayısını izler; her N kill'de
/// InventoryReader ile envanter tarar. Dolu ise GoingToTown'a geçiş yapar.
/// </summary>
public sealed partial class AutonomousPlayerEngine
{
    private int _killsAtLastCheck;

    private void OnFarmTelemetryUpdated(object? sender, FarmTelemetry _)
    {
        if (_fsm != AutoPlayerState.Farming) return;

        int kills     = _farm.Telemetry.Kills;
        int threshold = _state.Autonomous.InventoryCheckEveryKills;
        if (threshold <= 0 || kills - _killsAtLastCheck < threshold) return;

        _killsAtLastCheck = kills;
        Log($"Kill eşiği ({threshold}) aşıldı — envanter kontrol ediliyor", "event");
        SetState(AutoPlayerState.InventoryCheck, "Envanter kontrol ediliyor…");
    }

    private async Task InventoryCheckTickAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;

        if (!_inventoryReader.IsReady)
        {
            Log("⚠ Envanter ROI kalibre edilmedi — farm devam ediyor", "event");
            SetState(AutoPlayerState.Farming, "Envanter kalibrasyonu eksik");
            return;
        }

        float ratio;
        try
        {
            ratio = await _inventoryReader.CheckAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"Envanter kontrol hatası: {ex.Message}", "event");
            SetState(AutoPlayerState.Farming, "Envanter kontrol hatası — farm devam ediyor");
            return;
        }

        int pct = (int)(ratio * 100);
        Log($"Envanter doluluğu: %{pct}", "event");

        if (ratio >= s.InventoryFullThreshold)
        {
            Log($"Envanter dolu (%{pct} ≥ %{(int)(s.InventoryFullThreshold * 100)}) — town'a gidiliyor", "event");
            if (_farm.IsRunning) _farm.Stop();
            SetState(AutoPlayerState.GoingToTown, $"Envanter dolu (%{pct})");
        }
        else
        {
            SetState(AutoPlayerState.Farming, $"Envanter boş (%{pct}) — farm devam ediyor");
        }
    }
}
