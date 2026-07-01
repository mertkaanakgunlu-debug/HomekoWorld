using HomekoWorld.Hardware;
using HomekoWorld.Models;

namespace HomekoWorld.Engine;

public sealed class ComboEngine
{
    private readonly IKeyDeviceTransport _transport;
    private readonly ISkillResolver?     _skillResolver;
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private readonly object _lock = new();

    /// <summary>
    /// Adaptive delay settings (Faz 14/15).
    /// MainViewModel replaces this with a new immutable record each time a setting
    /// changes. BuildTimeline captures a local reference at the start of each combo.
    /// </summary>
    public AdaptiveSettings AdaptiveSettings { get; set; } = AdaptiveSettings.Default;

    public event EventHandler<ComboFiredEventArgs>?     ComboFired;
    public event EventHandler<string>?                  Error;
    public event EventHandler<bool>?                    LoopStateChanged;
    public event EventHandler<StepProgressedEventArgs>? StepProgressed;

    public ComboEngine(IKeyDeviceTransport transport, ISkillResolver? skillResolver = null)
    {
        _transport     = transport;
        _skillResolver = skillResolver;
    }

    public void FireAsync(Combo combo)
    {
        lock (_lock)
        {
            if (_running.ContainsKey(combo.Id))
            {
                if (combo.IsLoop) { Cancel(combo.Id); }
                return;
            }

            var cts = new CancellationTokenSource();
            _running[combo.Id] = cts;
            _ = Task.Run(() => ExecuteAsync(combo, cts.Token));
        }
    }

    public void Cancel(string comboId)
    {
        lock (_lock)
        {
            if (_running.TryGetValue(comboId, out var cts))
            {
                cts.Cancel();
                _running.Remove(comboId);
            }
        }
    }

    public void CancelAll()
    {
        lock (_lock)
        {
            foreach (var cts in _running.Values)
                cts.Cancel();
            _running.Clear();
        }
    }

    /// <summary>Belirtilen kombo şu an çalışıyor mu (loop dahil). Farm idempotent ateşleme için.</summary>
    public bool IsRunning(string comboId)
    {
        lock (_lock) return _running.ContainsKey(comboId);
    }

    private async Task ExecuteAsync(Combo combo, CancellationToken ct)
    {
        if (combo.IsLoop) LoopStateChanged?.Invoke(this, true);
        try
        {
            var (timeline, stepStartMs) = BuildTimeline(combo);
            var totalMs = timeline.Count > 0 ? timeline[^1].ms + 50 : 50;

            var header = combo.IsLoop ? HidBridgeProtocol.ExecLoop : HidBridgeProtocol.ExecBatch;
            await SendBatchAsync(header, timeline, ct);

            if (combo.IsLoop)
            {
                do
                {
                    await FireProgressAsync(combo.Id, stepStartMs, totalMs, ct);
                    if (!ct.IsCancellationRequested)
                        await Task.Delay(50, ct);
                } while (!ct.IsCancellationRequested);
            }
            else
            {
                await FireProgressAsync(combo.Id, stepStartMs, totalMs, ct);
                ComboFired?.Invoke(this, new ComboFiredEventArgs(combo.Id, totalMs));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
        finally
        {
            // Tell Android to stop any in-progress execution
            try { await _transport.SendLineAsync(HidBridgeProtocol.Cancel, CancellationToken.None); }
            catch { }
            lock (_lock) _running.Remove(combo.Id);
            if (combo.IsLoop) LoopStateChanged?.Invoke(this, false);
        }
    }

    // Builds the entire EXEC/EXEC_LOOP block as one string and sends it in a single
    // TCP write so all lines arrive in one packet — Android starts executing only after
    // ENDEXEC arrives, with no inter-line WiFi jitter affecting keystroke timing.
    private async Task SendBatchAsync(string header, List<(string line, int ms)> timeline, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(header);
        foreach (var (line, _) in timeline)
            sb.AppendLine(line);
        sb.AppendLine(HidBridgeProtocol.ExecEnd);
        await _transport.SendRawAsync(sb.ToString(), ct);
    }

    // Fires StepProgressed events locally based on the same timeline timestamps.
    // The HUD progress bar stays accurate without needing Android feedback.
    private async Task FireProgressAsync(string comboId, List<int> stepStartMs, int totalMs, CancellationToken ct)
    {
        var total = stepStartMs.Count;
        var start = DateTime.UtcNow;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var wait = stepStartMs[i] - (int)(DateTime.UtcNow - start).TotalMilliseconds;
            if (wait > 0) await Task.Delay(wait, ct);
            StepProgressed?.Invoke(this, new StepProgressedEventArgs(comboId, i, total));
        }

        var tailWait = totalMs - (int)(DateTime.UtcNow - start).TotalMilliseconds;
        if (tailWait > 0) await Task.Delay(tailWait, ct);
    }

    // Called by TestRunnerViewModel to build a timeline without triggering ComboEngine events.
    internal (List<(string line, int ms)> timeline, List<int> stepStartMs) BuildTimelineForTest(
        Combo combo, AdaptiveSettings? adapt = null) => BuildTimeline(combo, adapt);

    // KO ~60-125Hz tick örnekler; çok kısa bir tuş-basımı (down+up aynı frame'e düşerse) oyun tarafından
    // hiç basılmamış sayılır ("2 tuş düşmesi"). Bu yüzden her tuş-hold'a ≥2 frame'lik bir taban uygulanır.
    private const int MinHoldMs = 35;

    // Converts combo steps to a flat list of (KEYDOWN/KEYUP line, absoluteMs) events.
    // Returns the event list and a parallel list of step-start times for progress events.
    private (List<(string line, int ms)> timeline, List<int> stepStartMs) BuildTimeline(
        Combo combo, AdaptiveSettings? adaptOverride = null)
    {
        var timeline    = new List<(string line, int ms)>();
        var stepStartMs = new List<int>();
        int cursor      = 0;
        int activeBar   = _skillResolver?.ActiveBarIndex ?? 0;

        // Snapshot adaptive settings once per combo execution (Faz 14/15).
        // HoldMs is NEVER adapted — only DelayMs (post-step wait).
        var adapt = adaptOverride ?? AdaptiveSettings;

        foreach (var step in combo.Steps)
        {
            stepStartMs.Add(cursor);

            // Effective delay after optional ping/FPS adaptation
            int effectiveDelay = DelayCalculator.Compute(step.DelayMs, step.IsAdaptive, adapt);

            // Tuş-hold tabanı: patolojik küçük HoldMs'i ≥2 KO frame'ine yükselt (down+up frame-çakışması = tuş yutulması).
            // Varsayılan HoldMs=50 zaten taban üstü → mevcut combo timeline'ları değişmez.
            int holdMs = Math.Max(MinHoldMs, step.HoldMs);

            switch (step.Kind)
            {
                case StepKind.Skill:
                    if (_skillResolver is null || string.IsNullOrEmpty(step.SkillId))
                    {
                        Error?.Invoke(this, $"Skill adımı atlandı: '{step.SkillId}' — Skill Barı konfigüre edilmedi.");
                        cursor += effectiveDelay;
                        break;
                    }
                    var resolved = _skillResolver.Resolve(step.SkillId);
                    if (resolved is null)
                    {
                        Error?.Invoke(this, $"Skill '{step.SkillId}' slot haritasında bulunamadı.");
                        cursor += effectiveDelay;
                        break;
                    }
                    var (barIdx, slotKey) = resolved.Value;
                    if (barIdx != activeBar)
                    {
                        var fKey = $"F{barIdx + 1}";
                        timeline.Add((HidBridgeProtocol.BatchKeyDown(fKey, cursor),             cursor));
                        timeline.Add((HidBridgeProtocol.BatchKeyUp(fKey,   cursor + MinHoldMs), cursor + MinHoldMs));
                        cursor   += MinHoldMs + 20; // hold (taban) + 20ms settle
                        activeBar = barIdx;
                    }
                    timeline.Add((HidBridgeProtocol.BatchKeyDown(slotKey, cursor),          cursor));
                    timeline.Add((HidBridgeProtocol.BatchKeyUp(slotKey,   cursor + holdMs), cursor + holdMs));
                    cursor += holdMs + effectiveDelay;
                    break;

                default:
                    var key = step.Kind == StepKind.Cancel
                        ? (step.Key.Length > 0 ? step.Key : "W")
                        : step.Key;
                    timeline.Add((HidBridgeProtocol.BatchKeyDown(key, cursor),          cursor));
                    timeline.Add((HidBridgeProtocol.BatchKeyUp(key,   cursor + holdMs), cursor + holdMs));
                    cursor += holdMs + effectiveDelay;
                    break;
            }
        }

        if (_skillResolver is not null)
            _skillResolver.ActiveBarIndex = activeBar;

        return (timeline, stepStartMs);
    }
}

public record ComboFiredEventArgs(string ComboId, double ElapsedMs);
public record StepProgressedEventArgs(string ComboId, int StepIndex, int TotalSteps);
