using System.Windows;
using Ghast.ViewModels;

namespace Ghast.Views;

public partial class ClosePromptDialog : Window
{
    public ClosePromptDialog() => InitializeComponent();

    /// <summary>Defaults to Cancel if the window is dismissed without a choice.</summary>
    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Revert;
        Close();
    }

    private void Leave_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.LeaveApplied;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        Close();
    }
}
