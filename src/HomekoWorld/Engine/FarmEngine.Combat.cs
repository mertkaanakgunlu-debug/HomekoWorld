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

        // Hedef-geçiş metriği (2026-07-03): kill→bu angajman arası süre + nedenleri tek satırda —
        // kullanıcının "bazen direkt bazen duraksıyor" gözleminin doğrudan ölçümü. Banka çalıştıysa
        // süre medyana katılmaz (banka turu meşru uzun; istatistiği zehirlemesin).
        long switchMs = NowMs() - _lastEngageEndMs;
        Program.Log($"[Farm] hedef-geçiş: {switchMs}ms (deneme={_switchAcqAttempts} " +
                    $"guardian-red={_switchGuardianBlocks} boş-tarama={_switchEmptyScans}sn " +
                    $"banka={(_bankRanSinceEngage ? "EVET" : "hayır")}) → {target.ClassName} trk={target.TrackId}");
        if (!_bankRanSinceEngage && Telemetry.SwitchTimesMs.Count < 2000)
        {
            Telemetry.SwitchTimesMs.Add((int)Math.Min(switchMs, int.MaxValue));
            Telemetry.SwitchAvgMs = (int)Telemetry.SwitchTimesMs.Average(); // HUD koşan ortalaması
        }
        _switchAcqAttempts = 0; _switchGuardianBlocks = 0; _switchEmptyScans = 0; _bankRanSinceEngage = false;

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

        EngageResult result;
        try
        {
            do
            {
                result = await TrackAndCombatAsync(target, combo, rangePx, s, ct);
                // Timeout ama HP bar hâlâ görünüyorsa hedef seçili — tıklamadan yeniden engage.
                // Killed/LostFast → döngüden çık. CT iptal → çık.
            }
            while (result == EngageResult.Timeout && !ct.IsCancellationRequested &&
                   WtmVision.IsTargetAliveSmoothed(_appState.Wtm)); // renk tarama — kalibre ise kontrol et
        }
        finally
        {
            // Angajman bitti (mob öldü / hedef kayıp / iptal) — loop kombo dahil her şeyi durdur.
            _combo.CancelAll();
            _currentTargetForOverlay = null; // scanning'e dönerken vurgulu hedef kalmasın
        }

        if (result == EngageResult.Timeout)
        {
            Telemetry.Timeouts++;
            _lastEngageEndMs = NowMs();
            SetState(FarmState.Scanning, "Hedef kaybedildi");
            return;
        }

        // Killed / Lost / LostFast: hepsinde konum kara-listelenir + Scanning'e dönülür + sayaçlar sıfırlanır.
        //  • Killed   = pencere kayboldu + HASAR GÖRÜLDÜ (V3 hasar-kapısı) → kill say + iz "ölü" damgala + loot.
        //  • Lost     = pencere kayboldu ama hasar GÖRÜLMEDİ (çalıntı/deselect/okuma arızası) → kill SAYMA,
        //               iz damgalama YOK, KISA blacklist → envanter tetiği şişmez, canlı mob cesetlenmez.
        //  • LostFast = HP kırmızı yok + YOLO izi TargetLostYoloMs kayıp (uzak hedef) → aynı kısa-blacklist yolu.
        bool killed = result == EngageResult.Killed;
        long expire = NowMs() + (killed ? s.DeadBlacklistMs : MissReselectSkipMs);
        _deadBlacklist.Add((_lastEngagedCenter, expire));
        // F2: ceset ayakta-merkezden aşağı düşer → düşen kutuyu da kapsa (ayrı entry).
        _deadBlacklist.Add((new PointF(_lastEngagedCenter.X, _lastEngagedCenter.Y + CorpseFallOffsetPx), expire));

        // Oturum-özeti sayaçları (yalnız FarmLoop zincirinden — kilit gerekmez).
        switch (result)
        {
            case EngageResult.Lost:     Telemetry.Losts++;     break;
            case EngageResult.LostFast: Telemetry.LostFasts++; break;
        }

        if (killed)
        {
            Telemetry.Kills++;
            EmitTelemetry();
            _tracker.MarkDead(_lastEngagedTrackId);   // kill hasar-kapılı → Confirmed (varsayılan); damga kalıcı
            // 13.tur (S3): onaylı kill → nüfus borcu (MobRespawnMs sonra düşer). Tavan RegionMobCount:
            // tek-vuruş yanlış-pozitifi / çift sayım borcu şişirse bile bastırma üst sınırı aşamaz.
            if (s.RegionMobCount > 0)
            {
                _killDebt.Add(NowMs());
                while (_killDebt.Count > s.RegionMobCount) _killDebt.RemoveAt(0);
            }
            if (s.LootEnabled)
            {
                SetState(FarmState.Looting, "Loot toplanıyor…");
                await LootAsync(target, s, ct);
            }
            // Dev 3 — kill eşiği: envanter boşaltma tetiğini kur (ana tick tarama fazında çalıştırır).
            // Otonom kontrolündeyken bastırılır (Otonom kendi town-sat akışını yürütür).
            // 2026-07-04 (7.tur-b): YALIN sabit eşik (6.tur'un adaptif deneyi kaldırıldı — hiç devreye
            // girmiyordu ve tetiği belirsizleştiriyordu) + tetik LOGU: "banka neden açılmadı/açıldı" artık
            // bellekteki gerçek değerlerle logdan izlenir (oturum-başlangıç satırı da banka konfigini yazar).
            if (!AutonomousControlled && Bank is not null && Bank.Enabled &&
                Telemetry.Kills - _killsAtLastBankCheck >= Bank.CheckEveryKills)
            {
                Program.Log($"[Farm] banka-tetik: kill={Telemetry.Kills} " +
                            $"(son-kontrol={_killsAtLastBankCheck}, eşik={Bank.CheckEveryKills}) → kuyruğa alındı");
                _killsAtLastBankCheck = Telemetry.Kills;
                _bankPending = true;
            }
        }

        // Hedef-geçiş metriği: angajman TAMAMEN bitti (loot dahil) → sonraki geçişin saati burada başlar
        // (loot süresi geçişe sayılmaz; metrik saf hedefleme yavaşlığını ölçsün).
        _lastEngageEndMs = NowMs();
        SetState(FarmState.Scanning, "Taranıyor…");
        _idleWatch.Restart();
        _noCombatWatch.Restart();   // Dev 2 — angajman oldu → "dönüş" sayacını sıfırla
    }

    // ── Takip + kombat döngüsü ────────────────────────────────────────────────
    // Tek döngüde: mesafe → W bas/bırak → R tap → kombo → ölüm tespiti.
    // Döner: true = mob öldü; false = timeout / CT iptal / hedef kayıp.

    private async Task<EngageResult> TrackAndCombatAsync(
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

        Detection currentTarget = target; // KİMLİK: kilitli iz — yalnız kesin-aynı-TrackId eşleşmesiyle ilerler
        // 9.tur: NİŞAN/KİMLİK AYRIMI — aimTarget mesafe/overlay için EN TAZE gözlemi izler (120px pozisyon-
        // fallback dahil); currentTarget/_lastEngagedCenter/_lastEngagedTrackId ise yalnız kesin-aynı-iz
        // eşleşmesinde güncellenir. Eski davranışta fallback currentTarget+_lastEngagedCenter'ı da
        // kaydırıyordu → kill-sonrası ceset blacklist'i 120px komşunun (canlı mob/ceset) üstüne düşebiliyor,
        // kimlik sürükleniyordu.
        Detection aimTarget     = target;
        _lastEngagedCenter  = target.Center;  // ilk tick'te HP ile ölüm tespit edilse de geçerli konum
        _lastEngagedTrackId = target.TrackId; // kill sonrası MobTracker.MarkDead için kilitli iz kimliği
        bool walkHeld     = false;
        bool comboFiring  = false;
        // 14.tur (2.3 — dış denetim bulgusu): tek-vuruş kill kuralının kanıtı engageStarted DEĞİL
        // (non-archer'da baştan true — kombo hiç atılmadan da true'ydu, combo=null'da bile) —
        // GERÇEKTEN en az bir FireAsync çağrıldı mı? Yanlış-Killed artık S3 kill-borcunu da
        // kirletiyordu (beklenen-canlı erken 0'a iner → hareketsiz CANLI moblar bastırılır).
        bool comboFiredOnce = false;
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
        // Angajman başladıktan sonra hadHpOnce (bar/yapı en az bir kez "canlı") hiç true olmazsa bu kadar
        // ms sonra vazgeçilir — bkz FarmSettings.HpFirstAliveGraceMs dokümantasyonu (kök neden + canlı log kanıtı).
        int          hpFirstAliveGraceMs = Math.Max(300, s.HpFirstAliveGraceMs);
        // V4 hızlı-terk (2026-07-03): yapı skoru İLK örnekten beri kesintisiz absent-eşiğin ALTINDAYSA
        // (pencere hiç açılmadı = hayalet/ıska tık) grace'in (1500ms) tamamını bekleme — canlı logda 3 Lost'un
        // her biri tam 1512ms yaktı, skorlar baştan 0.05-0.13'tü. ≥4 taze kare + duvar-saati çifte şartı
        // (missFrames deseniyle aynı); skor eşik-bandına/VAR'a dönerse sayaç sıfırlanır.
        long structAbsentSinceMs = -1;
        int  structAbsentFrames  = 0;
        int  structAbandonMs     = Math.Max(200, s.StructAbsentAbandonMs);
        float structAbsentThr    = _appState.Wtm.TargetFrameAbsentThreshold;
        long firstHpMissMs = -1;     // "bar TAMAMEN yok" ilk anı (-1 = bar var)
        long lastRedSeenMs = -1;     // kırmızının en son görüldüğü an (-1 = hiç görülmedi)
        long lastHpCheck   = -1000;  // HSV/HP ekran yakalamasını ≤~33ms'de bire sınırla (GDI yükü)
        long lastBarDiag   = -10000; // koyu-oluk/kırmızı tanılama log'u throttle (eşik tune'u için)
        // Alive-gate (#1): bu angajmanda HP barını en az bir kez "canlı" gördük mü?
        // Görülmeden ölüm bildirme → angajman başında HP barının henüz yüklenmediği kısa pencerede
        // yanlış-ölüm üretmez (mob sadece seçildi, bar çıkmadı → hemen "öldü" deme).
        bool hadHpOnce = false;

        // ── V3 ölüm-tespiti (2026-07-02) — kanıt: 18:47 canlı log, Tyon 2-5sn'de ölürken angajmanlar
        // 0.5-1sn'de LOGSUZ "Killed" ile bitiyordu (kill sayacı şişti, canlı mob cesetlendi) ──
        long lastBarStamp = -1;           // işlenen son LastBarScan damgası — DONMUŞ kare tekrar teyit sayılmaz
        int  missFrames   = 0;            // "pencere yok" okunan TAZE kare sayısı (ölüm için ≥3 şart)
        int  maxRedSeen   = 0;            // bu angajmanda canlı-karelerdeki en yüksek kırmızı (hasar-tepe)
        int  minRedSeen   = int.MaxValue; // ... en düşük kırmızı (hasar-ilerleme kanıtı)

        // Dev 1 — hızlı hedef-kaybı: hedefin YOLO izinin en son "görüldüğü" an (sw damgası) + eşik.
        // Başlangıçta 0 = "sw başında az önce görüldü" (t≈0) → ilk tick'lerde yanlış-kayıp yok.
        long lastStickySeenMs = 0;
        int  targetLostYoloMs = Math.Max(80, s.TargetLostYoloMs);
        // Dev 1 FIX (2026-07-02, canlı test bulgusu): yakın dövüşte karakter modeli mobu ÖRTER → YOLO izi
        // CANLI mobda da uzun süre kaybolur; mob düşük HP'ye inince (kırmızı yok + siyah boş-bar) iki sinyal
        // yanlışlıkla birleşip "çalındı" diyordu → mob ölmeden başka moba atlama. Hızlandırma yalnız hedef
        // ekran-merkezinden uzakken (yaklaşma/çalınma senaryosu) geçerli; yakında ölümü zaten hpDeathConfirmMs
        // (60ms, pencere-kayboldu) yolu yakalar, kötü ihtimalle grace (600ms) tavanı işler.
        const float LostFastMinDistPx = 200f;

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

        // ── V3 hasar-kapısı + adli özet ──────────────────────────────────────
        // Kill YALNIZ bar'ın bu angajmanda gerçekten AZALDIĞI görüldüyse sayılır (min ≤ %55×max):
        // Tyon 2-5sn'de ölür → 30ms örneklemede düşüş mutlaka görülür. Bar hiç azalmadan pencere
        // kaybolduysa (çalıntı / deselect / tek-kare okuma arızası) bu bir KILL DEĞİLDİR → Lost:
        // kill SAYILMAZ (envanter tetiği şişmez), MarkDead YOK, kısa blacklist → hızla yeni hedef.
        // Her angajman sonu TEK satır adli log — bundan sonra teşhis körlemesine yapılmaz.
        const float KillRedDropFrac = 0.55f;
        bool DamageSeen() => maxRedSeen > 0 && minRedSeen != int.MaxValue &&
                             minRedSeen <= (int)(maxRedSeen * KillRedDropFrac);
        EngageResult Finish(EngageResult r, string reason)
        {
            // Teşhis (2026-07-03): kombonun bu angajmanda hiç ATEŞLENİP ateşlenmediğini logla — özellikle
            // archer yaklaşma modunda (ArcherApproachRangePx çok küçükse) hedef menzile GİRMEDEN kaybedilirse
            // "Lost" hep kombo hiç başlamadan üretilir; bu satır olmadan "mesafe" tek başına ayırt ettirmiyordu.
            string comboState = combo is null ? "YOK (seçili değil)"
                : !engageStarted ? "YAKLAŞIYOR (hiç ateşlenmedi)"
                : comboFiring    ? "ateşleniyor"
                : "beklemede";
            Program.Log($"[Farm] angajman-sonu: {r} ({reason}) süre={sw.ElapsedMilliseconds}ms " +
                        $"kırmızı[max={maxRedSeen} min={(minRedSeen == int.MaxValue ? -1 : minRedSeen)}] " +
                        $"trk={currentTarget.TrackId} mesafe={(int)currentTarget.DistanceTo(charCenter)}px " +
                        $"kombo={comboState}");
            return r;
        }
        EngageResult FinishDeath(string via)
        {
            if (DamageSeen())
                return Finish(EngageResult.Killed, via + " + hasar görüldü");
            // 2026-07-04 (7.tur-b) TEK-VURUŞ kill'leri: karakter güçlenince mob TEK vuruşta ölüyor — bar TAM
            // DOLU görünüp hiç ara-değer örneklenmeden kayboluyor (canlı kanıt: 21:00 oturumu, 6 Lost'un HEPSİ
            // kırmızı[min==max=dolu] + kombo=ateşleniyor + süre 1-2.5sn → kill'lerin ~%23'ü sayılmıyordu).
            // Kombo bu angajmanda GERÇEKTEN ateşlendiyse kill say: aksi hâlde (a) kill sayacı eksik → banka
            // kill-tetiği gecikiyor, (b) ceset MarkDead'siz kalıp yeniden problanıyor (cesede-tıklama beslemesi).
            // Çalıntı/deselect aynı imzayı verebilir ama solo spotta nadir; yanlış-pozitifte canlı mob geçici
            // hedeflenemez kalır ve kamera-flip churn'üyle kendini toparlar (7.tur gerekçesiyle tutarlı).
            // 14.tur (2.3): şart artık comboFiredOnce da istiyor — "kombo ateşlendi" iddiası gerçek kanıt
            // (eskiden non-archer engageStarted=true yeterliydi; combo seçili değilken/hiç atılmamışken
            // pencere kaybı da Killed sayılıp kill sayacı + S3 borcu kirlenebiliyordu → artık Lost).
            if (engageStarted && comboFiredOnce)
                return Finish(EngageResult.Killed, via + " + tek-vuruş (hasar örneklenemedi, kombo ateşlendi)");
            return Finish(EngageResult.Lost, via + " ama HASAR GÖRÜLMEDİ → kill sayılmaz (çalıntı/deselect/arıza?)");
        }

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
                    // V4: kare özeti tespit thread'inden (WtmVision.ScanTargetBar → LastTargetBar; yapı + renk +
                    // offset + damga ATOMİK). null = HSV taraması çalışmıyor (ColorScan modu) → GDI legacy dalı.
                    var tbOpt = WtmVision.LastTargetBar;
                    if (tbOpt is { } tb)
                    {
                        // V3 tazelik kapısı: yalnız YENİ (damga değişmiş) + TAZE (≤300ms) kare karar ilerletir —
                        // tespit thread'i takılıp değer donduğunda aynı kötü kare tekrar "teyit" sayılmaz.
                        bool freshFrame = tb.StampMs != lastBarStamp && NowMs() - tb.StampMs <= 300;
                        lastBarStamp = tb.StampMs;

                        bool structMode   = tb.StructureKnown;              // V4: çerçeve şablonu öğretilmiş
                        bool redPresent   = tb.RedAlive;                    // BarPresent (kırmızı+dolu) kararı
                        // V4 modunda canlı/seçili TEK otorite = ÇERÇEVE (pencere açık ↔ mob seçili+canlı;
                        // düşük-HP'de kırmızı yok ama çerçeve durur → belirsizlik YOK). Renk yalnız hasar ölçer.
                        bool aliveNow     = structMode ? tb.StructurePresent : redPresent;
                        bool emptyBarHere = !structMode && !redPresent
                            && tb.DarkFrac >= EmptyBarMinDarkFrac
                            && tb.DarkFrac <  _appState.Wtm.HpTroughAllDarkMaxFrac;
                        bool targetAlive  = aliveNow || emptyBarHere;       // teşhis logu için

                        if (freshFrame)
                        {
                            if (structMode)
                            {
                                // ── V4 yolu: yapı VAR = pencere açık = seçili + canlı ──
                                if (aliveNow)
                                {
                                    hadHpOnce     = true;
                                    firstHpMissMs = -1;
                                    missFrames    = 0;
                                    lastRedSeenMs = sw.ElapsedMilliseconds;
                                    if (redPresent)
                                    {
                                        // Hasar-geçmişi: kill kanıtı (FinishDeath hasar-kapısı).
                                        if (tb.Red > maxRedSeen) maxRedSeen = tb.Red;
                                        if (tb.Red < minRedSeen) minRedSeen = tb.Red;
                                    }
                                }
                                else if (hadHpOnce)
                                {
                                    // Yapı YOK → pencere kapandı (öldü / çalıntı / deselect). Süre + ≥3 taze kare
                                    // teyidi; Killed/Lost ayrımını hasar-kapısı yapar. Grace tavanı ve LostFast bu
                                    // modda GEREKSİZ: koyu-arka-plan belirsizliği yapı sinyalinde yoktur.
                                    if (firstHpMissMs < 0) { firstHpMissMs = sw.ElapsedMilliseconds; missFrames = 1; }
                                    else if (++missFrames >= 3 &&
                                             sw.ElapsedMilliseconds - firstHpMissMs >= hpDeathConfirmMs)
                                        return FinishDeath($"yapı-kayboldu skor={tb.StructureScore:F2}");
                                }
                                else
                                {
                                    // ── Hızlı-terk (2026-07-03): yapı HİÇ görünmedi (hadHpOnce=false) ──
                                    // Skor KESİNTİSİZ absent-eşiğin altında (histerezis bandı sayılmaz — banda
                                    // giren/VAR olan okuma sayacı sıfırlar) → pencere hiç açılmadı: hayalet/ıska
                                    // tık ~500ms'de LostFast ile bırakılır (kısa blacklist; MarkDead YOK).
                                    // hpFirstAliveGraceMs (1500ms) dış sınır olarak durur (V3-renk modunu da korur).
                                    if (tb.StructureScore < structAbsentThr)
                                    {
                                        if (structAbsentSinceMs < 0) { structAbsentSinceMs = sw.ElapsedMilliseconds; structAbsentFrames = 1; }
                                        else if (++structAbsentFrames >= 4 &&
                                                 sw.ElapsedMilliseconds - structAbsentSinceMs >= structAbandonMs)
                                            return Finish(EngageResult.LostFast,
                                                $"yapı hiç görünmedi skor={tb.StructureScore:F2}<{structAbsentThr:F2} ({structAbandonMs}ms kesintisiz)");
                                    }
                                    else { structAbsentSinceMs = -1; structAbsentFrames = 0; }
                                }
                            }
                            else
                            {
                                // ── V3 renk yolu (çerçeve öğretilmemiş) — davranış birebir korunur ──
                                // Dev 1 — YOLO-hızlandırmalı kayıp: mesafe kapılı (yakın dövüşte iz kaybı normal).
                                if (!redPresent && hadHpOnce && Inferrer is not null &&
                                    aimTarget.DistanceTo(charCenter) > LostFastMinDistPx &&
                                    sw.ElapsedMilliseconds - lastStickySeenMs >= targetLostYoloMs)
                                    return Finish(EngageResult.LostFast, $"YOLO izi {targetLostYoloMs}ms yok + HP kırmızı yok");

                                if (redPresent)
                                {
                                    hadHpOnce     = true;
                                    firstHpMissMs = -1;
                                    missFrames    = 0;
                                    lastRedSeenMs = sw.ElapsedMilliseconds;
                                    if (tb.Red > maxRedSeen) maxRedSeen = tb.Red;
                                    if (tb.Red < minRedSeen) minRedSeen = tb.Red;
                                }
                                else if (hadHpOnce)
                                {
                                    // KIRMIZI KAYBOLDU → gerçekten öldü mü: SİYAH boş bar hâlâ var mı?
                                    if (!emptyBarHere)
                                    {
                                        if (firstHpMissMs < 0) { firstHpMissMs = sw.ElapsedMilliseconds; missFrames = 1; }
                                        else if (++missFrames >= 3 &&
                                                 sw.ElapsedMilliseconds - firstHpMissMs >= hpDeathConfirmMs)
                                            return FinishDeath("pencere-kayboldu");
                                    }
                                    else
                                    {
                                        // SİYAH boş bar VAR → çok düşük HP, canlı → devam; anti-freeze tavanı.
                                        firstHpMissMs = -1;
                                        missFrames    = 0;
                                        if (lastRedSeenMs >= 0 && sw.ElapsedMilliseconds - lastRedSeenMs >= RedGoneGraceMs)
                                            return FinishDeath($"grace-tavanı {RedGoneGraceMs}ms");
                                    }
                                }
                            }
                        }

                        // Tanılama (1sn throttle): renk + (V4'te) yapı skoru — eşik tune'u için.
                        if (sw.ElapsedMilliseconds - lastBarDiag >= 1000)
                        {
                            lastBarDiag = sw.ElapsedMilliseconds;
                            string st = structMode
                                ? $" yapı={(tb.StructurePresent ? "VAR" : "YOK")}({tb.StructureScore:F2})" : "";
                            Program.Log($"[Farm] HP tespit: kırmızı={tb.Red} oluk=%{(int)(tb.DarkFrac * 100)} " +
                                        $"dolu=%{(int)(tb.FillFrac * 100)} ({tb.Dark}/{tb.Total}) → {(targetAlive ? "canlı" : "ölü")}{st}");
                        }
                    }
                    else
                    {
                        // ── Legacy GDI dalı (ColorScan modu vb.) — senkron tarama = her okuma taze ──
                        // Damga makinesi uygulanamaz; hasar-geçmişi yok. 8.tur (2026-07-08): eskiden burada
                        // doğrudan Finish(Killed) çağrılıyordu = angaje bile olmamış (kombo hiç ateşlenmemiş,
                        // örn. archer yaklaşmadayken çalınan) hedefler de kill sayılıyordu. Artık FinishDeath:
                        // hasar-geçmişi bu dalda hiç örneklenmediği için karar tek-vuruş kuralına iner —
                        // engageStarted ise Killed, değilse Lost (V4/V3 dallarıyla tutarlı).
                        bool redPresent = WtmVision.IsTargetAliveSmoothed(_appState.Wtm);
                        if (redPresent)
                        {
                            hadHpOnce = true; firstHpMissMs = -1; lastRedSeenMs = sw.ElapsedMilliseconds;
                        }
                        else if (hadHpOnce)
                        {
                            if (firstHpMissMs < 0) firstHpMissMs = sw.ElapsedMilliseconds;
                            else if (sw.ElapsedMilliseconds - firstHpMissMs >= hpDeathConfirmMs)
                                return FinishDeath("pencere-kayboldu (GDI renk-modu)");
                        }
                    }
                }

                // ── KÖK NEDEN FIX (2026-07-03, canlı log kanıtı) ────────────────────────
                // hadHpOnce hâlâ false ise (bu angajmanda HP barı/yapı HİÇ "canlı" onaylanmadı) → yukarıdaki
                // ÜÇ dalın ("V4 yapı-kayboldu", "V3 pencere-kayboldu", "legacy GDI pencere-kayboldu") hepsi
                // `else if (hadHpOnce)` şartlı olduğundan hiçbiri ASLA çalışmaz; ayrıca yukarısı `freshFrame`
                // şartlıdır (tespit thread'i takılırsa bu da tetiklenmez). Canlı log kanıtı: acquisition
                // (GDI renk taraması, yapıdan bağımsız) "canlı" onaylayıp engage başlatmış, ama V4 yapı skoru
                // bu angajmanda HİÇ Match eşiğine ulaşmamış (tek-örnek şablon fragile / hedef aslında zaten
                // geçersiz) → 34+ saniye kesintisiz "yapı=YOK" + SIFIR angajman-sonu satırı; kombo bu süre
                // boyunca boşa atılmaya devam etti (safetyMs=120sn'e kadar sürebilirdi). Bu kontrol freshFrame'e
                // BAĞLI DEĞİL (yalnız duvar-saati + hadHpOnce) → tespit thread'i donsa bile üst sınır garantili.
                // `hpBarCalibrated` şartı: HP bar HİÇ kalibre değilse (kör mod) zaten hadHpOnce hiçbir zaman
                // true olamaz — bu durumda YENİ davranış (1.5sn'de vazgeç) her angajmanı erken keser (mob'a
                // vurma fırsatı bile vermeden). Kalibrasyonsuzken ESKİ davranış (safetyMs'e güven) korunur;
                // bu fix yalnız "kalibreliyken güvenilir sinyal bekleniyor ama hiç gelmiyor" durumuna hedeflenir.
                if (hpBarCalibrated && !hadHpOnce && sw.ElapsedMilliseconds >= hpFirstAliveGraceMs)
                    return Finish(EngageResult.Lost,
                        $"HP bar/yapı hiç canlı görülmedi ({hpFirstAliveGraceMs}ms doldu) — hedef geçersiz/hayalet olabilir");

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
                    // Track kaybolduysa pozisyon-kilidi (aynı tür + 120px en yakın) fallback — 9.tur: çapa
                    // aimTarget.Center (en taze gözlem; hızlı mobda iz churn'ünde takip kabiliyeti korunur)
                    // ve ölü/guardian aday nişan hedefi olamaz (cesede doğru yürüme/yönelme olmasın).
                    if (sticky is null)
                    {
                        const float stickyRadius = 120f;
                        sticky = dets
                            .Where(d => d.ClassId == currentTarget.ClassId &&
                                        !d.Dead && !d.Guardian &&
                                        d.DistanceTo(aimTarget.Center) <= stickyRadius)
                            .OrderBy(d => d.DistanceTo(aimTarget.Center)) // pozisyon kilidi: aynı mob'u izle
                            .FirstOrDefault();
                    }

                    if (sticky is not null)
                    {
                        aimTarget = sticky;                    // nişan/mesafe/overlay daima en taze gözlem
                        // 9.tur: KİMLİK + ceset-bl çapası YALNIZ kesin-aynı-iz (TrackId) eşleşmesinde ilerler.
                        // Pozisyon-fallback yakındaki BAŞKA bir mobu yakalayabilir → kimliği/çapayı ona
                        // kaydırmak kill-sonrası blacklist'i komşuya düşürüyor, MarkDead'i yanlış ize
                        // taşıyabiliyordu. Fallback'te eski kimlik/çapa korunur; mob iz değiştirdiyse yeni iz
                        // DeadInheritRadiusPx köprüsüyle yine ölü damgalanır, canlı komşu yanlışlıkla elenmez.
                        if (stuckByTrackId)
                        {
                            currentTarget       = sticky;
                            _lastEngagedCenter  = sticky.Center; // kill sonrası ceset kara listesi için taze konum
                            _lastEngagedTrackId = sticky.TrackId;
                        }
                        lastStickySeenMs = sw.ElapsedMilliseconds;   // Dev 1 — iz en son bu an görüldü
                                                                     // (fallback'te de tazelenir: LostFast
                                                                     // semantiği değişmesin — komşu görünürlüğü
                                                                     // de "iz görüldü" sayılır, eski davranış)
                    }
                    // sticky yoksa: aimTarget/currentTarget (+ _lastEngaged*) son değerinde KALIR (zıplama yok).

                    // Overlay vurgusu: nişan alınan hedef (kutuları DetectionLoop çizer).
                    _currentTargetForOverlay = aimTarget;
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
                    float dist = aimTarget.DistanceTo(charCenter); // ekran merkez mesafesi (en taze gözlem)
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
                    comboFiring    = true;
                    comboFiredOnce = true; // 14.tur (2.3): tek-vuruş kill kanıtı — gerçek ateşleme oldu
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

        return Finish(EngageResult.Timeout, "safetyMs zaman aşımı veya CT iptal");
    }
}

/// <summary>Dev 1 + V3 — angajman sonucu. Killed = pencere kayboldu VE bu angajmanda hasar-ilerleme görüldü
/// (kill sayılır, iz ölü damgalanır); Lost = pencere kayboldu ama HASAR GÖRÜLMEDİ (çalıntı/deselect/okuma
/// arızası → kill SAYILMAZ, MarkDead YOK, kısa blacklist); LostFast = HP kırmızı yok + YOLO izi kayıp
/// (uzak hedef; hızlı geçiş, kill yok); Timeout = safetyMs doldu veya CT iptal.</summary>
public enum EngageResult
{
    Killed,
    Lost,
    LostFast,
    Timeout,
}
