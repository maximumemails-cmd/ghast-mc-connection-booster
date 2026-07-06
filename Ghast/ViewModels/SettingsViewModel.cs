using CommunityToolkit.Mvvm.ComponentModel;

namespace Ghast.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public string[] TuningOptions { get; } =
        { "Disabled", "HighlyRestricted", "Restricted", "Normal", "Experimental" };

    public string[] TypeOptions { get; } = { "Fiber", "Cable", "DSL", "Satellite", "WiFi" };

    [ObservableProperty] private bool _smartPackets = true;

    /// <summary>0-4. Mirrors Advanced.PacketsDelay through the shared TcpDelAckTicks value (see README).</summary>
    [ObservableProperty] private int _latency;

    /// <summary>0-20 slider; the registry value written is 20 - this.</summary>
    [ObservableProperty] private int _responsiveness = 20;

    [ObservableProperty] private string _tuning = "Restricted";

    [ObservableProperty] private string _connectionType = "Fiber";

    [ObservableProperty] private bool _connectionStable = true;

    [ObservableProperty] private bool _competitiveMode = true;

    // ---- app options (live Windows state, not part of GhastConfig — see MainViewModel wiring) ----

    /// <summary>HKCU Run key entry. Side effects run in MainViewModel.</summary>
    [ObservableProperty] private bool _startWithWindows;

    /// <summary>Best-effort taskbar pin. Side effects run in MainViewModel.</summary>
    [ObservableProperty] private bool _pinToTaskbar;
}
