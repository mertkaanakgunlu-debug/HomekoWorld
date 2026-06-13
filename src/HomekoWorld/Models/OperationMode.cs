namespace HomekoWorld.Models;

/// <summary>
/// FujiMacro birleşik mod sistemi — tek anda yalnız bir ana mod çalışır.
/// Oto Pot bağımsız bir overlay'dir (bu enum'a dahil değildir; bkz. AutoPotService).
/// </summary>
public enum OperationMode
{
    /// <summary>Sadece tuş kombinasyonu ateşler (BindingDispatcher hotkey'leri).</summary>
    Kombo,

    /// <summary>Seçilen mobu bul/hedefle/kombo, mobdan moba (FarmEngine).</summary>
    OtoFarm,

    /// <summary>Oto Farm + envanter + town + sat/tamir + portaldan dönüş (AutonomousPlayerEngine).</summary>
    Otonom,

    /// <summary>Oyunun Genie botu + town döngüsü (şimdilik devre dışı — "yakında").</summary>
    YariOto,
}
