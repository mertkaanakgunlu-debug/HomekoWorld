namespace HomekoWorld.Models.Farm;

/// <summary>FarmEngine'in anlık çalışma durumu.</summary>
public enum FarmState
{
    /// <summary>Farm durdurulmuş / hiç başlamadı.</summary>
    Idle,

    /// <summary>Ekran taranıyor, mob aranıyor.</summary>
    Scanning,

    /// <summary>Mob bulundu, fare ile hedef kilitleniyor (tıklama).</summary>
    Targeting,

    /// <summary>Mob'a yürünüyor + kombo atılıyor.</summary>
    Engaging,

    /// <summary>Mob öldü, loot toplanıyor.</summary>
    Looting,

    // NOT (8.tur, 2026-07-08): "Potting" üyesi kaldırıldı — hiçbir SetState kullanmıyordu;
    // potlama tek otorite AutoPotService'te (ayrı thread), farm state makinesine hiç girmiyor.

    /// <summary>Spot boşaldı, waypoint'lere yürünüyor.</summary>
    Roaming,

    /// <summary>Dev 2 — uzun süre hedef yok, başlangıç farm koordinatına yürünüyor.</summary>
    ReturningHome,

    /// <summary>Dev 3 — envanter dolu, klan bankasına boşaltılıyor.</summary>
    Banking,

    /// <summary>F12 kill-switch — güvenli durdurma.</summary>
    KillSwitched,
}
