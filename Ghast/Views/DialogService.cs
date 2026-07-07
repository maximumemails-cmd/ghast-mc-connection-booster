using System.Windows;
using Ghast.Models;
using Ghast.ViewModels;

namespace Ghast.Views;

public class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

    public bool Confirm(string title, IEnumerable<string> lines, string confirmText)
    {
        var dialog = new ConfirmDialog(title, lines, confirmText) { Owner = Owner };
        return dialog.ShowDialog() == true;
    }

    public string? Prompt(string title, string hint)
    {
        var dialog = new PromptDialog(title, hint) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public void ShowAppSettings(MainViewModel viewModel)
    {
        var dialog = new AppSettingsDialog(viewModel) { Owner = Owner };
        dialog.ShowDialog();
    }

    public RunProgressOutcome ShowRunProgress(RunProgressMode mode,
        Func<IProgress<ApplyProgress>, Task<bool>> operation)
    {
        var dialog = new RunProgressWindow(mode, operation) { Owner = Owner };
        dialog.ShowDialog();
        return dialog.Outcome;
    }

    public CloseChoice PromptClose()
    {
        var dialog = new ClosePromptDialog { Owner = Owner };
        dialog.ShowDialog();
        return dialog.Choice;
    }

    public void ShowPresetExplanations()
    {
        var dialog = new ExplainPresetsDialog { Owner = Owner };
        dialog.ShowDialog();
    }
}
