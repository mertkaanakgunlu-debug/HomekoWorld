using System.Drawing;
using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services;
using HomekoWorld.Services.Vision;

namespace HomekoWorld.Engine;

public sealed partial class FarmEngine
{
    // ── Engaging — moba yürü + takip + kombo ─────────────────────────────────

    private async Task EngageAsync(
        Detection target, MobInfo? mobInfo, FarmSettings s, CancellationToken ct)
    {
        _combo.CancelAll();

        // Archer: ayrı, UI'dan ayarlanabilir yaklaşma mesafesi (per-mob'u geçersiz kılar).
        // Archer dışı: rangePx kullanılmaz (kombo hemen ateşlenir) ama tutarlılık için doldur.
        int    rangePx  = s.EngageMovement == EngageMovement.ArcherWalkAndFace
            ? s.ArcherApproachRangePx
            : (mobInfo?.EngagementRangePx ?? s.DefaultEngagementRangePx);
        string comboId  = _appState.Farm.SelectedComboId;
        Combo? combo    = string.IsNullOrEmpty(comboId)
            ? null
            : _appState.Combos.FirstOrDefault(c => c.Id == comboId);

        Program.Log($"[Farm] Sabit kombo: {comboId}");

        bool died;
        try
        {
            do
            {
                died = await TrackAndCombatAsync(target, combo, rangePx, s, ct);
                // Timeout ama HP bar hâlâ görünüyorsa hedef seçili — tıklamadan yeniden engage.
                // HP bar yoksa veya CT iptal olduysa döngüden çık.
            }
            while (!died && !ct.IsCancellationRequested &&
                   WtmVision.IsTargetAliveSmoothed(_appState.Wtm, HpClassifier)); // renk tarama veya template — kalibre ise kontrol et
        }
        finally
        {
            // Angajman bitti (mob öldü / hedef kayıp / iptal) — loop kombo dahil her şeyi durdur.
            _combo.CancelAll();
            _currentTargetForOverlay = null; // scanning'e dönerken vurgulu hedef kalmasın
        }

        if (!died)
        {
            SetState(FarmState.Scanning, "Hedef kaybedildi");
            return;
        }

        Telemetry.Kills++;
        EmitTelemetry();
        // Ölü mob kara listesi: cesedi (son bilinen konum) kısa süre atla → sonraki tarama
        // en yakın DİĞER mob'a geçer, ölüye tekrar tıklamaz (Sorun 5).
        long corpseExpire = NowMs() + s.DeadBlacklistMs;
        _deadBlacklist.Add((_lastEngagedCenter, corpseExpire));
        // F2: ceset ayakta-merkezden aşağı düşer → düşen kutuyu da kapsa (ayrı entry).
        _deadBlacklist.Add((new PointF(_lastEngagedCenter.X, _lastEngagedCenter.Y + CorpseFallOffsetPx), corpseExpire));
        if (s.LootEnabled)
        {
            SetState(FarmState.Looting, "Loot toplanıyor…");
            await LootAsync(target, s, ct);
        }
        SetState(FarmState.Scanning, "Taranıyor…");
        _idleWatch.Restart();
    }

    // ── Takip + kombat döngüsü ────────────────────────────────────────────────
    // Tek döngüde: mesafe → W bas/bırak → R tap → kombo → ölüm tespiti.
    // Döner: true = mob öldü; false = timeout / CT iptal / hedef kayıp.

    private async Task<bool> TrackAndCombatAsync(
        Detection target, Combo? combo, int rangePx, FarmSettings s, CancellationToken ct)
    {
        bool hpBarCalibrated   = _appState.Wtm.IsHpBarLocated;
        // #1: karakter daima fiziksel ekran merkezi (kalibrasyon kaldırıldı).
        var charCenter = ScreenCenter();
        var  sw              = System.Diagnostics.Stopwatch.StartNew();
        const int safetyMs   = 120_000; // 2 dakika; boss/tank moblar için yeterli
        const string walkKey = "W";
        const string faceKey = "R";

        // ── B1: hareket modu ───────────────────────────────────────────────────
        // Archer dışı sınıflar kombo başlayınca oyunun kendi pathing'iyle hedefe gidip vurur →
        // engageStarted baştan true (W/R yok, kombo hemen). Archer ise B2 yaklaşmasını yapar.
        bool archerApproach = s.EngageMovement == EngageMovement.ArcherWalkAndFace;

        Detection currentTarget = target;
        _lastEngagedCenter = target.Center; // ilk tick'te HP ile ölüm tespit edilse de geçerli konum
        bool walkHeld     = false;
        bool comboFiring  = false;
        bool engageStarted = !archerApproach; // archer dışı: yaklaşma yok → hemen kombo
        // Ölüm tespiti ZAMAN-tabanlı: HP barı (HSV ROI) bu kadar ms KESİNTİSİZ "kırmızı yok" okursa öldü.
        // Tick'ten BAĞIMSIZ (WtmTick 15/50 fark etmez). Tek titrek HSV karesinde yanlış-ölüm → canlı mob'u
        // bırakma (#2) engeller. YALNIZ ölüm anında kill başına bir kez ödenir — combat/acquisition'ı
        // yavaşlatmaz. A6: ayardan (FarmSettings.HpDeathConfirmMs, varsayılan ~60ms ≈ 2 teyit; titrek
        // HSV'de 100-150 daha güvenli). Min 20ms tabanına kıstırılır.
        int hpDeathConfirmMs = Math.Max(20, s.HpDeathConfirmMs);
        long firstHpMissMs = -1;     // HP'nin ilk "yok" okunduğu an (-1 = canlı)
        long lastHpCheck   = -1000;  // HSV/HP ekran yakalamasını ≤~33ms'de bire sınırla (GDI yükü)
        // Alive-gate (#1): bu angajmanda HP barını en az bir kez "canlı" gördük mü?
        // Görülmeden ölüm bildirme → angajman başında HP barının henüz yüklenmediği kısa pencerede
        // yanlış-ölüm üretmez (mob sadece seçildi, bar çıkmadı → hemen "öldü" deme).
        bool hadHpOnce = false;

        // NOT: YOLO ARTIK ölüm sinyali DEĞİL (eski missingYoloFrom/graceMs kaldırıldı). Ölüm yalnız HP
        // barından gelir; YOLO kaybı angajmanı bitirmez → flicker false "öldü" üretmez (#3/#5).

        // ── B2v2: archer AKICI yaklaşma (W sürekli basılı; yakında yönelme YOK → orbit yok) ──
        long approachStartMs = -1;   // ilk W basıldığı an (güvenlik zaman aşımı için)
        long lastFaceTap     = -100000; // ilk tick'te hemen yönelsin
        int  inRangeStreak   = 0;    // ardışık "menzilde" okuma (jitter'a karşı küçük histerezis)
        const int InRangeStreakNeeded = 2;

        // ComboFired event → comboFiring = false
        void OnComboFired(object? _, ComboFiredEventArgs e) { comboFiring = false; }
        _combo.ComboFired += OnComboFired;

        try
        {
            while (!ct.IsCancellationRequested && sw.ElapsedMilliseconds < safetyMs)
            {
                // ── Duraklat: W bırak, kombo durdur, yerinde bekle ───────────
                if (_paused)
                {
                    if (walkHeld)
                    {
                        await _router.KeyUpAsync(walkKey, CancellationToken.None);
                        walkHeld = false;
                    }
                    _combo.CancelAll();
                    comboFiring = false;
                    while (_paused && !ct.IsCancellationRequested)
                        await Task.Delay(120, ct);
                    if (ct.IsCancellationRequested) break;
                }

                // ── HP bar ölüm kontrolü (kendi küçük ROI yakalamasını yapar) ──
                // Tick 15ms olsa da HSV ekran yakalamasını ≤~33ms'de bir yap → GDI yükü yarıya iner.
                if (hpBarCalibrated && sw.ElapsedMilliseconds - lastHpCheck >= 30)
                {
                    lastHpCheck = sw.ElapsedMilliseconds;
                    // T2 sinerji: taze snapshot HSV hedef-canlı değeri varsa onu kullan (ek GDI yakalama YOK);
                    // yoksa (ML/ColorScan modu veya snapshot bayat) eski yola düş.
                    var hpSnap = _latestDetections;
                    bool targetAlive = (hpSnap?.TargetAliveHsv is bool av && NowMs() - hpSnap.PublishedAtMs < 200)
                        ? av
                        : WtmVision.IsTargetAliveSmoothed(_appState.Wtm, HpClassifier);

                    if (!targetAlive)
                    {
                        // Alive-gate: bu angajmanda HP'yi hiç görmedik → henüz bar yüklenmemiş olabilir,
                        // ölüm bildirme (angajman başında yanlış-kill üretimini önler).
                        if (hadHpOnce)
                        {
                            if (firstHpMissMs < 0) firstHpMissMs = sw.ElapsedMilliseconds;
                            else if (sw.ElapsedMilliseconds - firstHpMissMs >= hpDeathConfirmMs)
                                return true; // HP barı ~60ms kesintisiz yok → mob öldü / seçim düştü
                        }
                    }
                    else
                    {
                        hadHpOnce     = true;  // bu angajmanda en az bir kez canlı gördük
                        firstHpMissMs = -1;    // canlı okuma → debounce sıfırla
                    }
                }

                // ── YOLO sticky tracking (HEDEF KİLİDİ) — yalnız archer yürüme yönü + overlay için ──
                // YOLO ASLA ölüm sinyali değildir ve hedefi YENİDEN SEÇMEZ. Kilitli mob'u (sınıf + son
                // konum yakını) izler; bir an kaybedilirse SON BİLİNEN konum KORUNUR → başka moba zıplama
                // yok (#2/#3). Ölüm yalnız HP barından gelir.
                if (Inferrer is not null)
                {
                    var snap = _latestDetections;
                    // Snapshot çok eskiyse (tespit thread'i takıldıysa) bayat veriye güvenme.
                    var dets = (snap is not null && NowMs() - snap.PublishedAtMs < 1000)
                        ? snap.Dets
                        : (IReadOnlyList<Detection>)Array.Empty<Detection>();

                    const float stickyRadius = 120f;
                    var sticky = dets
                        .Where(d => d.ClassId == currentTarget.ClassId &&
                                    d.DistanceTo(currentTarget.Center) <= stickyRadius)
                        .OrderBy(d => d.DistanceTo(currentTarget.Center)) // pozisyon kilidi: aynı mob'u izle
                        .FirstOrDefault();

                    if (sticky is not null)
                    {
                        currentTarget      = sticky;
                        _lastEngagedCenter = sticky.Center; // kill sonrası ceset kara listesi için taze konum
                    }
                    // sticky yoksa: currentTarget (+ _lastEngagedCenter) son değerinde KALIR (zıplama yok).

                    // Overlay vurgusu: kilitli/saldırılan hedef (kutuları DetectionLoop çizer).
                    _currentTargetForOverlay = currentTarget;
                }

                // ── B2v2: Archer AKICI yaklaşma — W SÜREKLİ basılı; yakında yönelme YOK ──
                // Eski "burst yürü" (350ms bas-bırak) stop-start hareket + her döngüde R-sonra-W
                // "attack stopped" üretiyordu, ayrıca yavaş olduğu için zaman aşımı uzaktan komboyu
                // başlatıyordu. Burada W kesintisiz basılı kalır (akıcı koşu); R yalnız UZAKTAYKEN
                // ve W BASILIYKEN taplanır (W basılıyken R atak başlatmaz → "attack stopped" yok);
                // yakın zonda (dist ≤ 2×menzil) hiç yönelme yok → hedef etrafında dönme imkânsız;
                // menzile girince W bırakılır ve LATCH. (Archer dışı modda engageStarted baştan true
                // → bu blok çalışmaz, kombo hemen.)
                if (!engageStarted)
                {
                    float dist = currentTarget.DistanceTo(charCenter); // ekran merkez mesafesi
                    bool  approachTO = approachStartMs >= 0 &&
                                       sw.ElapsedMilliseconds - approachStartMs >= s.EngageApproachTimeoutMs;

                    if ((dist <= rangePx && ++inRangeStreak >= InRangeStreakNeeded) || approachTO)
                    {
                        // Menzilde (jitter histerezisi geçti) veya güvenlik zaman aşımı → bırak, kilitle.
                        if (walkHeld)
                        {
                            await _router.KeyUpAsync(walkKey, CancellationToken.None);
                            walkHeld = false;
                            await Task.Delay(40, ct); // W key-up settle
                        }
                        engageStarted = true; // LATCH → bir daha yürüme yok, orbit imkânsız
                        StatusChanged?.Invoke(this, (approachTO && dist > rangePx)
                            ? "Archer: zaman aşımı — kombo başlıyor"
                            : "Menzilde — kombo başlıyor (archer)");
                    }
                    else
                    {
                        if (dist > rangePx) inRangeStreak = 0;
                        // W'yi bir kez bas, SÜREKLİ basılı tut → akıcı koşu (stop-start yok).
                        if (!walkHeld)
                        {
                            await _router.KeyDownAsync(walkKey, ct);
                            walkHeld = true;
                            if (approachStartMs < 0) approachStartMs = sw.ElapsedMilliseconds;
                        }
                        // Yönelme: yalnız UZAKTAYKEN (dist > 2×menzil) ve W basılıyken → yakında dönme yok,
                        // "attack stopped" yok (hareket hâlindeyken R atak başlatmaz).
                        if (dist > rangePx * 2f && sw.ElapsedMilliseconds - lastFaceTap >= s.FaceTargetRetapMs)
                        {
                            await TapKeyAsync(faceKey, 40, ct);
                            lastFaceTap = sw.ElapsedMilliseconds;
                        }
                        StatusChanged?.Invoke(this, $"Archer yaklaşıyor: {(int)dist}px → {rangePx}px");
                    }
                }

                // ── Kombo ateşle (engageStarted sonrası) ──────────────────────
                // Combo YOLO'dan BAĞIMSIZ: hedef kilitlendikten sonra YOLO mob'u bir an kaybetse bile
                // combo kesintisiz döner (oyun seçili hedefi tutar; yeni tık yok → mob ölene kadar saldırı).
                // Loop kombo: zaten çalışıyorsa yeniden ateşleme → kesintisiz döner (stutter yok).
                // Non-loop kombo: ComboFired comboFiring'i sıfırlayana kadar tek atış.
                bool canFire = combo is not null && engageStarted &&
                               (combo.IsLoop ? !_combo.IsRunning(combo.Id) : !comboFiring);
                if (canFire)
                {
                    _combo.FireAsync(combo!);
                    comboFiring = true;
                    StatusChanged?.Invoke(this, $"Kombo: {combo!.Name}");
                }

                await Task.Delay(s.WtmTickMs, ct);
            }
        }
        finally
        {
            _combo.ComboFired -= OnComboFired;
            // Not: CancelAll burada YOK — loop kombo, TrackAndCombat tekrar-girişlerinde
            // (safetyMs zaman aşımı ama HP hâlâ canlı → EngageAsync yeniden girer) hayatta kalmalı.
            // Angajman bitince EngageAsync.finally garanti durdurur.
            if (walkHeld)
                await _router.KeyUpAsync(walkKey, CancellationToken.None);
        }

        return false; // timeout veya CT iptal
    }
}
