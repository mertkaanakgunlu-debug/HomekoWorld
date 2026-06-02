namespace HomekoWorld.Models;

/// <summary>
/// Ayarlar penceresinden kullanıcı tarafından atanabilen test hotkey'leri.
/// </summary>
public class SettingsHotkeys
{
    /// <summary>Mouse click injection test tuşu (varsayılan F8).</summary>
    public string MouseClickTestKey { get; set; } = "F8";
    /// <summary>Klavye vuruş test tuşu (varsayılan F7).</summary>
    public string KeyboardTestKey   { get; set; } = "F7";
}
