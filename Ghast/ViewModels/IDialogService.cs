using Ghast.Models;

namespace Ghast.ViewModels;

/// <summary>Implemented by the view layer so view-models never construct windows directly.</summary>
public interface IDialogService
{
    bool Confirm(string title, IEnumerable<string> lines, string confirmText);

    string? Prompt(string title, string hint);

    void ShowAppSettings(MainViewModel viewModel);

    /// <summary>
    /// Shows the modal Run/Stop progress popup, runs <paramref name="operation"/> to completion
    /// (reporting progress), and returns how the popup ended. Blocks until the popup closes.
    /// </summary>
    RunProgressOutcome ShowRunProgress(RunProgressMode mode, Func<IProgress<ApplyProgress>, Task<bool>> operation);

    /// <summary>Three-way "Ghast is still active — revert before closing?" prompt.</summary>
    CloseChoice PromptClose();

    /// <summary>Modal that explains the built-in presets honestly.</summary>
    void ShowPresetExplanations();
}
