namespace HomekoWorld.Models.Autonomous;

/// <summary>
/// Otonom Oyuncu FSM durumları. Faz 31'de yalnız <see cref="Idle"/>/<see cref="Farming"/>
/// kullanılır; town/sat/dön durumları Faz 33-37'de devreye girer.
/// </summary>
public enum AutoPlayerState
{
    /// <summary>Otonom mod kapalı / hiç başlamadı.</summary>
    Idle,

    /// <summary>Farm çalışıyor; envanter periyodik izleniyor.</summary>
    Farming,

    /// <summary>Envanter açılıp dolu mu diye taranıyor (Faz 33).</summary>
    InventoryCheck,

    /// <summary>Town TP tuşuyla şehre ışınlanılıyor (Faz 35).</summary>
    GoingToTown,

    /// <summary>Koordinatla merchant'a yürünüyor (Faz 34/36).</summary>
    NavToMerchant,

    /// <summary>Merchant'a eşyalar satılıyor (Faz 36).</summary>
    Selling,

    /// <summary>Aynı merchant'ta eşyalar tamir ediliyor (Faz 40; RepairEnabled açıksa).</summary>
    Repairing,

    /// <summary>Koordinatla portala yürünüyor (Faz 34/35).</summary>
    NavToPortal,

    /// <summary>Portal sol/sağ tık + konum seçimiyle field'a dönülüyor (Faz 35).</summary>
    UsingPortal,

    /// <summary>Field girişinden farm noktasına yürünüyor (Faz 34).</summary>
    NavToFarmSpot,

    /// <summary>Timeout/hata sonrası kurtarma (Faz 37).</summary>
    Recovering,

    /// <summary>Kullanıcı tarafından durduruldu.</summary>
    Stopped,
}
