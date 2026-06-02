using System.Text.Json.Serialization;

namespace HomekoWorld.Models.Farm;

/// <summary>
/// Bir mob türünün tanım verisi. mobs.json'dan yüklenir.
/// Faz 17 YOLO farm bot — class_id → YOLO sınıf eşleştirmesi.
/// </summary>
public class MobInfo
{
    /// <summary>YOLO class id (0-based, data.yaml sırası ile aynı).</summary>
    [JsonPropertyName("id")]
    public int    Id                 { get; set; }

    /// <summary>Oyun içi Türkçe mob ismi (gösterim için).</summary>
    [JsonPropertyName("name_tr")]
    public string NameTr             { get; set; } = "";

    /// <summary>YOLO etiket ismi (data.yaml'daki isim, İngilizce/snake_case).</summary>
    [JsonPropertyName("name_en")]
    public string NameEn             { get; set; } = "";

    /// <summary>Hedef seçim önceliği (0=en yüksek, 100=en düşük).</summary>
    [JsonPropertyName("priority")]
    public int    Priority           { get; set; } = 50;

    /// <summary>Mob'a yaklaşıldığında ateşlenecek kombo ID'si (Combo.Id).</summary>
    [JsonPropertyName("combo_id")]
    public string ComboId            { get; set; } = "";

    /// <summary>
    /// Moba hangi ekran piksel mesafesinde "yakın" sayılacak.
    /// </summary>
    [JsonPropertyName("engagement_range_px")]
    public int    EngagementRangePx  { get; set; } = 100;

    /// <summary>
    /// İlk tıklama denemesinde bbox yüksekliğine göre Y kayması (negatif = yukarı = isim etiketi).
    /// Örn: -0.35 → kutunun %35 yukarısı (varsayılan KO isim etiketi konumu).
    /// Her mob için CVAT eğitimi sonrası ince ayar yapılabilir.
    /// </summary>
    [JsonPropertyName("click_offset_y_pct")]
    public float  ClickOffsetYPct    { get; set; } = -0.35f;
}
