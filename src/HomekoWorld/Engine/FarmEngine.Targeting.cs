using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services;
using HomekoWorld.Services.Farm;
using HomekoWorld.Services.Vision;

namespace HomekoWorld.Engine;

public sealed partial class FarmEngine
{
    // ── Scanning ──────────────────────────────────────────────────────────────

    private async Task ScanningTickAsync(
        IReadOnlyList<Detection> candidates,
        FarmSettings s,
        CancellationToken ct)
    {
        // Süresi dolan kara liste girdilerini temizle (görünür aday olsun olmasın)
        long now = NowMs();
        _guardianBlacklist.RemoveAll(e => e.expireAt < now);
        _deadBlacklist.RemoveAll(e => e.expireAt < now);

        // Kara listede (koruma mobu veya ölü/seçilemeyen ceset) olan adayları filtrele
        var filteredCandidates = candidates.Where(d =>
            !NearAny(d.Center, _guardianBlacklist, GuardianBlacklistRadiusPx) &&
            !NearAny(d.Center, _deadBlacklist,     DeadBlacklistRadiusPx)).ToList();

        // Görünürde CANLI (blacklist dışı) hedef YOK — ya hiç aday yok ya da hepsi ceset/koruma.
        // F4: her iki durum da idle saatini büyütür → ScanIdleMs sonra kamera-scan, yoksa roam tetiklenir.
        // (Eskiden candidates>0 ama hepsi blacklist iken _idleWatch resetlenip bot boş dönüyordu = stall.)
        if (filteredCandidates.Count == 0)
        {
            await HandleNoLiveTargetAsync(candidates.Count, s, ct);
            return;
        }

        // Canlı hedef bulundu → idle + kamera scan tur sayacını sıfırla
        _idleWatch.Restart();
        _scanAttempts = 0;

        // #1: karakter daima fiziksel ekran merkezi (kalibrasyon kaldırıldı).
        var charCenter = ScreenCenter();

        var target = s.Priority switch
        {
            TargetPriorityMode.HighestPriority =>
                filteredCandidates.OrderBy(d => _mobLibrary.FindById(d.ClassId)?.Priority ?? 99).First(),
            _ =>
                filteredCandidates.OrderBy(d => d.DistanceTo(charCenter)).First(),
        };

        _lastTarget = target;
        var mobInfo = _mobLibrary.FindById(target.ClassId);
        Telemetry.CurrentMob = target.ClassName;

        SetState(FarmState.Targeting, $"Hedef: {target.ClassName}");
        bool targeted = await TargetAsync(target, s, ct);

        if (!targeted)
        {
            SetState(FarmState.Scanning, "Hedef alınamadı — tekrar tarıyor");
            return;
        }

        SetState(FarmState.Engaging, $"Angaje: {target.ClassName}");
        await EngageAsync(target, mobInfo, s, ct);
    }

    // Görünürde canlı (blacklist dışı) hedef yokken ortak davranış: idle büyüdükçe kamera-scan ya da
    // roam tetikle. Hem "hiç aday yok" hem "hepsi ceset/blacklist" (F4) buraya düşer → bot boş dönmez.
    private async Task HandleNoLiveTargetAsync(int visibleCount, FarmSettings s, CancellationToken ct)
    {
        if (s.ScanModeEnabled && _idleWatch.ElapsedMilliseconds > s.ScanIdleMs)
        {
            await CameraScanStepAsync(s, ct);
            return;
        }
        if (_idleWatch.ElapsedMilliseconds > s.IdleBeforeRoamMs && s.RoamWaypoints.Count > 0)
            SetState(FarmState.Roaming, "Spot boş — yürünüyor…");
        else
            StatusChanged?.Invoke(this, $"Taranıyor… ({visibleCount} görünür, canlı hedef yok)");
    }

    // ── Targeting — moba tıkla, HP bar bekle ─────────────────────────────────

    private async Task<bool> TargetAsync(
        Detection target, FarmSettings s, CancellationToken ct)
    {
        bool hpBarCalibrated   = _appState.Wtm.IsHpBarLocated;

        // ── YOLO modu ─────────────────────────────────────────────────────────
        if (Inferrer is not null)
        {
            // ── Tıklama noktası: pose modeli → keypoint; BBox modeli → offset fallback ──
            float firstDx, firstDy;
            if (target.KeyPoint.HasValue)
            {
                // Pose modeli: eğitilmiş isim etiketi koordinatı (offset hesabı yok)
                firstDx = target.ClickPoint.X - target.Center.X;
                firstDy = target.ClickPoint.Y - target.Center.Y;
            }
            else
            {
                // BBox modeli fallback: mob başına ayarlanabilir Y offset
                var   mobInfoForOffset = _mobLibrary.FindById(target.ClassId);
                float offsetPct        = mobInfoForOffset?.ClickOffsetYPct ?? -0.35f;
                firstDx = 0f;
                firstDy = Math.Clamp(target.BBox.Height * offsetPct, -45f, 45f);
            }

            (float dx, float dy)[] yoloOffsets =
            [
                (firstDx, firstDy),  // 1. deneme: keypoint veya offset
                (0f,      0f),       // 2. deneme: merkez
            ];

            for (int i = 0; i < yoloOffsets.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                // KRİTİK: tıklamadan HEMEN ÖNCE hedefin EN TAZE tespit konumunu al. Mob/kamera hareketli
                // olduğundan ScanningTick'te seçilen konum birkaç on-ms'de bayatlar → bayat yere tık = ıska
                // ("kutuya tıkladık, mob orada yok"). Mob bu an TAZE karede YOKSA (flicker/kayıp) hiç tıklama
                // → taramaya dön (asla bayat/hayalet konuma tıklanmaz).
                var clickSnap = _latestDetections;
                var liveTarget = clickSnap?.Dets
                    .Where(d => d.ClassId == target.ClassId && d.DistanceTo(target.Center) < 100f)
                    .OrderBy(d => d.DistanceTo(target.Center))
                    .FirstOrDefault();
                if (liveTarget is null)
                {
                    StatusChanged?.Invoke(this, "Hedef taze karede yok — tıklama atlandı, yeniden tarıyor");
                    return false;
                }
                // Faz B: tıklanacak tespitin yaşı (capture→şimdi) — objektif "bayat kutu" ölçümü.
                if (clickSnap is not null) Telemetry.FrameAgeAtClickMs = (int)(NowMs() - clickSnap.CapturedAtMs);

                // Offset deltası ScanningTick hedefinden; konum TAZE tespitten → güncel yere tıkla.
                int cx = (int)(liveTarget.Center.X + yoloOffsets[i].dx);
                int cy = (int)(liveTarget.Center.Y + yoloOffsets[i].dy);

                // Tıklamadan önce karakter bölgesi örneği (motion detection — küçük bölge yakalama).
                byte[] preCrop = SampleCharRegionDirect();

                await _router.MoveAbsAsync(cx, cy, ct);
                await Task.Delay(s.ClickPreDelayMs, ct);
                await _router.ClickAsync(MouseButton.Left, ct);
                await Task.Delay(s.ClickPostDelayMs, ct);

                if (hpBarCalibrated && await PollHpBarAsync(ct))
                    return await CheckGuardianAndReturnAsync(liveTarget, ct);

                // HP bar yok — karakter hareket ettiyse auto-walk'ı iptal et
                byte[] postCrop = SampleCharRegionDirect();
                if (IsCharacterMoving(preCrop, postCrop))
                    await CancelClickMovementAsync(ct);

                // Mob hâlâ sahnede mi? — tıkladığımız TAZE konuma göre kontrol et.
                var snap = _latestDetections;
                bool mobStillThere = snap is not null && snap.Dets.Any(d =>
                    d.ClassId == target.ClassId &&
                    d.DistanceTo(liveTarget.Center) < 80f);

                if (!mobStillThere)
                {
                    // Tık sonrası tespit o noktada yok → mob kaymış olabilir VEYA tıklanamayan statik
                    // false-positive (log'da aynı (686,546)'ya conf 0.94 ile SONSUZ re-pick görüldü).
                    // Kısa süre blacklist'le → aynı noktaya kilitlenip döngüye girmesin; F4 ile kamerayı
                    // çevirip başka moba geçer. Gerçekten kaydıysa süre kısa, mob yeni konumunda geri alınır.
                    _deadBlacklist.Add((target.Center, NowMs() + MissReselectSkipMs));
                    return false;
                }

                StatusChanged?.Invoke(this,
                    $"Tıklama ıskandı ({(i == 0 ? "isim" : "merkez")}) — tekrar…");
            }

            // Tüm tıklamalar HP bar üretmedi ama mob hâlâ sahnede (yukarıda erken dönülmedi) →
            // büyük olasılıkla ölü ceset / seçilemeyen hedef. Kısa süre kara listeye al ki
            // bir sonraki tarama EN YAKIN DİĞER mob'u seçsin (ölüye tıklama döngüsünü kırar).
            if (hpBarCalibrated)
            {
                // F2: 2 tık da HP üretmedi → KESİN ceset/seçilemez. Normal kill-blacklist'ten UZUN tut (2×)
                // ki uzun süre ekranda kalan ceset (log'da 10sn+) tekrar tekrar probe edilmesin.
                long corpseExpire = NowMs() + s.DeadBlacklistMs * 2;
                _deadBlacklist.Add((target.Center, corpseExpire));
                StatusChanged?.Invoke(this, "Hedef seçilemedi (ölü/ceset?) — kısa süre atlanıyor");
            }
            else
                StatusChanged?.Invoke(this, "⚠ HP bar kalibrasyonu yok — hedef doğrulanamadı");
            return false;
        }

        // ── Kör mod ───────────────────────────────────────────────────────────
        float ox = Math.Clamp(target.BBox.Width  * 0.18f, 10f, 28f);
        float oy = Math.Clamp(target.BBox.Height * 0.18f, 10f, 28f);

        (float dx2, float dy2)[] blindOffsets =
        [
            (   0f, -oy),
            (   0f,   0f),
            (   0f, +oy),
            ( -ox,   0f),
            ( +ox,   0f),
        ];

        for (int i = 0; i < blindOffsets.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            int cx = (int)(target.Center.X + blindOffsets[i].dx2);
            int cy = (int)(target.Center.Y + blindOffsets[i].dy2);

            await _router.MoveAbsAsync(cx, cy, ct);
            await Task.Delay(80, ct);
            await _router.ClickAsync(MouseButton.Left, ct);
            await Task.Delay(200, ct);

            if (hpBarCalibrated && await PollHpBarAsync(ct))
                return await CheckGuardianAndReturnAsync(target, ct);

            if (!hpBarCalibrated)
                return true;

            await CancelClickMovementAsync(ct);
            StatusChanged?.Invoke(this, $"Hedef ıskandı ({i + 1}/5) — tekrar deniyor…");
        }

        string rotKey = _cameraRotateDir > 0 ? "D" : "A";
        _cameraRotateDir = -_cameraRotateDir;
        StatusChanged?.Invoke(this, $"Hedef alınamadı — kamera {rotKey} döndürülüyor");
        await TapKeyAsync(rotKey, 180, ct);
        await Task.Delay(120, ct);
        return false;
    }

    private async Task CancelClickMovementAsync(CancellationToken ct)
    {
        await _router.KeyDownAsync("W", ct);
        await Task.Delay(10, ct);
        await _router.KeyUpAsync("W", CancellationToken.None);
        await Task.Delay(50, ct);
    }

    /// <summary>
    /// HP bar görüldükten sonra nameplate renk kontrolü yapar.
    /// Koruma mobu ise W tap ile hedefi bırak, kara listeye ekle, false döner.
    /// Aksi hâlde true döner.
    /// </summary>
    private async Task<bool> CheckGuardianAndReturnAsync(Detection target, CancellationToken ct)
    {
        if (_appState.Wtm.GuardianDetectionEnabled &&
            (_appState.Wtm.IsNameBandCalibrated || _appState.Wtm.IsTargetHpColorCalibrated))
        {
            var nameClass = WtmVision.ReadNameplateClass(_appState.Wtm);
            if (nameClass == WtmVision.NameplateClass.Guardian)
            {
                StatusChanged?.Invoke(this, "Koruma mobu tespit edildi — atlanıyor");
                await CancelClickMovementAsync(ct);
                long expireAt = System.Diagnostics.Stopwatch.GetTimestamp()
                               / (System.Diagnostics.Stopwatch.Frequency / 1000)
                               + GuardianBlacklistDurationMs;
                _guardianBlacklist.Add((target.Center, expireAt));
                return false;
            }
        }
        return true;
    }

    // ── HP bar varlık kontrolü (tıklama sonrası) ─────────────────────────────
    // Temporal buffer'ı sıfırlar, 6 frame poll eder (50ms aralık).
    // Bar seçimden hemen sonra geldiğinde smoothed=True için 4 True frame gerekir.
    private async Task<bool> PollHpBarAsync(CancellationToken ct)
    {
        // Agresif: HSV ham sonuç → ilk pozitifte hemen dön (tıklama sonrası bar anında yakalanır).
        // ~6×25ms = ~150ms bütçe (eski 6×50ms=300ms'nin yarısı).
        for (int i = 0; i < 6; i++)
        {
            if (WtmVision.IsTargetAliveSmoothed(_appState.Wtm))
                return true;
            await Task.Delay(25, ct);
        }
        return false;
    }

    /// <summary>
    /// Karakter merkezi etrafında küçük bir bölgeyi DOĞRUDAN yakalar (tam ekran değil) ve
    /// BGR örnek döndürür. Tıklama öncesi/sonrası hareket karşılaştırması için — Sorun 1
    /// kapsamında tam ekran yakalama yerine kullanılır (daha hızlı tepki).
    /// </summary>
    private static byte[] SampleCharRegionDirect(int radius = 12)
    {
        // #1: karakter daima fiziksel ekran merkezi (kalibrasyon kaldırıldı).
        var c = ScreenCenter();
        int cx = (int)c.X, cy = (int)c.Y;

        int x0 = Math.Max(0, cx - radius);
        int y0 = Math.Max(0, cy - radius);
        int w  = radius * 2 + 1;
        int h  = radius * 2 + 1;

        using var bmp = WtmVision.CaptureRegion(x0, y0, w, h);
        var rect   = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data   = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = Math.Abs(data.Stride);
        var pixels = new byte[stride * bmp.Height];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        bmp.UnlockBits(data);

        var crop = new byte[bmp.Width * bmp.Height * 3];
        int idx  = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < bmp.Width; x++)
            {
                int pos = row + x * 4; // BGRA
                crop[idx++] = pixels[pos];
                crop[idx++] = pixels[pos + 1];
                crop[idx++] = pixels[pos + 2];
            }
        }
        return crop;
    }

    /// <summary>
    /// İki crop arasındaki SAD farkı piksel başına >5% ise karakter hareket ediyordur.
    /// </summary>
    private static bool IsCharacterMoving(byte[] a, byte[] b, double threshold = 0.05)
    {
        if (a.Length == 0 || a.Length != b.Length) return false;

        long sad = 0;
        for (int i = 0; i < a.Length; i++)
            sad += Math.Abs(a[i] - b[i]);

        double meanDiff = (double)sad / a.Length; // 0–255
        return meanDiff / 255.0 > threshold;
    }
}
