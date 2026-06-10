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

public sealed partial class FarmEngine
{
    // ── Ana döngü ─────────────────────────────────────────────────────────────

    private async Task FarmLoopAsync(CancellationToken ct)
    {
        // ── Kalibrasyon uyarısı ────────────────────────────────────────────────
        if (!_appState.Wtm.IsTargetHpColorCalibrated)
            StatusChanged?.Invoke(this,
                "⚠ Hedef HP bar kalibre edilmemiş — Geliştirilmiş Ayarlar > Hedef HP Bar'ı Kalibre Et");

        while (!ct.IsCancellationRequested)
        {
            var s = _appState.Farm;   // Her iterasyonda taze al — ayar değişiklikleri anında yansır
            try
            {
                await TickAsync(s, ct);
                await Task.Delay(30, ct);   // 50ms → 30ms: ~33 FPS tarama
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState(FarmState.Scanning, $"Hata: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task TickAsync(FarmSettings s, CancellationToken ct)
    {
        if (Inferrer is null)
        {
            SetState(FarmState.Idle, "⚠ Model yüklü değil — .onnx seç");
            await Task.Delay(2000, ct);
            return;
        }

        if (_paused)
        {
            await Task.Delay(80, ct);
            return;
        }

        // Tespitler ayrı thread'den (DetectionLoop) gelir — burada inference YOK.
        var snap = _latestDetections;
        if (snap is null)
        {
            await Task.Delay(15, ct); // ilk tespit henüz hazır değil
            return;
        }

        var dets = snap.Dets;

        // Çoklu mob seçimi: SelectedMobNames içindeki sınıfları filtrele; boşsa tümü.
        var selectedIds = _mobLibrary.GetSelectedIds(s.SelectedMobNames);
        var candidates = selectedIds.Count == 0
            ? dets
            : dets.Where(d => selectedIds.Contains(d.ClassId)).ToList();

        // (Overlay yayını DetectionLoop'tan yapılır — burada tekrar yayınlanmaz.)
        // Pot: tek otorite global AutoPotService (ayrı thread, combo'yu/akışı KESMEZ). Farm-içi pot
        // KALDIRILDI — eski hâli combo'yu CancelAll edip state'i Scanning'e zorluyordu (footgun + çift-pot).

        switch (_state)
        {
            case FarmState.Scanning:
                await ScanningTickAsync(candidates, s, ct);
                break;
            case FarmState.Roaming:
                await RoamingTickAsync(s, ct);
                break;
        }
    }

    private void SetState(FarmState state, string status)
    {
        _state = state;
        StatusChanged?.Invoke(this, status);
        EmitTelemetry(); // CurrentMob/sayaçlar HUD'da canlı kalsın
    }

    private void EmitTelemetry()
        => TelemetryUpdated?.Invoke(this, Telemetry);
}
