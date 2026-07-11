using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghast.Services;

namespace Ghast.ViewModels;

public partial class PingViewModel : ObservableObject
{
    private readonly PingService _ping;

    public PingViewModel(PingService ping) => _ping = ping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleMonitorCommand))]
    private string _address = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool _isTesting;

    [ObservableProperty] private string? _progressText;

    [ObservableProperty] private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyPropertyChangedFor(nameof(ResolvedText))]
    [NotifyPropertyChangedFor(nameof(ScoreText))]
    [NotifyPropertyChangedFor(nameof(PlayersText))]
    [NotifyPropertyChangedFor(nameof(ComparisonText))]
    private PingReport? _report;

    /// <summary>Quick measurement captured just before the last Run — the before/after proof.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BaselineText))]
    [NotifyPropertyChangedFor(nameof(ComparisonText))]
    private PingReport? _baseline;

    public bool HasReport => Report is not null;

    public string? BaselineText => Baseline is { } b
        ? $"Baseline (measured just before Run): {b.AvgMs:F1} ms avg · {b.JitterMs:F1} ms jitter"
        : null;

    /// <summary>
    /// Honest before/after verdict. Differences inside the jitter band are reported as
    /// "no measurable change" — Ghast never claims a win the numbers don't show.
    /// </summary>
    public string? ComparisonText
    {
        get
        {
            if (Baseline is not { } b || Report is not { } r)
                return null;
            var diff = b.AvgMs - r.AvgMs; // positive = lower latency now
            var threshold = Math.Max(2.0, Math.Max(b.JitterMs, r.JitterMs));
            if (Math.Abs(diff) <= threshold)
                return "≈ No measurable change vs the baseline — an honest result: Ghast removes " +
                       "Windows-added delay, it can't shorten the route to the server.";
            return diff > 0
                ? $"▼ {diff:F1} ms lower than the baseline (jitter {b.JitterMs:F1} → {r.JitterMs:F1} ms)."
                : $"▲ {-diff:F1} ms higher than the baseline — if this persists, hit Stop to revert.";
        }
    }

    /// <summary>
    /// Quick 4-sample measurement used as the pre-Run baseline. Never throws: a Run must
    /// not fail because the target server is down.
    /// </summary>
    public async Task CaptureBaselineAsync()
    {
        if (string.IsNullOrWhiteSpace(Address))
            return;
        try
        {
            Baseline = await _ping.TestAsync(Address.Trim(), null, default, 4, TimeSpan.FromSeconds(3.5));
        }
        catch (Exception ex)
        {
            Logger.Log($"baseline ping skipped: {ex.Message}");
        }
    }

    public string ResolvedText => Report is { } r
        ? $"{r.Host}:{r.Port}" + (r.ResolvedVia == "SRV record" ? "  (via SRV record)" : "")
        : "";

    public string ScoreText => Report is { } r ? $"{r.Score} / 100" : "";

    public string PlayersText => Report is { PlayersOnline: { } on, PlayersMax: { } max }
        ? $"{on:N0} / {max:N0} players online"
        : "";

    // ---------- live connection monitor ----------

    private CancellationTokenSource? _monitorCts;
    private readonly List<double?> _monitorSamples = new(); // null = failed probe (counts as loss)
    private const int MonitorWindow = 40;
    private const int MonitorIntervalMs = 4000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitorButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleMonitorCommand))]
    private bool _isMonitoring;

    [ObservableProperty] private string? _monitorSummary;

    [ObservableProperty] private PointCollection? _sparklinePoints;

    public string MonitorButtonText => IsMonitoring ? "Stop Monitor" : "Monitor";

    private bool CanToggleMonitor() => IsMonitoring || !string.IsNullOrWhiteSpace(Address);

    [RelayCommand(CanExecute = nameof(CanToggleMonitor))]
    private void ToggleMonitor()
    {
        if (IsMonitoring)
        {
            StopMonitor();
            return;
        }

        _monitorSamples.Clear();
        SparklinePoints = null;
        MonitorSummary = "Starting monitor…";
        IsMonitoring = true;
        _monitorCts = new CancellationTokenSource();
        _ = MonitorLoopAsync(_monitorCts.Token);
    }

    private void StopMonitor()
    {
        _monitorCts?.Cancel();
        _monitorCts = null;
        IsMonitoring = false;
        if (MonitorSummary is not null)
            MonitorSummary += "  (stopped)";
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        (string Host, int Port) target;
        try
        {
            target = await _ping.ResolveAsync(Address.Trim(), ct);
        }
        catch (Exception ex)
        {
            MonitorSummary = ex is PingTestException ? ex.Message : "Couldn't resolve that address.";
            IsMonitoring = false;
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            double? ms = null;
            try
            {
                ms = await _ping.ProbeAsync(target.Host, target.Port, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // failed probe — recorded as loss
            }

            if (ct.IsCancellationRequested)
                return;

            _monitorSamples.Add(ms);
            if (_monitorSamples.Count > MonitorWindow)
                _monitorSamples.RemoveAt(0);
            UpdateMonitorReadout();

            try { await Task.Delay(MonitorIntervalMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void UpdateMonitorReadout()
    {
        var ok = _monitorSamples.Where(s => s.HasValue).Select(s => s!.Value).ToList();
        var lossPct = _monitorSamples.Count == 0
            ? 0
            : 100.0 * _monitorSamples.Count(s => !s.HasValue) / _monitorSamples.Count;

        if (ok.Count == 0)
        {
            MonitorSummary = $"No replies yet ({_monitorSamples.Count} probe(s) failed) — server offline?";
            SparklinePoints = null;
            return;
        }

        var avg = ok.Average();
        double jitter = 0;
        for (var i = 1; i < ok.Count; i++)
            jitter += Math.Abs(ok[i] - ok[i - 1]);
        jitter = ok.Count > 1 ? jitter / (ok.Count - 1) : 0;

        var span = _monitorSamples.Count * MonitorIntervalMs / 1000;
        MonitorSummary = $"{avg:F0} ms avg · {jitter:F1} ms jitter · {lossPct:F0}% loss (last {span}s)";

        // Sparkline in a fixed 300×40 space; failed probes just leave a gap in the line.
        const double w = 300, h = 40, pad = 3;
        var min = ok.Min();
        var max = Math.Max(ok.Max(), min + 1);
        var points = new PointCollection();
        for (var i = 0; i < _monitorSamples.Count; i++)
        {
            if (_monitorSamples[i] is not { } value)
                continue;
            var x = _monitorSamples.Count == 1 ? w : i * w / (_monitorSamples.Count - 1);
            var y = pad + (h - 2 * pad) * (1 - (value - min) / (max - min));
            points.Add(new System.Windows.Point(x, y));
        }
        points.Freeze();
        SparklinePoints = points;
    }

    // ---------- one-shot test ----------

    private bool CanTest() => !IsTesting && !string.IsNullOrWhiteSpace(Address);

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        if (IsMonitoring)
            StopMonitor(); // don't interleave monitor probes with a full test

        IsTesting = true;
        ErrorText = null;
        Report = null;
        ProgressText = "Connecting…";
        var progress = new Progress<string>(msg => ProgressText = msg);

        try
        {
            Report = await Task.Run(() => _ping.TestAsync(Address, progress));
        }
        catch (PingTestException ex)
        {
            ErrorText = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.Error("ping test", ex);
            ErrorText = "Something went wrong running the test — see log.txt for details.";
        }
        finally
        {
            ProgressText = null;
            IsTesting = false;
        }
    }
}
