using System.Windows;
using System.Windows.Input;

namespace Ghast.Views;

public partial class PromptDialog : Window
{
    public PromptDialog(string title, string hint)
    {
        InitializeComponent();
        TitleText.Text = title;
        InputBox.Tag = hint;
        Loaded += (_, _) => InputBox.Focus();
    }

    public string Value => InputBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            DialogResult = true;
        else if (e.Key == Key.Escape)
            DialogResult = false;
    }
}
