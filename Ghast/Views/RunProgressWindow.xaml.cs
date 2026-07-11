using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Ghast.Models;
using Ghast.Services;
using Ghast.ViewModels;

namespace Ghast.Views;

/// <summary>
/// Modal progress popup for a Start (apply), Stop (revert) or Flush (adapter bounce) pass.
/// It owns the operation: runs it on a background thread, shows real per-step progress,
/// holds open ≥900ms so it doesn't flash, then shows a terminal state.
/// After a Start pass that touched per-interface TCP values, it shows the reconnect nudge
/// and the opt-in "Apply to live connection" button (spec: never bounce without consent).
/// The parent reads <see cref="Outcome"/> after ShowDialog returns.
/// </summary>
public partial class RunProgressWindow : Window
{
    private readonly RunProgressMode _mode;
    private readonly Func<IProgress<ApplyProgress>, Task<OperationResult>> _operation;
    private bool _finished;

    public RunProgressOutcome Outcome { get; private set; } = RunProgressOutcome.Closed;

    public RunProgressWindow(RunProgressMode mode, Func<IProgress<ApplyProgress>, Task<OperationResult>> operation)
    {
        InitializeComponent();
        _mode = mode;
        _operation = operation;
        TitleText.Text = _mode switch
        {
            RunProgressMode.Start => "Starting Ghast",
            RunProgressMode.Stop => "Stopping Ghast",
            _ => "Applying to live connection"
        };
        StatusText.Text = RunningStatus();
        StepText.Text = _mode switch
        {
            RunProgressMode.Start => "Preparing…",
            RunProgressMode.Stop => "Reading backups…",
            _ => "Flushing DNS cache…"
        };
        Loaded += async (_, _) => await RunAsync();
    }

    private string RunningStatus() => _mode switch
    {
        RunProgressMode.Start => "Running optimisations…",
        RunProgressMode.Stop => "Reverting changes…",
        _ => "Bouncing network adapters…"
    };

    private async Task RunAsync()
    {
        Bar.Value = 0;
        var progress = new Progress<ApplyProgress>(p =>
        {
            Bar.Value = p.Percent;
            if (!string.IsNullOrEmpty(p.Step))
                StepText.Text = p.Step;
        });

        var sw = Stopwatch.StartNew();
        OperationResult result;
        string? error = null;
        try
        {
            result = await Task.Run(() => _operation(progress));
        }
        catch (Exception ex)
        {
            result = new OperationResult(false);
            error = ex.Message;
            Logger.Error("run/stop/flush operation", ex);
        }

        // Minimum on-screen time so a fast apply doesn't flash the popup.
        var elapsed = sw.ElapsedMilliseconds;
        if (elapsed < 900)
            await Task.Delay((int)(900 - elapsed));

        _finished = true;
        if (result.Success)
            ShowSuccess(result);
        else
            ShowFailure(error);
    }

    private void ShowSuccess(OperationResult result)
    {
        Bar.Value = 100;
        Check.Visibility = Visibility.Visible;

        switch (_mode)
        {
            case RunProgressMode.Start:
                StatusText.Text = "Running";
                Outcome = RunProgressOutcome.Completed;
                StopButton.Visibility = Visibility.Visible;
                CloseButton.Visibility = Visibility.Visible;
                if (result.ReconnectAdvised)
                {
                    StepText.Text = "Optimisations applied.";
                    NudgePanel.Visibility = Visibility.Visible;
                    FlushButton.Visibility = Visibility.Visible;
                }
                else
                {
                    StepText.Text = "Optimisations applied.";
                }
                break;

            case RunProgressMode.Stop:
                StatusText.Text = "Stopped";
                StepText.Text = "All changes reverted and verified against the backups.";
                Outcome = RunProgressOutcome.Completed;
                AutoClose();
                break;

            default: // Flush
                StatusText.Text = "Done";
                StepText.Text = "Adapters reconnected — your open connections now use the new TCP settings.";
                Outcome = RunProgressOutcome.Completed;
                CloseButton.Visibility = Visibility.Visible;
                AutoClose(1400);
                break;
        }
    }

    private void ShowFailure(string? error)
    {
        Bar.Value = 100;
        StatusText.Text = _mode switch
        {
            RunProgressMode.Start => "Couldn't apply everything",
            RunProgressMode.Stop => "Couldn't revert everything",
            _ => "Flush didn't complete"
        };
        StepText.Text = _mode switch
        {
            RunProgressMode.Start => $"{error ?? "Some steps failed"} — changes were rolled back. See log.txt.",
            RunProgressMode.Stop => $"{error ?? "Some steps failed"} — backups were kept so you can retry. See log.txt.",
            _ => $"{error ?? "Some steps failed"} — check your adapters are back up (see log.txt)."
        };
        Outcome = RunProgressOutcome.Failed;
        RetryButton.Visibility = Visibility.Visible;
        CloseButton.Visibility = Visibility.Visible;
    }

    private async void AutoClose(int delayMs = 700)
    {
        await Task.Delay(delayMs);
        Close();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Collapsed;
        FlushButton.Visibility = Visibility.Collapsed;
        NudgePanel.Visibility = Visibility.Collapsed;
        Check.Visibility = Visibility.Collapsed;
        Outcome = RunProgressOutcome.Closed;
        _finished = false;
        StatusText.Text = RunningStatus();
        StepText.Text = "Retrying…";
        await RunAsync();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        Outcome = RunProgressOutcome.StopRequested;
        Close();
    }

    private void Flush_Click(object sender, RoutedEventArgs e)
    {
        Outcome = RunProgressOutcome.FlushRequested;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Block Alt+F4 while the operation is mid-flight.
        if (!_finished)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
