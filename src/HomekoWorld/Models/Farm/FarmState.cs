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

    /// <summary>HP veya MP düştü, pot içiliyor.</summary>
    Potting,

    /// <summary>Spot boşaldı, waypoint'lere yürünüyor.</summary>
    Roaming,

    /// <summary>F12 kill-switch — güvenli durdurma.</summary>
    KillSwitched,
}
