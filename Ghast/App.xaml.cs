using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using Ghast.Services;
using Ghast.ViewModels;
using Ghast.Views;

namespace Ghast;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("unhandled", (Exception)args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("dispatcher", args.Exception);
            MessageBox.Show($"Unexpected error: {args.Exception.Message}\n\nSee {Paths.LogPath}",
                "Ghast", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // The manifest already demands elevation; this is the belt-and-braces check
        // for environments that ignore manifests (e.g. some launchers).
        if (OperatingSystem.IsWindows() && !IsElevated())
        {
            var relaunched = TryRelaunchElevated();
            if (!relaunched)
            {
                MessageBox.Show(
                    "Ghast changes HKLM registry values, netsh settings and adapter options,\n" +
                    "so it must run as Administrator. Right-click Ghast.exe → Run as administrator.",
                    "Ghast needs elevation", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        Paths.EnsureCreated();
        Logger.Log("---- Ghast started ----");

        // Composition root — plain constructor wiring, no DI container needed.
        var backup = new BackupService();
        var registry = new RegistryService(backup);
        var netsh = new NetshService(backup, registry);
        var qos = new QosService(registry, backup);
        var adapters = new AdapterService(registry, backup);
        var process = new ProcessPriorityService();
        var configService = new ConfigService();
        var presetService = new PresetService();
        var apply = new ApplyService(registry, netsh, qos, adapters, process, backup, configService);
        var startup = new StartupService();
        var taskbarPin = new TaskbarPinService();
        var ping = new PingService();
        var dialogs = new DialogService();

        var mainViewModel = new MainViewModel(apply, configService, backup, presetService,
            startup, taskbarPin, ping, dialogs);
        var window = new MainWindow { DataContext = mainViewModel };
        MainWindow = window;
        window.Closing += (_, _) => mainViewModel.SaveConfig();
        window.Show();

        // One-time welcome (Start with Windows / Pin to taskbar). The flag is saved
        // immediately so the dialog never nags again, even if the app crashes later.
        if (!mainViewModel.FirstRunDone)
        {
            new FirstRunDialog(mainViewModel) { Owner = window }.ShowDialog();
            mainViewModel.CompleteFirstRun();
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRelaunchElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
                return false;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch
        {
            // User declined the UAC prompt.
            return false;
        }
    }
}
