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

    /// <summary>Archer yaklaşma davranışı (W sürekli + R yönelme) YALNIZ Sınıf="archer" seçiliyken uygulanır.
    /// KÖK NEDEN (2026-07-01): "🏹 Archer modu" checkbox'ı (s.EngageMovement) sınıf değişince SIFIRLANMIYOR —
    /// OtoFarmPage'de yalnız CurrentClass≠"archer" iken panel GİZLENİYOR (ayar aynen kalıyor, checkbox görünmez
    /// olduğundan kullanıcı kapatamıyor bile). Kullanıcı Archer'da işaretleyip başka sınıfa geçince ayar
    /// ArcherWalkAndFace'te takılı kalıyor → archer dışı sınıfta da W-hold+R yaklaşması çalışıyordu.</summary>
    private bool ArcherApproachActive(FarmSettings s) =>
        s.EngageMovement == EngageMovement.ArcherWalkAndFace &&
        string.Equals(_appState.ClassId, "archer", StringComparison.OrdinalIgnoreCase);

    private async Task EngageAsync(
        Detection target, MobInfo? mobInfo, FarmSettings s, CancellationToken ct)
    {
        _combo.CancelAll();

        // Archer: ayrı, UI'dan ayarlanabilir yaklaşma mesafesi (per-mob'u geçersiz kılar).
        // Archer dışı: rangePx kullanılmaz (kombo hemen ateşlenir) ama tutarlılık için doldur.
        int    rangePx  = ArcherApproachActive(s)
            ? s.ArcherApproachRangePx
            : (mobInfo?.EngagementRangePx ?? s.DefaultEngagementRangePx);
        string comboId  = _appState.Farm.SelectedComboId;
        Combo? combo    = string.IsNullOrEmpty(comboId)
            ? null
            : _appState.Combos.FirstOrDefault(c => c.Id == comboId);

        if (combo is null)
        {
            // KÖK NEDEN olabilir: SelectedComboId boş/geçersiz → beceri kombosu HİÇ atılmaz (yalnız yaklaşma +
            // archer R-yönelme görülür). Net uyarı bas ki "combo atmıyor" sebebi log/HUD'da açık olsun.
            Program.Log($"[Farm] ⚠ KOMBO SEÇİLİ DEĞİL (SelectedComboId='{comboId}') → beceri kombosu ATILMAZ. " +
                        $"Farm sekmesinden kombo seçin.");
            StatusChanged?.Invoke(this, "⚠ Kombo seçili değil — beceri kombosu atılmıyor");
        }
        else
            Program.Log($"[Farm] Sabit kombo: {combo.Name} ({comboId})");

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
                   WtmVision.IsTargetAliveSmoothed(_appState.Wtm)); // renk tarama — kalibre ise kontrol et
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
        // #3: öldürülen izi tracker'da "ölü" damgala → ceset tespit edilmeye devam etse de aday dışı
        // (4 ceset yan yana olsa bile tekrar denenmez; iz despawn olunca düşer). Pozisyon-blacklist yedek.
        _tracker.MarkDead(_lastEngagedTrackId);
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
        bool archerApproach = ArcherApproachActive(s);

        Detection currentTarget = target;
        _lastEngagedCenter  = target.Center;  // ilk tick'te HP ile ölüm tespit edilse de geçerli konum
        _lastEngagedTrackId = target.TrackId; // kill sonrası MobTracker.MarkDead için kilitli iz kimliği
        bool walkHeld     = false;
        bool comboFiring  = false;
        bool engageStarted = !archerApproach; // archer dışı: yaklaşma yok → hemen kombo
        // Ölüm tespiti ZAMAN-tabanlı: HP barı (HSV ROI) bu kadar ms KESİNTİSİZ "kırmızı yok" okursa öldü.
        // Tick'ten BAĞIMSIZ (WtmTick 15/50 fark etmez). Tek titrek HSV karesinde yanlış-ölüm → canlı mob'u
        // bırakma (#2) engeller. YALNIZ ölüm anında kill başına bir kez ödenir — combat/acquisition'ı
        // yavaşlatmaz. A6: ayardan (FarmSettings.HpDeathConfirmMs, varsayılan ~60ms ≈ 2 teyit; titrek
        // HSV'de 100-150 daha güvenli). Min 20ms tabanına kıstırılır.
        int hpDeathConfirmMs = Math.Max(20, s.HpDeathConfirmMs);
        // Kullanıcı modeli (2026-06-20): KIRMIZI birincil canlı sinyali (hızlı, eski davranış). Kırmızı KAYBOLUNCA
        // hemen "öldü" DEME — "siyah boş bar" hâlâ varsa mob ÇOK DÜŞÜK HP'de = CANLI (düşük-HP sahte-ölümü önlenir).
        // Gerçek ölümde KO hedef-penceresi ANINDA kaybolur → ne kırmızı ne siyah-bar kalır = ölü. Normal+duyuru geçerli.
        const double EmptyBarMinDarkFrac = 0.45; // kırmızı yokken koyu-oran ≥ bu → "boş/düşük-HP bar yapısı VAR" (canlı)
        // Anti-freeze tavanı: kırmızı bu kadar dönmeyip yalnız koyu sürerse → gerçek düşük-HP mob bu sürede ölürdü
        // → koyu-arka-plan say, bitir. Ayardan (FarmSettings.HpEmptyBarGraceMs, varsayılan 600); eski hardcoded
        // 3000ms koyu sahnelerde her kill'de ~3sn "taranıyor" gecikmesi üretiyordu. Min 150ms tabanına kıstırılır.
        int          RedGoneGraceMs      = Math.Max(150, s.HpEmptyBarGraceMs);
        long firstHpMissMs = -1;     // "bar TAMAMEN yok" ilk anı (-1 = bar var)
        long lastRedSeenMs = -1;     // kırmızının en son görüldüğü an (-1 = hiç görülmedi)
        long lastHpCheck   = -1000;  // HSV/HP ekran yakalamasını ≤~33ms'de bire sınırla (GDI yükü)
        long lastBarDiag   = -10000; // koyu-oluk/kırmızı tanılama log'u throttle (eşik tune'u için)
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
                    // LastBarScan: son DXGI ROI taramasının kırmızı + koyu-oranı (DetectionLoop her ~30ms tazeler;
                    // TargetAliveHsv ile AYNI taramadan gelir). Kullanıcı modeli: önce KIRMIZI, kırmızı yokken SİYAH-bar.
                    var bar = WtmVision.LastBarScan;
                    // bar.alive = BarPresent'in fill-farkında kararı (red≥thr && fill≥HpBarFillMinFrac) → ROI'deki
                    // KIRMIZI arka planı (red var ama fill düşük) "canlı" SANMAZ. Eskiden ham bar.red≥HpHsvMinPx idi →
                    // arka plan kırmızısı (log: red=2078) combat'ı sahte-canlıda takıyordu ("kırmızı=2078 → canlı"
                    // tekrarları). Düşük-HP korunur: gerçek düşük-HP bar fill≥%74 → alive true; kırmızı büsbütün
                    // gidince aşağıdaki emptyBarHere (darkFrac) devreye girer.
                    bool redPresent   = bar.alive;                                      // canlı/seçili → BAR VAR (kırmızı + dolu)
                    bool emptyBarHere = !redPresent
                        && bar.darkFrac >= EmptyBarMinDarkFrac                          // kırmızı yok ama SİYAH boş bar var
                        && bar.darkFrac <  _appState.Wtm.HpTroughAllDarkMaxFrac;        // tüm-koyu ekran (mağara/gece) hariç
                    bool targetAlive  = redPresent || emptyBarHere;                     // log/teşhis için

                    if (redPresent)
                    {
                        hadHpOnce     = true;                       // bu angajmanda en az bir kez canlı (kırmızı) gördük
                        firstHpMissMs = -1;
                        lastRedSeenMs = sw.ElapsedMilliseconds;
                    }
                    else if (hadHpOnce)
                    {
                        // KIRMIZI KAYBOLDU → "mob öldü?" anı. Gerçekten öldü mü: SİYAH boş bar hâlâ var mı?
                        if (!emptyBarHere)
                        {
                            // Ne kırmızı ne siyah-bar → KO hedef penceresi YOK (arka plana düştü) → öldü/seçim düştü.
                            if (firstHpMissMs < 0) firstHpMissMs = sw.ElapsedMilliseconds;
                            else if (sw.ElapsedMilliseconds - firstHpMissMs >= hpDeathConfirmMs)
                                return true;
                        }
                        else
                        {
                            // SİYAH boş bar VAR → mob ÇOK DÜŞÜK HP'de, HÂLÂ CANLI → vurmaya devam (sahte-ölüm YOK).
                            firstHpMissMs = -1;
                            // Anti-freeze tavanı: gerçek düşük-HP mob vurmaya devam edince RedGoneGraceMs içinde ölür
                            // (bar kaybolur). Kırmızı bu kadar süredir hiç dönmediyse bar değil koyu-ARKA-PLANdır → bitir.
                            if (lastRedSeenMs >= 0 && sw.ElapsedMilliseconds - lastRedSeenMs >= RedGoneGraceMs)
                            {
                                // Grace-tavanı ile "öldü" sayıldı (gerçek ölüm değil de uzun koyu-arka-plan olabilir).
                                // Normal ölümden ayırt edilebilsin diye etiketli logla → kill-kaçırma şüphesinde
                                // kullanıcı bunu görüp HpEmptyBarGraceMs'i tek-tür tank mob için büyütebilir.
                                Program.Log($"[Farm] ölüm: grace-tavanı ({RedGoneGraceMs}ms kırmızı yok) → öldü-sayıldı");
                                return true;
                            }
                        }
                    }

                    // Tune yardımı: son HP-bar taramasının kırmızı/koyu-oluk değerlerini ara sıra logla
                    // (düşük-HP sahte-ölüm eşiklerini gerçek arka planlarda ayarlamak için — bkz HpTrough*).
                    if (sw.ElapsedMilliseconds - lastBarDiag >= 1000)
                    {
                        lastBarDiag = sw.ElapsedMilliseconds;
                        var bs = WtmVision.LastBarScan;
                        Program.Log($"[Farm] HP tespit: kırmızı={bs.red} oluk=%{(int)(bs.darkFrac * 100)} " +
                                    $"dolu=%{(int)(bs.fillFrac * 100)} ({bs.dark}/{bs.total}) → {(targetAlive ? "canlı" : "ölü")}");
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

                    // Önce kalıcı TrackId ile eşle (en sağlam — aynı türden komşu moba KAYMAZ; #4).
                    Detection? sticky = currentTarget.TrackId >= 0
                        ? dets.FirstOrDefault(d => d.TrackId == currentTarget.TrackId)
                        : null;
                    bool stuckByTrackId = sticky is not null; // kesin-aynı-iz mi yoksa pozisyon-fallback mi?
                    // Track kaybolduysa eski pozisyon-kilidi (aynı tür + 120px en yakın) fallback.
                    if (sticky is null)
                    {
                        const float stickyRadius = 120f;
                        sticky = dets
                            .Where(d => d.ClassId == currentTarget.ClassId &&
                                        d.DistanceTo(currentTarget.Center) <= stickyRadius)
                            .OrderBy(d => d.DistanceTo(currentTarget.Center)) // pozisyon kilidi: aynı mob'u izle
                            .FirstOrDefault();
                    }

                    if (sticky is not null)
                    {
                        currentTarget      = sticky;
                        _lastEngagedCenter = sticky.Center;    // kill sonrası ceset kara listesi için taze konum
                        // _lastEngagedTrackId YALNIZ kesin-aynı-iz (TrackId) eşleşmesinde güncellenir (#2):
                        // pozisyon-fallback yakındaki BAŞKA bir CANLI mobu yakalayabilir → onu MarkDead etme.
                        // Fallback'te eski iz kimliği korunur; mob iz değiştirdiyse yeni iz DeadInheritRadiusPx
                        // köprüsüyle yine ölü damgalanır, canlı komşu yanlışlıkla elenmez.
                        if (stuckByTrackId) _lastEngagedTrackId = sticky.TrackId;
                    }
                    // sticky yoksa: currentTarget (+ _lastEngaged*) son değerinde KALIR (zıplama yok).

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
