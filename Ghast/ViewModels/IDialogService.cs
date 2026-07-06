namespace Ghast.ViewModels;

/// <summary>Implemented by the view layer so view-models never construct windows directly.</summary>
public interface IDialogService
{
    bool Confirm(string title, IEnumerable<string> lines, string confirmText);

    string? Prompt(string title, string hint);

    void ShowAppSettings(MainViewModel viewModel);
}
