using System.Windows;

namespace Ghast.Views;

public partial class MainWindow : Window
{
    // Reference design size for the body content; the UI scales up from here when the
    // window grows (maximized/fullscreen) so there are no dead side gaps. Capped at
    // 1.8x so text never blows up cartoonishly, floored at 1.0 so small windows just scroll.
    private const double ReferenceWidth = 1280;
    private const double ReferenceHeight = 800;

    public MainWindow()
    {
        InitializeComponent();

        // Borderless windows otherwise cover the taskbar when maximized.
        MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
        MaxWidth = SystemParameters.MaximizedPrimaryScreenWidth;

        SizeChanged += (_, _) => UpdateBodyScale();
        Loaded += (_, _) => UpdateBodyScale();
    }

    private void UpdateBodyScale()
    {
        var scale = Math.Clamp(
            Math.Min(ActualWidth / ReferenceWidth, ActualHeight / ReferenceHeight),
            1.0, 1.8);
        BodyScale.ScaleX = scale;
        BodyScale.ScaleY = scale;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
