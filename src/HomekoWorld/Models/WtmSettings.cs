namespace HomekoWorld.Models;

public class WtmSettings
{
    public bool    Enabled { get; set; } = false;
    public string? ComboId { get; set; }

    // ── HP bar tespiti — renk tarama (yeni, birincil yöntem) ──────────────────
    // Tek tıklamayla kalibre edilir: kullanıcı hedef HP bar'ının kırmızı bölgesine tıklar.
    // Örneklenen renk + otomatik tespit edilen bar genişliğiyle 1 satır piksel taraması yapılır.
    // Bar görünüyorsa (mob canlı) ≥ HpColorMinPx piksel eşleşir; kaybolunca 0 eşleşir.
    public int  HpColorScanX     { get; set; }        // Tıklanan nokta X (fiziksel piksel)
    public int  HpColorScanY     { get; set; }        // Taranacak satır Y
    public int  HpColorScanHalfW { get; set; } = 100; // Bar yarı genişliği (otomatik tespit)
    public byte HpColorR         { get; set; } = 200; // Örneklenen renk
    public byte HpColorG         { get; set; } = 30;
    public byte HpColorB         { get; set; } = 30;
    public int  HpColorTol       { get; set; } = 60;  // Renk toleransı (±)
    public int  HpColorMinPx     { get; set; } = 5;   // Minimum eşleşen piksel → canlı

    /// <summary>Renk tarama kalibrasyonu tamamlanmış mı?</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTargetHpColorCalibrated => HpColorScanX > 0 && HpColorScanY > 0;

    // Character center — where the character model appears on screen (calibrated once)
    public int CharacterCenterX { get; set; }
    public int CharacterCenterY { get; set; }

    // Green ring color — HSV hue-based for lighting robustness
    public int RingHue          { get; set; } = 120;
    public int RingHueTolerance { get; set; } = 25;
    public int MinRingPixels    { get; set; } = 30;

    // Adjacency detection — scan ONLY a small window around CharacterCenter for ring pixels.
    // Far-away grass is excluded; only ring pixels right next to the character are counted.
    public int CenterWindowRadius { get; set; } = 80;

    // Walk keys — KO default: W = forward, R = face target
    public string WalkKey      { get; set; } = "W";
    public string DirectionKey { get; set; } = "R";

    public int PollIntervalMs  { get; set; } = 100;

    // ── Koruma mobu (guardian) isim etiketi renk tespiti ─────────────────────
    // Target HP bar'ının NameplateOffsetY piksel üstündeki satır taranır.
    // Normal mob ismi (mor) ve koruma mobu ismi (kırmızı) ayrı ayrı kalibre edilir.
    public bool GuardianDetectionEnabled { get; set; } = true;
    // 9.tur: Unknown = isim OKUNAMADI (bant boş/oklüzyon/duyuru geçişi) — guardian HÜKMÜ değil, okuma-
    // kalitesi sinyali. Strict modda Unknown'a saldırılmaz: kısa konum atlaması (iz-damga YOK; okuma
    // düzelince mob geri alınır). Unattended kullanım için varsayılan AÇIK — guardian'a yanlışlıkla
    // vurmak birkaç normal mob kaçırmaktan daha pahalı.
    public bool GuardianUnknownStrict    { get; set; } = true;
    public int  NameplateOffsetY         { get; set; } = -22; // HpColorScanY + bu ofset = nameplate Y
    public byte NormalNameR              { get; set; }
    public byte NormalNameG              { get; set; }
    public byte NormalNameB              { get; set; }
    public byte GuardianNameR            { get; set; }
    public byte GuardianNameG            { get; set; }
    public byte GuardianNameB            { get; set; }
    public int  NameplateColorTol        { get; set; } = 50;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsNameplateCalibrated =>
        (NormalNameR > 0 || NormalNameG > 0 || NormalNameB > 0) &&
        (GuardianNameR > 0 || GuardianNameG > 0 || GuardianNameB > 0);

    // ── Koruma isim bandı — kullanıcının çizdiği dikdörtgen (fiziksel piksel) ──
    // HP bar ML ROI kalibrasyonuyla aynı UX ile çizilir; HSV kırmızı tespiti bu
    // bandı tarar. Sabit ofset (NameplateOffsetY) yalnızca bant kalibre değilse
    // fallback olarak kullanılır.
    public int NameBandX { get; set; }
    public int NameBandY { get; set; }
    public int NameBandW { get; set; }
    public int NameBandH { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsNameBandCalibrated =>
        NameBandX > 0 && NameBandY > 0 && NameBandW > 0 && NameBandH > 0;

    // ── HSV kırmızı (koruma) isim tespiti eşikleri ───────────────────────────
    // Bant pikselleri HSV'ye çevrilir; siyah çerçeve/gölge (düşük V) ve beyaz/gri
    // (düşük S) elenir. Kalan "renkli yazı" pikselleri içinde kırmızı oranı eşiği
    // geçerse koruma mobu. Kırmızı bandı dar tutulur (magenta ~300-330 dışarıda).
    // 10.tur (2026-07-09): eski 0,35/0,50 kullanıcının KENDİ kalibre ettiği Normal referansının
    // (S≈0,32) ALTINDAydı → "renkli yazı" filtresi neredeyse hiç piksel geçirmiyordu (canlı log:
    // 105 kontrolden 104'ü Unknown). İlk düzeltmede İKİSİ de (0,12/0,20) fazla gevşetildi — eski
    // koyu Guardian örneği (V≈0,17) yüzünden V'yi de indirmiştim, ama asıl sorun yalnız S'ydi.
    // Sonuç: gevşek V karanlık arka-plan/gölge pikselini de "yazı" sayıp guardian oy-oranını
    // sulandırdı → canlı testte guardian 2× YANLIŞ Normal sayılıp vuruldu (0 guardian hiç
    // yakalanmadı). 10.tur-b: V neredeyse eski değerine (0,30) geri çekildi — ne Normal (V≈0,78)
    // ne yeni kalibre Guardian (V≈0,86, kullanıcı parlak kırmızı yeniden seçti) düşük V gerektirmiyor,
    // düşük V'nin TEK amacı zaten sağlam-parlak iki referansın arasına giren gölge/panel pikselini
    // elemekti. S 0,25'te bırakıldı (Normal'in 0,32'sinin altında, ama 0,20'den daha az gürültü alır).
    public float NameplateMinVal   { get; set; } = 0.30f; // < bu = siyah çerçeve/gölge → ele
    public float NameplateMinSat   { get; set; } = 0.25f; // < bu = beyaz/gri → renkli yazı sayma
    public int   NameplateRedHueLo { get; set; } = 12;    // hue <= bu → kırmızı (alt uç)
    public int   NameplateRedHueHi { get; set; } = 348;   // hue >= bu → kırmızı (360° sarması)
    // 10.tur-d (2026-07-09): iki TEMİZ canlı ölçüm (10.tur-c telemetrisiyle) — guardian hedefte
    // guardianVotes=**2512** (2 bağımsız oturumda AYNI sayı), normal mobda guardianVotes=**0**. Hue-band
    // filtresi (10.tur-c) bu sayıyı HİÇ değiştirmedi → mesele "alakasız renk" değil: isim "[Random] Wild
    // Tyon" gibi uzun bir string + HER nameplate'te bulunan sabit bir arka-plan paneli var, panelin rengi
    // Normal referansına (270°) yakın okunuyor (30° filtreyi GEÇİYOR, o yüzden elenmiyor) → payda (~6900)
    // panel+metin karışımı oluyor, oran (2512/6900=%36) %45 eşiğinin altında kalıyor — bu UI'da oran testi
    // YAPISAL olarak kırık (panel her zaman birkaç bin "normal-renkli" piksel katıyor). 2512 vs 0 arasında
    // devasa bir boşluk var → mutlak eşiğe güvenmek yeterli ve sağlam: RedMinPx 12→200 (2512'nin çok altı,
    // gürültüden çok üstü), RedFrac 0,45→0,15 (artık pratik sınırlayıcı DEĞİL, yalnız ek güvenlik).
    public float NameplateRedFrac  { get; set; } = 0.15f; // kırmızı / renkli-yazı oranı eşiği (ikincil)
    public int   NameplateRedMinPx { get; set; } = 200;   // mutlak min kırmızı piksel (asıl ayırt edici)
    // 10.tur-c: ref-hue modunda bir piksel NE normale NE guardian'a (ikisinin en yakınına göre) yeterince
    // benzemiyorsa (dünya-uzayı/tamamen alakasız renk) textPixels'e HİÇ girmesin. NOT (10.tur-d): bu ölçülen
    // vak'ada aktif sınırlayıcı değildi (panel zaten Normal'e yakın okunuyordu) ama gelecekte gerçekten
    // alakasız bir renk sızarsa hâlâ zararsız bir koruma.
    public float NameplateRefHueMaxDist { get; set; } = 30f; // en yakın referanstan bu dereceden uzak piksel ele

    // ── Mob HP bar ROI (iki-köşe dikdörtgen) ─────────────────────────────────
    // (HpBarRoiX, HpBarRoiY, W, H) bölgesi HSV kırmızı-oran tespiti için kullanılır.
    public int    HpBarRoiX               { get; set; }
    public int    HpBarRoiY               { get; set; }
    public int    HpBarRoiW               { get; set; } = 240;
    public int    HpBarRoiH               { get; set; } = 60;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHpBarRoiCalibrated => HpBarRoiX > 0 && HpBarRoiY > 0;

    /// <summary>Hedef HP bar konumu HERHANGİ bir yöntemle kalibre edildi mi
    /// (ML ROI kutusu VEYA tek-tık renk noktası). HSV/ML/ColorScan modlarının ortak "yeri biliniyor" kapısı.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHpBarLocated => IsHpBarRoiCalibrated || IsTargetHpColorCalibrated;

    // ── HP bar tespit yöntemi + HSV kırmızı eşikleri ─────────────────────────
    // Varsayılan Hsv: koruma mobu nameplate yöntemiyle aynı hızlı HSV kırmızı-oran.
    // ÖNCELİK ML ROI kutusunu (HpBarRoiX/Y/W/H) tarar → kullanıcının zaten kalibre ettiği
    // bölge; ayrı HSV kalibrasyonu GEREKMEZ. ROI yoksa tek-satır HpColorScan'e düşer.
    // 2D kutu taraması düşük HP'de bile bar yüksekliği boyunca bol kırmızı verir (azalan HP ≠ yok olan bar).
    public HpBarDetectionMode HpBarMode { get; set; } = HpBarDetectionMode.Hsv;
    public int   HpHueLo     { get; set; } = 12;     // hue <= bu → kırmızı (alt uç)
    public int   HpHueHi     { get; set; } = 348;    // hue >= bu → kırmızı (360° sarması)
    public float HpHsvMinSat { get; set; } = 0.45f;  // < bu = soluk/gri → say­ma
    public float HpHsvMinVal { get; set; } = 0.30f;  // < bu = koyu/gölge → sayma
    public int   HpHsvMinPx  { get; set; } = 5;      // ≥ bu kırmızı piksel → bar görünür (canlı)

    // ── Düşük-HP sahte-ölüm fix: boş-bar koyu-oluk tespiti ───────────────────
    // Çok düşük HP'de (örn. 14/14089) kırmızı dilim ~görünmez olur; barın koyu/siyah OLUĞU hâlâ durur.
    // "Kırmızı yok = öldü" yanlış. Mob ÖLÜNCE KO hedef-penceresi ANINDA tamamen kaybolur → ROI çimen/gök/
    // taşa düşer (parlak/renkli/değişken → koyu-oluk oranı çöker). Çözüm: ROI'de geniş bir koyu-oluk bandı
    // varsa, kırmızı 0 olsa bile CANLI say; ne kırmızı ne oluk varsa ÖLÜ. Bir piksel "oluk" sayılır:
    // val ≤ HpTroughMaxVal (koyu) VE sat ≤ HpTroughMaxSat (doygunsuz/gri).
    public float HpTroughMaxVal  { get; set; } = 0.28f; // ≤ bu parlaklık → koyu (boş bar zemini/çerçeve)
    public float HpTroughMaxSat  { get; set; } = 0.40f; // ≤ bu doygunluk → doygunsuz/gri (oluk)
    // "Bar var" kararı (WtmVision.BarPresent): red≥HpHsvMinPx VE (kırmızı+koyu-oluk)/toplam ≥ bu — AND.
    // Dolu-oranı, ROI'yi doldurmayan KIRMIZI arka planı (kırmızı obje; red yüksek ama dolu düşük) eler;
    // saf-kırmızı taban ise KOYU arka planı (red~0) eler. Gerçek HP barı her HP'de ROI'yi ~%74+ doldurur
    // (kırmızı+koyu-oluk). Canlı test verisi (2026-07-01): kırmızı arka plan dolu≈%29, gerçek bar dolu≈%74-91
    // (düşük-HP dahil) → eşik ~0.60 ikisini güvenle ayırır. NOT: eski OR (red VEYA dolu) koyu-çalıyı sahte-canlı
    // yapıyordu; AND o regresyonu önler. "Bar var sanıyor / seçili sanıyor" olursa BÜYÜT, düşük-HP'yi kaçırırsa KÜÇÜLT.
    public float HpBarFillMinFrac { get; set; } = 0.60f;
    // #5 Karanlık-zemin guard: darkFrac (koyu-oluk/toplam) ≥ bu VE kırmızı yoksa → ROI tek-renk karanlık
    // zemin (bar yok/ölü), "canlı" sayma. Gerçek boş bar ~%79-88 dolu kalır (kenarlar koyu değil) → 0.95
    // güvenli; karanlık mağara/yükleme ~%100 koyu olur. Karanlık haritada ölüm-tespitini düzeltir.
    public float HpTroughAllDarkMaxFrac { get; set; } = 0.95f;

    // ── Duyuru kayması (alan 1 / alan 2) ─────────────────────────────────────
    /// <summary>
    /// Üstten duyuru/announcement geçerken mob HP barı + ismi belirli miktar AŞAĞI kayar.
    /// Bu, normal konumdan kayık konuma dikey ofset (master piksel, &gt;0). Tespit önce normal
    /// (alan 1), bulamazsa +AnnounceShiftY (alan 2) konumunu dener — HP barı ve isim birlikte
    /// kaydığı için ikisine de uygulanır. 0 = kapalı. Runtime'da ResolutionMapper.ScaleLen ile ölçeklenir.
    /// </summary>
    public int AnnounceShiftY { get; set; }

    // ── V4 (2026-07-02) — hedef penceresi ÇERÇEVE şablonu (yapı-tabanlı seçili/canlı/öldü) ─────
    // "Pencere açık mı?" artık renk sayımından değil, HP'den bağımsız SABİT çerçeve parçasının NCC
    // eşleşmesinden okunur (kullanıcı doğruladı: çerçeve sabit, boş barda oluk+çerçeve yerinde kalır,
    // bölgede çakışan UI yok). Pencere ekranda sabit konumda → kayan arama YOK, yalnız 2 sabit konum
    // (offset 0 ve +AnnounceShiftY) puanlanır. Öğretilmemişse tüm sistem eski renk (V3) yoluyla çalışır.
    public int      TargetFrameRectX { get; set; }
    public int      TargetFrameRectY { get; set; }
    public int      TargetFrameRectW { get; set; }
    public int      TargetFrameRectH { get; set; }
    /// <summary>Çerçeve şablonları (≤3 örnek, base64 PNG — yakalandıkları fiziksel boyutta; runtime'da ölçeklenir).</summary>
    public string[] TargetFrameTemplatesB64 { get; set; } = System.Array.Empty<string>();
    /// <summary>NCC ≥ bu → çerçeve VAR (pencere açık). Test butonuyla gözlenen skora göre ayarlanabilir.</summary>
    public float    TargetFrameMatchThreshold  { get; set; } = 0.80f;
    /// <summary>NCC &lt; bu → çerçeve YOK. Aradaki bant histerezis: son durum korunur (sınırda titreme yok).</summary>
    public float    TargetFrameAbsentThreshold { get; set; } = 0.65f;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTargetFrameCalibrated =>
        TargetFrameRectW > 0 && TargetFrameRectH > 0 && TargetFrameTemplatesB64.Length > 0;
}

/// <summary>Hedef HP bar varlık tespiti yöntemi.</summary>
public enum HpBarDetectionMode
{
    /// <summary>HSV kırmızı-oran (varsayılan, en hızlı; koruma mobu yöntemiyle aynı).</summary>
    Hsv = 0,
    /// <summary>RGB renk taraması (HpColor* eşikleri). (1 = eski Ml, kaldırıldı; ColorScan değeri state.json uyumu için korunur.)</summary>
    ColorScan = 2,
}
