namespace HomekoWorld.Models;

/// <summary>
/// Global otomatik pot servisi yapılandırması.
/// FarmEngine'dan bağımsız; Active=true iken her zaman çalışır.
/// </summary>
public class AutoPotSettings
{
    public bool   Enabled      { get; set; } = false;
    public bool   HpPotEnabled { get; set; } = true;
    public int    HpPotPercent { get; set; } = 50;
    public string HpPotKey     { get; set; } = "F1";
    public bool   MpPotEnabled { get; set; } = true;
    public int    MpPotPercent { get; set; } = 30;
    public string MpPotKey     { get; set; } = "F2";
    /// <summary>Kaç ms'de bir HP/MP barı okunur.</summary>
    public int    TickMs       { get; set; } = 200;
    /// <summary>Aynı pot için iki tap arasındaki minimum süre (ms).</summary>
    public int    CooldownMs   { get; set; } = 800;
    /// <summary>AutoPot'u açıp kapayan global hotkey (default F11).</summary>
    public string StartKey     { get; set; } = "F11";
}
