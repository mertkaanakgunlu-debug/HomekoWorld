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

        // Dev 2 — ev koordinatını KARARLI oku (iki uyuşan okuma): tek okumadaki hane-hatası
        // evi bozup botu yanlış yere yürütemez. Okunamazsa dönüş bu oturumda devre dışı.
        if (_homeReadPending)
        {
            _homeReadPending = false;
            try { _homeCoord = await ReadHomeStableAsync(ct); }
            catch (OperationCanceledException) { return; }
            Program.Log(_homeCoord is null
                ? "[Farm] ⚠ başlangıç farm koordinatı okunamadı — otomatik dönüş bu oturumda devre dışı"
                : $"[Farm] başlangıç farm koordinatı: ({_homeCoord.Value.x},{_homeCoord.Value.y})");
        }

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
                // 2026-07-04 (7.tur-b): GÖRÜNMEZ-HATA deliği kapatıldı — bu catch eskiden yalnız HUD status'a
                // yazıyordu (dosya loguna HİÇ düşmüyordu); tick zincirindeki (loot/engage/scan) tekrarlayan bir
                // exception log-adli-analizinde tamamen görünmezdi. Artık dosyaya tam iz yazılır.
                Program.Log($"[Farm] TICK HATASI: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                SetState(FarmState.Scanning, $"Hata: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>Dev 2 — ev koordinatını KARARLI okur: en çok 6 denemede, ±3 birim uyuşan İKİ okuma ister.
    /// V2 okuyucu şüpheli okumada zaten null döner; buradaki çifte-teyit kalan tekil yanlış-okumayı da eler.
    /// Bulunamazsa null (dönüş özelliği bu oturumda devre dışı kalır).</summary>
    private async Task<(int x, int y)?> ReadHomeStableAsync(CancellationToken ct)
    {
        (int x, int y)? prev = null;
        for (int i = 0; i < 6 && !ct.IsCancellationRequested; i++)
        {
            var r = CoordReader?.Read();
            if (r is not null && prev is not null &&
                Math.Abs(r.Value.x - prev.Value.x) <= 3 && Math.Abs(r.Value.y - prev.Value.y) <= 3)
                return r;
            if (r is not null) prev = r;
            await Task.Delay(150, ct);
        }
        return null;
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

        // Dev 3 — envanter dolu tetiği (kill eşiği): banka boşaltmayı combat'tan AYRI, tarama tick'inde çalıştır.
        // Combat sırasında EngageAsync _bankPending'i kurar; burada duraklat → boşalt → tarama'ya dön.
        if (_bankPending && Bank is not null)
        {
            _bankPending = false;
            _bankRanSinceEngage = true;   // hedef-geçiş metriği: bu geçiş banka içeriyor → medyana katma
            Telemetry.BankRuns++;
            SetState(FarmState.Banking, "Envanter kontrol — klan bankası…");
            _combo.CancelAll();
            try
            {
                var msg = await Bank.RunAsync(ct);
                Telemetry.BankItemsMoved += Bank.LastMovedCount;
                StatusChanged?.Invoke(this, $"Banka: {msg}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log($"[Farm] banka hatası: {ex.Message}"); }
            SetState(FarmState.Scanning, "Taranıyor…");
            _idleWatch.Restart();
            _noCombatWatch.Restart();
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
