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
    public bool IsCoordReady => IsCoordCalibrated || IsCoordComboCalibrated;

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

    // Faz 34: BİRLEŞİK koord ROI — tek geniş ROI "X, Y" metninin tamamını kapsar; virgül
    // ayıracıyla bölünür (öncesi X, sonrası Y). X 3↔4 hane değişince ayrı ROI'ler kayıyordu;
    // bu konuma değil VİRGÜLE dayandığından hane sayısı değişse de doğru okur. Kalibre edilirse
    // iki-ROI modunun yerini alır; virgül 11. glyph sınıfı olarak öğretilmeli.
    public int CoordComboRoiX { get; set; }
    public int CoordComboRoiY { get; set; }
    public int CoordComboRoiW { get; set; }
    public int CoordComboRoiH { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCoordComboCalibrated => CoordComboRoiW > 0 && CoordComboRoiH > 0;

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

    /// <summary>A/D dönüş yönü ters mi — minimap koordinat sistemi sol-elli (Y aşağı artıyor) ise
    /// bot hesapladığı yönün tersine döner; bu açıkken A↔D rolü değişir, yakınsama düzelir.</summary>
    public bool  NavTurnInvert      { get; set; } = false;

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

    /// <summary>Nav okumada "ışınlanma" filtresi — son konuma bu birimden uzak okumalar (anlık çöp,
    /// örn. virgül-misdetection) atılır; karakter bir okuma arası bu kadar yol gidemez.</summary>
    public int   NavMaxJumpCoords   { get; set; } = 300;

    /// <summary>Akıcı navigasyon: W SÜREKLİ basılı, yürürken A/D ile düzelt (dur-kalk probe yerine). Çok daha akıcı.</summary>
    public bool  NavContinuous          { get; set; } = true;
    /// <summary>Akıcı modda okuma aralığı (ms) — karakter bu kadar yürür, sonra konum okunup düzeltilir.</summary>
    public int   NavContinuousReadMs    { get; set; } = 250;
    /// <summary>Akıcı modda dönüş kazancı (0.1-1.5) — açı hatasının ne kadarını her düzeltmede dönsün. Düşük=yumuşak kıvrım, yüksek=keskin.</summary>
    public float NavContinuousSteerGain { get; set; } = 0.5f;
    /// <summary>Saf akıcı: W'yi BIRAKMA, yürürken oku+düzelt (en akıcı; koord okuması güvenilir + A/D W basılıyken döndürüyorsa). Kapalı=hibrit (yürü-dur-düzelt).</summary>
    public bool  NavContinuousHold     { get; set; } = false;

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

    /// <summary>Yuva-başına satış: true = sağ-tık; false = sol-tık.</summary>
    public bool   SellRightClick     { get; set; } = true;
    /// <summary>Her yuva satışı arasında bekleme (ms).</summary>
    public int    SellItemDelayMs    { get; set; } = 200;

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

    // ── Faz 36 REWRITE: gerçek KO satış akışı ────────────────────────────────────
    // sol-tık(seç) → sağ-tık(menü) → "I would like to trade." → "Sell Item" sekmesi →
    // mod-değiştir "Confirm" → ikon-eşleşen yuvaları sağ-tık ile satış çantasına taşı →
    // "Sell" → son "Confirm". (sağ-tık=anında-sat DEĞİL; çanta + Sell + Confirm gerekir.)

    /// <summary>"I would like to trade." diyalog butonu (master uzay; 0 = kalibre edilmedi).</summary>
    public int    TradeDialogX       { get; set; }
    public int    TradeDialogY       { get; set; }
    /// <summary>Trade seçildikten sonra alış/satış penceresi açılana dek bekleme (ms).</summary>
    public int    TradeDialogDelayMs { get; set; } = 800;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool   IsTradeDialogCalibrated => TradeDialogX > 0 || TradeDialogY > 0;

    /// <summary>Purchase→Sell mod değişimi onay butonu ("Confirm"; master uzay).</summary>
    public int    SellModeConfirmX       { get; set; }
    public int    SellModeConfirmY       { get; set; }
    /// <summary>Mod onayından sonra satış arayüzü oturana dek bekleme (ms).</summary>
    public int    SellModeConfirmDelayMs { get; set; } = 500;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool   IsSellModeConfirmCalibrated => SellModeConfirmX > 0 || SellModeConfirmY > 0;

    /// <summary>Merchant penceresindeki envanter ızgarası ROI — normal 'I' envanterinden AYRI konumda olabilir (master uzay).</summary>
    public int    MerchantInvGridX { get; set; }
    public int    MerchantInvGridY { get; set; }
    public int    MerchantInvGridW { get; set; }
    public int    MerchantInvGridH { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool   IsMerchantInvGridCalibrated => MerchantInvGridW > 0 && MerchantInvGridH > 0;
    /// <summary>Merchant envanter satır sayısı (28 kare; yön ROI en-boy oranından — geniş→4, uzun→7).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int    MerchantInvRows => MerchantInvGridW >= MerchantInvGridH ? 4 : 7;
    /// <summary>Merchant envanter sütun sayısı (geniş→7, uzun→4).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int    MerchantInvCols => MerchantInvGridW >= MerchantInvGridH ? 7 : 4;

    /// <summary>"Sell" butonu — satış çantasındaki eşyaları satar (master uzay).</summary>
    public int    SellButtonX       { get; set; }
    public int    SellButtonY       { get; set; }
    /// <summary>Sell butonuna basıldıktan sonra onay penceresi belirene dek bekleme (ms).</summary>
    public int    SellButtonDelayMs { get; set; } = 500;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool   IsSellButtonCalibrated => SellButtonX > 0 || SellButtonY > 0;

    /// <summary>Satılacak eşya ikon template'leri (NCC; base64 PNG, en fazla IconMatcher.MaxSamples).</summary>
    public string[] SellTemplatesB64  { get; set; } = System.Array.Empty<string>();
    /// <summary>Bir yuva ikonunun "satılacak" sayılması için minimum NCC skoru (0–1).</summary>
    public float    SellMatchThreshold { get; set; } = 0.80f;
    /// <summary>Satış çantası kapasitesi (KO: 7×2=14). Bir turda en fazla bu kadar eşya taşınır, sonra Sell+Confirm; kalan varsa döngü tekrarlar.</summary>
    public int      SellBagCapacity    { get; set; } = 14;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool     IsSellIconReady => SellTemplatesB64 is { Length: > 0 };

    // ── Faz 35: Town TP + portal etkileşimi ──────────────────────────────────────

    /// <summary>Town ışınlanma tuşu — yalnızca UI butonu kalibre EDİLMEMİŞSE fallback (bu oyunda TP sabit UI butonu).</summary>
    public string TownTpKey    { get; set; } = "F7";

    /// <summary>Town ışınlanma UI butonu konumu (master uzay). Bu oyunda TP kısayol DEĞİL, sabit bir UI butonu → buna sol-tık. 0 = kalibre edilmedi → TownTpKey fallback.</summary>
    public int    TownTpButtonX { get; set; }
    public int    TownTpButtonY { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool   IsTownTpButtonCalibrated => TownTpButtonX > 0 || TownTpButtonY > 0;

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

    // ── Faz 40: Merchant tamir (repair) ──────────────────────────────────────────
    // Satıştan sonra AYNI merchant NPC'de: NPC sol-seç→sağ-menü (satışla aynı offset)
    // → "I want to make repairs." → envanter açılır (çekiç imleç) → seçili giyili ekipman
    // slotlarına sırayla birer sol tık (anında onarır, onay yok) → kapat.

    /// <summary>Tamir adımı etkin mi — kapalıysa Selling'den doğrudan portala geçilir.</summary>
    public bool   RepairEnabled { get; set; } = false;

    /// <summary>"I want to make repairs." diyalog butonu (master uzay; 0 = kalibre edilmedi).</summary>
    public int    RepairDialogX       { get; set; }
    public int    RepairDialogY       { get; set; }
    /// <summary>Diyalogdan sonra envanter (çekiç imleç) açılana dek bekleme (ms).</summary>
    public int    RepairDialogDelayMs { get; set; } = 1000;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool   IsRepairDialogCalibrated => RepairDialogX > 0 || RepairDialogY > 0;

    /// <summary>Giyili ekipman ızgarası ROI (3×5=15 slot; master uzay). Envanterdeki worn-gear paneli.</summary>
    public int    RepairEquipGridX { get; set; }
    public int    RepairEquipGridY { get; set; }
    public int    RepairEquipGridW { get; set; }
    public int    RepairEquipGridH { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool   IsRepairEquipGridCalibrated => RepairEquipGridW > 0 && RepairEquipGridH > 0;
    /// <summary>Ekipman ızgarası sütun sayısı (KO worn-gear: 3).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int    RepairEquipCols => 3;
    /// <summary>Ekipman ızgarası satır sayısı (KO worn-gear: 5).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int    RepairEquipRows => 5;

    /// <summary>Tamir edilecek ekipman slot indeksleri — SEÇİM SIRASINDA (index = satır*3 + sütun, 0-14).</summary>
    public int[]  RepairSlots { get; set; } = System.Array.Empty<int>();

    /// <summary>Tamir için slot tıklamaları arası bekleme (ms).</summary>
    public int    RepairItemDelayMs { get; set; } = 250;
}
