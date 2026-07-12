using System.Collections.Generic;

namespace HomekoWorld.Models.Farm;

/// <summary>
/// Oto Farm yapılandırması — AppState.Farm üzerinden persist edilir.
/// WtmSettings.cs kalıbını takip eder.
/// </summary>
public class FarmSettings
{
    // ── Aktifleştirme ──────────────────────────────────────────────────────────
    public bool   Enabled         { get; set; }
    /// <summary>Makro "Başlat" modundayken farm'ı başlatacak tuş.</summary>
    public string HotKey          { get; set; } = "F9";

    // ── YOLO model dosyaları ───────────────────────────────────────────────────
    /// <summary>Eğitilmiş modelin .onnx dosya yolu.</summary>
    public string ModelPath       { get; set; } = "";
    /// <summary>Mob tanım dosyası yolu (mobs.json).</summary>
    public string MobsJsonPath    { get; set; } = "";
    public double ConfidenceThreshold { get; set; } = 0.65;
    /// <summary>
    /// NMS IoU eşiği (0.20-0.80, varsayılan 0.45). Yüksek = dip-dibe moblarda iki kutu birbirini daha
    /// az bastırır (çift-kutu riski artar); düşük = daha agresif birleştirme. Inferrer'a canlı aktarılır.
    /// </summary>
    public double IouThreshold { get; set; } = 0.45;
    /// <summary>
    /// Modelin eğitildiği/export edildiği giriş boyutu (imgsz). C# inference bu boyutta letterbox yapar.
    /// 640'ta export edilen model → 640; 960'ta export → 960. Eğitim imgsz'siyle eşleşmezse doğruluk düşer.
    /// </summary>
    public int    ModelInputSize { get; set; } = 640;
    public TargetPriorityMode Priority { get; set; } = TargetPriorityMode.Nearest;

    // ── #2 Perspektif-bilinçli hedef puanı (tek tür mob) ─────────────────────
    // Nearest modunda düz ekran-merkezi mesafesi yerine derinlik puanı: derinlik = bbox YÜKSEKLİĞİ
    // (büyük=yakın). Puan = (DepthW + CenterW × yatay_ofset_normalize) / yükseklik; küçük = seçilir.
    /// <summary>Derinlik ağırlığı (baz): yükseklik tabanlı yakınlık. Yüksek = derinlik baskın.</summary>
    public float TargetDepthWeight  { get; set; } = 1.0f;
    /// <summary>Merkez ağırlığı: ekran-merkezine yatay yakınlık tercihi (0 = yalnız derinlik).</summary>
    public float TargetCenterWeight { get; set; } = 0.3f;

    // ── #2/#3/#4 Object tracker (ByteTrack-lite) eşikleri ────────────────────
    /// <summary>Tracker IoU eşik: kare-kare aynı moba kalıcı kimlik. Düşük = daha gevşek eşleme.</summary>
    public float TrackIouThreshold  { get; set; } = 0.3f;
    /// <summary>Tracker: bir iz kaç kare eşleşmezse düşürülür (ceset/kayıp izlerin kare ömrü).</summary>
    public int   TrackMaxAgeFrames  { get; set; } = 15;

    // ── Seçili mob & kombo ────────────────────────────────────────────────────
    /// <summary>
    /// Seçili mob isimleri (çoklu seçim). Boş = tüm tespit sınıflarını avla.
    /// Avlanacak mobları buradan filtrele; öncelik <see cref="Priority"/> ile ayarlanır.
    /// </summary>
    public List<string> SelectedMobNames { get; set; } = [];
    /// <summary>
    /// Geriye uyumluluk / migration için — eski tek-seçim state.json alanı.
    /// Yeni kod <see cref="SelectedMobNames"/> kullanır. Yükleme sırasında SelectedMobNames
    /// boşsa buradan taşınır.
    /// </summary>
    public string SelectedMobName { get; set; } = "";
    /// <summary>
    /// Kullanıcının seçtiği kombo ID'si. mob.ComboId'den bağımsız; bot bu sabit değeri kullanır.
    /// </summary>
    public string SelectedComboId { get; set; } = "";

    // ── WtM (Walk-to-Mob) yeniden tasarım ─────────────────────────────────────
    /// <summary>DEPRECATED: karakter merkezi artık daima fiziksel ekran merkezidir
    /// (kalibrasyon kaldırıldı). Alan state.json uyumu için bırakıldı, kullanılmaz.</summary>
    public int    CharacterCenterX         { get; set; }
    public int    CharacterCenterY         { get; set; }
    public int    DefaultEngagementRangePx { get; set; } = 120;
    /// <summary>
    /// Ölü/seçilemeyen mob (ceset) kara liste süresi (ms). Kill sonrası veya tıklama
    /// HP bar üretmediğinde o konum bu süre boyunca atlanır → en yakın diğer moba geçilir.
    /// Ceset despawn süresi kadar; respawn'ı kalıcı engellemeyecek kadar kısa tutulur.
    /// F2: 4000→8000 — cesetler ~10sn+ ekranda kaldığından 4sn dolunca aynı cesede yeniden tıklanıyordu.
    /// 2026-07-03: 8000→20000 — 8sn HÂLÂ "10sn+" gözleminin altındaydı (MobTracker.DeadLingerMs=12000 ile
    /// birlikte "cesede tıklama" A-belirtisinin ikinci kaynağıydı). Artık MobTracker kendi izini ceset
    /// görünür kaldığı sürece süresiz tutuyor (bkz. MobTracker.DeadLingerMs) — bu alan yalnız YEDEK
    /// (pozisyon-tabanlı) savunma; cömert tutmak güvenli (en kötü ihtimalle aynı noktaya respawn biraz
    /// geç fark edilir, ceset yanlışlıkla canlı sayılmaz).
    /// </summary>
    public int    DeadBlacklistMs          { get; set; } = 20000;
    /// <summary>Angajman + tracking döngüsü tick süresi (ms). Düşük = daha hızlı takip.
    /// Faz 22: YOLO ayrı thread'e alındığı için döngü inference beklemez → 15ms güvenli.</summary>
    public int    WtmTickMs                { get; set; } = 15;
    /// <summary>
    /// Ölüm onayı debounce (ms). HP barı bu kadar KESİNTİSİZ "kırmızı yok" okununca mob öldü sayılır.
    /// V3 (2026-07-02): varsayılan 60→150 — 60ms tek titrek/donuk DXGI karesiyle sahte-ölüm üretebiliyordu;
    /// 150 + tazelik/3-taze-kare şartı + hasar-kapısı dengeli. 200+ = daha güvenli/yavaş.
    /// hadHpOnce gate ile birlikte (önce en az bir kez canlı görülmeli). Combat tick'inden bağımsız.
    /// </summary>
    public int    HpDeathConfirmMs         { get; set; } = 150;
    /// <summary>
    /// "Boş/siyah bar" anti-freeze tavanı (ms, varsayılan 600). Kırmızı kaybolup yerinde koyu-oluk
    /// kaldığında mob "çok düşük HP, hâlâ canlı" sayılır; ama kırmızı bu kadar süredir HİÇ dönmediyse
    /// bu bir HP barı değil, ölüm-sonrası koyu ARKA PLAN'dır → "öldü" denir. Eski hardcoded 3000ms,
    /// koyu sahnelerde her kill'de ~3 sn "taranıyor" gecikmesi üretiyordu. Düşük (400-600) = hızlı geçiş;
    /// gerçek düşük-HP mob kombo sürerken bu süreden önce ölür. Min 150ms tabanına kıstırılır.
    /// </summary>
    public int    HpEmptyBarGraceMs        { get; set; } = 600;
    /// <summary>
    /// Angajman başladıktan sonra HP barı/yapı HİÇ "canlı" onaylanmazsa (hadHpOnce hiç true olmazsa) bu kadar
    /// ms sonra vazgeçilir (Lost — kill sayılmaz, kısa blacklist, hemen yeni hedefe geçilir). KÖK NEDEN
    /// (2026-07-03 canlı log): acquisition (GDI renk taraması, yapıdan BAĞIMSIZ) "canlı" onaylayıp engage
    /// başlatabiliyor, ama combat döngüsünün V4 yapı skoru bu angajmanda HİÇ Match eşiğine ulaşmayabiliyor
    /// (tek-örnek şablon fragile / hedef aslında zaten geçersiz) → hadHpOnce hiç true olmazsa "yapı-kayboldu"
    /// dalı `hadHpOnce` şartlı olduğundan ASLA çalışmaz → kombo safetyMs'e (120sn) kadar boşa atılırdı (canlı
    /// log kanıtı: 34+ saniye kesintisiz "yapı=YOK" + sıfır angajman-sonu satırı). Min 300ms tabanına kıstırılır.
    /// </summary>
    public int    HpFirstAliveGraceMs      { get; set; } = 1500;
    /// <summary>
    /// V4 yapı modu hızlı-terk (2026-07-03): angajman başında yapı skoru KESİNTİSİZ absent-eşiğin ALTINDA
    /// kalırsa (hadHpOnce hiç olmadan) bu kadar ms sonra LostFast ile bırakılır — hayalet/ıska tık, hedef
    /// penceresi HİÇ yokken <see cref="HpFirstAliveGraceMs"/> (1500ms) dolmasını beklemez (canlı log: 3 Lost,
    /// her biri tam 1512ms, yapı skoru İLK örnekten beri 0.05-0.13). Yalnız StructureKnown iken aktif;
    /// skor eşik-bandına/VAR'a dönerse sayaç sıfırlanır ("kesintisiz" şartı). Min 200ms tabanına kıstırılır.
    /// </summary>
    public int    StructAbsentAbandonMs    { get; set; } = 500;
    /// <summary>
    /// Dev 1 — hızlı hedef-kaybı: hedefin YOLO izi bu kadar ms (varsayılan 250) görünmez VE HP barı
    /// "kırmızı yok" iken hedef gitti (başkası kesti / despawn / bizim kill) sayılır → karanlık arka planda
    /// <see cref="HpEmptyBarGraceMs"/> tavanını (600ms) beklemeden ANINDA yeni hedefe geçilir. Kırmızı VARsa
    /// bu dala hiç girilmez (canlı) → flicker-güvenli (iki bağımsız sinyal aynı anda "gitti" demeli). Min 80ms.
    /// </summary>
    public int    TargetLostYoloMs         { get; set; } = 250;
    /// <summary>
    /// YOLO tespit thread'i için minimum kare aralığı (ms) = FPS sınırı. Faz 26: DXGI yakalama
    /// ~1-2ms olduğundan eski GDI-dönemi cap'i (40ms ≈ 25 FPS) gevşetildi → 12ms ≈ 80 FPS (agresif:
    /// kutu güncelleme + takip ~3× akıcı). Pratikte loop GPU inference süresiyle sınırlanır.
    /// İmleç takılması / oyun FPS düşüşü / TDR olursa ARTIR (20–40ms) ya da RecordingMode'u aç.
    /// </summary>
    public int    DetectionMinIntervalMs   { get; set; } = 12;
    /// <summary>
    /// Kayıt/Performans modu (A4): açıkken YOLO tespit hızı <see cref="RecordingModeMinIntervalMs"/>'e
    /// düşürülür → GPU'ya nefes verir. OBS ile ekran kaydı alırken GPU çakışmasını (imleç takılması,
    /// TDR riski) azaltır. Öncelik daima kusursuz uygulama; kayıt ikincil.
    /// </summary>
    public bool   RecordingMode            { get; set; }
    /// <summary>Kayıt modunda minimum kare aralığı (ms) — varsayılan ~15 FPS.</summary>
    public int    RecordingModeMinIntervalMs { get; set; } = 66;
    /// <summary>
    /// Ekran yakalama yöntemi (T2). Dxgi = Desktop Duplication (GPU, ~1-2ms, varsayılan);
    /// Gdi = BitBlt (~15-30ms, fallback). DXGI başlatılamazsa (exclusive-fullscreen/RDP/sürücü)
    /// FarmEngine otomatik GDI'ye düşer — bu yalnız tercih edilen yöntem.
    /// </summary>
    public CaptureBackend CaptureBackend { get; set; } = CaptureBackend.Dxgi;
    /// <summary>
    /// P2: pipelined inference — capture+preprocess (CPU) ve GPU Run+postprocess AYRI thread'lerde üst üste
    /// biner (üretici/tüketici) → seri ~66ms yerine ~max(aşamalar) → FPS ~2× artar. AÇIK varsayılan.
    /// Sorun çıkarsa KAPAT → kanıtlanmış SERİ tek-thread yola döner (regresyon yok).
    /// </summary>
    public bool   PipelinedInference { get; set; } = true;

    // NOT: P3 "CaptureRoi*" (kısmi-ekran yakalama) KALDIRILDI (2026-07-02). CUDA + DXGI sonrası
    // ölçülür faydası kalmadı (inference girdisi zaten ModelInputSize'a küçülür); ham-piksel ROI
    // farklı çözünürlük/DPI'da kayma riskiydi. Eski state.json'lardaki alanlar sessizce yok sayılır.
    /// <summary>
    /// YOLO/ONNX çalıştırma sağlayıcısı (T4). Auto = donanım GPU varsa DirectML, yoksa CPU (DXGI autodetect);
    /// DirectML = zorla GPU (her DX12 GPU: NVIDIA/AMD/Intel); Cpu = zorla CPU.
    /// (NVIDIA TensorRT/CUDA yalnız ayrı 'Cuda' build flavor'ında — HOMEKO_CUDA.)
    /// </summary>
    public InferenceBackend InferenceBackend { get; set; } = InferenceBackend.Auto;
    public int    FaceTargetRetapMs        { get; set; } = 150;
    /// <summary>
    /// Angaje kilidi GÜVENLİK zaman aşımı (ms): bu süre boyunca menzile girilemezse kombo
    /// zorla başlatılır. Dönmeyi asıl kıran "ilerleme yok" (dist 500ms azalmaz) kontrolüdür;
    /// bu yalnız uzun bir emniyet kapağı (uzak moblara erken hava-kombo yapmamak için yüksek).
    /// </summary>
    public int    EngageApproachTimeoutMs  { get; set; } = 3000;

    /// <summary>
    /// Angajman hareket modu (B1):
    /// • ComboHandlesApproach (varsayılan, archer dışı sınıflar): hedef seçilip HP bar doğrulanınca
    ///   kombo HEMEN ateşlenir; oyunun kendi skill pathing'i karaktere yaklaşmayı yaptırır →
    ///   net, tek atımlık, W/R yok.
    /// • ArcherWalkAndFace (archer): AoE/yaylım oklarının değmesi için moba yakın olunmalı →
    ///   "önce yönel (R) sonra kısa düz yürü (W)" döngüsüyle yaklaşır (orbit yok), menzile girince kilitler.
    /// </summary>
    public EngageMovement EngageMovement { get; set; } = EngageMovement.ComboHandlesApproach;

    /// <summary>
    /// Archer modunda komboya başlamadan önce moba ne kadar yaklaşılacağı (ekran piksel, merkez mesafesi).
    /// Küçük = daha yakına koşar (AoE oklarının değmesi için), büyük = daha uzaktan vurur.
    /// Çözünürlük/zoom'a göre UI kaydırıcısından ayarlanır. mobInfo.EngagementRangePx'i geçersiz kılar.
    /// </summary>
    public int    ArcherApproachRangePx    { get; set; } = 70;

    // ── Tıklama hız ayarları ───────────────────────────────────────────────────
    /// <summary>
    /// Mouse move sonrası tıklamadan önceki EK bekleme (ms). 13.tur (P4): 15→0 — ClickAsync
    /// zaten içeride move + 25ms hover-oturması bekliyor, dıştaki bekleme saf tekrardı.
    /// Oyun tıkı yutuyorsa (çok kötü ping/FPS) 15'e geri çıkarılabilir.
    /// </summary>
    public int    ClickPreDelayMs          { get; set; } = 0;
    /// <summary>
    /// Tıklama sonrası EK bekleme (ms). 13.tur (P4): 80→0 — HP-poll ilk iterasyonda zaten bar'a
    /// bakar (yoksa 30ms sonra tekrar, 450ms bütçe); bu bekleme yalnız ilk bakışı geciktiriyordu.
    /// Ping yüksekse 80'e geri çıkarılabilir.
    /// </summary>
    public int    ClickPostDelayMs         { get; set; } = 0;

    // ── Kamera-hareket kapısı (13.tur / S5) ───────────────────────────────────
    /// <summary>
    /// Global sahne-akışı (px/ms) bu eşiğin ÜSTÜNDEyken tıklama ertelenir. Veri (2026-07-11
    /// log+replay): başarılı tıklarda tık-anı akış ~0.1 px/ms, kayıplarda 0.4-2.5 px/ms —
    /// kamera dönerken atılan tık hem ıskalıyor hem izi koparıyor, blacklist yanlış noktaya
    /// yazılıyordu (kayıpların ~%74'ü). 0 = kapı tamamen KAPALI (geri-alma anahtarı).
    /// </summary>
    public float  MotionGatePxPerMs        { get; set; } = 0.20f;
    /// <summary>
    /// Kamera-hareket kapısının hedef başına en fazla bekletme süresi (ms). Dolarsa hedeften
    /// vazgeçilir (blacklist'siz) ve yeniden taranır. Flip quiet penceresi 800ms ile uyumlu;
    /// ScanWaitMsBetween(1000) altında kalmalı.
    /// </summary>
    public int    MotionGateMaxWaitMs      { get; set; } = 700;

    // ── Nüfus muhasebesi (13.tur / S3) ────────────────────────────────────────
    /// <summary>
    /// Farm bölgesindeki normal (avlanan) mob sayısı; guardian SAYILMAZ. Her onaylı kill "borç"
    /// yazar (MobRespawnMs sonra düşer); beklenen-canlı = bu sayı − borç ≤ 0 olunca ekrandaki
    /// hareketsiz adaylar (büyük olasılıkla ceset — model ceset/canlı ayrımı yapamıyor) tıklanmaz.
    /// Spot'a özgüdür — farklı bölgede güncellenmeli. 0 = özellik tamamen KAPALI.
    /// </summary>
    public int    RegionMobCount           { get; set; } = 7;
    /// <summary>
    /// Kill-borcunun düştüğü süre (ms) ≈ mob respawn süresi. Ölçülen ~25sn'nin hafif ALTI tutulur:
    /// bastırma gerçek respawn'ı asla maskeleyemesin (erken düşmenin maliyeti tek ceset-probu,
    /// geç düşmeninki canlı mobu bekletmek).
    /// </summary>
    public int    MobRespawnMs             { get; set; } = 24000;

    // ── Pot ───────────────────────────────────────────────────────────────────
    public bool   HpPotEnabled    { get; set; } = true;
    public bool   MpPotEnabled    { get; set; } = true;
    public int    HpPotPercent    { get; set; } = 50;
    public int    MpPotPercent    { get; set; } = 30;
    public string HpPotKey        { get; set; } = "F1";
    public string MpPotKey        { get; set; } = "F2";

    // ── Kendi HP/MP bar okuma kalibrasyonu (iki-köşe dikdörtgen + HSV) ────────
    // İki köşe tıklanarak çizilir (sol-üst + sağ-alt); barın DOLU olması gerekmez.
    // StatBarReader sütun-tabanlı HSV doluluk okur (HP=kırmızı, MP=mavi). Koordinatlar
    // master pikselde saklanır; runtime'da ResolutionMapper.Map ile geçerli ekrana eşlenir.
    /// <summary>HP bar dikdörtgeninin sol kenarı (X).</summary>
    public int    HpBarLeft       { get; set; }
    /// <summary>HP bar dikdörtgeninin üst kenarı (Y).</summary>
    public int    HpBarTop        { get; set; }
    public int    HpBarWidth      { get; set; } = 200;
    public int    HpBarHeight     { get; set; } = 12;
    public int    MpBarLeft       { get; set; }
    public int    MpBarTop        { get; set; }
    public int    MpBarWidth      { get; set; } = 200;
    public int    MpBarHeight     { get; set; } = 12;

    /// <summary>Bar HSV doluluk eşiği: bu değerin altındaki doygunluk → soluk/gri, sayılmaz.</summary>
    public float  BarHsvMinSat    { get; set; } = 0.35f;
    /// <summary>Bar HSV doluluk eşiği: bu değerin altındaki parlaklık → koyu/boş, sayılmaz.</summary>
    public float  BarHsvMinVal    { get; set; } = 0.25f;

    // DEPRECATED (eski tek-satır RGB eşleştirme) — state.json uyumu için bırakıldı, kullanılmaz.
    public int    HpBarY          { get; set; }
    public int    MpBarY          { get; set; }
    public byte   HpBarFullR      { get; set; } = 220;
    public byte   HpBarFullG      { get; set; } = 50;
    public byte   HpBarFullB      { get; set; } = 50;
    public byte   MpBarFullR      { get; set; } = 50;
    public byte   MpBarFullG      { get; set; } = 50;
    public byte   MpBarFullB      { get; set; } = 220;
    public int    BarColorTol     { get; set; } = 60;

    // ── Loot ──────────────────────────────────────────────────────────────────
    // Varsayılan KAPALI: çoğu oyunda loot otomatik toplanır; açıksa her kill'den
    // sonra fareyi cesete götürüp LootKey'e LootTapsCount kez basar (~1 sn/kill).
    public bool   LootEnabled     { get; set; } = false;
    public string LootKey         { get; set; } = "F";
    public int    LootTapsCount   { get; set; } = 3;
    /// <summary>Loot tap'leri arası bekleme (ms). 8.tur: 200→120 — loot bloğu kill başına ~990ms→~630ms
    /// (loot sırasında tarama durur; kill temposunda görünür duraksamaydı).</summary>
    public int    LootTapDelayMs  { get; set; } = 120;

    // ── Tespit overlay (YOLO kutuları) ────────────────────────────────────────
    /// <summary>Açıksa farm çalışırken YOLO tespit kutuları ekranda çizilir (seçili mob türü).</summary>
    public bool   ShowDetectionOverlay { get; set; } = false;

    // ── Replay/benchmark kaydı (Faz B) — varsayılan KAPALI ────────────────────
    /// <summary>Açıkken farm çalışırken gerçek kareler + tespitler diske kaydedilir (replays/&lt;zaman&gt;/);
    /// offline model/runtime/conf/iou A/B karşılaştırması için (tools/yolo_trainer/src/replay_benchmark.py).
    /// Ayrı worker thread + throttle → farm'ı YAVAŞLATMAZ.</summary>
    public bool   ReplayEnabled    { get; set; } = false;
    /// <summary>Replay kayıt aralığı (ms) — sıklık. 500 ≈ 2 fps (disk-dostu benchmark seti). Min 50.</summary>
    public int    ReplayIntervalMs { get; set; } = 500;
    /// <summary>Replay oturumu başına maksimum kare (disk taşmasını önler). 600 ≈ 5 dk @ 2fps.</summary>
    public int    ReplayMaxFrames  { get; set; } = 600;

    // ── Roam ──────────────────────────────────────────────────────────────────
    public List<WaypointPoint> RoamWaypoints  { get; set; } = [];
    public int    IdleBeforeRoamMs { get; set; } = 4000;

    // ── Dev 2 — Farm lokasyonuna dönüş ────────────────────────────────────────
    // Bot uzak bir spawn'ı kesince kalabalık noktadan uzaklaşıyor → tanıma düşüyor. Bu kadar süre (ms)
    // hiç canlı hedef seçilmez/savaşılmazsa başlangıç farm koordinatına yürünür. OtoFarm: başlangıç koordinatı
    // başlatmada CoordinateReader ile okunur. Otonom: kalibre FarmSpotX/Y kullanılır. Nav+koord kalibre değilse
    // özellik sessizce devre dışı. Kamera-scan (hızlı spawn için) önce çalışır; dönüş uzun-vadeli kurtarmadır.
    /// <summary>Farm lokasyonuna otomatik dönüş açık mı (nav + koordinat okuyucu kalibre gerektirir).</summary>
    public bool   ReturnHomeEnabled        { get; set; } = false;
    /// <summary>Bu kadar süre (ms, vars. 20000) canlı hedef/savaş yoksa başlangıç farm koordinatına dönülür.</summary>
    public int    ReturnHomeIdleMs         { get; set; } = 20000;
    /// <summary>Ev koordinatına bu kadar oyun-biriminden (vars. 30) yakınsa dönüş tetiklenmez (boşa yürüme yok).</summary>
    public int    ReturnHomeMinStrayCoords { get; set; } = 30;

    // ── Kill-switch ───────────────────────────────────────────────────────────
    public string KillSwitchKey   { get; set; } = "F12";

    // ── Scan mod (hedef yoksa kamera çevirme) ────────────────────────────────
    public bool ScanModeEnabled     { get; set; } = true;
    /// <summary>Bu kadar süre (ms) mob bulunamazsa kamera drag başlar. 8.tur: 2000→1000 —
    /// boş sahada ilk kamera-dönüşü kararı 1sn erken gelsin (spawn-tepki hissi).</summary>
    public int  ScanIdleMs          { get; set; } = 1000;
    /// <summary>Her scan adımında sağa sürüklenen piksel (göreli).</summary>
    public int  ScanDragPx          { get; set; } = 100;
    /// <summary>İki scan adımı arasında bekleme (ms).</summary>
    public int  ScanWaitMsBetween   { get; set; } = 1000;
    /// <summary>360° dönüşe karşılık gelen adım sayısı.</summary>
    public int  ScanMaxAttempts     { get; set; } = 6;

    // ── Test Click (click injection diagnostic) ───────────────────────────────
    /// <summary>Kullanıcının seçtiği test tıklaması noktası — inject edilen click'in oyuna ulaşıp
    /// ulaşmadığını doğrulamak için kullanılır (0,0 = kalibre edilmemiş).</summary>
    public int    TestClickX   { get; set; }
    public int    TestClickY   { get; set; }
    /// <summary>Test Click'i tetikleyen global hotkey (oyun önde iken çalışır).</summary>
    public string TestClickKey { get; set; } = "F8";
}

public class WaypointPoint
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Ekran yakalama yöntemi — bkz. <see cref="FarmSettings.CaptureBackend"/>.</summary>
public enum CaptureBackend
{
    /// <summary>DXGI Desktop Duplication (GPU hızlandırmalı, ~1-2ms) — varsayılan.</summary>
    Dxgi,
    /// <summary>GDI BitBlt (~15-30ms) — DXGI kullanılamazsa fallback.</summary>
    Gdi,
}

/// <summary>ONNX execution provider tercihi — bkz. <see cref="FarmSettings.InferenceBackend"/>.</summary>
public enum InferenceBackend
{
    /// <summary>Donanım GPU varsa DirectML, yoksa CPU (DXGI autodetect) — varsayılan.</summary>
    Auto,
    /// <summary>DirectML (her DX12 GPU) — zorla GPU.</summary>
    DirectML,
    /// <summary>CPU — zorla.</summary>
    Cpu,
}

/// <summary>Angajman hareket modu — bkz. <see cref="FarmSettings.EngageMovement"/>.</summary>
public enum EngageMovement
{
    /// <summary>Archer dışı: kombo hemen ateşlenir, oyun pathing'i yaklaştırır (W/R yok).</summary>
    ComboHandlesApproach,
    /// <summary>Archer: "önce yönel sonra kısa yürü" döngüsüyle moba yaklaşır, menzilde kilitler.</summary>
    ArcherWalkAndFace,
}

public enum TargetPriorityMode
{
    /// <summary>Karaktere en yakın mob.</summary>
    Nearest,
    /// <summary>MobInfo.Priority değeri en düşük olan (yüksek öncelik).</summary>
    HighestPriority,
    /// <summary>HP barı en az olan (lowest HP bar pixel ratio).</summary>
    LowestHp,
}
