namespace HomekoWorld.Engine;

/// <summary>
/// Immutable snapshot of adaptive delay parameters.
/// MainViewModel replaces the whole object (reference swap) each time any
/// value changes — avoids partial-update races without a lock.
/// ComboEngine reads it once at BuildTimeline start; a stale read is
/// acceptable (at most one combo uses slightly old settings).
/// </summary>
public sealed record AdaptiveSettings(
    // ── Faz 14: Ping offset ─────────────────────────────────────────────
    bool   AdaptPingEnabled,
    double PingMultiplier,   // applied as: delay += currentPing × multiplier
    double CurrentPingMs,

    // ── Faz 15: FPS scale ───────────────────────────────────────────────
    bool   AdaptFpsEnabled,
    int    CalibrationFps,   // FPS at which step delays were calibrated
    double CurrentFps)       // parsed from user input ("30-60" → 45, "60" → 60)
{
    /// <summary>Neutral settings — no adaptation, no side-effects.</summary>
    public static readonly AdaptiveSettings Default =
        new(false, 1.0, 0, false, 60, 60);
}
