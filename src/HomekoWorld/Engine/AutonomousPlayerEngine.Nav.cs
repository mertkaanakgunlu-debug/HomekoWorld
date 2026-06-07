using HomekoWorld.Models.Autonomous;
using HomekoWorld.Services.Autonomous;

namespace HomekoWorld.Engine;

/// <summary>
/// Faz 34/35 — WorldNavigator entegrasyonu.
/// NavToMerchant / NavToPortal / NavToFarmSpot + takılma kurtarma.
/// </summary>
public sealed partial class AutonomousPlayerEngine
{
    // ── NavToMerchant ─────────────────────────────────────────────────────────────

    private async Task NavToMerchantAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;
        if (s.MerchantX == 0 && s.MerchantY == 0)
        {
            Log("⚠ Merchant koordinatı kalibre edilmedi", "event");
            SetState(AutoPlayerState.Farming, "Merchant konumu eksik — navigasyon atlandı");
            return;
        }

        _navigator.StatusChanged += OnNavStatus;
        try
        {
            Log($"Merchant'a yürünüyor → ({s.MerchantX},{s.MerchantY})", "event");
            await _navigator.NavigateToAsync(s.MerchantX, s.MerchantY, ct);
            Log("Merchant'a ulaşıldı", "event");
            SetState(AutoPlayerState.Selling, "Merchant'ta satış yapılıyor…");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"Merchant nav hatası: {ex.Message}", "event");
            SetState(AutoPlayerState.Recovering, "Navigasyon hatası");
        }
        finally { _navigator.StatusChanged -= OnNavStatus; }
    }

    // ── NavToPortal ───────────────────────────────────────────────────────────────

    private async Task NavToPortalAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;
        if (s.PortalX == 0 && s.PortalY == 0)
        {
            Log("⚠ Portal koordinatı kalibre edilmedi", "event");
            SetState(AutoPlayerState.UsingPortal, "Portal konumu eksik — doğrudan UsingPortal");
            return;
        }

        _navigator.StatusChanged += OnNavStatus;
        try
        {
            Log($"Portala yürünüyor → ({s.PortalX},{s.PortalY})", "event");
            await _navigator.NavigateToAsync(s.PortalX, s.PortalY, ct);
            Log("Portal konumuna ulaşıldı", "event");
            SetState(AutoPlayerState.UsingPortal, "Portal kullanılıyor…");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"Portal nav hatası: {ex.Message}", "event");
            SetState(AutoPlayerState.Recovering, "Navigasyon hatası");
        }
        finally { _navigator.StatusChanged -= OnNavStatus; }
    }

    // ── NavToFarmSpot ─────────────────────────────────────────────────────────────

    private async Task NavToFarmSpotAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;
        if (s.FarmSpotX == 0 && s.FarmSpotY == 0)
        {
            Log("⚠ Farm noktası kalibre edilmedi", "event");
            _farm.Start();
            SetState(AutoPlayerState.Farming, "Farm noktası eksik — mevcut konumda farm başlatıldı");
            return;
        }

        _navigator.StatusChanged += OnNavStatus;
        try
        {
            Log($"Farm noktasına yürünüyor → ({s.FarmSpotX},{s.FarmSpotY})", "event");
            await _navigator.NavigateToAsync(s.FarmSpotX, s.FarmSpotY, ct);
            Log("Farm noktasına ulaşıldı — farm başlatılıyor", "event");
            Telemetry.TripsCompleted++;
            _farm.Start();
            SetState(AutoPlayerState.Farming, $"Farming (tur #{Telemetry.TripsCompleted} tamamlandı)");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"Farm nav hatası: {ex.Message}", "event");
            SetState(AutoPlayerState.Recovering, "Navigasyon hatası");
        }
        finally { _navigator.StatusChanged -= OnNavStatus; }
    }

    // ── Kurtarma (Faz 37: son hatalı duruma göre akıllı geri dönüş) ───────────────

    private async Task RecoveringTickAsync(CancellationToken ct)
    {
        Log($"Kurtarma: son hata [{_lastFailedState}]", "event");
        await Task.Delay(2500, ct);

        // Town zincirinde hata → turu başından yeniden dene (TP → nav → sat → portal → dön)
        if (_lastFailedState is
            AutoPlayerState.NavToMerchant or
            AutoPlayerState.Selling       or
            AutoPlayerState.NavToPortal   or
            AutoPlayerState.UsingPortal)
        {
            Log("Town turunun başından yeniden deneniyor (GoingToTown)", "event");
            SetState(AutoPlayerState.GoingToTown, "Kurtarma → Town TP yeniden deneniyor");
            return;
        }

        // Farm tarafında hata → mevcut konumda farm yeniden başlat
        Log("Kurtarma → farm yeniden başlatılıyor", "event");
        _farm.Start();
        SetState(AutoPlayerState.Farming, "Kurtarmadan sonra farm");
    }

    // ── Yardımcı ─────────────────────────────────────────────────────────────────

    private void OnNavStatus(object? sender, string msg)
        => StatusChanged?.Invoke(this, msg);
}
