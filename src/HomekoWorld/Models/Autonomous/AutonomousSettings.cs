namespace HomekoWorld.Models.Autonomous;

/// <summary>
/// Otonom Oyuncu yapılandırması. FarmEngine üstünde lojistik döngüyü
/// (farm → envanter dolu → town → sat → dön) yöneten orkestratörün ayarları.
/// Faz 31'de çekirdek alanlar; koordinat ROI, waypoint zincirleri, NPC/portal/merchant
/// kalibrasyonları sonraki fazlarda buraya eklenir.
/// </summary>
public class AutonomousSettings
{
    /// <summary>Otonom Oyuncu özelliği etkin mi (hotkey gate'i; Faz 37).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Otonom modu açıp kapayan global hotkey (Faz 37).</summary>
    public string HotKey { get; set; } = "F10";

    /// <summary>FSM tick aralığı (ms). Durum geçişleri seyrek → düşük frekans yeterli.</summary>
    public int TickMs { get; set; } = 300;

    /// <summary>
    /// Art arda bu kadar hata olursa otonom mod durur (Faz 37 dayanıklılık).
    /// Her başarılı durum geçişinde sayaç sıfırlanır.
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>Kaç kill'de bir envanter "dolu mu" kontrolü yapılsın (Faz 33).</summary>
    public int InventoryCheckEveryKills { get; set; } = 30;

    // ── Faz 37: Kalibrasyon özeti (hesaplanmış; JSON'a yazılmaz) ──────────────────

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCoordReady => IsCoordCalibrated;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsWaypointsReady =>
        (MerchantX != 0 || MerchantY != 0) &&
        (PortalX   != 0 || PortalY   != 0) &&
        (FarmSpotX != 0 || FarmSpotY != 0);

    /// <summary>
    /// Tüm zorunlu kalibrasyonlar tamamsa true (Faz 37 ön-kontrol).
    /// Glyph hazırlığı (10/10) MainViewModel'de kontrol edilir, burada ROI/waypoint/envanter yeterli.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFullyConfigured =>
        IsCoordCalibrated &&
        IsInventoryGridCalibrated &&
        IsWaypointsReady;

    // ── Faz 33: Envanter doluluğu ─────────────────────────────────────────────

    /// <summary>Envanteri açıp kapatan tuş.</summary>
    public string InventoryKey             { get; set; } = "I";

    /// <summary>Tuşa basıldıktan sonra envanter penceresi (açılma animasyonu dahil) TAM açılana
    /// dek bekleme (ms). Çok kısa olursa yarı-açık/animasyonlu kare yakalanır → hatalı doluluk okuması.</summary>
    public int    InventoryOpenDelayMs     { get; set; } = 1000;

    /// <summary>Envanter ızgara ROI sol-üst köşe ve boyutu (master uzayda, ResolutionMapper ile ölçeklenir).</summary>
    public int    InventoryGridX { get; set; }
    public int    InventoryGridY { get; set; }
    public int    InventoryGridW { get; set; }
    public int    InventoryGridH { get; set; }

    // KO envanteri = 28 kare; YÖN istemciye göre değişir (bu oyunda 7 geniş × 4 yüksek).
    // Önceki "sabit 4×7" varsayımı YANLIŞTI → 7×4 ızgarayı 4×7 bölünce hücreler kayıp boş
    // kareler "dolu" okunuyordu (%89). Yuvalar KARE olduğundan yön, kalibre edilen ROI'nin
    // en-boy oranından türetilir: geniş (W≥H) → 7 sütun × 4 satır; uzun (W<H) → 4 sütun × 7 satır.
    /// <summary>Envanter satır sayısı — geniş ROI'de 4, uzun ROI'de 7 (28 kare; yön ROI'den otomatik).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int    InventoryRows => InventoryGridW >= InventoryGridH ? 4 : 7;

    /// <summary>Envanter sütun sayısı — geniş ROI'de 7, uzun ROI'de 4 (28 kare; yön ROI'den otomatik).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int    InventoryCols => InventoryGridW >= InventoryGridH ? 7 : 4;

    /// <summary>Bu oran veya üstündeyse envanter "dolu" sayılır (0.0–1.0).</summary>
    public float  InventoryFullThreshold   { get; set; } = 0.85f;

    // Yuva içerik eşikleri (parlaklık/renk/kapsama) InventorySlotScanner içinde sabit.

    /// <summary>Envanter ızgara ROI kalibre edildi mi.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsInventoryGridCalibrated => InventoryGridW > 0 && InventoryGridH > 0;

    // ── Faz 32: Koordinat okuma (minimap X/Y) ─────────────────────────────────
    // İki ayrı ROI: kullanıcı X sayısının ve Y sayısının üstüne ayrı dikdörtgen çizer
    // (KO yerleşiminden bağımsız çalışır). Koordinatlar master uzayda saklanır (ResolutionMapper).
    public int CoordXRoiX { get; set; }
    public int CoordXRoiY { get; set; }
    public int CoordXRoiW { get; set; }
    public int CoordXRoiH { get; set; }
    public int CoordYRoiX { get; set; }
    public int CoordYRoiY { get; set; }
    public int CoordYRoiW { get; set; }
    public int CoordYRoiH { get; set; }

    /// <summary>Öğretilen rakam glyph'leri — çok örnekli (0-9, her rakam için en fazla 5 PNG base64). DigitGlyphsB64'ün yerini aldı.</summary>
    public string[][] DigitGlyphsMulti { get; set; } = new string[10][];
    /// <summary>Eski tek-örnekli alan (geriye dönük uyumluluk için okunur, artık yazılmaz).</summary>
    public string[] DigitGlyphsB64 { get; set; } = new string[10];

    /// <summary>Binarizasyon eşiği (0-255); -1 = otomatik (Otsu).</summary>
    public int CoordBinThreshold { get; set; } = -1;
    /// <summary>Rakamlar koyu zemine AÇIK ise false; açık zemine KOYU ise true.</summary>
    public bool CoordBinInvert { get; set; } = false;
    /// <summary>İki rakam arası minimum boşluk (px) — segmentasyonu böler.</summary>
    public int CoordMinGapPx { get; set; } = 2;
    /// <summary>Minimum rakam genişliği (px) — bundan dar run'lar gürültü sayılır.</summary>
    public int CoordMinDigitW { get; set; } = 2;
    /// <summary>Rakam kabul güven eşiği — altındaki tahmin "okunamadı" yapar.</summary>
    public float MinDigitConfidence { get; set; } = 0.60f;

    /// <summary>X ve Y ROI'leri çizilmiş mi.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCoordCalibrated =>
        CoordXRoiW > 0 && CoordXRoiH > 0 && CoordYRoiW > 0 && CoordYRoiH > 0;

    // ── Faz 34: WorldNavigator (probe-correct koordinat navigasyonu) ──────────────

    /// <summary>Hedefe bu kadar koordinat biriminden yaklaşınca "varıldı" sayılır.</summary>
    public int   NavToleranceCoords { get; set; } = 15;

    /// <summary>Yön ölçme için W probu süresi (ms). Kısa tutunca step sayısı artar; uzun tutunca daha stabil heading.</summary>
    public int   NavProbeMs         { get; set; } = 500;

    /// <summary>Adım başına maksimum W süresi (ms). Yakın hedefe otomatik kısaltılır (overshoot önleme).</summary>
    public int   NavStepMs          { get; set; } = 800;

    /// <summary>Kamera right-drag: piksel başına derece (Farm scan overlay için; navigasyonda kullanılmaz).</summary>
    public float NavCameraPixPerDeg { get; set; } = 8.5f;

    /// <summary>Kamera döndürme sonrası yerleşme bekleme (ms).</summary>
    public int   NavCameraMs        { get; set; } = 80;

    /// <summary>Kamera döndürme yönü ters mi.</summary>
    public bool  NavCameraInvert    { get; set; } = false;

    /// <summary>Navigasyon dönüşü için A/D tuş süresi (ms/derece). 2000 / ölçülen_derece ile kalibre edilir.</summary>
    public float NavTurnMsPerDeg    { get; set; } = 10f;

    /// <summary>Sola dönüş tuşu (varsayılan A).</summary>
    public string NavTurnKeyLeft    { get; set; } = "A";

    /// <summary>Sağa dönüş tuşu (varsayılan D).</summary>
    public string NavTurnKeyRight   { get; set; } = "D";

    /// <summary>N ardışık probe'da hareket yok → takılma kurtarması.</summary>
    public int   NavStuckThreshold  { get; set; } = 3;

    /// <summary>Güvenlik: bu adım sayısından sonra TimeoutException fırlatır.</summary>
    public int   NavMaxSteps        { get; set; } = 300;

    /// <summary>Koordinat okuma deneme sayısı (glyph bazen bir kare okunamayabilir).</summary>
    public int   NavReadRetries     { get; set; } = 5;

    /// <summary>Okuma denemeleri arası bekleme (ms).</summary>
    public int   NavReadRetryMs     { get; set; } = 120;

    // ── Faz 34: Waypoint'ler (oyun koordinatları; ResolutionMapper bağımsız) ──────

    /// <summary>Town'daki merchant NPC oyun koordinatı.</summary>
    public int MerchantX { get; set; }
    public int MerchantY { get; set; }

    /// <summary>Town'daki farm portali oyun koordinatı (Faz 35).</summary>
    public int PortalX { get; set; }
    public int PortalY { get; set; }

    /// <summary>Farm alanındaki başlangıç noktası oyun koordinatı (portal sonrası).</summary>
    public int FarmSpotX { get; set; }
    public int FarmSpotY { get; set; }

    // ── Faz 36: Merchant NPC etkileşimi ve satış ─────────────────────────────────

    /// <summary>Merchant NPC tıklama X ofseti (ekran merkezinden piksel cinsinden).</summary>
    public int    MerchantClickOffsetX    { get; set; } = 0;
    /// <summary>Merchant NPC tıklama Y ofseti (ekran merkezinden piksel cinsinden).</summary>
    public int    MerchantClickOffsetY    { get; set; } = 0;
    /// <summary>true = sağ-tık (açılır menü); false = sol-tık (doğrudan etkileşim).</summary>
    public bool   MerchantRightClick      { get; set; } = true;
    /// <summary>NPC tıklamasından sonra diyalog/mağaza açılana dek bekleme (ms).</summary>
    public int    MerchantInteractDelayMs { get; set; } = 1200;

    /// <summary>Satış sekmesi ekran konumu X (master uzay; 0 = kalibre edilmedi).</summary>
    public int    SellTabX       { get; set; }
    /// <summary>Satış sekmesi ekran konumu Y (master uzay; 0 = kalibre edilmedi).</summary>
    public int    SellTabY       { get; set; }
    /// <summary>Satış sekmesi tıklamasından sonra bekleme (ms).</summary>
    public int    SellTabDelayMs { get; set; } = 600;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool   IsSellTabCalibrated => SellTabX > 0 || SellTabY > 0;

    /// <summary>true = tek bir "Hepsini Sat" butonuna bas; false = her dolu yuvayı ayrı ayrı tıkla.</summary>
    public bool   UseSellAllButton          { get; set; } = false;
    /// <summary>"Hepsini Sat" butonu X (master uzay).</summary>
    public int    SellAllButtonX            { get; set; }
    /// <summary>"Hepsini Sat" butonu Y (master uzay).</summary>
    public int    SellAllButtonY            { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool   IsSellAllButtonCalibrated => SellAllButtonX > 0 || SellAllButtonY > 0;

    /// <summary>Yuva-başına satış: true = sağ-tık; false = sol-tık.</summary>
    public bool   SellRightClick     { get; set; } = true;
    /// <summary>Her satış sonrası basılacak onay tuşu (boş = yok).</summary>
    public string SellConfirmKey     { get; set; } = "";
    /// <summary>Her yuva satışı arasında bekleme (ms).</summary>
    public int    SellItemDelayMs    { get; set; } = 200;
    /// <summary>Onay diyaloğu bekleme (ms) — SellConfirmKey öncesi.</summary>
    public int    SellConfirmDelayMs { get; set; } = 300;

    /// <summary>Toplu satış onay butonu X (master uzay; 0 = kullanılmıyor).</summary>
    public int    SellConfirmButtonX            { get; set; }
    /// <summary>Toplu satış onay butonu Y (master uzay; 0 = kullanılmıyor).</summary>
    public int    SellConfirmButtonY            { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool   IsSellConfirmButtonCalibrated => SellConfirmButtonX > 0 || SellConfirmButtonY > 0;
    /// <summary>Toplu onay butonuna basıldıktan sonra bekleme (ms).</summary>
    public int    SellConfirmButtonDelayMs      { get; set; } = 500;

    /// <summary>Satış penceresi kapanış tuşu (boş = Escape).</summary>
    public string MerchantCloseKey     { get; set; } = "Escape";
    /// <summary>Kapat komutu sonrası bekleme (ms).</summary>
    public int    MerchantCloseDelayMs { get; set; } = 300;

    // ── Faz 35: Town TP + portal etkileşimi ──────────────────────────────────────

    /// <summary>Town ışınlanma tuşu (KO'da genellikle F7 veya sınıfa özgü).</summary>
    public string TownTpKey    { get; set; } = "F7";

    /// <summary>Town TP sonrası harita yükleme bekleme süresi (ms). Yükleme bitene kadar koord okunamaz.</summary>
    public int    TownTpWaitMs { get; set; } = 10_000;

    /// <summary>Portal tıklama X offseti (ekran merkezinden; piksel). Portal sağda/solda ise ayarla.</summary>
    public int    PortalClickOffsetX   { get; set; } = 0;

    /// <summary>Portal tıklama Y offseti (ekran merkezinden; piksel). Portal yukarıda olduğu için varsayılan negatif.</summary>
    public int    PortalClickOffsetY   { get; set; } = -60;

    /// <summary>Tıklama → onay mesajı belirme bekleme (ms).</summary>
    public int    PortalInteractDelayMs { get; set; } = 800;

    /// <summary>Portal girişini onaylayan tuş. Boş → Enter. KO'da genellikle Enter yeterlidir.</summary>
    public string PortalConfirmKey     { get; set; } = "";

    /// <summary>Portal geçişi sonrası harita yükleme bekleme süresi (ms).</summary>
    public int    PortalWaitMs         { get; set; } = 10_000;
}
