using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Ghast.Models;
using Ghast.Services;
using Ghast.ViewModels;

namespace Ghast.Views;

/// <summary>
/// Modal progress popup for a Start (apply) or Stop (revert) pass. It owns the operation:
/// runs it on a background thread, shows real per-step progress, holds open ≥900ms so it
/// doesn't flash, then shows a terminal state (success → Running/Stopped, failure → Retry/Close).
/// The parent reads <see cref="Outcome"/> after ShowDialog returns.
/// </summary>
public partial class RunProgressWindow : Window
{
    private readonly RunProgressMode _mode;
    private readonly Func<IProgress<ApplyProgress>, Task<bool>> _operation;
    private bool _finished;

    public RunProgressOutcome Outcome { get; private set; } = RunProgressOutcome.Closed;

    public RunProgressWindow(RunProgressMode mode, Func<IProgress<ApplyProgress>, Task<bool>> operation)
    {
        InitializeComponent();
        _mode = mode;
        _operation = operation;
        TitleText.Text = mode == RunProgressMode.Start ? "Starting Ghast" : "Stopping Ghast";
        StatusText.Text = RunningStatus();
        StepText.Text = mode == RunProgressMode.Start ? "Preparing…" : "Reading backups…";
        Loaded += async (_, _) => await RunAsync();
    }

    private string RunningStatus() =>
        _mode == RunProgressMode.Start ? "Running optimisations…" : "Reverting changes…";

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
        bool ok;
        string? error = null;
        try
        {
            ok = await Task.Run(() => _operation(progress));
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
            Logger.Error("run/stop operation", ex);
        }

        // Minimum on-screen time so a fast apply doesn't flash the popup.
        var elapsed = sw.ElapsedMilliseconds;
        if (elapsed < 900)
            await Task.Delay((int)(900 - elapsed));

        _finished = true;
        if (ok)
            ShowSuccess();
        else
            ShowFailure(error);
    }

    private void ShowSuccess()
    {
        Bar.Value = 100;
        Check.Visibility = Visibility.Visible;

        if (_mode == RunProgressMode.Start)
        {
            StatusText.Text = "Running";
            StepText.Text = "Optimisations applied.";
            Outcome = RunProgressOutcome.Completed;
            StopButton.Visibility = Visibility.Visible;
            CloseButton.Visibility = Visibility.Visible;
        }
        else
        {
            StatusText.Text = "Stopped";
            StepText.Text = "All changes reverted.";
            Outcome = RunProgressOutcome.Completed;
            AutoClose();
        }
    }

    private void ShowFailure(string? error)
    {
        Bar.Value = 100;
        StatusText.Text = _mode == RunProgressMode.Start
            ? "Couldn't apply everything"
            : "Couldn't revert everything";
        StepText.Text = _mode == RunProgressMode.Start
            ? $"{error ?? "Some steps failed"} — changes were rolled back. See log.txt."
            : $"{error ?? "Some steps failed"} — backups were kept so you can retry. See log.txt.";
        Outcome = RunProgressOutcome.Failed;
        RetryButton.Visibility = Visibility.Visible;
        CloseButton.Visibility = Visibility.Visible;
    }

    private async void AutoClose()
    {
        await Task.Delay(700);
        Close();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Collapsed;
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
