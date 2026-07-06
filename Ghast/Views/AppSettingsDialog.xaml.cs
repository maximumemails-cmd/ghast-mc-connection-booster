using System.Windows;
using Ghast.ViewModels;

namespace Ghast.Views;

public partial class AppSettingsDialog : Window
{
    private readonly MainViewModel _viewModel;

    public AppSettingsDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => _viewModel.SaveConfig();
    }

    private async void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (_viewModel.RestoreDefaultsCommand.CanExecute(null))
            await _viewModel.RestoreDefaultsCommand.ExecuteAsync(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
