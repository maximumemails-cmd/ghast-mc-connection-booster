using System.Windows;
using Ghast.Models;

namespace Ghast.Views;

/// <summary>Scrollable Before/Now table used for the "what changed" receipt and dry-run preview.</summary>
public partial class ReceiptDialog : Window
{
    public ReceiptDialog(string title, string header, IReadOnlyList<ReceiptLine> lines)
    {
        InitializeComponent();
        TitleText.Text = title;
        HeaderText.Text = header;
        LinesList.ItemsSource = lines.Count > 0
            ? lines
            : new[] { new ReceiptLine("Nothing to show", "", "—", "—") };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
