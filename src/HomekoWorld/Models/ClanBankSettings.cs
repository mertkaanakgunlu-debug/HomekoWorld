using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace HomekoWorld.Models;

/// <summary>
/// Dev 3 — OtoFarm envanter boşaltma (klan bankası). Otonom'un Town/NPC'siz basit hâli:
/// envanter dolunca 'U' menü → 'Clan' sekmesi → hazine sandığı → depolanabilir eşyalara sağ-tık → kapat.
/// Menü ögeleri (Clan sekmesi, sandık) TEMPLATE-MATCH ile bulunur (kör tık değil); eşyalar tür-başına
/// NCC şablon bankası ile tanınır. Envanter ROI + satır/sütun Otonom kalibrasyonundan (AutonomousSettings) alınır.
/// </summary>
public sealed class ClanBankSettings
{
    // ── Tetik / eşikler ────────────────────────────────────────────────────────
    /// <summary>OtoFarm'da envanter boşaltma açık mı (Otonom bunu kullanmaz; kendi town-sat akışı var).</summary>
    public bool  Enabled            { get; set; } = false;
    /// <summary>Her N kill'de envanter doluluğu kontrol edilir.</summary>
    public int   CheckEveryKills    { get; set; } = 30;
    /// <summary>Doluluk ≥ bu (0–1) → boşalt; altındaysa envanter kapatılıp farm devam.</summary>
    public float FullThreshold      { get; set; } = 0.85f;
    /// <summary>Depolanabilir eşya NCC eşiği: slot ikonu herhangi bir tür örneğine ≥ bu skorda uyarsa depolanır.
    /// 2026-07-04: 0.80→0.70. Canlı log (6.5sa, 20+ tur) coin skorlarını 0.75-0.92 (miktar-rakamı/hücre-çerçeve
    /// varyansı), coin-DIŞI eşyaları ≤0.55 gösterdi → temiz boşluk 0.55↔0.75. Eşik 0.80 coin kümesinin ALT
    /// yarısını (0.75-0.79) kesiyordu → 37 tur "0 eşleşme" (envantere göre coin tanınmıyor). 0.70 tüm coinleri
    /// (≥0.75) yakalar, coin-dışını (≤0.55) reddeder; her iki yönde ~0.05-0.15 marj.</summary>
    public float ItemMatchThreshold { get; set; } = 0.70f;
    /// <summary>Menü ögesi (Clan sekmesi / sandık) template-locator eşiği: bu skorun altında "bulunamadı" → iptal.</summary>
    public float MenuMatchThreshold { get; set; } = 0.80f;

    // ── Menü ögesi: 'Clan' sekmesi (arama-ROI master-uzayı + çoklu-örnek şablon) ──
    public int ClanTabRoiX { get; set; }
    public int ClanTabRoiY { get; set; }
    public int ClanTabRoiW { get; set; }
    public int ClanTabRoiH { get; set; }
    public string[] ClanTabTemplatesB64 { get; set; } = System.Array.Empty<string>();

    // ── Menü ögesi: hazine sandığı sembolü ──────────────────────────────────────
    public int ChestRoiX { get; set; }
    public int ChestRoiY { get; set; }
    public int ChestRoiW { get; set; }
    public int ChestRoiH { get; set; }
    public string[] ChestTemplatesB64 { get; set; } = System.Array.Empty<string>();

    // ── (Opsiyonel) depo penceresi "açıldı" doğrulaması: açık depo penceresinin sabit bir ögesi ──
    public int StorageOpenRoiX { get; set; }
    public int StorageOpenRoiY { get; set; }
    public int StorageOpenRoiW { get; set; }
    public int StorageOpenRoiH { get; set; }
    public string[] StorageOpenTemplatesB64 { get; set; } = System.Array.Empty<string>();

    // ── (Opsiyonel) Sandık AÇIKKEN kişisel envanter ızgarası ────────────────────
    // Bazı oyunlarda hazine/depo penceresi açıldığında kişisel envanter FARKLI konumda/boyutta
    // gösterilir (Otonom'un 'I' tek-başına kalibre ettiği envanterden ayrı bir düzen olabilir).
    // Kalibre EDİLMEDİYSE (W/H=0) eski davranış korunur: Otonom'un InventoryGridX/Y/W/H'si kullanılır.
    public int ChestInvGridX { get; set; }
    public int ChestInvGridY { get; set; }
    public int ChestInvGridW { get; set; }
    public int ChestInvGridH { get; set; }

    // Otonom'un envanter ızgarasıyla aynı kural: yuvalar KARE, yön ROI en-boy oranından türetilir
    // (28 yuva; geniş ROI → 7 sütun × 4 satır, uzun ROI → 4 sütun × 7 satır).
    [JsonIgnore] public int  ChestInvRows => ChestInvGridW >= ChestInvGridH ? 4 : 7;
    [JsonIgnore] public int  ChestInvCols => ChestInvGridW >= ChestInvGridH ? 7 : 4;
    [JsonIgnore] public bool IsChestInvGridCalibrated => ChestInvGridW > 0 && ChestInvGridH > 0;

    // ── Depolanabilir eşya türleri (her tür = ayrı NCC örnek seti) ──────────────
    public List<ItemTemplateSet> ItemTypes { get; set; } = new();

    // ── Tuşlar + gecikmeler ─────────────────────────────────────────────────────
    /// <summary>Menü (Clan) tuşu. KO'da 'U'.</summary>
    public string MenuKey        { get; set; } = "U";
    /// <summary>Envanter tuşu. KO'da 'I' (Otonom InventoryKey ile aynı olmalı).</summary>
    public string InventoryKey   { get; set; } = "I";
    /// <summary>Kapatma tuşu. VARSAYILAN "toggle" (U ile menüyü, I ile envanteri kapat) — Esc kill-switch
    /// footgun'ından kaçınır. "Escape" verilebilir ama Farm.KillSwitchKey ile aynıysa otomatik toggle'a düşer.</summary>
    public string CloseKey       { get; set; } = "toggle";
    /// <summary>Menü/pencere açılış bekleme (ms).</summary>
    public int    OpenDelayMs      { get; set; } = 700;
    /// <summary>Tık sonrası bekleme (ms).</summary>
    public int    ClickDelayMs     { get; set; } = 150;
    /// <summary>Eşya taşıma sağ-tık'ları arası bekleme (ms).</summary>
    public int    ItemClickDelayMs { get; set; } = 120;
    /// <summary>Depolama tur güvenlik valfi (eşya kaydığında yeniden tarar; sonsuz döngü olmasın).</summary>
    public int    MaxRounds        { get; set; } = 8;
    /// <summary>Görsel teşhis (2026-07-03): ilk turda dolu slot varken eşleşen=0 çıkarsa skorlu grid
    /// dökümünü (clan_bank_debug.png + şablonlar) otomatik kaydet. Eski state.json'da alan yoksa
    /// varsayılan true → ayar-tabanlı fine-tuning fazında herkes döküm alır (migration gerekmez).</summary>
    public bool   AutoDumpOnMiss   { get; set; } = true;

    // ── Durum yardımcıları ──────────────────────────────────────────────────────
    [JsonIgnore] public bool IsClanTabCalibrated => ClanTabRoiW > 0 && ClanTabRoiH > 0 && ClanTabTemplatesB64.Length > 0;
    [JsonIgnore] public bool IsChestCalibrated   => ChestRoiW   > 0 && ChestRoiH   > 0 && ChestTemplatesB64.Length   > 0;
    [JsonIgnore] public bool HasStorageOpenCheck => StorageOpenRoiW > 0 && StorageOpenRoiH > 0 && StorageOpenTemplatesB64.Length > 0;
    [JsonIgnore] public bool HasItemTypes        => ItemTypes is { Count: > 0 } && ItemTypes.Any(t => t.TemplatesB64.Length > 0);
    /// <summary>Boşaltma çalışabilir mi (açık + Clan/sandık kalibre + en az bir eşya türü öğretilmiş).
    /// Envanter ROI ayrıca AutonomousSettings.IsInventoryGridCalibrated ile kontrol edilir (servis içinde).</summary>
    [JsonIgnore] public bool IsReady => Enabled && IsClanTabCalibrated && IsChestCalibrated && HasItemTypes;
}

/// <summary>Dev 3 — bir depolanabilir eşya türü: ad + NCC örnek seti (base64 PNG). Aynı aksiyona (sağ-tık→depo)
/// gider; tür ayrımı yalnız tanıma içindir (her tür kendi normalize örnekleriyle daha isabetli eşleşir).</summary>
public sealed class ItemTemplateSet
{
    public string   Name         { get; set; } = "";
    public string[] TemplatesB64 { get; set; } = System.Array.Empty<string>();
}
