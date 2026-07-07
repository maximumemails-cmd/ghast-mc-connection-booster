using System.Windows;
using Ghast.Services;

namespace Ghast.Views;

public partial class ExplainPresetsDialog : Window
{
    public ExplainPresetsDialog()
    {
        InitializeComponent();
        IntroText.Text = PresetService.BuiltInIntro;
        List.ItemsSource = PresetService.Explanations;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
