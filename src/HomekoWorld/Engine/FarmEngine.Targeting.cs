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
    // ── Hedef-alma YOLO-flicker toleransı (2026-07-04, 6.tur) ────────────────────────────────────────
    // Kanıt: 57dk canlı log — 2441 hedef-alma denemesinin 1541'i (%63) "tık-sonrası-kayıp", bunların
    // 1334'ü (%87) İLK offset denemesinde bitti, bunların da 1297'si (%84) `isim:iz-kayıp` — yani
    // PollHpBarAsync'teki eski 150ms erken-çıkış tetiklendi. Toplam 17.5dk (oturumun %30'u) bu 1541
    // başarısızlıkta harcandı. 480ms'lik toplam poll bütçesinin yalnız üçte biri kullanılıyordu; geri
    // kalanı hiç denenmiyordu. Eşik ikiye katlandı (320ms, hâlâ 480ms bütçe içinde), eşleşme yarıçapı
    // genişletildi (160px) — mob'un poll penceresi boyunca gerçek hareketine tolerans tanır.
    // DOKUNULMADI: satır ~296-298'deki "tık sonrası mob hâlâ orada mı" 80px kontrolü (farklı semantik —
    // tam tıklanan noktanın ıskalandığını doğrular, blacklist/escalation'ı besler).
    private const int   AcqYoloLossGraceMs = 320;  // idi: satır-içi 150
    private const float AcqMatchRadiusPx   = 160f; // idi: satır-içi 100f (TargetAsync taze-arama + PollHpBarAsync stillThere)
    // 9.tur: HP-poll bütçesi DUVAR-SAATİ oldu — eski 16-iterasyon×30ms sayacı "480ms" varsayıyordu ama her
    // iterasyondaki SENKRON GDI yakalaması (~30ms, IsTargetAliveNow içinde) fiilen ~950ms sürdürüyordu
    // (canlı log 2026-07-08: isim:hp-yok(947-1006ms) × 121). Başarılı onay ~230ms tepesinde bimodal →
    // 450ms yeterli marj bırakır, başarısız denemenin maliyeti yarıdan fazla iner. İKİ deneme de aynı
    // bütçeyi kullanır (simetrik `denemeler` telemetrisi; tek ayar düğmesi).
    private const int   AcqHpPollBudgetMs  = 450;

    // ── 14.tur (Faz 6): guardian çoklu-örnek GDI hack'i KALDIRILDI ───────────────────────────────
    // Eski 3-örnek okuma, seçim-anı geçici kırmızı vurgunun ayrı/gecikmeli GDI okumasını kirletmesine
    // karşı bir yamaydı. Faz 6 kök nedeni yapısal olarak çözdü: isim artık HP-yapısıyla AYNI DXGI
    // karesinde okunuyor (WtmVision.ScanTargetBar → TargetBarState.NameClass) ve o karede isim zaten saf
    // mor — vurgu yok. Örnekleme/gecikme gereksiz.

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

        // Adayları filtrele: (a) tracker'ın "ölü" damgaladığı izler (#3 — ceset tekrar denenmez),
        // (b) tracker'ın "guardian" damgaladığı izler (2026-07-03 — yürüyen guardian konum listesinden kaçar,
        //     iz-damga mob görünür kaldıkça kalıcı), (c) koruma mobu konum kara listesi (churn yedeği),
        // (d) pozisyon-tabanlı ölü/ceset kara listesi — HAREKETLİ aday MUAF (cesetler yürümez: kümülatif
        //     yer değiştirmesi eşik üstündeki aday kesin canlıdır; respawn/yakından-geçen mob elenmesin),
        // (e) 2026-07-04 (P2b) TrackId-bazlı tekrar-başarısızlık durağı — (d)'nin hareket-muafiyetinin
        //     ARKASINDA DEĞİL, ondan TAMAMEN bağımsız son bir şart: hareketli-canlı bir mob bile art arda N
        //     kez tıklama-sonrası başarısız olursa (kanıt: 13-19 ardışık tekrar) kısa süre aday-dışı kalır.
        var filteredCandidates = candidates.Where(d =>
            !d.Dead &&
            !d.Guardian &&
            !NearAny(d.Center, _guardianBlacklist, GuardianBlacklistRadiusPx) &&
            !(NearAny(d.Center, _deadBlacklist, DeadBlacklistRadiusPx) &&
              d.MovedPx < MobTracker.MovingAliveExemptPx) &&
            !IsTrackFailThrottled(d.TrackId)).ToList();

        // 13.tur (S3): nüfus muhasebesi — beklenen-canlı ≤ 0 iken (bölgedeki tüm normal moblar
        // kill-borçlu, respawn henüz gelmedi) ekrandaki hareketsiz adaylar büyük olasılıkla cesettir
        // → tıklama iştahı kesilir. BELİRGİN hareketli iz (kesin-canlı kanıtı) muaf: sayım yanılsa
        // bile yürüyen gerçek mob asla bastırılmaz. RegionMobCount=0 → özellik tamamen kapalı.
        int popDebt = 0, popExpected = -1;
        if (s.RegionMobCount > 0)
        {
            popDebt     = KillDebtCount(s, now);
            popExpected = s.RegionMobCount - popDebt;
            if (popExpected <= 0 && filteredCandidates.Count > 0)
            {
                int before = filteredCandidates.Count;
                filteredCandidates = filteredCandidates
                    .Where(d => d.MovedPx >= MobTracker.MovingAliveExemptPx).ToList();
                if (before > filteredCandidates.Count && now - _lastPopGateLogMs >= 1000)
                {
                    _lastPopGateLogMs = now;
                    int suppressed = before - filteredCandidates.Count;
                    Program.Log($"[Farm] nüfus-kapısı: beklenen-canlı=0 (borç={popDebt}/{s.RegionMobCount}) — " +
                                $"{suppressed} hareketsiz aday bastırıldı (hareketli muaf)");
                    _telemetryWriter?.Offer(FormattableString.Invariant(
                        $"{{\"t\":\"pop_gate\",\"ts\":{now},\"borc\":{popDebt},\"bolgeMob\":{s.RegionMobCount},\"bastirilan\":{suppressed}}}"));
                }
            }
        }

        // ── Teşhis (throttle ~1s): adaylar NEDEN elendi? (alanlar ayrık — toplamları ≈ elenen sayısı) ──
        // B-belirtisi imzası: aday>0 ama canlı=0 iken spatial-bl>0 (90px konum) veya ölü-track>0 (miras) →
        // canlı mob konumla/mirasla eleniyor. hareket-muaf>0 = muafiyet canlı mob kurtardı (fix çalışıyor).
        if (candidates.Count > 0 && now - _lastScanDiagMs >= 1000)
        {
            _lastScanDiagMs = now;
            int deadTrackN   = candidates.Count(d => d.Dead);
            int guardTrackN  = candidates.Count(d => !d.Dead && d.Guardian);
            int guardianN    = candidates.Count(d => !d.Dead && !d.Guardian &&
                                NearAny(d.Center, _guardianBlacklist, GuardianBlacklistRadiusPx));
            int spatialN     = candidates.Count(d => !d.Dead && !d.Guardian &&
                                !NearAny(d.Center, _guardianBlacklist, GuardianBlacklistRadiusPx) &&
                                NearAny(d.Center, _deadBlacklist, DeadBlacklistRadiusPx) &&
                                d.MovedPx < MobTracker.MovingAliveExemptPx);
            // 2026-07-04 (P2b): trk-throttle önce ayrılır (hareket-muaf'tan ÇIKARILIR) — "alanlar ayrık,
            // toplamı ≈ elenen" invaryantını korumak için (bir aday HEM hareket-muaf HEM trk-throttle olabilir,
            // yalnız birinde sayılmalı).
            int trkThrottleN = candidates.Count(d => !d.Dead && !d.Guardian &&
                                !NearAny(d.Center, _guardianBlacklist, GuardianBlacklistRadiusPx) &&
                                !(NearAny(d.Center, _deadBlacklist, DeadBlacklistRadiusPx) && d.MovedPx < MobTracker.MovingAliveExemptPx) &&
                                IsTrackFailThrottled(d.TrackId));
            int movedExemptN = candidates.Count(d => !d.Dead && !d.Guardian &&
                                !NearAny(d.Center, _guardianBlacklist, GuardianBlacklistRadiusPx) &&
                                NearAny(d.Center, _deadBlacklist, DeadBlacklistRadiusPx) &&
                                d.MovedPx >= MobTracker.MovingAliveExemptPx &&
                                !IsTrackFailThrottled(d.TrackId));
            Program.Log($"[Farm] tarama teshis: aday={candidates.Count} olu-track={deadTrackN} " +
                        $"guard-track={guardTrackN} spatial-bl={spatialN} hareket-muaf={movedExemptN} " +
                        $"trk-throttle={trkThrottleN} guardian={guardianN} canli={filteredCandidates.Count}" +
                        $"{(s.RegionMobCount > 0 ? $" beklenen-canli={popExpected} borc={popDebt}" : "")}");
        }

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
        _noCombatWatch.Restart();   // Dev 2 — angajman/hedef var → "dönüş" sayacını sıfırla
        _scanAttempts = 0;

        // #1: karakter daima fiziksel ekran merkezi (kalibrasyon kaldırıldı).
        var charCenter = ScreenCenter();
        float halfW = Math.Max(1f, charCenter.X); // ekran yarı genişliği (merkez X = w/2)

        var target = s.Priority switch
        {
            TargetPriorityMode.HighestPriority =>
                filteredCandidates.OrderBy(d => _mobLibrary.FindById(d.ClassId)?.Priority ?? 99).First(),
            // #2: perspektif-bilinçli yakınlık (tek tür mob) — düz merkez-mesafesi yerine derinlik puanı.
            _ =>
                filteredCandidates.OrderBy(d => TargetScore(d, charCenter.X, halfW, s)).First(),
        };

        _lastTarget = target;
        // Tanılama: seçilen hedefin iz kimliği + bu karede kaç aday "ölü" elendi (ceset filtresi çalışıyor mu?).
        Program.Log($"[Farm] Hedef: {target.ClassName} trk={target.TrackId} " +
                    $"(aday={candidates.Count}, ölü-eleme={candidates.Count(d => d.Dead)}, canlı={filteredCandidates.Count})");
        var mobInfo = _mobLibrary.FindById(target.ClassId);
        Telemetry.CurrentMob = target.ClassName;

        SetState(FarmState.Targeting, $"Hedef: {target.ClassName}");
        _switchAcqAttempts++;
        Telemetry.AcqAttempts++;
        bool targeted = await TargetAsync(target, s, ct);

        if (!targeted)
        {
            SetState(FarmState.Scanning, "Hedef alınamadı — tekrar tarıyor");
            return;
        }
        Telemetry.AcqSuccesses++;

        SetState(FarmState.Engaging, $"Angaje: {target.ClassName}");
        await EngageAsync(target, mobInfo, s, ct);
    }

    // Görünürde canlı (blacklist dışı) hedef yokken ortak davranış: idle büyüdükçe kamera-scan ya da
    // roam tetikle. Hem "hiç aday yok" hem "hepsi ceset/blacklist" (F4) buraya düşer → bot boş dönmez.
    private async Task HandleNoLiveTargetAsync(int visibleCount, FarmSettings s, CancellationToken ct)
    {
        // Hedef-geçiş telemetrisi: canlı-hedefsiz geçen ~saniye sayısı (30ms tick başına değil — şişmesin).
        long nowE = NowMs();
        if (nowE - _lastEmptyScanCountMs >= 1000) { _lastEmptyScanCountMs = nowE; _switchEmptyScans++; }
        // Dev 2 — uzun süre (ReturnHomeIdleMs) hiç angajman yoksa ve evden uzaklaşmışsak başlangıç farm
        // koordinatına dön. Kamera-scan _idleWatch'i sürekli resetlediğinden bu kontrol ONDAN ÖNCE, bağımsız
        // _noCombatWatch ile yapılır (yoksa hiç tetiklenmez). Kamera-scan (hızlı spawn) yine ilk 20sn'de çalışır.
        if (await TryReturnHomeAsync(s, ct)) return;

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

    /// <summary>Dev 2 — koşullar (özellik açık + nav/koord kalibre + ev bilgisi + ReturnHomeIdleMs boşta +
    /// evden ReturnHomeMinStrayCoords'tan uzak) sağlanıyorsa başlangıç farm koordinatına yürür. İşlendiyse true.
    /// Nav döngüsü CT'yi dinler (kill-switch/stop nav'ı keser). Kalibre değilse sessizce false → normal idle akışı.</summary>
    private async Task<bool> TryReturnHomeAsync(FarmSettings s, CancellationToken ct)
    {
        if (!s.ReturnHomeEnabled || Navigator is null || CoordReader is null || _homeCoord is null) return false;
        if (!CoordReader.IsReady) return false;
        if (_noCombatWatch.ElapsedMilliseconds < s.ReturnHomeIdleMs) return false;

        var cur = CoordReader.Read();
        if (cur is null)
        {
            // Koordinat şu an okunamadı (hane-düşmesi vs.) — döngüyü boşa döndürme; kısa sonra tekrar dener.
            _noCombatWatch.Restart();
            return false;
        }

        var (hx, hy) = _homeCoord.Value;
        double dx = cur.Value.x - hx, dy = cur.Value.y - hy;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist <= s.ReturnHomeMinStrayCoords)
        {
            // Zaten ev civarındayız → dönüş gereksiz; sayacı sıfırla (tekrar ReturnHomeIdleMs say).
            _noCombatWatch.Restart();
            return false;   // kamera-scan/roam normal aksın
        }

        SetState(FarmState.ReturningHome,
            $"Spot boş {s.ReturnHomeIdleMs / 1000}sn — farm lokasyonuna dönülüyor ({hx},{hy})…");
        _combo.CancelAll();
        try
        {
            await Navigator.NavigateToAsync(hx, hy, ct);
            StatusChanged?.Invoke(this, "Farm lokasyonuna dönüldü — taranıyor…");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Program.Log($"[Farm] dönüş hatası: {ex.Message}");
        }
        // 9.tur: nav D-rotasyonu/yürüyüşü sahneyi kaydırdı (hata yolunda da kısmen dönmüş olabilir) —
        // tarama dönmeden hareket kredisi kesilsin (bkz FarmEngine.CameraFlipSuppressMs).
        _tracker.SuppressMotionCredit(NowMs() + CameraFlipSuppressMs);
        SetState(FarmState.Scanning, "Taranıyor…");
        _idleWatch.Restart();
        _noCombatWatch.Restart();
        _scanAttempts = 0;
        return true;
    }

    // Perspektif-bilinçli hedef puanı (TEK TÜR mob): küçük = daha yakın/tercih edilir.
    // Yere paralel kamerada ekran-merkezi mesafesi 3D mesafeyle örtüşmez (kenarda görünen mob 3D'de daha
    // yakın olabilir). Derinlik vekili bbox YÜKSEKLİĞİ (büyük=yakın; genişlikten kararlı). 1/yükseklik hem
    // derinliği hem (yatay ofset × derinlik) cezasını ölçekler → büyük-kutu kenar mob düşük puan alır (seçilir).
    private static float TargetScore(Detection d, float screenCx, float halfW, FarmSettings s)
    {
        float height  = Math.Max(1f, d.BBox.Height);
        float lateral = Math.Min(1f, Math.Abs(d.Center.X - screenCx) / halfW);
        return (s.TargetDepthWeight + s.TargetCenterWeight * lateral) / height;
    }

    // ── Targeting — moba tıkla, HP bar bekle ─────────────────────────────────

    private async Task<bool> TargetAsync(
        Detection target, FarmSettings s, CancellationToken ct)
    {
        bool hpBarCalibrated   = _appState.Wtm.IsHpBarLocated;
        // Acquisition telemetrisi (2026-07-03): TÜM çıkış yolları tek özet satırla loglanır — canlı logda
        // "Hedef: trk=X" sonrası angajmansız sessiz dönüşler (14×) geçiş-yavaşlığının en büyük kaynağıydı
        // ve HİÇBİRİ loglu değildi. `denemeler` listesi offset-başına sonucu + süreyi taşır → bimodal
        // (~230ms / ~1250ms) alma süresinin hangi offsetten geldiği artık ölçülür.
        var acqSw    = System.Diagnostics.Stopwatch.StartNew();
        var attempts = new List<string>(2);

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

            // 9.tur: MERKEZ-ÖNCE — canlı log (2026-07-08): isim-önce ilk deneme 121× "hp-yok" ile ~950ms/adet
            // yaktı (farm süresinin ~%20'si); başarılı alımların ezici kısmı merkez tıkından geldi.
            // İsim/keypoint noktası fallback'e indi.
            (float dx, float dy)[] yoloOffsets =
            [
                (0f,      0f),       // 1. deneme: merkez
                (firstDx, firstDy),  // 2. deneme: keypoint veya isim-offset (fallback)
            ];

            // V3: HP-poll'lerden herhangi biri YOLO-iz-kaybı erken-çıkışıyla bittiyse "kesin ceset" kanıtı yok.
            bool anyYoloLost = false;
            // 2026-07-04 (P2a): döngü-sonrası blacklist siteleri (iz-titremesi/ceset-varsayımı) için EN SON
            // gerçekten tıklanan konum — `liveTarget` döngü-lokal, döngü dışında scope'ta değil. Bayat
            // `target.Center` yerine bunu kullanmak, hareketli mob için ardışık denemelerin AYNI escalation
            // noktasına düşmesini sağlar (kanıt: trk=215 gibi sürüklenen mob'larda target.Center tık-anından
            // tık-anına 300+px kayabiliyordu, escalation'ı (5.tur) etkisiz kılıyordu).
            PointF lastLiveCenter = target.Center;
            // 9.tur: damga/sayaç kimliği — TIKLANAN izin TrackId'si (bayat scan-anı target.TrackId DEĞİL).
            // liveTarget döngü-lokal; döngü-sonrası MarkDead/RecordAcqFailure bunun üstünden yazar. Eski
            // davranışta ceset damgası hiç tıklanmamış ize gidebiliyor, tıklanan iz "canlı aday" kalıp
            // tekrar tekrar seçiliyordu (başarı krediti zaten liveTarget.TrackId'ye gidiyordu — asimetri).
            int lastLiveTrackId = target.TrackId;

            // 13.tur (S5): kamera-hareket kapısı — TargetAsync-geneli TEK bekleme bütçesi (2 deneme
            // ayrı ayrı MaxWait'e şişmesin) + son akış ölçümü tüm loglara yazılır (eşik kalibrasyonu).
            long  gateWaitedMs = 0;
            float gateFlow     = -1f;
            bool  gateCounted  = false;

            // 14.tur (Faz 4.3): acq_attempt JSONL olayı — TargetAsync'in HER çıkış noktasında bir kez
            // çağrılır (kesit: karar→mouse-down→HP-onay). Ayrık sayaç ayrımının ham veri kaynağı.
            void EmitAcq(string result, string reason)
            {
                var writer = _telemetryWriter;
                if (writer is null) return;
                // FormattableString.Invariant yalnız TEK bir interpolated-string literalını kabul eder
                // ('+' ile birleştirilmiş parçalar derleme-zamanında düz string'e indirgenir) — tek satır.
                writer.Offer(FormattableString.Invariant(
                    $"{{\"t\":\"acq_attempt\",\"ts\":{NowMs()},\"trk\":{lastLiveTrackId},\"result\":\"{result}\",\"reason\":\"{reason}\",\"durMs\":{acqSw.ElapsedMilliseconds},\"kaymaPxMs\":{gateFlow:F2},\"kapiMs\":{gateWaitedMs},\"frameAgeMs\":{Telemetry.FrameAgeAtClickMs}}}"));
            }

            for (int i = 0; i < yoloOffsets.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                string offName  = i == 0 ? "merkez" : "isim";
                long   attStart = acqSw.ElapsedMilliseconds;

                // 13.tur (S5): tık-öncesi kamera-hareket kapısı. Veri (2026-07-11 12:52 log+replay):
                // başarılı tıklarda tık-anı sahne-akışı ~0.1 px/ms, kayıplarda 0.4-2.5 px/ms (kayıpların
                // %74'ü) — flip animasyonu / karakter hareketi sürerken atılan tık ıskalıyor, iz kopuyor,
                // blacklist YANLIŞ ekran-noktasına yazılıyordu. Sahne akarken (veya quiet penceresi
                // aktifken — flip komutu az önce gitti, animasyon karelere henüz yansımamış olabilir)
                // tıklama ERTELENİR; bütçe dolarsa hedeften vazgeçilir (bl YOK, başarısızlık sayacı YOK —
                // tıklama-ÖNCESİ çıkış, "taze-karede-yok" ile aynı sınıf). Ölçüm kapı kapalıyken de alınır
                // (kayma= telemetrisi eşik kalibrasyonuna veri üretsin).
                var flow = _tracker.MotionState(NowMs());
                gateFlow = flow.pxPerMs;
                if (s.MotionGatePxPerMs > 0f)
                {
                    while (flow.quiet || (flow.known && flow.pxPerMs > s.MotionGatePxPerMs))
                    {
                        if (gateWaitedMs >= s.MotionGateMaxWaitMs)
                        {
                            _gateGiveUps++;
                            Telemetry.GateGiveUps++;         // 14.tur (Faz 4.1)
                            Telemetry.NoClickFreshMiss++;    // tıklama hiç atılmadı
                            StatusChanged?.Invoke(this, "Kamera hareketli — tıklama ertelendi, yeniden taranıyor");
                            Program.Log($"[Farm] hedef-bırakıldı trk={lastLiveTrackId}: kamera-akış " +
                                        $"(kayma={gateFlow:0.00}px/ms{(flow.quiet ? "+quiet" : "")}, " +
                                        $"bekleme={gateWaitedMs}ms) — bl YOK");
                            EmitAcq("gate_giveup", "kamera-akis");
                            return false;
                        }
                        if (!gateCounted)
                        {
                            gateCounted = true; _gateDeferrals++;
                            // 14.tur (Faz 4.3): gate_defer — bu denemede EN AZ bir kez ertelendi (spam
                            // olmasın diye yalnız İLK ertelemede, poll döngüsünün her turunda değil).
                            _telemetryWriter?.Offer(FormattableString.Invariant(
                                $"{{\"t\":\"gate_defer\",\"ts\":{NowMs()},\"trk\":{lastLiveTrackId},\"kaymaPxMs\":{gateFlow:F2}}}"));
                        }
                        long w0 = NowMs();
                        await Task.Delay(15, ct);
                        long wd = Math.Max(1, NowMs() - w0);
                        gateWaitedMs         += wd;
                        _gateDeferredMsTotal += wd;
                        flow     = _tracker.MotionState(NowMs());
                        gateFlow = flow.pxPerMs;
                    }
                }

                // KRİTİK: tıklamadan HEMEN ÖNCE hedefin EN TAZE tespit konumunu al. Mob/kamera hareketli
                // olduğundan ScanningTick'te seçilen konum birkaç on-ms'de bayatlar → bayat yere tık = ıska
                // ("kutuya tıkladık, mob orada yok"). Mob bu an TAZE karede YOKSA (flicker/kayıp) hiç tıklama
                // → taramaya dön (asla bayat/hayalet konuma tıklanmaz).
                var clickSnap = _latestDetections;
                // 9.tur: önce AYNI İZ (en sağlam — scan→tık arası on-ms'lerde iz ışınlanamaz, yarıçap şartsız),
                // yoksa tür+mesafe fallback; İKİSİ DE ölü/guardian DIŞI. Eski seçim yalnız tür+mesafeydi:
                // tıklanan taze aday aynı türden yakın CESET/GUARDIAN olabiliyordu (!Dead/!Guardian filtreleri
                // yalnız ScanningTick'te, orijinal target üstünde uygulanıyordu). Mesafe çapası da bayat
                // target.Center yerine son tıklanan konum (lastLiveCenter) — hareketli mobda 2. deneme
                // gerçekten tıklanan yerin çevresinde arar.
                Detection? liveTarget = null;
                if (clickSnap is not null)
                {
                    liveTarget = clickSnap.Dets.FirstOrDefault(d =>
                        d.TrackId == lastLiveTrackId && !d.Dead && !d.Guardian);
                    liveTarget ??= clickSnap.Dets
                        .Where(d => d.ClassId == target.ClassId && !d.Dead && !d.Guardian &&
                                    d.DistanceTo(lastLiveCenter) < AcqMatchRadiusPx)
                        .OrderBy(d => d.DistanceTo(lastLiveCenter))
                        .FirstOrDefault();
                }
                if (liveTarget is null)
                {
                    Telemetry.NoClickFreshMiss++;   // 14.tur (Faz 4.1): tıklama hiç atılmadı
                    StatusChanged?.Invoke(this, "Hedef taze karede yok — tıklama atlandı, yeniden tarıyor");
                    Program.Log($"[Farm] hedef-bırakıldı trk={target.TrackId}: taze-karede-yok " +
                                $"(deneme {i + 1}/2, {acqSw.ElapsedMilliseconds}ms, kayma={gateFlow:0.00}px/ms) — bl YOK");
                    EmitAcq("no_click_fresh_miss", "taze-karede-yok");
                    return false;
                }
                lastLiveCenter  = liveTarget.Center;
                lastLiveTrackId = liveTarget.TrackId;
                // Faz B: tıklanacak tespitin yaşı (capture→şimdi) — objektif "bayat kutu" ölçümü.
                if (clickSnap is not null) Telemetry.FrameAgeAtClickMs = (int)(NowMs() - clickSnap.CapturedAtMs);

                // Offset deltası ScanningTick hedefinden; konum TAZE tespitten → güncel yere tıkla.
                int cx = (int)(liveTarget.Center.X + yoloOffsets[i].dx);
                int cy = (int)(liveTarget.Center.Y + yoloOffsets[i].dy);

                // Tıklamadan önce karakter bölgesi örneği (motion detection — küçük bölge yakalama).
                byte[] preCrop = SampleCharRegionDirect();

                await _router.MoveAbsAsync(cx, cy, ct);
                await Task.Delay(s.ClickPreDelayMs, ct);
                // 14.tur (Faz 6): freshness çapası — tıklamadan HEMEN önce alınır. WaitForSelectedTargetAsync
                // yalnız CapturedAtMs >= bu olan kareyi "yeni seçim" sayar (tıklamadan önceki eski hedef
                // penceresi yeni seçim sanılmasın).
                long clickIssuedAt = NowMs();
                await _router.ClickAsync(MouseButton.Left, ct);
                Telemetry.ClicksIssued++;   // 14.tur (Faz 4.1): gerçekten gönderilen tıklama
                await Task.Delay(s.ClickPostDelayMs, ct);

                // 14.tur (Faz 6): seçim OTORİTESİ = çerçeve yapısı. Tıklamadan SONRAKİ ilk yapı-teyitli DXGI
                // karesini bekle; o karenin SAME-FRAME isim sınıfını (guardian mı) taşıyan TargetBarState'ini al.
                // null = seçilemedi (template yok / tık ıskaladı) → guardian HÜKMÜ YOK, aşağıdaki ıska yollarına düş.
                var sel = hpBarCalibrated
                    ? await WaitForSelectedTargetAsync(clickIssuedAt, liveTarget, ct)
                    : (selected: (TargetBarState?)null, yoloLost: false);
                if (sel.selected is { } selUi)
                {
                    // 2026-07-04 (P2b): seçim onaylandı → bu TrackId'nin ardışık-başarısızlık serisi sıfırlanır
                    // (guardian sonucundan BAĞIMSIZ — seçim/HP-tespiti burada zaten BAŞARILI; guardian reddi ayrı).
                    RecordAcqSuccess(liveTarget.TrackId);
                    // 14.tur (Faz 4.1): "tıklama isabet etti mi" saf cevabı — guardian kararından BAĞIMSIZ.
                    Telemetry.ClicksConfirmed++;
                    attempts.Add($"{offName}:seçili({acqSw.ElapsedMilliseconds - attStart}ms)");
                    bool ok = await CheckGuardianAndReturnAsync(liveTarget, selUi, ct);
                    if (ok)
                    {
                        Program.Log($"[Farm] hedef-alındı trk={liveTarget.TrackId} offset={offName}({i + 1}/2) " +
                                    $"süre={acqSw.ElapsedMilliseconds}ms kare-yaşı={Telemetry.FrameAgeAtClickMs}ms " +
                                    $"kayma={gateFlow:0.00}px/ms" +
                                    $"{(gateWaitedMs > 0 ? $" kapı-bekleme={gateWaitedMs}ms" : "")} " +
                                    $"denemeler=[{string.Join(",", attempts)}]");
                        EmitAcq("confirmed", offName);
                    }
                    else
                    {
                        Program.Log($"[Farm] hedef-bırakıldı trk={liveTarget.TrackId}: guardian-kapısı " +
                                    $"(süre={acqSw.ElapsedMilliseconds}ms, kayma={gateFlow:0.00}px/ms)");
                        EmitAcq("guardian_block", "guardian-kapisi");
                    }
                    return ok;
                }
                if (sel.yoloLost) anyYoloLost = true;
                attempts.Add($"{offName}:{(sel.yoloLost ? "iz-kayıp" : "seçilemedi")}({acqSw.ElapsedMilliseconds - attStart}ms)");

                // HP bar yok — karakter hareket ettiyse auto-walk'ı iptal et
                byte[] postCrop = SampleCharRegionDirect();
                if (IsCharacterMoving(preCrop, postCrop))
                    await CancelClickMovementAsync(ct);

                // Mob hâlâ sahnede mi? — tıkladığımız TAZE konuma göre kontrol et.
                // 14.tur (2.2 — dış denetim bulgusu): KİMLİK-önce + ölü/guardian DIŞI. Eski hâli yalnız
                // tür+80px idi: komşu CESET/guardian "mob hâlâ orada" dedirtip ikinci denemeyi ve sonunda
                // ceset-varsayımı yolunu (tıklanan CANLI ize yanlış MarkDead dahil) besleyebiliyordu.
                var snap = _latestDetections;
                bool mobStillThere = snap is not null && snap.Dets.Any(d =>
                    !d.Dead && !d.Guardian &&
                    (d.TrackId == liveTarget.TrackId ||
                     (d.ClassId == target.ClassId && d.DistanceTo(liveTarget.Center) < 80f)));

                if (!mobStillThere)
                {
                    // Tık sonrası tespit o noktada yok → mob kaymış olabilir VEYA tıklanamayan statik
                    // false-positive (log'da aynı (686,546)'ya conf 0.94 ile SONSUZ re-pick görüldü).
                    // 2026-07-04: kısa sabit blacklist yerine ESCALATION — aynı nokta tekrar tekrar başarısız
                    // olursa (kronik ağaç/ceset) süre 2.5s→45s→90s katlanır → birkaç denemede susar. Kaymış
                    // canlı mob ilk düşüşte 2.5s alır (hareketle zaten filtre-muaf → yeni konumunda hemen geri alınır).
                    // 2026-07-04 (P2a): escalation/blacklist noktası artık GERÇEKTEN tıklanan taze konum
                    // (liveTarget.Center) — eskiden bayat target.Center kullanılıyordu, hareketli mob için
                    // ardışık denemeler farklı noktalara düşüp escalation'ı atlıyordu.
                    long missBlMs = EscalatedBlacklistMs(liveTarget.Center, MissReselectSkipMs, out int missRep);
                    _deadBlacklist.Add((liveTarget.Center, NowMs() + missBlMs));
                    RecordAcqFailure(liveTarget.TrackId);   // P2b sayacı — 9.tur: TIKLANAN ize yazılır (bayat scan-id değil)
                    Program.Log($"[Farm] hedef-bırakıldı trk={liveTarget.TrackId}: tık-sonrası-kayıp " +
                                $"@({(int)liveTarget.Center.X},{(int)liveTarget.Center.Y}) " +
                                $"(denemeler=[{string.Join(",", attempts)}], {acqSw.ElapsedMilliseconds}ms, " +
                                $"kayma={gateFlow:0.00}px/ms) — " +
                                $"bl {missBlMs}ms{(missRep > 1 ? $" (nokta tekrar #{missRep} → escalation)" : "")}");
                    EmitAcq("post_click_lost", "tik-sonrasi-kayip");
                    return false;
                }

                StatusChanged?.Invoke(this,
                    $"Tıklama ıskandı ({(i == 0 ? "merkez" : "isim")}) — tekrar…");
            }

            // Tüm tıklamalar HP bar üretmedi ama mob hâlâ sahnede (yukarıda erken dönülmedi) →
            // büyük olasılıkla ölü ceset / seçilemeyen hedef. Kısa süre kara listeye al ki
            // bir sonraki tarama EN YAKIN DİĞER mob'u seçsin (ölüye tıklama döngüsünü kırar).
            if (hpBarCalibrated)
            {
                if (anyYoloLost)
                {
                    // V3 (2026-07-02): "HP yok" kararlarından biri YOLO-iz-kaybı ERKEN-ÇIKIŞINDAN geldi —
                    // tam 480ms pencere izlenmedi, bu KESİN ceset kanıtı DEĞİL. Yoğun sürüde iz titremesi
                    // CANLI mobu bu yola sokup 2×blacklist + MarkDead ile ceset damgalatıyordu (18:47 log:
                    // miras-DEAD "B-belirtisi" zinciri → canlı mob atlama). Yalnız KISA atla; iz damgalama YOK.
                    _deadBlacklist.Add((lastLiveCenter, NowMs() + MissReselectSkipMs));
                    Telemetry.HpValidationFailed++;   // 14.tur (Faz 4.1): tık gitti, doğrulama belirsiz kaldı
                    StatusChanged?.Invoke(this, "Hedef doğrulanamadı (iz kayboldu) — kısa atlanıyor");
                    Program.Log($"[Farm] hedef-bırakıldı trk={lastLiveTrackId}: iz-titremesi " +
                                $"(denemeler=[{string.Join(",", attempts)}], {acqSw.ElapsedMilliseconds}ms, " +
                                $"kayma={gateFlow:0.00}px/ms) — " +
                                $"bl {MissReselectSkipMs}ms, iz-damga YOK");
                    EmitAcq("hp_validation_failed", "iz-titremesi");
                }
                else
                {
                    // F2: 2 tık da TAM pencere boyunca HP üretmedi → büyük olasılıkla ceset/seçilemez.
                    // 2026-07-03: süre 2× → 1× (taban zaten 20sn'ye çıktı; 40sn×90px kör bölgeler kill
                    // sonrası canli=0 boş taramaları besliyordu) + iz-damga VARSAYIM kaynaklı (salt teşhis;
                    // 7.tur: damga KALICI — canlı mob yanlış damgalanırsa kamera-flip churn'ü telafi eder).
                    // 2026-07-04: ceset blacklist'i de ESCALATION'lı — aynı ceset/false-positive tekrar tekrar
                    // ceset-varsayımına düşerse süre 20s→45s→90s katlanır (kronik nokta uzun susar).
                    long corpseBlMs   = EscalatedBlacklistMs(lastLiveCenter, s.DeadBlacklistMs, out int corpseRep);
                    long corpseExpire = NowMs() + corpseBlMs;
                    _deadBlacklist.Add((lastLiveCenter, corpseExpire));
                    // Düşen ceset kutusunu da kapsa + izi "ölü" damgala (#3): bu track ve yakın doğan ceset
                    // track'leri (MobTracker.DeadInheritRadiusPx mirası) bir daha aday olmaz.
                    _deadBlacklist.Add((new PointF(lastLiveCenter.X, lastLiveCenter.Y + CorpseFallOffsetPx), corpseExpire));
                    // 9.tur: damga TIKLANAN ize (lastLiveTrackId) — eski target.TrackId, iz churn'ünde hiç
                    // tıklanmamış izi damgalayıp gerçek cesedi "canlı aday" bırakıyordu.
                    _tracker.MarkDead(lastLiveTrackId, MobTracker.DeadSource.Assumed);
                    Telemetry.CorpseAssumptions++;   // 14.tur (Faz 4.1)
                    StatusChanged?.Invoke(this, "Hedef seçilemedi (ölü/ceset?) — atlanıyor");
                    Program.Log($"[Farm] hedef-bırakıldı trk={lastLiveTrackId}: ceset-varsayımı (2 tık HP üretmedi, " +
                                $"denemeler=[{string.Join(",", attempts)}], {acqSw.ElapsedMilliseconds}ms, " +
                                $"kayma={gateFlow:0.00}px/ms) — " +
                                $"bl {corpseBlMs}ms ×2nokta + iz-damga(varsayım)" +
                                $"{(corpseRep > 1 ? $" (nokta tekrar #{corpseRep} → escalation)" : "")}");
                    EmitAcq("corpse_assumed", "ceset-varsayimi");
                }
            }
            else
            {
                Telemetry.HpValidationFailed++;   // 14.tur (Faz 4.1): kalibrasyonsuz — doğrulama imkansız
                StatusChanged?.Invoke(this, "⚠ HP bar kalibrasyonu yok — hedef doğrulanamadı");
                Program.Log($"[Farm] hedef-bırakıldı trk={lastLiveTrackId}: HP-bar-kalibrasyonsuz " +
                            $"(denemeler=[{string.Join(",", attempts)}], {acqSw.ElapsedMilliseconds}ms)");
                EmitAcq("no_hp_calibration", "hp-bar-kalibrasyonsuz");
            }
            // 2026-07-04 (P2b): iz-titremesi/ceset-varsayımı/HP-bar-kalibrasyonsuz — üçü de tıklama-SONRASI
            // başarısızlık (taze-karede-yok HARİÇ — o tıklama ÖNCESİ, ayrı bir yol, sayaca dahil değil).
            RecordAcqFailure(lastLiveTrackId);   // 9.tur: tıklanan ize (bayat scan-id değil)
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
            long clickIssuedAt = NowMs();
            await _router.ClickAsync(MouseButton.Left, ct);
            await Task.Delay(200, ct);

            var sel = hpBarCalibrated
                ? await WaitForSelectedTargetAsync(clickIssuedAt, target, ct)
                : (selected: (TargetBarState?)null, yoloLost: false);
            if (sel.selected is { } selUi)
                return await CheckGuardianAndReturnAsync(target, selUi, ct);

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

    /// <summary>14.tur (Faz 6): SAF guardian kararı. Ekran görüntüsü YOK, GDI YOK, offset arama YOK — isim
    /// sınıfı, çağıranın verdiği <paramref name="ui"/>.NameClass'tan okunur (HP-yapısıyla AYNI DXGI karesinde,
    /// yapının offset'inde WtmVision.ScanTargetBar tarafından hesaplanmıştı). Bu, iki kök nedeni de kapatır:
    /// offset artık yapıdan (duyuru-şeridi offset=0 imkânsız) ve isim seçim-anı geçici kırmızı vurgunun
    /// olmadığı DXGI karesinden okunur.
    /// Guardian HÜKMÜ yalnız REF-HUE (kalibre normal+guardian renkleri) ile verilir — bu oyunda seçili başlık
    /// geçici kırmızı olabildiğinden sabit-kırmızı fallback güvenilmez; fallback'te ASLA hüküm verilmez
    /// (saldır), yalnız loglanır. Koruma ise: iz-damga (MarkGuardian) + konum-bl + W-tap ile hedefi bırak →
    /// false. Aksi (Normal/Unknown, veya fallback-guardian) → true (saldır). Belirsizlikte RecordAcqFailure
    /// YOK — seçim/HP zaten onaylandı (GPT #4 çelişkisi giderildi).</summary>
    private async Task<bool> CheckGuardianAndReturnAsync(Detection target, TargetBarState ui, CancellationToken ct)
    {
        if (!_appState.Wtm.GuardianDetectionEnabled) return true;

        string gDiag = $"piksel={ui.NameTextPx} oy={ui.NameGuardianVotes} " +
                       $"mod={(ui.NameUsedRefHue ? "ref-hue" : "fallback-kırmızı")} sınıf={ui.NameClass} offset={ui.OffsetY} " +
                       $"topHue={(ui.NameDomHue < 0 ? "yok" : $"{ui.NameDomHue:0}°(%{ui.NameDomFrac * 100:0})")}";

        // Guardian HÜKMÜ: yalnız ref-hue (kalibre iki isim rengi) ayırt ettiyse. Same-frame okuma sayesinde
        // artık yapı-teyit kapısına gerek yok — isim, yapının AÇIK olduğu (StructurePresent) karede,
        // yapının offset'inde okunmuştur (WaitForSelectedTargetAsync bunu garanti eder).
        if (ui.NameClass == WtmVision.NameplateClass.Guardian && ui.NameUsedRefHue)
        {
            // 2026-07-03: verdict İZE de damgalanır (MarkGuardian) — yürüyen guardian'ı yalnız-konumsal
            // blacklist 60px dışında kaçırıp aynı izi 4× kontrol ettiriyordu (canlı log trk=98). İz-damga
            // mob ekranda kaldıkça kalıcı; konumsal liste churn yedeği.
            Program.Log($"[Farm] Guardian kontrol: sonuç=Guardian {gDiag} → iz-damga trk={target.TrackId} " +
                        $"+ konum-bl {GuardianBlacklistDurationMs / 1000}sn@({(int)target.Center.X},{(int)target.Center.Y})");
            _switchGuardianBlocks++;
            Telemetry.GuardianBlocks++;
            _tracker.MarkGuardian(target.TrackId);
            StatusChanged?.Invoke(this, "Koruma mobu tespit edildi — atlanıyor");
            await CancelClickMovementAsync(ct);
            _guardianBlacklist.Add((target.Center, NowMs() + GuardianBlacklistDurationMs));
            return false;
        }

        // Guardian görünse bile ref-hue yoksa (kalibre renk yok → fallback-kırmızı modu): HÜKÜM YOK, saldır.
        // Sabit-kırmızı bu oyunda seçim vurgusuyla karışır; yanlış-skip riski gerçek-guardian-kaçırmadan pahalı.
        if (ui.NameClass == WtmVision.NameplateClass.Guardian)
            Program.Log($"[Farm] Guardian kontrol: sonuç=Guardian-fallback (kalibrasyonsuz → HÜKÜM YOK, saldırılıyor) {gDiag}");
        else
            Program.Log($"[Farm] Guardian kontrol: sonuç={ui.NameClass} {gDiag}");
        return true;
    }

    // ── Seçim onayı (tıklama sonrası) — 14.tur (Faz 6) ───────────────────────────────────────────
    // Eski PollHpBarAsync, seçimi RENK-alive (IsTargetAliveNow: GDI+DXGI renk) ile onaylar, guardian
    // offset'ini ondan taşırdı. Bu, iki guardian kök nedenini besliyordu: (a) renk offset=0 duyuru-şeridini
    // seçebiliyordu, (b) canlılık ile isim okuması AYRI karelerdi. Faz 6: seçim OTORİTESİ = çerçeve YAPISI;
    // isim guardian sınıfı o yapıyla AYNI DXGI karesinden (TargetBarState.NameClass) gelir.
    /// <summary>Tıklamadan SONRA üretilmiş İLK yapı-teyitli (çerçeve AÇIK) DXGI karesini bekler; o karenin
    /// SAME-FRAME isim sınıfını taşıyan TargetBarState'ini döner. Çerçeve şablonu kalibre DEĞİLSE (deployment
    /// fallback) eski renk-alive yoluna düşer (guardian PASİF, NameClass=Unknown — bkz StartFarm uyarısı).
    /// <paramref name="clickIssuedAtMs"/>: freshness çapası — CapturedAtMs bundan küçük (tıklama-öncesi)
    /// kareler ELENIR ki eski hedef penceresi yeni seçim sanılmasın. Döner (selected, yoloLost): selected
    /// null + yoloLost=true → bekleme sırasında iz despawn/çalıntı (erken bırak; KESİN ceset kanıtı DEĞİL).
    /// selected null + yoloLost=false → bütçe doldu, yapı hiç teyit edilmedi (tık ıskaladı / seçilemedi).</summary>
    private async Task<(TargetBarState? selected, bool yoloLost)> WaitForSelectedTargetAsync(
        long clickIssuedAtMs, Detection target, CancellationToken ct)
    {
        bool frameCalibrated = _appState.Wtm.IsTargetFrameCalibrated;
        long lostSinceMs = -1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < AcqHpPollBudgetMs)
        {
            ct.ThrowIfCancellationRequested();
            var snap = _latestDetections;

            if (frameCalibrated)
            {
                // Seçim otoritesi = YAPI: tıklama-SONRASI taze kare (CapturedAtMs >= clickIssuedAtMs) +
                // çerçeve AÇIK (StructurePresent). İsim sınıfı bu kareyle AYNI (offset yapıdan, isim saf-DXGI).
                if (snap?.Bar is { } bar && snap.CapturedAtMs >= clickIssuedAtMs &&
                    bar.StructureKnown && bar.StructurePresent)
                    return (bar, false);
            }
            else if (IsTargetAliveNow(out int offY))
            {
                // Deployment fallback: çerçeve şablonu yok → eski renk-alive. Guardian PASİF (NameClass=Unknown
                // → CheckGuardian saldırır). Kalibre müşteride bu yola HİÇ düşülmez (StartFarm uyarısı).
                var synth = new TargetBarState(false, true, -2f, offY, 0, 0, 0, 0f, 0f, true, NowMs());
                return (synth, false);
            }

            // Dev 1 (V3): bekleme sırasında hedef izi merkez yakınında AcqYoloLossGraceMs kaybolursa erken bırak
            // (steal/despawn). KİMLİK-önce + ölü/guardian DIŞI (yoğun sürüde komşu ceset erken-çıkışı bastırmasın).
            if (Inferrer is not null)
            {
                bool stillThere = snap is not null && snap.Dets.Any(d =>
                    !d.Dead && !d.Guardian &&
                    (d.TrackId == target.TrackId ||
                     (d.ClassId == target.ClassId && d.DistanceTo(target.Center) < AcqMatchRadiusPx)));
                if (stillThere) lostSinceMs = -1;
                else
                {
                    long now = NowMs();
                    if (lostSinceMs < 0) lostSinceMs = now;
                    else if (now - lostSinceMs >= AcqYoloLossGraceMs) return (null, true);   // YOLO kaybı → erken bırak
                }
            }
            await Task.Delay(10, ct);
        }
        return (null, false);
    }

    /// <summary>Hedef seçili/canlı mı — GDI canlı (gecikmesiz) VEYA DXGI snapshot (güvenilir/gecikmeli); biri yeterli.
    /// barOffsetY: BU kararla eşleşen nameplate offset'i (V4: yapı bulunduysa yapıdan, değilse renk taramasından —
    /// atomik, aynı kareden). GDI dalı hız için renk-yalnız kalır (yalnız POZİTİF kabul; yapı kontrolü GDI'de yok,
    /// ama DXGI dalı zaten ~30ms'de bir tazelenip yapıyla teyit ediyor — GDI'nin nadir sahte-pozitifi engage'in
    /// ilk 30ms'sinde kendini düzeltir). KÖK NEDEN (2026-07-01): DXGI dalı eskiden racy global okurdu; artık
    /// snap.Bar ile atomik eşleşme, duyuru açılır/kapanır anında da doğru.</summary>
    private bool IsTargetAliveNow(out int barOffsetY)
    {
        // V4 review-fix: offset AYNI çağrıdan (out-param) — eskiden bool dönüşünden SONRA ayrı bir
        // satırda global WtmVision.LastBarOffsetY okunuyordu; araya sürekli çalışan tespit thread'i
        // girip başka bir taramanın offset'ini yazabiliyordu (guardian yanlış konumda arandı).
        if (WtmVision.IsTargetAliveSmoothed(_appState.Wtm, out int gdiOffsetY))         // GDI: gecikmesiz, senkron taze
        {
            barOffsetY = gdiOffsetY;
            // 8.tur (2026-07-08): yapı (V4) offset-OTORİTESİ — taze snapshot'ta çerçeve şablonu
            // bulunduysa nameplate offset'ini ondan al: ScanTargetBar iki konumu max(s0,s1) NCC ile
            // kıyaslar, renk kapısından çok daha güvenilir konum kanıtıdır. Canlılık kararı GDI'de
            // kalır (gecikmesiz); yalnız guardian/nameplate'in bakacağı konum otoritesi değişir.
            var structSnap = _latestDetections;
            if (structSnap?.Bar is { } stb && NowMs() - structSnap.PublishedAtMs < 300 &&
                stb.StructureKnown && stb.StructurePresent)
                barOffsetY = stb.OffsetY;
            return true;
        }
        var snap = _latestDetections;                                                  // DXGI: GDI siyah dönerse
        if (snap?.Bar is { } tb && NowMs() - snap.PublishedAtMs < 300)
        {
            // V4: yapı öğretilmişse TEK otorite (StructurePresent); değilse eski renk kararı (RedAlive).
            bool alive = tb.StructureKnown ? tb.StructurePresent : tb.RedAlive;
            if (alive)
            {
                barOffsetY = tb.OffsetY;
                return true;
            }
        }
        barOffsetY = 0;
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
