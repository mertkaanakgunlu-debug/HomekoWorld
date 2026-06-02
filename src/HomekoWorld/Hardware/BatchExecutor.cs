using System.Diagnostics;

namespace HomekoWorld.Hardware;

/// <summary>
/// Executes a sorted list of BatchEvents with high-precision timing.
/// The inject delegate is called for each key event — callers supply their own
/// injection implementation (SendInput for Local, DeviceIoControl for Kernel).
///
/// Three-tier hybrid wait — requires timeBeginPeriod(1) active in Program.Main:
///   remaining > 15 ms → Sleep(10) — large gaps, CPU-friendly
///   remaining >  3 ms → Sleep(1)  — true ~1 ms sleep (timer resolution)
///   remaining ≤  3 ms → SpinWait  — 3 ms guard (Sleep overshoots on IRQ)
/// </summary>
internal static class BatchExecutor
{
    /// <summary>
    /// Runs the batch. Blocks until all events are dispatched or ct is cancelled.
    /// On cancellation, releases any keys that were pressed during this batch.
    /// </summary>
    internal static void Run(
        List<BatchParser.BatchEvent> events,
        Action<string, bool> inject,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            foreach (var ev in events)
            {
                ct.ThrowIfCancellationRequested();
                WaitUntilMs(sw, ev.Ms, ct);
                ct.ThrowIfCancellationRequested();
                inject(ev.Key, ev.IsDown);
            }
        }
        catch (OperationCanceledException)
        {
            // Release any keys from this batch that may still be held down
            foreach (var ev in events)
                if (ev.IsDown) try { inject(ev.Key, false); } catch { }
        }
    }

    internal static void WaitUntilMs(Stopwatch sw, double targetMs, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            double remaining = targetMs - sw.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return;

            if (remaining > 15)     Thread.Sleep(10);
            else if (remaining > 3) Thread.Sleep(1);
            else                    Thread.SpinWait(100);
        }
    }
}
