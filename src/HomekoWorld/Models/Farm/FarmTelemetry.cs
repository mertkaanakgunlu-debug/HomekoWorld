namespace HomekoWorld.Models.Farm;

/// <summary>Bir farm oturumunun anlık istatistikleri.</summary>
public sealed class FarmTelemetry
{
    public int    Kills        { get; set; }
    public int    HpPotsUsed   { get; set; }
    public int    MpPotsUsed   { get; set; }
    public int    LoopFps      { get; set; }   // FarmEngine tick FPS
    public int    InferenceFps { get; set; }   // YOLO detection FPS
    public long   SessionMs    { get; set; }   // ms since Start()
    public string CurrentMob   { get; set; } = "";
    /// <summary>Farm engine'in son gönderdiği tuş (TapKeyAsync'den güncellenir).</summary>
    public string LastKeyTapped { get; set; } = "";

    /// <summary>Sıfırlar (yeni oturum başında çağır).</summary>
    public void Reset()
    {
        Kills = HpPotsUsed = MpPotsUsed = LoopFps = 0;
        SessionMs   = 0;
        CurrentMob  = "";
        LastKeyTapped = "";
    }
}
