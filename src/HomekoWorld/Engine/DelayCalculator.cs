using System.Globalization;

namespace HomekoWorld.Engine;

/// <summary>
/// Computes effective step delays for Faz 14 (ping) and Faz 15 (FPS) adaptation.
///
/// Research doc (section 2.1): "eğer gecikme ağ pinginden daha kısaysa, sunucu
/// yetenek paketini reddeder." → delays must exceed current ping.
///
/// FPS formula: same number of game frames must elapse regardless of frame rate.
/// At 60 FPS a 350 ms delay = 21 frames. At 30 FPS the same 21 frames = 700 ms.
/// Formula: effectiveDelay = baseDelay × (calibrationFps / currentFps) + ping × multiplier
///
/// HoldMs is intentionally NOT adapted — key-hold duration is hardware timing,
/// not server-state timing, so it is never affected by FPS or ping.
/// </summary>
public static class DelayCalculator
{
    /// <param name="baseDelayMs">Raw delay from ComboStep.DelayMs.</param>
    /// <param name="isAdaptive">If false, returns baseDelayMs unchanged.</param>
    /// <param name="s">Current adaptive settings snapshot.</param>
    public static int Compute(int baseDelayMs, bool isAdaptive, AdaptiveSettings s)
    {
        if (!isAdaptive || baseDelayMs <= 0) return baseDelayMs;

        double d = baseDelayMs;

        // FPS scale first: higher FPS → shorter delays (fewer ms per frame)
        if (s.AdaptFpsEnabled && s.CalibrationFps > 0 && s.CurrentFps > 0)
            d = d * s.CalibrationFps / s.CurrentFps;

        // Ping offset: ensures the gap between consecutive keystrokes exceeds ping
        if (s.AdaptPingEnabled && s.CurrentPingMs >= 0)
            d += s.CurrentPingMs * s.PingMultiplier;

        return (int)Math.Max(0, Math.Round(d));
    }

    /// <summary>
    /// Parses a user-entered FPS value: "60" → 60.0, "30-60" → 45.0 (average).
    /// Returns 60.0 on empty/invalid input.
    /// </summary>
    public static double ParseFpsInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return 60.0;
        var t    = input.Trim();
        var dash = t.IndexOf('-');
        if (dash > 0
            && double.TryParse(t[..dash].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var lo)
            && double.TryParse(t[(dash + 1)..].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var hi)
            && lo > 0 && hi > 0)
            return Math.Max(1.0, (lo + hi) / 2.0);

        return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? Math.Max(1.0, v)
            : 60.0;
    }

    /// <summary>
    /// Produces a human-readable preview line, e.g.:
    ///   "350ms → 353ms  (+3ms ping)"
    ///   "350ms → 703ms  (×2.0 FPS, +3ms ping)"
    ///   "350ms → 350ms  (adaptasyon kapalı)"
    /// Used in the MainWindow settings panel for live feedback.
    /// </summary>
    public static string Preview(int sampleBase, bool isAdaptive, AdaptiveSettings s)
    {
        if (!isAdaptive || (!s.AdaptPingEnabled && !s.AdaptFpsEnabled))
            return $"{sampleBase}ms → {sampleBase}ms  (adaptasyon kapalı)";

        double d = sampleBase;
        var parts = new List<string>();

        if (s.AdaptFpsEnabled && s.CalibrationFps > 0 && s.CurrentFps > 0)
        {
            var ratio = s.CalibrationFps / s.CurrentFps;
            d *= ratio;
            parts.Add($"×{ratio:F2} FPS");
        }

        if (s.AdaptPingEnabled && s.CurrentPingMs >= 0)
        {
            var pingAdd = s.CurrentPingMs * s.PingMultiplier;
            d += pingAdd;
            parts.Add($"+{pingAdd:F0}ms ping");
        }

        var result = (int)Math.Max(0, Math.Round(d));
        return $"{sampleBase}ms → {result}ms  ({string.Join(", ", parts)})";
    }
}
