using System.Windows;

namespace Ghast.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, IEnumerable<string> lines, string confirmText)
    {
        InitializeComponent();
        TitleText.Text = title;
        LinesList.ItemsSource = lines.ToList();
        ConfirmButton.Content = confirmText;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
