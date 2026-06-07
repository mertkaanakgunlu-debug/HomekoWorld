namespace HomekoWorld.Models.Autonomous;

/// <summary>Bir otonom oturumun anlık istatistikleri.</summary>
public sealed class AutonomousTelemetry
{
    /// <summary>Tamamlanan town turu (farm→sat→dön döngüsü) sayısı.</summary>
    public int TripsCompleted { get; set; }

    /// <summary>Satılan toplam eşya (Faz 36).</summary>
    public int ItemsSold { get; set; }

    /// <summary>Kurtarma gerektiren hata sayısı.</summary>
    public int Failures { get; set; }

    /// <summary>Oturum başlangıç zamanı (Faz 38).</summary>
    public DateTime SessionStart { get; private set; } = DateTime.Now;

    /// <summary>Oturum süresi.</summary>
    public TimeSpan Elapsed => DateTime.Now - SessionStart;

    /// <summary>Oturum süresi HH:mm:ss formatında.</summary>
    public string ElapsedText
    {
        get
        {
            var e = Elapsed;
            return e.TotalHours >= 1
                ? $"{(int)e.TotalHours:D2}:{e.Minutes:D2}:{e.Seconds:D2}"
                : $"{e.Minutes:D2}:{e.Seconds:D2}";
        }
    }

    public void Reset()
    {
        TripsCompleted = ItemsSold = Failures = 0;
        SessionStart   = DateTime.Now;
    }
}
