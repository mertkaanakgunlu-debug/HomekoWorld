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
    public float NameplateMinVal   { get; set; } = 0.35f; // < bu = siyah çerçeve/gölge → ele
    public float NameplateMinSat   { get; set; } = 0.50f; // < bu = beyaz/gri → renkli yazı sayma
    public int   NameplateRedHueLo { get; set; } = 12;    // hue <= bu → kırmızı (alt uç)
    public int   NameplateRedHueHi { get; set; } = 348;   // hue >= bu → kırmızı (360° sarması)
    public float NameplateRedFrac  { get; set; } = 0.45f; // kırmızı / renkli-yazı oranı eşiği
    public int   NameplateRedMinPx { get; set; } = 12;    // mutlak min kırmızı piksel

    // ── ML HP bar ROI + binary classifier ────────────────────────────────────
    // CaptureHpBarRoi ile (HpBarRoiX, HpBarRoiY, W, H) bölgesi her tick alınır.
    // hpbar_classifier.onnx yüklüyse HpBarPresenceClassifier predict eder.
    public int    HpBarRoiX               { get; set; }
    public int    HpBarRoiY               { get; set; }
    public int    HpBarRoiW               { get; set; } = 240;
    public int    HpBarRoiH               { get; set; } = 60;
    public string HpBarClassifierPath     { get; set; } = "Assets/HpBar/hpbar_classifier.onnx";
    public float  HpBarClassifierThreshold { get; set; } = 0.6f;
    public int    HpBarTemporalWindow      { get; set; } = 6;
    public int    HpBarTemporalMinPositive { get; set; } = 4;
    /// <summary>ML modunda her HP kontrolünde debug_hp.txt'ye yaz (varsayılan KAPALI — senkron disk I/O performansı düşürür).</summary>
    public bool   HpBarDebugLog            { get; set; } = false;

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

    // ── Duyuru kayması (alan 1 / alan 2) ─────────────────────────────────────
    /// <summary>
    /// Üstten duyuru/announcement geçerken mob HP barı + ismi belirli miktar AŞAĞI kayar.
    /// Bu, normal konumdan kayık konuma dikey ofset (master piksel, &gt;0). Tespit önce normal
    /// (alan 1), bulamazsa +AnnounceShiftY (alan 2) konumunu dener — HP barı ve isim birlikte
    /// kaydığı için ikisine de uygulanır. 0 = kapalı. Runtime'da ResolutionMapper.ScaleLen ile ölçeklenir.
    /// </summary>
    public int AnnounceShiftY { get; set; }
}

/// <summary>Hedef HP bar varlık tespiti yöntemi.</summary>
public enum HpBarDetectionMode
{
    /// <summary>HSV kırmızı-oran (varsayılan, en hızlı; koruma mobu yöntemiyle aynı).</summary>
    Hsv,
    /// <summary>ONNX binary classifier + temporal smoothing (eski birincil).</summary>
    Ml,
    /// <summary>RGB renk taraması (HpColor* eşikleri).</summary>
    ColorScan,
}
