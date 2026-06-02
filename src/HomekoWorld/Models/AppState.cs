namespace HomekoWorld.Models;

public class AppState
{
    // Bump when combo timing semantics change (forces fresh default combos on load)
    public int Version { get; set; } = 0;

    // "wifi" | "usb" | "local"  (eski "kernel" değeri startup'ta "local"'e migrate edilir)
    public string ConnectionMode { get; set; } = "local";

    // "tr" | "en"
    public string Language { get; set; } = "tr";

    // "combo" | "farm"
    public string CurrentPage { get; set; } = "combo";

    /// <summary>Uygulamayı aktif/pasif eden global tuş (varsayılan F12).</summary>
    public string GlobalStartKey  { get; set; } = "F12";
    public string KeyboardTestKey { get; set; } = "F9";

    public bool Active { get; set; }
    public string ProfileId { get; set; } = "pk";
    public string ClassId { get; set; } = "archer";
    public string SerialHost { get; set; } = "192.168.1.100";
    // Standard Samsung USB tethering IP; user can change and it persists here
    public string UsbHost     { get; set; } = "192.168.42.129";
    public int SerialPort { get; set; } = 5556;


    // ---- Faz 14: Adaptive ping delay ----
    public bool   AdaptPingEnabled { get; set; } = false;
    /// <summary>
    /// Scale factor applied to currentPing before adding to each adaptive step delay.
    /// Formula: effectiveDelay = baseDelay × fpsFactor + currentPing × PingMultiplier
    /// Default 1.0: full ping added (conservative — matches research doc rule "delay > ping").
    /// </summary>
    public double PingMultiplier   { get; set; } = 1.0;

    // ---- Faz 15: FPS-based delay scaling ----
    public bool   AdaptFpsEnabled  { get; set; } = false;
    /// <summary>FPS at which combo delays were calibrated (default 60).</summary>
    public int    CalibrationFps   { get; set; } = 60;
    /// <summary>
    /// Current in-game FPS entered by user. Accepts "60" or "30-60" (average used).
    /// Formula: effectiveDelay = baseDelay × (CalibrationFps / parsedCurrentFps)
    /// </summary>
    public string CurrentFpsInput  { get; set; } = "60";

    // ---- WtM (Walk to Mob) ----
    public WtmSettings Wtm { get; set; } = new();

    // ---- Faz 17: Oto Farm ----
    public Farm.FarmSettings Farm { get; set; } = new();

    /// <summary>Faz 22: eski yavaş tıklama/tick varsayılanlarının bir kez agresife taşındığı işareti.</summary>
    public bool AggressiveTimingMigrated { get; set; }

    /// <summary>Faz 26: DXGI sonrası tespit FPS cap'inin (40ms) bir kez 12ms'e taşındığı işareti
    /// (kullanıcı slider'dan elle değiştirdiyse ezilmez).</summary>
    public bool DetectionFpsBoostMigrated { get; set; }

    // ---- Faz 18: Global Auto-Pot & Settings ----
    public AutoPotSettings   AutoPot        { get; set; } = new();
    public SettingsHotkeys   Hotkeys        { get; set; } = new();

    public List<CharacterClass> Classes  { get; set; } = [];
    public List<Profile>        Profiles { get; set; } = [];
    public List<Combo> Combos { get; set; } = [];
    public Dictionary<string, ComboStats> Stats { get; set; } = new();
    public SkillSlotMap SkillBar { get; set; } = new();
}

public class ComboStats
{
    public int TotalCasts { get; set; }
    public double AvgSpeedMs { get; set; }
}
