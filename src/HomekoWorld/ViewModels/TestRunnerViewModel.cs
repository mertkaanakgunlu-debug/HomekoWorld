using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomekoWorld.Core;
using HomekoWorld.Engine;
using HomekoWorld.Hardware;
using HomekoWorld.Hooks;
using HomekoWorld.Models;

namespace HomekoWorld.ViewModels;

public class TestLogRow
{
    public int    IterationNumber { get; init; }
    public int    PlannedMs       { get; init; }
    public string ActualMsStr     { get; init; } = string.Empty;
    public string DeltaMsStr      { get; init; } = string.Empty;
    public string KeyName         { get; init; } = string.Empty;
    public bool   IsDown          { get; init; }
    public bool   IsSkip          { get; init; }
    public string ArrowStr => IsDown ? "↓" : "↑";
}

public class DeviationSampleVm
{
    private const double MaxDisplayMs  = 30.0;
    private const double ChartHeightPx = 100.0;

    public double DeviationMs { get; init; }
    public bool   IsSkip      { get; init; }

    public double BarHeight =>
        Math.Max(1.0, Math.Min(ChartHeightPx, DeviationMs / MaxDisplayMs * ChartHeightPx));

    public string BarColor => IsSkip ? "#FFFF6B6B"
        : DeviationMs <= 2.0 ? "#FF7ACC50"
        : "#FFD4A44A";
}

public partial class TestRunnerViewModel : ObservableObject
{
    private readonly IKeyDeviceTransport _transport;
    private readonly GlobalKeyboardHook  _hook;
    private readonly BindingDispatcher   _dispatcher;
    private readonly ComboEngine         _engine;

    // ── Observable state ─────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isRunning;
    [ObservableProperty] private double _skipThresholdMs = 5.0;
    [ObservableProperty] private int    _iterationCount;
    [ObservableProperty] private int    _successfulIterations;
    [ObservableProperty] private int    _skipCount;
    [ObservableProperty] private double _avgDeviationMs;
    [ObservableProperty] private double _p95Ms;
    [ObservableProperty] private double _p99Ms;
    [ObservableProperty] private double _maxDeviationMs;
    [ObservableProperty] private string _statusText = "Hazır";
    [ObservableProperty] private Combo? _selectedCombo;

    public double SuccessRate =>
        IterationCount > 0 ? Math.Round(SuccessfulIterations * 100.0 / IterationCount, 1) : 0.0;

    // Threshold line position in the chart (100px = 30ms max display)
    public double ThresholdBarHeight =>
        Math.Max(1.0, Math.Min(100.0, SkipThresholdMs / 30.0 * 100.0));

    public ObservableCollection<Combo>           Combos           { get; } = [];
    public ObservableCollection<TestLogRow>      LiveLog          { get; } = [];
    public ObservableCollection<DeviationSampleVm> DeviationSamples { get; } = [];

    // ── Running loop state ────────────────────────────────────────────────────

    private CancellationTokenSource? _loopCts;
    private volatile ConcurrentBag<HookSample>? _captureSink;
    private Stopwatch _batchSw = new();
    private readonly List<double> _allDeviations = [];

    // ── Construction ─────────────────────────────────────────────────────────

    public TestRunnerViewModel(
        IKeyDeviceTransport transport,
        GlobalKeyboardHook  hook,
        BindingDispatcher   dispatcher,
        ComboEngine         engine,
        AppState            state)
    {
        _transport  = transport;
        _hook       = hook;
        _dispatcher = dispatcher;
        _engine     = engine;

        foreach (var c in state.Combos)
            Combos.Add(c);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedCombo is null || IsRunning) return;
        IsRunning  = true;
        StatusText = "Çalışıyor…";

        _loopCts?.Cancel();
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;

        _hook.Start(); // ensure hook is running regardless of dispatcher active state
        _hook.KeyDown += OnHookKeyDown;
        _hook.KeyUp   += OnHookKeyUp;
        _dispatcher.PauseForTest();

        try
        {
            await Task.Run(() => RunLoopAsync(SelectedCombo, ct), ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _hook.KeyDown -= OnHookKeyDown;
            _hook.KeyUp   -= OnHookKeyUp;
            _dispatcher.ResumeAfterTest();
            IsRunning  = false;
            StatusText = $"Durduruldu — {IterationCount} iterasyon, {SkipCount} skip";
        }
    }

    private bool CanStart() => SelectedCombo is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _loopCts?.Cancel();

    private bool CanStop() => IsRunning;

    [RelayCommand]
    private void Clear()
    {
        IterationCount       = 0;
        SuccessfulIterations = 0;
        SkipCount            = 0;
        AvgDeviationMs       = 0;
        P95Ms                = 0;
        P99Ms                = 0;
        MaxDeviationMs       = 0;
        _allDeviations.Clear();
        LiveLog.Clear();
        DeviationSamples.Clear();
        StatusText = "Temizlendi";
        OnPropertyChanged(nameof(SuccessRate));
    }

    partial void OnSelectedComboChanged(Combo? value)  => StartCommand.NotifyCanExecuteChanged();
    partial void OnSkipThresholdMsChanged(double value) => OnPropertyChanged(nameof(ThresholdBarHeight));
    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    // ── Loop motor (thread pool) ──────────────────────────────────────────────

    private async Task RunLoopAsync(Combo combo, CancellationToken ct)
    {
        var (timelineLines, _) = _engine.BuildTimelineForTest(combo);
        var planned = ParseTimelineEvents(timelineLines);
        if (planned.Count == 0) return;

        int    lastMs   = planned[^1].Ms;
        string payload  = BuildBatchPayload(timelineLines);

        while (!ct.IsCancellationRequested)
        {
            var captured = new ConcurrentBag<HookSample>();
            _captureSink = captured;

            _batchSw = Stopwatch.StartNew();
            await _transport.SendRawAsync(payload, ct);

            // Wait for last event + 30 ms hook-delivery buffer
            int waitMs = Math.Max(0, lastMs + 30 - (int)_batchSw.Elapsed.TotalMilliseconds);
            if (waitMs > 0) await Task.Delay(waitMs, ct);

            _captureSink = null;

            var matched = TestMeasurement.Match(planned, [.. captured], SkipThresholdMs);
            await Application.Current.Dispatcher.InvokeAsync(() => ApplyResults(matched));

            if (!ct.IsCancellationRequested)
                await Task.Delay(80, ct);
        }
    }

    // ── Hook callbacks (hook thread) ─────────────────────────────────────────

    private void OnHookKeyDown(object? sender, HookKeyEventArgs e)
    {
        var name = KeyCode.ToName(e.Key);
        if (name is not null)
            _captureSink?.Add(new HookSample(_batchSw.Elapsed.TotalMilliseconds, name, true));
    }

    private void OnHookKeyUp(object? sender, HookKeyEventArgs e)
    {
        var name = KeyCode.ToName(e.Key);
        if (name is not null)
            _captureSink?.Add(new HookSample(_batchSw.Elapsed.TotalMilliseconds, name, false));
    }

    // ── Result application (UI thread) ───────────────────────────────────────

    private void ApplyResults(List<MatchedEvent> matched)
    {
        int  iter    = IterationCount + 1;
        bool hasSkip = false;

        foreach (var m in matched)
        {
            double absdev = m.DeviationMs.HasValue ? Math.Abs(m.DeviationMs.Value) : 0.0;

            if (m.DeviationMs.HasValue)
            {
                _allDeviations.Add(absdev);
                if (DeviationSamples.Count >= 200)
                    DeviationSamples.RemoveAt(0);
                DeviationSamples.Add(new DeviationSampleVm
                    { DeviationMs = absdev, IsSkip = m.IsSkip });
            }

            if (m.IsSkip) hasSkip = true;

            // Log skips and near-threshold events (top deviations)
            if (m.IsSkip || absdev >= SkipThresholdMs * 0.6)
            {
                if (LiveLog.Count >= 500)
                    LiveLog.RemoveAt(LiveLog.Count - 1);

                LiveLog.Insert(0, new TestLogRow
                {
                    IterationNumber = iter,
                    PlannedMs       = m.PlannedMs,
                    ActualMsStr     = m.ActualMs.HasValue ? $"{m.ActualMs.Value:F1}" : "—",
                    DeltaMsStr      = m.IsSkip && !m.ActualMs.HasValue ? "MISS"
                                    : m.DeviationMs.HasValue ? $"{m.DeviationMs.Value:+0.0;-0.0;0.0}"
                                    : "—",
                    KeyName         = m.Key,
                    IsDown          = m.IsDown,
                    IsSkip          = m.IsSkip,
                });
            }
        }

        IterationCount++;
        if (!hasSkip) SuccessfulIterations++;
        if (hasSkip)  SkipCount += matched.Count(m => m.IsSkip);

        if (_allDeviations.Count > 0)
        {
            var (avg, p95, p99, max) = TestMeasurement.Aggregate(_allDeviations);
            AvgDeviationMs = Math.Round(avg, 2);
            P95Ms          = Math.Round(p95, 2);
            P99Ms          = Math.Round(p99, 2);
            MaxDeviationMs = Math.Round(max, 2);
        }

        OnPropertyChanged(nameof(SuccessRate));
        StatusText = $"Çalışıyor… {IterationCount} iter, {SkipCount} skip";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<TimelineEvent> ParseTimelineEvents(List<(string line, int ms)> timeline)
    {
        var result = new List<TimelineEvent>(timeline.Count);
        foreach (var (line, ms) in timeline)
        {
            var parts = line.Split(':');
            if (parts.Length != 3) continue;
            bool isDown;
            if (parts[0].Equals("KEYDOWN", StringComparison.OrdinalIgnoreCase))
                isDown = true;
            else if (parts[0].Equals("KEYUP", StringComparison.OrdinalIgnoreCase))
                isDown = false;
            else continue;
            result.Add(new TimelineEvent(ms, parts[1], isDown));
        }
        return result;
    }

    private static string BuildBatchPayload(List<(string line, int ms)> timeline)
    {
        var sb = new StringBuilder();
        sb.AppendLine(HidBridgeProtocol.ExecBatch);
        foreach (var (line, _) in timeline)
            sb.AppendLine(line);
        sb.AppendLine(HidBridgeProtocol.ExecEnd);
        return sb.ToString();
    }
}
