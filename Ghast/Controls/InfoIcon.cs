using System.Windows;
using System.Windows.Controls;

namespace Ghast.Controls;

/// <summary>
/// Small circle-i whose tooltip carries the plain-English explanation of a setting.
/// Templated in Themes/Styles.xaml.
/// </summary>
public class InfoIcon : Control
{
    public static readonly DependencyProperty InfoProperty = DependencyProperty.Register(
        nameof(Info), typeof(string), typeof(InfoIcon), new PropertyMetadata(string.Empty));

    public string Info
    {
        get => (string)GetValue(InfoProperty);
        set => SetValue(InfoProperty, value);
    }
}
