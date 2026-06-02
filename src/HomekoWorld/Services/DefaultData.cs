using HomekoWorld.Models;

namespace HomekoWorld.Services;

public static class DefaultData
{
    // Bump when timing semantics change — triggers default combo reset on next load
    // v2 → v3: added full class system + all-class combo templates
    // v3 → v4: user manages own combos; removed built-in templates; skill bar slot map added
    // v4 → v5: Rogue→Assassin migration; AutoPot global; "all" class removed; SelectedComboId
    public const int CurrentVersion = 5;

    public static AppState BuildInitialState() => new()
    {
        Version   = CurrentVersion,
        Active    = false,
        ProfileId = "all",
        ClassId   = "archer",
        Language  = "tr",
        CurrentPage = "combo",
        GlobalStartKey = "F12",
        SerialHost = "192.168.1.100",
        SerialPort = 5556,
        Classes   = DefaultClasses(),
        Profiles  = DefaultProfiles(),
        Combos    = AllDefaultCombos(),
    };

    public static List<CharacterClass> DefaultClasses() =>
    [
        new() { Id = "warrior",  Name = "Savaşçı", IconKey = "SW" },
        new() { Id = "priest",   Name = "B.Rahip", IconKey = "BP" },
        new() { Id = "kurian",   Name = "Kurian",  IconKey = "KR" },
        new() { Id = "mage",     Name = "Büyücü",  IconKey = "MG" },
        new() { Id = "assassin", Name = "Asas",    IconKey = "AS" },
        new() { Id = "archer",   Name = "Okçu",    IconKey = "OK" },
    ];

    public static List<Profile> DefaultProfiles() =>
    [
        new() { Id = "pk",   Name = "PK" },
        new() { Id = "farm", Name = "Farm" },
        new() { Id = "solo", Name = "Solo" },
        new() { Id = "csw",  Name = "CSW" },
    ];

    public static List<Combo> AllDefaultCombos() => [];

    // ── ARCHER ───────────────────────────────────────────────────────────────
    // Araştırma: Tablo 3 — 3'lü/5'li ok ve minor sıkıştırma (350-450ms delay)
    // Kept as reference; no longer seeded automatically (user creates own combos).
    public static List<Combo> ArcherCombos() =>
    [
        new()
        {
            Id = "ar_ms_as_combo", Name = "MS→AS DPS",
            Description = "Tablo 3: 3'lü ok (350ms W-cancel) → 5'li ok (450ms W-cancel). Archer ana DPS döngüsü.",
            ClassId = "archer", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F1" },
            Steps =
            [
                new("7", 350, 20),  // 3'lü ok (Multiple Shot) — skill key 7, 350ms post-delay
                new("W", 0,   20),  // W-cancel: toparlanma animasyonunu keser
                new("8", 450, 20),  // 5'li ok (Arrow Shower) — skill key 8, 450ms post-delay
                new("W", 0,   20),  // W-cancel
            ],
        },
        new()
        {
            Id = "ar_ms_minor_block", Name = "MS + Minor Sıkıştırma",
            Description = "Tablo 3: MS (350ms) → W cancel → minor blok (10× rapid) → AS döngüsü. Minor, ok atışları arasındaki boşluğa paketlenir.",
            ClassId = "archer", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F2" },
            Steps =
            [
                new("7", 350, 20),  // 3'lü ok
                new("W", 0,   20),  // cancel
                new("2", 0,   20),  // minor ×1
                new("2", 0,   20),  // minor ×2
                new("2", 0,   20),  // minor ×3
                new("2", 0,   20),  // minor ×4
                new("2", 0,   20),  // minor ×5
                new("2", 0,   20),  // minor ×6
                new("2", 0,   20),  // minor ×7
                new("2", 0,   20),  // minor ×8
                new("8", 450, 20),  // 5'li ok
                new("W", 0,   20),  // cancel
            ],
        },
        new()
        {
            Id = "ar_echo_ms_loop", Name = "Echo MS Loop",
            Description = "Sürekli MS döngüsü — W-cancel ile toparlanma evresi siliniyor. Loop modunda çalıştır.",
            ClassId = "archer", ProfileId = "pk", IsCustom = false, IsLoop = true,
            Binding = new() { Modifiers = [], Code = "F3" },
            Steps =
            [
                new("7", 380, 20),
                new("W", 0,   20),
            ],
        },
        new()
        {
            Id = "ar_minor_spam", Name = "Minor Spam",
            Description = "Sırf minör döngüsü — Arka Plan Kanalı olarak kullanım için. Loop modunda çalıştır.",
            ClassId = "archer", ProfileId = "all", IsCustom = false, IsLoop = true,
            Binding = null,
            Steps =
            [
                new("2", 0, 20),
            ],
        },
        new()
        {
            Id = "ar_farm_ms", Name = "Farm MS",
            Description = "Farm için tek MS döngüsü — mana tasarrufu ile sürekli vuruş.",
            ClassId = "archer", ProfileId = "farm", IsCustom = false, IsLoop = true,
            Binding = new() { Modifiers = [], Code = "F4" },
            Steps =
            [
                new("7", 400, 20),
                new("W", 0,   30),
                new("2", 0,   20),
                new("2", 0,   20),
            ],
        },
        new()
        {
            Id = "ar_pk_burst", Name = "PK Burst",
            Description = "Snare + MS + AS hızlı açılış kombinasyonu.",
            ClassId = "archer", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = ["Shift"], Code = "1" },
            Steps =
            [
                new("6", 200, 20),  // snare/slow skill
                new("7", 350, 20),  // MS
                new("W", 0,   20),  // cancel
                new("8", 450, 20),  // AS
                new("W", 0,   20),  // cancel
                new("2", 0,   20),  // minor
            ],
        },
    ];

    // ── WARRIOR ──────────────────────────────────────────────────────────────
    // Araştırma: Tablo 1 — W+R+Skill kombosu, ~500ms skill onay penceresi
    public static List<Combo> WarriorCombos() =>
    [
        new()
        {
            Id = "wa_polearm_combo", Name = "Raptor/Mızrak: W+R+Skill",
            Description = "Tablo 1: W-ileri (20ms) → R normal vuruş (120ms hit) → W-cancel → Skill (500ms). Polearm ve Raptor için ideal.",
            ClassId = "warrior", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F1" },
            Steps =
            [
                new("W", 0,   20),   // yürüme başlat (animasyon ivmesi)
                new("R", 120, 20),   // normal vuruş + 120ms hit registration bekleme
                new("W", 0,   20),   // W-cancel: silahın geri dönme animasyonunu sil
                new("1", 500, 20),   // skill (Berserker/Stun) + 500ms sunucu onayı
            ],
        },
        new()
        {
            Id = "wa_dual_combo", Name = "Çift El: S+R+Skill",
            Description = "Araştırma §4.1: S-geri (geri yürüme ile animasyon kısaltma) → R vuruş → Skill. Lugias+Hanguk için.",
            ClassId = "warrior", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F2" },
            Steps =
            [
                new("S", 0,   20),
                new("R", 120, 20),
                new("S", 0,   20),
                new("1", 500, 20),
            ],
        },
        new()
        {
            Id = "wa_2h_axe_combo", Name = "2El Balta: R+R+Skill",
            Description = "Araştırma §4.1: Çift R vuruşu (ağır silah hantallığını kırar) → Skill. Avedon/HellBreaker için.",
            ClassId = "warrior", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F3" },
            Steps =
            [
                new("R", 120, 20),
                new("R", 120, 20),
                new("1", 500, 20),
            ],
        },
        new()
        {
            Id = "wa_pk_loop", Name = "PK DPS Loop",
            Description = "Sürekli W+R+Skill döngüsü — loop modunda çalıştır.",
            ClassId = "warrior", ProfileId = "pk", IsCustom = false, IsLoop = true,
            Binding = new() { Modifiers = [], Code = "F4" },
            Steps =
            [
                new("W", 0,   20),
                new("R", 130, 20),
                new("W", 0,   20),
                new("1", 500, 20),
            ],
        },
        new()
        {
            Id = "wa_farm_loop", Name = "Farm Döngüsü",
            Description = "Farm için stabil hasar döngüsü.",
            ClassId = "warrior", ProfileId = "farm", IsCustom = false, IsLoop = true,
            Binding = null,
            Steps =
            [
                new("R", 130, 50),
                new("1", 500, 20),
            ],
        },
    ];

    // ── ASSASSIN ─────────────────────────────────────────────────────────────
    // Araştırma: §4.2 — 300ms skill confirm, W-cancel, dual-channel minor
    public static List<Combo> AssassinCombos() =>
    [
        new()
        {
            Id = "as_spike_combo", Name = "Spike+W Combo",
            Description = "Araştırma §4.2: R(15ms) → Spike(30ms gap) → 300ms bekleme → W-cancel. Asas temel DPS.",
            ClassId = "assassin", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F1" },
            Steps =
            [
                new("R", 15,  30),   // hızlı dagger vuruşu
                new("3", 300, 20),   // Spike skill + 300ms onay
                new("W", 0,   20),   // W-cancel
            ],
        },
        new()
        {
            Id = "as_thrust_combo", Name = "Thrust Burst",
            Description = "Thrust + Pierce ardışık combo — kritik kritikle açılış.",
            ClassId = "assassin", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F2" },
            Steps =
            [
                new("R", 15,  30),
                new("4", 300, 20),   // thrust
                new("W", 0,   20),
                new("5", 300, 20),   // pierce
                new("W", 0,   20),
            ],
        },
        new()
        {
            Id = "as_critical_loop", Name = "Critical Loop",
            Description = "Araştırma §4.2: Critical + saldırı döngüsü. Loop modunda çalıştır.",
            ClassId = "assassin", ProfileId = "pk", IsCustom = false, IsLoop = true,
            Binding = new() { Modifiers = [], Code = "F3" },
            Steps =
            [
                new("R", 15,  30),
                new("8", 300, 20),   // critical skill
                new("W", 0,   20),
                new("3", 300, 20),   // spike
                new("W", 0,   20),
            ],
        },
        new()
        {
            Id = "as_minor_channel", Name = "Minor Kanalı",
            Description = "Bağımsız minor loop — saldırı döngüsüyle paralel çalışır. Loop modunda çalıştır.",
            ClassId = "assassin", ProfileId = "all", IsCustom = false, IsLoop = true,
            Binding = null,
            Steps =
            [
                new("2", 0, 25),
            ],
        },
        new()
        {
            Id = "as_pk_burst", Name = "PK Açılış",
            Description = "Tam açılış: R → Spike → Pierce → Minor.",
            ClassId = "assassin", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = ["Shift"], Code = "1" },
            Steps =
            [
                new("R", 15,  30),
                new("3", 300, 20),
                new("W", 0,   20),
                new("4", 300, 20),
                new("W", 0,   20),
                new("2", 0,   20),
            ],
        },
    ];

    // ── BATTLE PRIEST ─────────────────────────────────────────────────────────
    // Araştırma: §4.4 — R+R+Helis, 550-600ms delay (silah ağırlığı nedeniyle)
    public static List<Combo> PriestCombos() =>
    [
        new()
        {
            Id = "bp_helis_combo", Name = "R+R+Helis",
            Description = "Araştırma §4.4: Çift R vuruşu → Helis yeteneği (550-600ms). Priest animasyon iskeleti daha ağır — delay uzun tutulmalı.",
            ClassId = "priest", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F1" },
            Steps =
            [
                new("R", 120, 20),
                new("R", 120, 20),
                new("1", 575, 20),   // Helis — 550-600ms arası ortalama
            ],
        },
        new()
        {
            Id = "bp_helis_loop", Name = "Helis Loop",
            Description = "Sürekli R+R+Helis döngüsü — loop modunda çalıştır.",
            ClassId = "priest", ProfileId = "pk", IsCustom = false, IsLoop = true,
            Binding = new() { Modifiers = [], Code = "F2" },
            Steps =
            [
                new("R", 120, 20),
                new("R", 120, 20),
                new("1", 575, 20),
            ],
        },
        new()
        {
            Id = "bp_heal_burst", Name = "Massive Heal",
            Description = "Araştırma §4.4: HP düşünce saldırıyı kesip anlık 1920HP heal — ardından komboya dönülür.",
            ClassId = "priest", ProfileId = "all", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F3" },
            Steps =
            [
                new("9", 300, 50),   // Massive Healing skill
                new("2", 0,   20),   // minor
            ],
        },
        new()
        {
            Id = "bp_buff_cycle", Name = "Buff Döngüsü",
            Description = "Çeşit buff yenileme — tüm profillerde kullanılabilir.",
            ClassId = "priest", ProfileId = "all", IsCustom = false,
            Binding = null,
            Steps =
            [
                new("5", 200, 30),
                new("6", 200, 30),
                new("7", 200, 30),
            ],
        },
        new()
        {
            Id = "bp_pk_opener", Name = "PK Açılış",
            Description = "Stun + Helis açılış.",
            ClassId = "priest", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = ["Shift"], Code = "1" },
            Steps =
            [
                new("3", 250, 20),   // stun skill
                new("R", 120, 20),
                new("R", 120, 20),
                new("1", 575, 20),
            ],
        },
    ];

    // ── MAGE ──────────────────────────────────────────────────────────────────
    // Araştırma: §4.5 — RR+Skill, W+RR+Skill (Slide), 400-600ms arası
    public static List<Combo> MageCombos() =>
    [
        new()
        {
            Id = "mg_rr_skill", Name = "RR+Skill (Standart)",
            Description = "Araştırma §4.5: Çift R asa vuruşu → elemental skill. Genel hasar döngüsü.",
            ClassId = "mage", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F1" },
            Steps =
            [
                new("R", 50,  20),
                new("R", 50,  20),
                new("1", 450, 20),   // ateş/buz/yıldırım skill
            ],
        },
        new()
        {
            Id = "mg_slide_combo", Name = "W+RR+Skill (Slide)",
            Description = "Araştırma §4.5: Kısa W hareketi → RR → Skill. Slide TAP yaklaşımı. Tam HOLD/RELEASE Faz 11'de gelecek.",
            ClassId = "mage", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F2" },
            Steps =
            [
                new("W", 0,   20),   // kısa W tap — hareket ivmesi
                new("R", 50,  20),
                new("R", 50,  20),
                new("1", 450, 20),
                new("W", 0,   20),   // tekrar hareket ivmesi
            ],
        },
        new()
        {
            Id = "mg_glacier_lock", Name = "Glacier Lock",
            Description = "Buz zinciri + hasar kombinasyonu — kaçamaz hale getir.",
            ClassId = "mage", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F3" },
            Steps =
            [
                new("3", 400, 20),   // glacier/freeze
                new("R", 50,  20),
                new("1", 450, 20),
            ],
        },
        new()
        {
            Id = "mg_nova_burst", Name = "Nova Burst",
            Description = "Alan büyüsü açılışı + follow-up.",
            ClassId = "mage", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = ["Shift"], Code = "1" },
            Steps =
            [
                new("4", 500, 20),   // nova
                new("R", 50,  20),
                new("1", 450, 20),
            ],
        },
        new()
        {
            Id = "mg_slide_loop", Name = "Slide Loop",
            Description = "Sürekli Slide döngüsü — loop modunda çalıştır.",
            ClassId = "mage", ProfileId = "pk", IsCustom = false, IsLoop = true,
            Binding = new() { Modifiers = [], Code = "F4" },
            Steps =
            [
                new("W", 0,   20),
                new("R", 50,  20),
                new("R", 50,  20),
                new("1", 450, 20),
                new("W", 0,   20),
            ],
        },
    ];

    // ── KURIAN ─────────────────────────────────────────────────────────────────
    // Araştırma: §4.6 — Smash + DoT zehir sıkıştırma, W-cancel
    public static List<Combo> KurianCombos() =>
    [
        new()
        {
            Id = "ku_smash_zehir", Name = "Smash + Zehir Stack",
            Description = "Araştırma §4.6: Smash → W-cancel → 3-4 zehir DoT art arda yükle. Sıkıştırma tekniği.",
            ClassId = "kurian", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F1" },
            Steps =
            [
                new("1", 300, 20),   // Smash
                new("W", 0,   20),   // cancel
                new("3", 200, 20),   // zehir DoT 1
                new("W", 0,   20),   // cancel
                new("4", 200, 20),   // zehir DoT 2
                new("W", 0,   20),   // cancel
                new("5", 200, 20),   // zehir DoT 3
                new("W", 0,   20),   // cancel
            ],
        },
        new()
        {
            Id = "ku_smash_loop", Name = "Smash Döngüsü",
            Description = "Araştırma §4.6: Smash toparlanma evresini R ile keserek sürekli Smash. Loop modunda çalıştır.",
            ClassId = "kurian", ProfileId = "pk", IsCustom = false, IsLoop = true,
            Binding = new() { Modifiers = [], Code = "F2" },
            Steps =
            [
                new("1", 300, 20),
                new("R", 80,  20),   // recovery iptali
                new("1", 300, 20),
                new("W", 0,   20),
            ],
        },
        new()
        {
            Id = "ku_dot_burst", Name = "DoT Burst",
            Description = "Hızlı zehir yükleme — DoT birikmesi sonrası Smash ile patlat.",
            ClassId = "kurian", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = [], Code = "F3" },
            Steps =
            [
                new("3", 200, 20),
                new("W", 0,   20),
                new("4", 200, 20),
                new("W", 0,   20),
                new("5", 200, 20),
                new("W", 0,   20),
                new("6", 200, 20),
                new("W", 0,   20),
                new("1", 350, 20),   // Smash — DoT birikimi sonrası
            ],
        },
        new()
        {
            Id = "ku_pk_opener", Name = "PK Açılış",
            Description = "Stun + Smash + DoT açılış kombinasyonu.",
            ClassId = "kurian", ProfileId = "pk", IsCustom = false,
            Binding = new() { Modifiers = ["Shift"], Code = "1" },
            Steps =
            [
                new("2", 250, 20),   // stun/CC skill
                new("1", 300, 20),   // Smash
                new("W", 0,   20),
                new("3", 200, 20),
                new("4", 200, 20),
            ],
        },
    ];
}
