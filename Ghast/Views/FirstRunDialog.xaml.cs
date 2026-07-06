using System.Windows;
using Ghast.ViewModels;

namespace Ghast.Views;

/// <summary>
/// One-time welcome dialog. Confirming pushes the chosen toggles through the same
/// SettingsViewModel properties the Settings tab uses, so a single code path owns
/// the side effects (and their error handling).
/// </summary>
public partial class FirstRunDialog : Window
{
    private readonly MainViewModel _viewModel;

    public FirstRunDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    private void GetStarted_Click(object sender, RoutedEventArgs e)
    {
        if (StartupToggle.IsChecked == true)
            _viewModel.Settings.StartWithWindows = true;
        if (PinToggle.IsChecked == true)
            _viewModel.Settings.PinToTaskbar = true;
        Close();
    }
}
