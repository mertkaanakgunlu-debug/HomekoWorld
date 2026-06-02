using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Models.Farm;

namespace HomekoWorld.Engine;

public sealed partial class FarmEngine
{
    // ── Loot ─────────────────────────────────────────────────────────────────

    private async Task LootAsync(Detection target, FarmSettings s, CancellationToken ct)
    {
        await _router.MoveAbsAsync((int)target.Center.X, (int)target.Center.Y, ct);
        await Task.Delay(150, ct);

        for (int i = 0; i < s.LootTapsCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            await TapKeyAsync(s.LootKey, 80, ct);
            await Task.Delay(s.LootTapDelayMs, ct);
        }
    }

    // ── Roaming ───────────────────────────────────────────────────────────────

    private async Task RoamingTickAsync(FarmSettings s, CancellationToken ct)
    {
        if (s.RoamWaypoints.Count == 0)
        {
            SetState(FarmState.Scanning, "Taranıyor…");
            return;
        }

        var wp = s.RoamWaypoints[_wayIndex % s.RoamWaypoints.Count];
        StatusChanged?.Invoke(this, $"Waypoint {_wayIndex + 1}/{s.RoamWaypoints.Count}'e yürünüyor…");

        await _router.KeyDownAsync("W", ct);
        await Task.Delay(3000, ct);
        await _router.KeyUpAsync("W", CancellationToken.None);

        _wayIndex = (_wayIndex + 1) % s.RoamWaypoints.Count;
        SetState(FarmState.Scanning, "Taranıyor…");
        _idleWatch.Restart();
    }

    // ── Scan mod — orta-tuş 180° kamera çevirme ──────────────────────────────

    private async Task CameraScanStepAsync(FarmSettings s, CancellationToken ct)
    {
        // Orta (scroll) tuş kamerayı 180° çevirdiği için 2 adım = tam 360° tarama.
        // (Persist edilmiş eski ScanMaxAttempts=6 değerine bağımlı kalmamak için sabit.)
        const int FullTurnSteps = 2;

        if (_scanAttempts >= FullTurnSteps)
        {
            // Tam tur tamamlandı — uzun bekleme + sıfırla
            _scanAttempts = 0;
            StatusChanged?.Invoke(this, "Scan tamamlandı — bekleniyor…");
            await Task.Delay(s.ScanIdleMs * 3, ct);
            return;
        }

        StatusChanged?.Invoke(this, $"Scan adımı {_scanAttempts + 1}/{FullTurnSteps} — kamera 180° çevriliyor…");

        // Orta (scroll) tuş = kamerayı 180° döndürür; ayrı atomic DOWN/UP (ACME kuralı).
        await _router.MouseDownAsync(MouseButton.Middle, ct);
        await _router.MouseUpAsync(MouseButton.Middle, ct);

        _scanAttempts++;
        // ScanWaitMsBetween: 180° flip sonrası kamera oturması + mob'un görünmesi için bekleme.
        await Task.Delay(s.ScanWaitMsBetween, ct);
        // #6: idle sayacını SIFIRLA → bir sonraki dönüş ancak YENİ bir tam ScanIdleMs boşluk sonrası olur.
        // Böylece arka-arkaya iki hızlı 180° biter; tespit, yeni görünen mob'u kilitlemeye zaman bulur
        // (candidates>0 olunca ScanningTickAsync hemen angajmana geçer).
        _idleWatch.Restart();
    }

    // ── Yardımcı ──────────────────────────────────────────────────────────────

    private async Task TapKeyAsync(string key, int ms, CancellationToken ct)
    {
        Telemetry.LastKeyTapped = key;
        EmitTelemetry();
        KeyLogged?.Invoke(this, new Models.Farm.ActivityEntry(
            DateTime.Now.ToString("HH:mm:ss"), key, "key"));
        await _router.KeyDownAsync(key, ct);
        await Task.Delay(ms, ct);
        await _router.KeyUpAsync(key, CancellationToken.None);
    }
}
