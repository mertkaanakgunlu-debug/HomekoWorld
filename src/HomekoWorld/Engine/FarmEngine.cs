using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using HomekoWorld.Hardware;
using HomekoWorld.Hooks;
using HomekoWorld.Models;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services;
using HomekoWorld.Services.Autonomous;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Farm;
using HomekoWorld.Services.Telemetry;
using HomekoWorld.Services.Vision;
using HomekoWorld.Services.Yolo;

namespace HomekoWorld.Engine;

/// <summary>
/// Faz 17 — Oto Farm state machine.
/// YOLO tespiti → hedef kilitleme → takip + kombo (TrackAndCombatAsync) →
/// loot → roam → tekrar.
/// </summary>
public sealed partial class FarmEngine
{
    // ── Bağımlılıklar ──────────────────────────────────────────────────────────
    private readonly TransportRouter      _router;
    private readonly ComboEngine          _combo;
    private readonly GlobalKeyboardHook   _keyHook;
    private readonly GlobalMouseHook      _mouseHook;
    private readonly MobLibrary           _mobLibrary;
    private          AppState             _appState;

    // ── Durum ─────────────────────────────────────────────────────────────────
    private FarmState _state = FarmState.Idle;
    private int _stopInProgress; // 9.tur: Stop tek-atım kilidi — Idle-guard tek başına yarışa açıktı
    private CancellationTokenSource? _cts;
    private Detection? _lastTarget;
    private int        _wayIndex;
    private int        _cameraRotateDir = 1; // +1 = D, -1 = A; her başarısız targeting'de değişir
    private readonly System.Diagnostics.Stopwatch _idleWatch = new();
    // Oturum-özeti süresi (2026-07-03): FarmTelemetry.SessionMs hiç populate edilmiyordu (yalnız Reset'te
    // 0'a yazılan ölü alan — gerçek oturum süresi ViewModel katmanında ayrı bir DateTime sayaçla tutuluyor,
    // FarmEngine'e erişimi yok). LogSessionSummary kendi Stopwatch'ını kullanır.
    private readonly System.Diagnostics.Stopwatch _sessionWatch = new();
    private int        _scanAttempts; // kamera tarama adım sayacı
    // Dev 2 — "dönüş" sayacı: yalnız canlı hedef bulununca / angajman olunca resetlenir. Kamera-scan ve roam
    // ONU resetlemez (oysa _idleWatch onlarda resetlenir → 20sn'yi asla biriktiremez). ReturnHomeIdleMs bunu okur.
    private readonly System.Diagnostics.Stopwatch _noCombatWatch = new();
    // Dev 2 — başlangıç farm koordinatı (oyun-birimi). OtoFarm: FarmLoopAsync başında CoordReader ile
    // KARARLI okunur (_homeReadPending bayrağı Start()'ta kurulur — UI thread'i bloklanmaz).
    // Otonom: HomeOverride (kalibre FarmSpotX/Y) ile set edilir. null → otomatik dönüş devre dışı.
    private (int x, int y)? _homeCoord;
    private bool _homeReadPending;
    // Dev 3 — banka tetiği: son banka kontrolündeki kill sayısı + "banka bekliyor" bayrağı (EngageAsync kurar,
    // ana tick tarama fazında çalıştırır → combat yolundan ayrı).
    private int  _killsAtLastBankCheck;
    private volatile bool _bankPending;
    // NOT (2026-07-04, 7.tur-b): 6.tur'un "adaptif banka kontrol zamanlaması" (dolum-hızı tahminli dinamik
    // eşik) KALDIRILDI — muhasebe hatası yüzünden hiç devreye girmiyordu (fillDelta her zaman 0 → hep sabit
    // eşiğe düşüyordu) ve kullanıcının "her N kill'de kontrol etsin" beklentisini belirsizleştiriyordu.
    // Tetik artık YALIN: her CheckEveryKills kill'de bir kontrol; tetiklenince "banka-tetik" satırı loglanır.
    private long       _lastScanDiagMs; // tarama teşhis log'u throttle damgası (ceset-eleme ayrımı)
    private long       _lastPerfLogMs;  // perf satırı throttle damgası (Detection FPS bloğundan)
    private int        _staleSkips;     // (d) bayat-kare atlaması sayacı (perf satırında raporlanır; Interlocked)

    // ── Hedef-geçiş telemetrisi (2026-07-03): kill → sonraki angajman arası süre + neden sayaçları ──
    // Kullanıcının "bazen direkt bazen duraksıyor" şikâyetinin doğrudan ölçümü. Sayaçlar YALNIZ FarmLoop
    // task zincirinden (ScanningTick/TargetAsync/EngageAsync sıralı) mutasyona uğrar → kilit gerekmez.
    private long _lastEngageEndMs;      // son angajmanın bittiği an (Start'ta NowMs)
    private int  _switchAcqAttempts;    // bu geçişte kaç hedef-alma denemesi yapıldı
    private int  _switchGuardianBlocks; // bu geçişte kaç guardian reddi yendi
    private int  _switchEmptyScans;     // bu geçişte canlı-hedefsiz geçen ~saniye sayısı (1sn throttle)
    private long _lastEmptyScanCountMs; // boş-tarama sayacının throttle damgası
    private bool _bankRanSinceEngage;   // geçişte banka çalıştı mı (medyanı zehirlemesin)

    // Koruma mobu kara listesi: (merkez koordinatı, süre sonu ms)
    private readonly List<(PointF pos, long expireAt)> _guardianBlacklist = new();
    private const int GuardianBlacklistRadiusPx = 60;
    private const int GuardianBlacklistDurationMs = 30_000;

    // Ölü/seçilemeyen mob kara listesi (kısa ömürlü): ceset hâlâ YOLO'da görünür;
    // "en yakın" sıralaması onu tekrar seçmesin diye o konum DeadBlacklistMs süre atlanır.
    private readonly List<(PointF pos, long expireAt)> _deadBlacklist = new();
    private const int DeadBlacklistRadiusPx = 90; // F2: 64→90, kayan/düşen ceset YOLO kutusunu kapsasın
    // F2: mob ölünce cesedi ayakta-merkezden ekranda AŞAĞI düşer (~100px; log'da @(987,650) standing →
    // @(986,758) corpse). Kill anında merkez + bu kadar aşağısı ayrı blacklist'lenir ki düşen kutu da kapsansın.
    private const int CorpseFallOffsetPx = 85;
    // Tık ıskaladı + tespit o an kayboldu (mobStillThere=False) → aynı noktaya kilitlenip sonsuz re-pick
    // döngüsüne girmesin diye KISA süre atla (statik false-positive ya da kaymış mob). Onaylanan ceset
    // blacklist'inden (DeadBlacklistMs) çok daha kısa: gerçekten kaydıysa mob kısa sürede geri alınır.
    private const int MissReselectSkipMs = 2500;
    // 9.tur: bot KENDİ kamerasını oynattığı pencerede (180° flip / nav dönüşü / roam yürüyüşü) tracker'a
    // hareket kredisi yazdırılmaz (MobTracker.SuppressMotionCredit): flip süpürmesi ~30px/kare ile
    // sıçrama-korumasının (220px/kare) altında kalıp cesetlere sahte MovedPx yazıyor, 90sn escalation
    // blacklist'i bile hareket-muafiyetiyle deliniyordu (canlı log 2026-07-08: aynı nokta 5× üst üste,
    // teshis spatial-bl=0 hareket-muaf=1). 800ms: süpürme + 200ms sert-oturma penceresini kapsar,
    // ScanWaitMsBetween(1000) altında kalır → oturma sonrası tarama normal muafiyet davranışı görür.
    private const int CameraFlipSuppressMs = 800;

    // 13.tur (S5): kamera-hareket kapısı oturum sayaçları (oturum-özetinde raporlanır; eşik kalibrasyonu
    // için — ertelenen çoksa/ort. bekleme uzunsa MotionGatePxPerMs gevşetilir). Tek yazar: FarmLoop task'i.
    private int  _gateDeferrals;       // en az bir kez ertelenen TargetAsync sayısı
    private long _gateDeferredMsTotal; // toplam erteleme süresi (ms)
    private int  _gateGiveUps;         // bütçe dolup vazgeçilen hedef sayısı

    // ── 13.tur (S3): nüfus muhasebesi ─────────────────────────────────────────
    // Bölgedeki normal mob sayısı SABİT (kullanıcı bilgisi: 7 normal + 1 guardian; respawn ~25sn
    // RASTGELE konumda; ceset despawn 18sn). Her ONAYLI kill "borç" yazar (MobRespawnMs sonra düşer);
    // beklenen-canlı = RegionMobCount − borç. 0'a inince ekranda görünen hareketsiz adaylar büyük
    // olasılıkla CESETTİR (model ceset/canlı ayrımı yapamıyor — 2026-07-11 replay görsel kanıtı:
    // yatık cesetlere 0.74-0.91 conf kutu) → tıklama iştahı kesilir; yalnız BELİRGİN hareketli iz
    // (MovedPx ≥ MovingAliveExemptPx = kesin-canlı kanıtı) muaf. Emniyet katmanları: borç tavanı
    // RegionMobCount (tek-vuruş yanlış-pozitifi borcu sonsuz şişirmesin), decay ≤ gerçek respawn
    // (bastırma respawn'ı asla maskeleyemez), çalıntı kill (Lost) borca yazılmaz (hata her zaman
    // "az bastırma" yönünde = güvenli). Tek yazar/okur: FarmLoop task zinciri — kilit gerekmez.
    private readonly List<long> _killDebt = new();
    private long _lastPopGateLogMs;

    /// <summary>Süresi dolan kill-borçlarını düşürüp güncel borç sayısını döndürür (S3).</summary>
    private int KillDebtCount(FarmSettings s, long now)
    {
        _killDebt.RemoveAll(t => now - t >= s.MobRespawnMs);
        return _killDebt.Count;
    }

    // ── Tekrar-suçlu escalation (2026-07-04) ─────────────────────────────────────
    // Canlı log (6.5sa): 156× "tık-sonrası-kayıp" + aynı 60px hücrede 8×'e kadar tekrar. Kök neden: statik
    // YOLO false-positive (kırmızı ağaç vb. — kodun eski yorumu "SONSUZ re-pick @(686,546) conf 0.94") ve
    // kalıcı ceset AYNI noktada tekrar tekrar seçilip her seferinde ~1-2sn (2 offset × ~1sn HP-bekleme)
    // harcatıyor; KISA (2.5sn) konumsal blacklist dolunca yeniden seçiliyor. Çözüm: nokta-başına düşüş
    // sayısı tutulur; kronik nokta blacklist süresi katlanarak uzar (2.5s → 45s → 90s) → birkaç denemede susar.
    // GÜVENLİ: dead-blacklist filtresi HAREKETLİ adayı zaten muaf tutuyor (MovingAliveExemptPx) → escalation
    // yalnız SABİT noktaları (ağaç/ceset) etkiler; canlı mob (hareket eder) asla escalation'la elenmez.
    private readonly List<(PointF pos, int count, long lastMs)> _dropHistory = new();
    private const int  DropHistoryRadiusPx = 70;    // bu yarıçaptaki düşüşler "aynı nokta" sayılır
    private const long DropHistoryForgetMs = 120_000; // 2dk düşüşsüz kalan nokta unutulur (yeni respawn olabilir)

    // ── TrackId-bazlı tekrar-başarısızlık durağı (2026-07-04, 6.tur) ─────────────────────────────────
    // Kanıt: 57dk canlı log'da en az 10 farklı TrackId 13-19 ardışık "hedef-bırakıldı" gösterdi (5-8sn
    // içinde) — AYNI canlı/hareket eden mob defalarca seçilip tıklanıp başarısız oluyordu (örn. trk=215:
    // pozisyonu @(656,1095)→@(720,1148) net sürüklendi, yani GERÇEKTEN canlı). Bu tekrar-döngüsü hem
    // konumsal dead-blacklist'i (hareketli aday zaten muaf — MovingAliveExemptPx) hem escalation'ı (5.tur)
    // aşıyor, çünkü ikisi de KONUM-anahtarlı ve hareket-muafiyeti onları by-design atlatıyor. Bu, konumdan/
    // hareketten TAMAMEN bağımsız bir arka-durak: aynı TrackId kısa pencerede N kez ardışık başarısız olursa
    // hareket-muafiyeti ne olursa olsun kısa süre aday-dışı. Kısa cooldown (4sn) — "kesin canlı" mobu kalıcı
    // yazmaz, yalnız döngüyü kırıp diğer adaylara şans tanır (kendi kendine iyileşir).
    private readonly Dictionary<int, (int consecutiveFails, long lastFailMs)> _trackFailStreak = new();
    // 9.tur: 5→3 — sayaç artık TIKLANAN ize yazılıyor (bayat scan-TrackId değil; bkz TargetAsync
    // lastLiveTrackId) → 3. ardışık başarısızlıkta durak devreye girer (canlı log: trk=44 aynı noktada
    // 5× denendi, throttle hiç tetiklenmedi — teshis trk-throttle=0).
    private const int  TrackFailStreakLimit    = 3;
    private const long TrackFailStreakWindowMs = 8_000;  // gözlenen 5.5-8sn pencereyle eşleşir
    private const long TrackFailCooldownMs     = 4_000;  // kısa — döngüyü kır, kalıcı yazma

    /// <summary>Bir hedef-alma denemesi (tıklama-SONRASI) başarısız oldu — ardışık sayaç artar (pencere
    /// dışına taşan eski kayıt sıfırdan başlar). "taze-karede-yok" (tıklama ÖNCESİ erken çıkış) buraya
    /// DAHİL EDİLMEZ — farklı bir başarısızlık türü, ayrıca zaten blacklist'e de girmiyor.</summary>
    private void RecordAcqFailure(int trackId)
    {
        if (trackId < 0) return;
        long now = NowMs();
        if (_trackFailStreak.TryGetValue(trackId, out var e) && now - e.lastFailMs <= TrackFailStreakWindowMs)
            _trackFailStreak[trackId] = (e.consecutiveFails + 1, now);
        else
            _trackFailStreak[trackId] = (1, now);
    }

    /// <summary>Hedef-alma başarılı oldu (HP bar onaylandı) — bu TrackId'nin başarısızlık serisi sıfırlanır.</summary>
    private void RecordAcqSuccess(int trackId)
    {
        if (trackId >= 0) _trackFailStreak.Remove(trackId);
    }

    /// <summary>Bu TrackId şu an kısa-süreli durak altında mı (ardışık N başarısızlık + hâlâ cooldown içinde).</summary>
    private bool IsTrackFailThrottled(int trackId)
    {
        if (trackId < 0 || !_trackFailStreak.TryGetValue(trackId, out var e)) return false;
        long now = NowMs();
        if (now - e.lastFailMs > TrackFailStreakWindowMs + TrackFailCooldownMs) return false; // bayat — ucuz kontrol, silmeye gerek yok
        return e.consecutiveFails >= TrackFailStreakLimit && (now - e.lastFailMs) <= TrackFailCooldownMs;
    }

    // ── V4 — hedef penceresi çerçeve şablonu (yapı-tabanlı seçili/canlı/öldü) ──
    // Start()'ta bir kez build edilir (thread'ler başlamadan → görünürlük güvenli); tespit thread'leri
    // her karede ScanTargetBar'a geçirir. null/boş = öğretilmemiş → renk (V3) yolu.
    private IReadOnlyList<Services.Autonomous.TemplateLocator.Template>? _frameTemplates;
    private Rectangle _frameRectPhys;

    // TrackAndCombat'in son bilinen hedef merkezi — kill sonrası ölü kara listesi için.
    private PointF _lastEngagedCenter;
    // TrackAndCombat'in son bilinen hedef iz kimliği — kill sonrası MobTracker.MarkDead için (-1 = yok).
    private int    _lastEngagedTrackId = -1;

    // ── Object tracker (ByteTrack-lite) ──────────────────────────────────────
    // YOLO kutularına kalıcı kimlik atar (ekstra inference yok). Ceset yok sayma (#3),
    // doğru overlay vurgusu (#4), stabil hedef puanı (#2) için. Tespit thread'i Update,
    // combat thread'i MarkDead çağırır → MobTracker içinde kilitlidir.
    private readonly MobTracker _tracker = new();

    // ── Decoupled YOLO tespit thread'i (Sorun 2) ─────────────────────────────
    // Inference kendi thread'inde sürekli döner; scanning/combat döngüleri bu anlık
    // görüntüyü (snapshot) okur, asla inference beklemez → daima en taze tespit
    // (bayat pozisyon takibi = hedef etrafında dönme biter).
    private volatile DetectionSnapshot? _latestDetections;
    private volatile Detection?         _currentTargetForOverlay; // combat'te saldırılan hedef (overlay vurgusu)
    private Thread?                     _detThread;
    private volatile ReplayRecorder?    _replay; // Faz B: açıkken kayıt; null = kapalı (DetectionLoop okur)
    // P2: pipelined inference — üretici (capture+preprocess) ∥ tüketici (GPU Run+post) ayrı thread.
    private Thread?                     _captureThread;            // üretici (yalnız pipelined modda)
    private readonly object             _pipeLock   = new();
    private readonly Stack<int>         _freeSlots  = new();       // boş tensor slot havuzu (triple-buffer)
    private PipeItem?                   _pending;                  // latest-wins mailbox (≤1)
    private readonly AutoResetEvent     _pipeSignal = new(false);  // tüketici uyandırma

    // 14.tur (Faz 3.2): event-driven scanning. FarmLoop eskiden her tick sonunda sabit 30ms
    // bekliyordu — yeni snapshot beklemenin HEMEN başında gelirse karar katmanı ~30ms'e kadar
    // boşuna bekliyordu (ort. yarım tick ≈15ms). _latestDetections'ı yazan İKİ yol (DetectionLoop
    // seri, InferConsumeLoop pipelined) yazımdan hemen sonra bu sinyali verir; FarmLoop 50ms
    // fallback'li WaitAsync ile bekler (Roaming/Banking/sinyal-kaçırma durumlarında canlılık
    // korunur — üst sınır eski 30ms tavanına yakın). Latest-wins: (0,1) kapasiteli, CurrentCount==0
    // iken Release (üretici tek-thread olduğundan — seri/pipelined ayrık moddur — yarış yok).
    private readonly SemaphoreSlim      _snapSignal = new(0, 1);

    private void SignalNewSnapshot()
    {
        if (_snapSignal.CurrentCount == 0) _snapSignal.Release();
    }

    // Faz B (frame identity): her inference'a monotonik FrameId + gerçek zaman damgaları.
    //  • FrameId       : tespit thread'inin ürettiği kare sayacı (replay/eşleştirme için).
    //  • CapturedAtMs  : ekran yakalamasının BAŞLADIĞI an → "frame age at click" = NowMs - CapturedAtMs
    //                    (capture+inference+publish+karar gecikmesinin tamamı; bayat-kutu tıklama ölçümü).
    //  • PublishedAtMs : snapshot'ın yayınlandığı an (inference bitişi). Combat "snapshot yaşı" için kullanır.
    // Bar (V4): karedeki hedef-penceresi durumunun TAM özeti (yapı + renk + offset + damga) — taramayla
    // ATOMİK eşleşir. Guardian kontrolü offset'i buradan alır (racy global yerine); acquire/combat canlı/ölü
    // kararını buradan okur. null = HSV taraması o karede çalışmadı (ColorScan modu / HP bar kalibresiz).
    private sealed record DetectionSnapshot(
        long FrameId, IReadOnlyList<Detection> Dets, int W, int H,
        long CapturedAtMs, long PublishedAtMs, TargetBarState? Bar = null);

    // P2: üreticiden tüketiciye geçen iş birimi. Yalnız TENSOR slot + meta geçer; capture Bitmap üreticide
    // KALIR (recorder klonu + HSV onun thread'inde). ReplayClone: replay açıksa üretici klonladı (tüketici yazar).
    // P3: RoiX/Y — ROI-yerel tespitleri ekran-uzayına çevirmek için ofset. 0,0 = tam-ekran.
    //     FullW/H — gerçek fiziksel ekran boyutu (snapshot + overlay ölçekleme için).
    private sealed record PipeItem(
        int Slot, long FrameId, long CapturedAtMs, float PadX, float PadY, float Scale,
        int W, int H, TargetBarState? Bar, double CapMs, double PrepMs, Bitmap? ReplayClone,
        int RoiX = 0, int RoiY = 0, int FullW = 0, int FullH = 0);

    // ms cinsinden monotonik "şimdi" (Stopwatch tabanlı).
    private static long NowMs() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);

    // p noktası, listedeki herhangi bir kara liste girdisinin radius'u içinde mi?
    private static bool NearAny(PointF p, List<(PointF pos, long expireAt)> list, int radius)
    {
        for (int i = 0; i < list.Count; i++)
        {
            float dx = p.X - list[i].pos.X, dy = p.Y - list[i].pos.Y;
            if (dx * dx + dy * dy <= radius * radius) return true;
        }
        return false;
    }

    // NearAny + süresi DOLMAMIŞ (expireAt >= now) girdi şartı. Bekleme/scan sırasında ScanningTick henüz
    // listeyi temizlememişken bayat bir blacklist girdisi canlı adayı gizlemesin (mutasyon yerine zaman kontrolü).
    private static bool NearAnyActive(PointF p, List<(PointF pos, long expireAt)> list, int radius, long now)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].expireAt < now) continue;
            float dx = p.X - list[i].pos.X, dy = p.Y - list[i].pos.Y;
            if (dx * dx + dy * dy <= radius * radius) return true;
        }
        return false;
    }

    /// <summary>Verilen düşüş-noktası için escalation'lı blacklist süresi (ms) döndürür; <paramref name="rep"/>
    /// = bu noktanın son 2dk içindeki kaçıncı düşüşü olduğu. 1. düşüş → <paramref name="baseMs"/> (şüpheden
    /// masun: gerçekten kaymış canlı mob olabilir); 2. → 45sn; 3+ → 90sn (kronik statik false-positive/ceset).
    /// GÜVENLİ: hareketli aday dead-blacklist filtresinden zaten muaf (MovingAliveExemptPx) → uzun süre yalnız
    /// SABİT noktayı (ağaç/ceset) susturur, hareket eden canlı mobu değil. 2dk düşüşsüz nokta unutulur (respawn).</summary>
    private long EscalatedBlacklistMs(PointF pos, long baseMs, out int rep)
    {
        long now = NowMs();
        _dropHistory.RemoveAll(e => now - e.lastMs > DropHistoryForgetMs);
        int idx = -1;
        for (int i = 0; i < _dropHistory.Count; i++)
        {
            float dx = pos.X - _dropHistory[i].pos.X, dy = pos.Y - _dropHistory[i].pos.Y;
            if (dx * dx + dy * dy <= DropHistoryRadiusPx * DropHistoryRadiusPx) { idx = i; break; }
        }
        if (idx >= 0) { rep = _dropHistory[idx].count + 1; _dropHistory[idx] = (pos, rep, now); }
        else          { rep = 1;                          _dropHistory.Add((pos, 1, now)); }
        return rep switch
        {
            1 => baseMs,       // ilk düşüş: kısa (kaymış canlı mob geri alınabilsin)
            2 => 45_000,       // ikinci: aynı nokta yine başarısız → büyük olasılıkla kronik
            _ => 90_000,       // üçüncü+: kesin statik false-positive / ceset → uzun sustur
        };
    }


    // Karakter daima ekranın FİZİKSEL merkezindedir (#1: kalibrasyon kaldırıldı). DIP'li
    // SystemParameters yerine GetSystemMetrics → yüksek DPI'da tespit koordinatlarıyla hizalı.
    private static PointF ScreenCenter()
    {
        var (w, h) = ScreenCapture.PhysicalSize();
        return new PointF(w / 2f, h / 2f);
    }

    /// <summary>Farm döngüsü şu an aktif mi (Idle veya KillSwitched değil).</summary>
    public bool IsRunning => _state != FarmState.Idle && _state != FarmState.KillSwitched;

    // ── Duraklat ────────────────────────────────────────────────────────────────
    // Session/sayaçlar korunur; tarama ve combat in-place beklemeye geçer.
    private volatile bool _paused;
    public bool IsPaused => _paused;
    public void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
        {
            _combo.CancelAll();
            SetState(_state, "⏸ Duraklatıldı");
        }
        else
        {
            SetState(_state, "▶ Devam ediliyor…");
        }
    }

    // ── Olaylar ───────────────────────────────────────────────────────────────
    public event EventHandler<string>?       StatusChanged;
    public event EventHandler<FarmTelemetry>? TelemetryUpdated;
    /// <summary>Gönderilen her tuş/aktivite — Log HUD canlı akışı için.</summary>
    public event EventHandler<Models.Farm.ActivityEntry>? KeyLogged;

    /// <summary>Her tick'te seçili mob tespitleri + saldırılan hedef — Tespit overlay'i için.
    /// Abone yokken yayınlama maliyeti ≈sıfır; overlay yalnızca görünürken bağlanır.</summary>
    public event EventHandler<DetectionFrame>? DetectionsUpdated;

    public FarmTelemetry Telemetry { get; } = new();

    public IYoloInferrer?            Inferrer   { get; set; }

    // Dev 2/3 — Otonom servisleri paylaşımlı FarmEngine'e enjekte edilir (Inferrer deseni). Atanmamış/kalibre
    // değilse ilgili özellik sessizce devre dışı (saf OtoFarm kullanıcısı etkilenmez). MainViewModel kurulumda set eder.
    public WorldNavigator?   Navigator   { get; set; }
    public CoordinateReader? CoordReader { get; set; }
    /// <summary>Dev 2 — Otonom, farm koordinatını (kalibre FarmSpotX/Y) buraya set eder; set iken Start() bunu
    /// ev olarak kullanır (koordinat okumaz). OtoFarm'da null → Start() CoordReader ile o anki konumu okur.</summary>
    public (int x, int y)? HomeOverride { get; set; }
    /// <summary>Dev 3 — envanter dolunca klan bankasına boşaltma servisi (atanmışsa). null → banka adımı atlanır.</summary>
    public Services.Farm.ClanBankService? Bank { get; set; }
    /// <summary>Dev 3 — Otonom kontrolündeyken (kendi town-sat akışı var) OtoFarm banka tetiği bastırılır.</summary>
    public bool AutonomousControlled { get; set; }

    public FarmEngine(
        TransportRouter    router,
        ComboEngine        combo,
        GlobalKeyboardHook keyHook,
        GlobalMouseHook    mouseHook,
        MobLibrary         mobLibrary,
        AppState           appState)
    {
        _router     = router;
        _combo      = combo;
        _keyHook    = keyHook;
        _mouseHook  = mouseHook;
        _mobLibrary = mobLibrary;
        _appState   = appState;
    }

    // ── Yaşam döngüsü ─────────────────────────────────────────────────────────

    public void Start()
    {
        // 9.tur: Idle yerine IsRunning — yarışta kaybeden bir Stop'un bıraktığı KillSwitched
        // kalıntı state'i restart'ı kilitlemesin (IsRunning: Idle ve KillSwitched = çalışmıyor).
        if (IsRunning) return;

        bool hpBarCalibrated   = _appState.Wtm.IsHpBarLocated;
        if (!hpBarCalibrated)
        {
            SetState(FarmState.Idle, "⚠ Farm kalibrasyonu eksik — 4. adımı tamamlayın (hedef HP bar)");
            return;
        }

        _keyHook.KeyDown += OnKeyDown;
        _cts = new CancellationTokenSource();
        Telemetry.Reset();
        _paused = false;
        _stopInProgress = 0; // 9.tur: yeni oturum → Stop tek-atım kilidi yeniden kurulur
        _idleWatch.Restart();
        _noCombatWatch.Restart();
        _sessionWatch.Restart();
        _killsAtLastBankCheck = 0;
        _bankPending = false;
        // Dev 2 — ev koordinatı: Otonom set ettiyse (HomeOverride) onu kullan; yoksa (OtoFarm) özellik açık +
        // koord okuyucu kalibre ise okuma FarmLoopAsync başında KARARLI yapılır (iki uyuşan okuma; tek
        // okumadaki hane-hatası evi bozamaz) — Start() UI thread'ini bloklamaz.
        _homeReadPending = false;
        if (HomeOverride is { } ho)
            _homeCoord = ho;
        else
        {
            _homeCoord = null;
            _homeReadPending = _appState.Farm.ReturnHomeEnabled && CoordReader?.IsReady == true;
        }
        _wayIndex = 0;
        _deadBlacklist.Clear();
        _dropHistory.Clear();   // yeni oturum: tekrar-suçlu escalation geçmişini sıfırla
        _trackFailStreak.Clear(); // yeni oturum: TrackId-bazlı tekrar-başarısızlık geçmişini sıfırla
        _latestDetections        = null;
        _currentTargetForOverlay = null;
        _lastEngagedTrackId      = -1;

        // V4 — çerçeve şablonlarını bir kez hazırla (tespit thread'leri başlamadan ÖNCE — görünürlük güvenli).
        WtmVision.ResetTargetBar();
        _frameTemplates = null;
        _frameRectPhys  = Rectangle.Empty;
        var wtmV4 = _appState.Wtm;
        if (wtmV4.IsTargetFrameCalibrated)
        {
            var pr = ResolutionMapper.Map(wtmV4.TargetFrameRectX, wtmV4.TargetFrameRectY,
                                          wtmV4.TargetFrameRectW, wtmV4.TargetFrameRectH);
            _frameRectPhys  = new Rectangle(pr.X, pr.Y, pr.Width, pr.Height);
            _frameTemplates = Services.Autonomous.TemplateLocator.BuildTemplates(
                wtmV4.TargetFrameTemplatesB64,
                pr.Width  / (float)wtmV4.TargetFrameRectW,
                pr.Height / (float)wtmV4.TargetFrameRectH);
            Program.Log($"[Farm] V4 çerçeve şablonu aktif: {_frameTemplates.Count} örnek " +
                        $"@({pr.X},{pr.Y} {pr.Width}×{pr.Height}) eşik={wtmV4.TargetFrameMatchThreshold:F2}");
        }

        // Object tracker'ı sıfırla + ayarlardan eşikleri uygula (yeni oturum = temiz izler).
        _tracker.Reset();
        _tracker.IouThreshold = _appState.Farm.TrackIouThreshold;
        // 12.tur: kare-bazlı kullanıcı ayarı süre-bazlıya çevrilir (~50ms/kare ≈ tarihi ~20fps tabanı;
        // 15 kare → 750ms). İz/ceset ömrü artık tespit FPS'inden bağımsız — FPS artışı ömrü kısaltmaz.
        _tracker.MaxAgeMs     = Math.Max(200, _appState.Farm.TrackMaxAgeFrames * 50);
        _tracker.Log          = Program.Log;   // teşhis: seyrek tracker olayları (miras-DEAD / sicrama-yoksay)
        _router.Rp2040AtomicMouseMove = _appState.Farm.AtomicMouseMove; // 14.tur (Faz 3.3)

        // Hedef-geçiş telemetri sıfırlama + oturum-başlangıç konfigürasyon özeti (2026-07-03):
        // fine-tuning için o oturumda geçerli eşikler tek satırda; sınıf+hareket modu da yazılır ki
        // archer-yaklaşmasının devrede olup olmadığı (sınıf-gate) logdan tek bakışta okunsun.
        _lastPerfLogMs        = 0;
        _staleSkips           = 0;
        _lastEngageEndMs      = NowMs();
        _switchAcqAttempts    = 0;
        _switchGuardianBlocks = 0;
        _switchEmptyScans     = 0;
        _bankRanSinceEngage   = false;
        _gateDeferrals        = 0;
        _gateDeferredMsTotal  = 0;
        _gateGiveUps          = 0;
        _killDebt.Clear();
        _lastPopGateLogMs     = 0;
        var sCfg = _appState.Farm;
        // 2026-07-04 (7.tur-b): banka konfigürasyonu da yazılır — "banka neden tetiklenmedi" sorusunda
        // bellekteki GERÇEK değerler (Enabled/CheckEveryKills) logdan okunabilsin (canlı testte kill=20'ye
        // rağmen 0 banka çalışması görüldü; eşiğin o an bellekte 10 mu 30 mu olduğu ispatlanamadı).
        // 13.tur (P5): EFEKTİF hareket modu açıkça yazılır — EngageMovement=ArcherWalkAndFace ayarda
        // takılı kalsa bile archer-dışı sınıfta fiilen ComboHandlesApproach uygulanır (sınıf-gate);
        // "ben archer oynamıyordum, bu mod ne?" karışıklığı logdan tek bakışta çözülsün.
        Program.Log($"[Farm] oturum-başlangıç: sınıf={_appState.ClassId} hareket={sCfg.EngageMovement} " +
                    $"efektif={(ArcherApproachActive(sCfg) ? "ArcherWalkAndFace" : "ComboHandlesApproach")} " +
                    $"model={System.IO.Path.GetFileName(sCfg.ModelPath)} imgsz={sCfg.ModelInputSize} " +
                    $"ep={(Inferrer as OnnxYoloInferrer)?.ActiveEp ?? "?"} " +
                    $"conf={sCfg.ConfidenceThreshold:F2} iou={sCfg.IouThreshold:F2} " +
                    $"deadBl={sCfg.DeadBlacklistMs}ms grace={sCfg.HpFirstAliveGraceMs}ms " +
                    $"yapı-terk={sCfg.StructAbsentAbandonMs}ms eşya-eşiği={_appState.ClanBank.ItemMatchThreshold:F2} " +
                    $"yapı-eşik={wtmV4.TargetFrameMatchThreshold:F2}/{wtmV4.TargetFrameAbsentThreshold:F2} " +
                    $"banka={(_appState.ClanBank.Enabled ? "açık" : "KAPALI")} " +
                    $"her={Math.Max(1, _appState.ClanBank.CheckEveryKills)}kill " +
                    $"doluluk-eşiği=%{(int)(_appState.ClanBank.FullThreshold * 100)}");

        // Faz B: replay recorder (yalnız açıksa) — DetectionLoop başlamadan ÖNCE kurulmalı.
        _replay = null;
        if (_appState.Farm.ReplayEnabled)
        {
            try
            {
                _replay = CreateReplayRecorder();
                Program.Log($"[Farm] Replay kaydı açık → {_replay.SessionDir}");
            }
            catch (Exception ex)
            {
                Program.Log($"[Farm] Replay recorder başlatılamadı: {ex.Message}");
                _replay = null;
            }
        }

        // YOLO inference ayrı thread'de sürekli döner (combat döngüsünü bloke etmez).
        // P2: pipelined ise 2 thread (üretici capture+prep ∥ tüketici GPU+post) + slot havuzu; değilse seri tek thread.
        if (_appState.Farm.PipelinedInference)
        {
            lock (_pipeLock)
            {
                _freeSlots.Clear();
                for (int i = 0; i < OnnxYoloInferrer.SlotCount; i++) _freeSlots.Push(i);
                if (_pending is { } p) p.ReplayClone?.Dispose();
                _pending = null;
            }
            _captureThread = new Thread(() => CaptureProduceLoop(_cts.Token))
                { IsBackground = true, Name = "FarmCapture" };
            _detThread = new Thread(() => InferConsumeLoop(_cts.Token))
                { IsBackground = true, Name = "FarmInference" };
            _captureThread.Start();
            _detThread.Start();
        }
        else
        {
            _detThread = new Thread(() => DetectionLoop(_cts.Token))
                { IsBackground = true, Name = "FarmDetection" };
            _detThread.Start();
        }

        _ = Task.Run(() => FarmLoopAsync(_cts.Token));
        SetState(FarmState.Scanning, "Taranıyor…");
    }

    public void Stop()
    {
        // 8.tur (2026-07-08): idempotent — F12 tek basışta ÜÇ ayrı durdurma yolu tetikleniyor
        // (ViewModel StopActiveEngineImmediate + ToggleSelectedMode→StopFarm + FarmEngine'in kendi
        // kill-switch hook'u) ve her biri tam teardown + oturum-özeti logluyordu (canlı logda özet
        // hep 3×). İlk çağrı state'i Idle'a çeker; sonrakiler burada döner.
        if (_state == FarmState.Idle) return;

        // 9.tur: Idle-guard atomik değil — kazanan Stop, Join(1000)'lerde ~1-2sn blokeyken ikinci yol
        // (UI BeginInvoke) state'i hâlâ Scanning görüp girebiliyordu (canlı logda özet yine 3×).
        // Tek-atım: kazanan tam teardown + özet basar; kaybedenler anında döner. Start() sıfırlar.
        if (Interlocked.Exchange(ref _stopInProgress, 1) != 0) return;

        _keyHook.KeyDown -= OnKeyDown;

        // A4: önce iptal → detection thread'i Join et → SONRA CTS'i dispose et. Eski sıra
        // (Cancel hemen ardından Dispose) thread hâlâ token okurken dispose ediyordu (broad-catch
        // yutuyordu ama kirli; ileride daha ağır runtime'da risk büyür). Join, FarmLoopAsync'in de
        // token henüz canlıyken temiz iptalini garanti eder.
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        _pipeSignal.Set(); // P2: tüketiciyi WaitOne'dan uyandır → hızlı çıkış

        // P2: pipelined modda üretici + tüketici iki thread → ikisini de Join et.
        if (_captureThread is not null && _captureThread.IsAlive && !_captureThread.Join(1000))
            Program.Log("[Farm] Capture thread 1sn içinde kapanmadı.");
        _captureThread = null;

        if (_detThread is not null && _detThread.IsAlive && !_detThread.Join(1000))
            Program.Log("[Farm] Detection thread 1sn içinde kapanmadı.");
        _detThread = null;

        // P2: kalan pending klonu temizle (her iki thread durdu → güvenli).
        lock (_pipeLock) { if (_pending is { } p) p.ReplayClone?.Dispose(); _pending = null; _freeSlots.Clear(); }

        // Faz B: replay recorder'ı kapat — detection thread durdu (yeni Offer gelmez); kuyruğu yaz + kapat.
        var replay = _replay;
        _replay = null;
        if (replay is not null)
        {
            try
            {
                replay.Dispose();
                Program.Log($"[Farm] Replay kaydı bitti — {replay.FramesWritten} kare → {replay.SessionDir}");
            }
            catch (Exception ex) { Program.Log($"[Farm] Replay kapatma hatası: {ex.Message}"); }
        }

        cts?.Dispose();

        _combo.CancelAll();
        _paused = false;
        _currentTargetForOverlay = null; // detection thread CT ile durur
        WtmVision.ResetTargetBar();      // V4: sonraki oturum/önizleme bayat StructurePresent okumasın
        LogSessionSummary();             // 2026-07-03 telemetri: oturum kapanışında tek satır istatistik
        SetState(FarmState.Idle, "Pasif");
        // Tespit overlay'ini temizle — aksi halde son kareler ekranda donar kalır.
        DetectionsUpdated?.Invoke(this, new DetectionFrame(Array.Empty<Detection>(), null, 0, 0));
    }

    /// <summary>Oturum kapanış özeti (2026-07-03): tüm sayaçlar + geçiş-süresi istatistikleri tek satırda.
    /// Eşik fine-tuning'inin ana çıktısı — medyan/maks geçiş süresi düzeltmelerin etkisini ölçer.</summary>
    private void LogSessionSummary()
    {
        var t = Telemetry;
        t.Resurrections = _tracker.ResurrectionCount;     // tracker'dan topla (tek yazar: tespit thread'i durdu)
        t.SessionMs     = _sessionWatch.ElapsedMilliseconds;
        if (t.AcqAttempts == 0 && t.Kills == 0) return;   // hiç iş yapılmadıysa gürültü basma

        string switchStats = "n=0";
        if (t.SwitchTimesMs.Count > 0)
        {
            var sorted = t.SwitchTimesMs.OrderBy(v => v).ToList();
            long avg = (long)sorted.Average();
            switchStats = $"ort={avg}ms medyan={sorted[sorted.Count / 2]}ms " +
                          $"min={sorted[0]} max={sorted[^1]} (n={sorted.Count})";
        }
        int successPct = t.AcqAttempts > 0 ? (int)(100.0 * t.AcqSuccesses / t.AcqAttempts) : 0;
        string dur = $"{t.SessionMs / 60000}:{(t.SessionMs / 1000) % 60:00}";
        string gateStats = _gateDeferrals > 0
            ? $"ertelenen={_gateDeferrals} ort={_gateDeferredMsTotal / _gateDeferrals}ms vazgeç={_gateGiveUps}"
            : $"ertelenen=0 vazgeç={_gateGiveUps}";
        Program.Log($"[Farm] oturum-özeti: süre={dur} kill={t.Kills} lost={t.Losts} " +
                    $"lostfast={t.LostFasts} timeout={t.Timeouts} | hedef-alma: deneme={t.AcqAttempts} " +
                    $"başarı={t.AcqSuccesses} (%{successPct}) guardian-red={t.GuardianBlocks} " +
                    $"diriliş={t.Resurrections} | geçiş: {switchStats} | kapı: {gateStats} | " +
                    $"banka: {t.BankRuns} çalışma, {t.BankItemsMoved} eşya");
    }

    // ── Kill-switch ────────────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, HookKeyEventArgs e)
    {
        var killKey = _appState.Farm.KillSwitchKey;
        if (e.Key.ToString().Equals(killKey, StringComparison.OrdinalIgnoreCase))
        {
            // 9.tur: bitmiş motoru yeniden KillSwitched'e ARM ETME — kill-switch tuşu GlobalStartKey ile
            // aynı (F12) olabildiğinden ViewModel yolu aynı basışta çoğu kez önce durdurur; Idle sonrası
            // state'i kirletmek Stop'un Idle-guard'ını deliyor ve oturum-özetini çoğaltıyordu.
            if (!IsRunning) return;
            SetState(FarmState.KillSwitched, "⛔ Kill-switch — durduruldu");
            Stop();
        }
    }

    /// <summary>
    /// Faz B: bu farm oturumu için replay recorder kurar. session.json model/runtime bağlamını taşır
    /// (offline benchmark hangi model/conf/iou/EP ile kaydedildiğini bilsin).
    /// </summary>
    private ReplayRecorder CreateReplayRecorder()
    {
        var s      = _appState.Farm;
        var (w, h) = ScreenCapture.PhysicalSize();
        string ep  = (Inferrer as OnnxYoloInferrer)?.ActiveEp ?? "?";
        var gpu    = OrtEpFactory.DetectPrimaryGpu();
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            createdAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            modelPath = s.ModelPath,
            imgsz     = s.ModelInputSize,
            conf      = s.ConfidenceThreshold,
            iou       = s.IouThreshold,
            backend   = s.InferenceBackend.ToString(),
            activeEp  = ep,
            gpu       = gpu.name,
            screenW   = w,
            screenH   = h,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return new ReplayRecorder(AppContext.BaseDirectory, s.ReplayMaxFrames, jpegQuality: 75, json);
    }
}
