using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Models.Farm;

namespace HomekoWorld.Engine;

public sealed partial class FarmEngine
{
    // ── Loot ─────────────────────────────────────────────────────────────────

    private async Task LootAsync(Detection target, FarmSettings s, CancellationToken ct)
    {
        // Ceset ayakta-merkezden ~CorpseFallOffsetPx aşağı düşer (combat'in ceset modeliyle tutarlı); ayrıca
        // angajman boyunca güncellenen _lastEngagedCenter'ı kullan (uzaktan ölen mobta ilk 'target' konumu kaymış olabilir).
        await _router.MoveAbsAsync((int)_lastEngagedCenter.X, (int)(_lastEngagedCenter.Y + CorpseFallOffsetPx), ct);
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
        // 9.tur: 3sn W-yürüyüşü tüm sahneyi kaydırdı — oturma penceresinde hareket kredisi verilmesin
        // (pencere içindeki ilk eşleşme yürüyüş boyunca birikmiş sahte MovedPx'i de siler).
        _tracker.SuppressMotionCredit(NowMs() + CameraFlipSuppressMs);

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
            // BLOKLAMAYAN bekleme: 6s tek Task.Delay yerine küçük adımlarla uyu; spawn olan CANLI aday
            // belirir belirmez hemen çık → ScanningTick ~30ms'de angaje olur. (Eski tek Task.Delay, mob bu
            // beklemenin ortasında doğunca ort. ~3s "spawn'ı geç yakalıyor" gecikmesi üretiyordu.)
            await InterruptibleIdleWaitAsync(s.ScanIdleMs * 3, s, ct);
            return;
        }

        StatusChanged?.Invoke(this, $"Scan adımı {_scanAttempts + 1}/{FullTurnSteps} — kamera 180° çevriliyor…");

        // Orta (scroll) tuş = kamerayı 180° döndürür; ayrı atomic DOWN/UP (ACME kuralı).
        await _router.MouseDownAsync(MouseButton.Middle, ct);
        await _router.MouseUpAsync(MouseButton.Middle, ct);
        // 9.tur: 180° süpürme başlıyor — pencere boyunca izlere hareket kredisi yazılmasın (ceset kamera
        // kaymasından "hareketli=canlı" muafiyeti devralamaz; bkz FarmEngine.CameraFlipSuppressMs).
        _tracker.SuppressMotionCredit(NowMs() + CameraFlipSuppressMs);

        _scanAttempts++;
        // ScanWaitMsBetween: 180° flip sonrası kamera oturması + mob'un görünmesi için bekleme.
        // Kısa taban (kamera oturması) + BLOKLAMAYAN kalan: yeni görünen mob varsa tam süreyi beklemeden çık.
        await Task.Delay(Math.Min(200, s.ScanWaitMsBetween), ct);
        await InterruptibleIdleWaitAsync(Math.Max(0, s.ScanWaitMsBetween - 200), s, ct);
        // #6: idle sayacını SIFIRLA → bir sonraki dönüş ancak YENİ bir tam ScanIdleMs boşluk sonrası olur.
        // Böylece arka-arkaya iki hızlı 180° biter; tespit, yeni görünen mob'u kilitlemeye zaman bulur
        // (candidates>0 olunca ScanningTickAsync hemen angajmana geçer).
        _idleWatch.Restart();
    }

    // ── Yardımcı ──────────────────────────────────────────────────────────────

    /// <summary>Verilen süreyi BLOKLAMADAN bekler: küçük adımlarla uyur, her adımda seçilebilir (canlı, blacklist
    /// dışı, seçili tür) aday belirirse hemen döner. Scan/bekleme sırasında spawn olan mob'a ~120ms'de tepki
    /// verilir (uzun tek Task.Delay yerine); aday görününce ScanningTick bir sonraki tick'te angaje olur.</summary>
    private async Task InterruptibleIdleWaitAsync(int totalMs, FarmSettings s, CancellationToken ct)
    {
        const int stepMs = 120;
        int waited = 0;
        while (waited < totalMs && !ct.IsCancellationRequested)
        {
            if (HasSelectableCandidate(s)) return;   // spawn yakalandı → beklemeyi kes
            int nap = Math.Min(stepMs, totalMs - waited);
            if (nap <= 0) break;
            await Task.Delay(nap, ct);
            waited += nap;
        }
    }

    /// <summary>Şu an EN TAZE tespitte seçilebilir (canlı, süresi geçmemiş blacklist dışı, seçili tür) bir aday
    /// var mı? Bekleme/scan'i erken kesmek için — spawn'a hızlı tepki. ScanningTickAsync'in aday filtresini yansıtır.</summary>
    private bool HasSelectableCandidate(FarmSettings s)
    {
        var snap = _latestDetections;
        if (snap is null) return false;
        var selectedIds = _mobLibrary.GetSelectedIds(s.SelectedMobNames);
        long now = NowMs();
        foreach (var d in snap.Dets)
        {
            if (d.Dead) continue;
            if (d.Guardian) continue;   // 2026-07-03: iz-damgalı guardian — tarama filtresiyle aynı
            if (selectedIds.Count > 0 && !selectedIds.Contains(d.ClassId)) continue;
            if (NearAnyActive(d.Center, _guardianBlacklist, GuardianBlacklistRadiusPx, now)) continue;
            // Hareketli aday muaf (cesetler yürümez) — tarama filtresiyle aynı kural.
            if (NearAnyActive(d.Center, _deadBlacklist, DeadBlacklistRadiusPx, now) &&
                d.MovedPx < MobTracker.MovingAliveExemptPx) continue;
            return true;
        }
        return false;
    }

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
