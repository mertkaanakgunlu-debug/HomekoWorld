namespace HomekoWorld.Engine;

public readonly record struct TimelineEvent(int Ms, string Key, bool IsDown);

public readonly record struct HookSample(double ActualMs, string Key, bool IsDown);

public readonly record struct MatchedEvent(
    int     PlannedMs,
    double? ActualMs,
    string  Key,
    bool    IsDown,
    double? DeviationMs,
    bool    IsSkip);

public static class TestMeasurement
{
    // Greedy left-to-right match: for each planned event, find the first unconsumed
    // captured sample with the same (key, isDown) whose ActualMs >= PlannedMs - 5 (early window).
    // Unmatched planned events become IsSkip=true with null ActualMs.
    // Matched events whose |deviation| > skipThresholdMs also become IsSkip=true.
    public static List<MatchedEvent> Match(
        IReadOnlyList<TimelineEvent> planned,
        IReadOnlyList<HookSample>    captured,
        double                       skipThresholdMs)
    {
        var result   = new List<MatchedEvent>(planned.Count);
        var consumed = new bool[captured.Count];

        foreach (var p in planned)
        {
            double? bestActual = null;
            int     bestIdx    = -1;

            for (int i = 0; i < captured.Count; i++)
            {
                if (consumed[i]) continue;
                var s = captured[i];
                if (!string.Equals(s.Key, p.Key, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.IsDown != p.IsDown) continue;
                if (s.ActualMs < p.Ms - 5.0) continue; // too early (out-of-window)

                // prefer the sample closest to planned time
                if (bestIdx < 0 || Math.Abs(s.ActualMs - p.Ms) < Math.Abs(bestActual!.Value - p.Ms))
                {
                    bestActual = s.ActualMs;
                    bestIdx    = i;
                }
            }

            if (bestIdx >= 0)
            {
                consumed[bestIdx] = true;
                double dev  = bestActual!.Value - p.Ms;
                bool isSkip = Math.Abs(dev) > skipThresholdMs;
                result.Add(new MatchedEvent(p.Ms, bestActual, p.Key, p.IsDown, dev, isSkip));
            }
            else
            {
                result.Add(new MatchedEvent(p.Ms, null, p.Key, p.IsDown, null, true));
            }
        }

        return result;
    }

    public static (double avg, double p95, double p99, double max) Aggregate(
        IReadOnlyList<double> deviations)
    {
        if (deviations.Count == 0) return (0, 0, 0, 0);

        var sorted = deviations.OrderBy(d => d).ToArray();
        double sum = 0;
        double max = 0;
        foreach (var d in sorted) { sum += d; if (d > max) max = d; }

        double avg = sum / sorted.Length;
        double p95 = sorted[Math.Min((int)(sorted.Length * 0.95), sorted.Length - 1)];
        double p99 = sorted[Math.Min((int)(sorted.Length * 0.99), sorted.Length - 1)];
        return (avg, p95, p99, max);
    }
}
