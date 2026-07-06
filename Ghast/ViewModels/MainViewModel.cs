using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghast.Models;
using Ghast.Services;

namespace Ghast.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ApplyService _apply;
    private readonly ConfigService _configService;
    private readonly BackupService _backup;
    private readonly StartupService _startup;
    private readonly TaskbarPinService _taskbarPin;
    private readonly IDialogService _dialogs;

    private bool _loading;
    private bool _syncingDelAck;
    private CompetitiveSnapshot? _competitiveSnapshot;

    public MainViewModel(ApplyService apply, ConfigService configService, BackupService backup,
        PresetService presetService, StartupService startup, TaskbarPinService taskbarPin,
        PingService ping, IDialogService dialogs)
    {
        _apply = apply;
        _configService = configService;
        _backup = backup;
        _startup = startup;
        _taskbarPin = taskbarPin;
        _dialogs = dialogs;

        Settings = new SettingsViewModel();
        Advanced = new AdvancedViewModel();
        Ping = new PingViewModel(ping);
        Presets = new PresetsViewModel(
            presetService,
            BuildConfig,
            ApplyPresetAsync,
            title => _dialogs.Prompt(title, "Preset name"),
            (title, lines, confirm) => _dialogs.Confirm(title, lines, confirm));

        Settings.PropertyChanged += OnSettingsChanged;
        Advanced.PropertyChanged += OnAdvancedChanged;

        LoadConfig(_configService.Load());

        // App-option toggles reflect live Windows state, not config.
        _loading = true;
        Settings.StartWithWindows = _startup.IsEnabled();
        Settings.PinToTaskbar = _taskbarPin.IsPinned();
        _loading = false;

        CurrentViewModel = Settings;
    }

    public SettingsViewModel Settings { get; }
    public AdvancedViewModel Advanced { get; }
    public PresetsViewModel Presets { get; }
    public PingViewModel Ping { get; }

    /// <summary>Set after the one-time welcome dialog has been shown.</summary>
    public bool FirstRunDone { get; private set; }

    public void CompleteFirstRun()
    {
        FirstRunDone = true;
        SaveConfig();
    }

    public ObservableCollection<ApplyResult> StatusItems { get; } = new();

    [ObservableProperty] private string _activeTab = "Settings";

    [ObservableProperty] private object? _currentViewModel;

    [ObservableProperty] private string _tier = "Lightning";

    /// <summary>none | cloudflare | google (gear menu).</summary>
    [ObservableProperty] private string _dnsChoice = "none";

    [ObservableProperty] private string? _statusText;

    [ObservableProperty] private bool _statusVisible;

    [ObservableProperty] private bool _isBusy;

    public string BackupFolder => Paths.DataDir;

    // ---------- tabs ----------

    [RelayCommand]
    private void SelectTab(string tab)
    {
        ActiveTab = tab;
        CurrentViewModel = tab switch
        {
            "Presets" => Presets,
            "Advanced" => Advanced,
            "Ping" => Ping,
            _ => (object)Settings
        };
    }

    // ---------- run / restore ----------

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsBusy)
            return;

        var config = BuildConfig();
        var plan = _apply.BuildPlan(config);
        if (!_dialogs.Confirm("Run Ghast — apply these tweaks?", plan, "Run"))
            return;

        IsBusy = true;
        StatusItems.Clear();
        StatusVisible = true;
        StatusText = "Applying…";
        var progress = new Progress<ApplyResult>(r =>
        {
            StatusItems.Add(r);
            StatusText = $"Applying… ({StatusItems.Count} done)";
        });

        List<ApplyResult> results;
        try
        {
            results = await Task.Run(() => _apply.RunAsync(config, progress));
        }
        catch (Exception ex)
        {
            Logger.Error("run", ex);
            StatusText = $"Run failed: {ex.Message}";
            IsBusy = false;
            return;
        }

        var ok = results.Count(r => r.Success);
        StatusText = $"Applied {ok}/{results.Count} tweaks";
        IsBusy = false;

        if (ok > 0 && _dialogs.Confirm("Flush the network stack now? (optional)",
                new[]
                {
                    "Flushes the DNS cache and briefly disables/re-enables each network adapter",
                    "so the TCP changes take effect without a reboot.",
                    "Your connection WILL drop for a few seconds."
                }, "Flush"))
        {
            IsBusy = true;
            StatusText = "Flushing network stack…";
            try
            {
                await Task.Run(() => _apply.FlushAsync());
                StatusText = $"Applied {ok}/{results.Count} tweaks — network stack flushed";
            }
            catch (Exception ex)
            {
                Logger.Error("flush", ex);
                StatusText = $"Flush failed: {ex.Message}";
            }
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        if (IsBusy)
            return;

        var lines = new List<string>
        {
            $"{_backup.Count} backed-up value(s) will be restored to their pre-Ghast state.",
            "Registry values Ghast created will be deleted; changed ones get their original value back."
        };
        if (!_dialogs.Confirm("Restore Defaults?", lines, "Restore"))
            return;

        IsBusy = true;
        StatusItems.Clear();
        StatusVisible = true;
        StatusText = "Restoring…";
        var progress = new Progress<ApplyResult>(r => StatusItems.Add(r));

        var results = await Task.Run(() => _apply.RestoreAllAsync(progress));
        var ok = results.Count(r => r.Success);
        StatusText = $"Restored {ok}/{results.Count} item(s)";
        IsBusy = false;
    }

    [RelayCommand]
    private void OpenAppSettings() => _dialogs.ShowAppSettings(this);

    [RelayCommand]
    private void DismissStatus() => StatusVisible = false;

    [RelayCommand]
    private void OpenBackupFolder()
    {
        try
        {
            Paths.EnsureCreated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Paths.DataDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error("opening backup folder", ex);
        }
    }

    public void SaveConfig() => _configService.Save(BuildConfig());

    // ---------- presets bridge ----------

    private async Task ApplyPresetAsync(Preset preset)
    {
        LoadConfig(preset.Config.Clone());
        await RunAsync();
    }

    // ---------- config <-> view-models ----------

    public GhastConfig BuildConfig() => new()
    {
        Tier = Tier,
        Dns = DnsChoice,
        CompetitiveSnapshot = _competitiveSnapshot,
        FirstRunDone = FirstRunDone,
        Settings = new SettingsSection
        {
            SmartPackets = Settings.SmartPackets,
            Latency = Settings.Latency,
            Responsiveness = Settings.Responsiveness,
            Tuning = Settings.Tuning,
            Type = Settings.ConnectionType,
            ConnectionStable = Settings.ConnectionStable,
            CompetitiveMode = Settings.CompetitiveMode
        },
        Advanced = new AdvancedSection
        {
            MtuAutomatic = Advanced.MtuAutomatic,
            MtuValue = Math.Clamp(Advanced.MtuValue, 576, 1500),
            PacketsDelay = Advanced.PacketsDelay,
            NetworkPriority = Advanced.NetworkPriority,
            CongestionProvider = Advanced.CongestionProvider,
            GhastPriorityMode = Advanced.GhastPriorityMode,
            NetworkPowerSaving = Advanced.NetworkPowerSaving
        }
    };

    public void LoadConfig(GhastConfig config)
    {
        config = ConfigService.Sanitize(config);
        _loading = true;
        try
        {
            Tier = config.Tier;
            DnsChoice = config.Dns;
            _competitiveSnapshot = config.CompetitiveSnapshot;
            FirstRunDone = config.FirstRunDone;

            Settings.SmartPackets = config.Settings.SmartPackets;
            Settings.Responsiveness = config.Settings.Responsiveness;
            Settings.Tuning = config.Settings.Tuning;
            Settings.ConnectionType = config.Settings.Type;
            Settings.ConnectionStable = config.Settings.ConnectionStable;
            Settings.CompetitiveMode = config.Settings.CompetitiveMode;

            Advanced.MtuAutomatic = config.Advanced.MtuAutomatic;
            Advanced.MtuValue = config.Advanced.MtuValue;
            Advanced.NetworkPriority = config.Advanced.NetworkPriority;
            Advanced.CongestionProvider = config.Advanced.CongestionProvider;
            Advanced.GhastPriorityMode = config.Advanced.GhastPriorityMode;
            Advanced.NetworkPowerSaving = config.Advanced.NetworkPowerSaving;

            // PacketsDelay is the authoritative delayed-ACK control; Latency mirrors it.
            Advanced.PacketsDelay = config.Advanced.PacketsDelay;
            Settings.Latency = ApplyService.LatencyFromTicks(
                ApplyService.TicksFromPacketsDelay(config.Advanced.PacketsDelay));
        }
        finally
        {
            _loading = false;
        }
    }

    // ---------- cross-control logic ----------

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading)
            return;

        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.Latency):
                if (!_syncingDelAck)
                {
                    _syncingDelAck = true;
                    Advanced.PacketsDelay = ApplyService.PacketsDelayFromTicks(
                        ApplyService.TicksFromLatency(Settings.Latency));
                    _syncingDelAck = false;
                }
                break;

            case nameof(SettingsViewModel.CompetitiveMode):
                OnCompetitiveModeToggled(Settings.CompetitiveMode);
                break;

            case nameof(SettingsViewModel.ConnectionType):
                SeedDefaultsForType(Settings.ConnectionType);
                break;

            case nameof(SettingsViewModel.StartWithWindows):
                _ = ApplyStartupToggleAsync(Settings.StartWithWindows);
                break;

            case nameof(SettingsViewModel.PinToTaskbar):
                _ = ApplyTaskbarPinToggleAsync(Settings.PinToTaskbar);
                break;
        }
    }

    // ---------- app-option toggles (Start with Windows / Pin to taskbar) ----------

    private async Task ApplyStartupToggleAsync(bool enabled)
    {
        try
        {
            await Task.Run(() => _startup.SetEnabled(enabled));
            ShowTransientStatus(enabled
                ? "Ghast will start with Windows (you may still see a UAC prompt at logon)."
                : "Ghast removed from Windows startup.");
        }
        catch (Exception ex)
        {
            Logger.Error("startup toggle", ex);
            RevertSettingsToggle(() => Settings.StartWithWindows = !enabled);
            ShowTransientStatus("Couldn't change the Windows startup entry — see log.txt.");
        }
    }

    private async Task ApplyTaskbarPinToggleAsync(bool pinned)
    {
        var ok = false;
        try
        {
            ok = await Task.Run(() => _taskbarPin.SetPinned(pinned));
        }
        catch (Exception ex)
        {
            Logger.Error("taskbar pin toggle", ex);
        }

        if (ok)
        {
            ShowTransientStatus(pinned ? "Ghast pinned to the taskbar." : "Ghast unpinned from the taskbar.");
        }
        else
        {
            RevertSettingsToggle(() => Settings.PinToTaskbar = !pinned);
            ShowTransientStatus(pinned
                ? "Windows blocked automatic pinning — right-click Ghast on the taskbar or Start Menu and choose \"Pin to taskbar\"."
                : "Windows blocked automatic unpinning — right-click the taskbar icon and choose \"Unpin from taskbar\".");
        }
    }

    private void RevertSettingsToggle(Action revert)
    {
        _loading = true;
        try { revert(); }
        finally { _loading = false; }
    }

    private void ShowTransientStatus(string message)
    {
        StatusItems.Clear();
        StatusText = message;
        StatusVisible = true;
    }

    private void OnAdvancedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading)
            return;

        if (e.PropertyName == nameof(AdvancedViewModel.PacketsDelay) && !_syncingDelAck)
        {
            _syncingDelAck = true;
            Settings.Latency = ApplyService.LatencyFromTicks(
                ApplyService.TicksFromPacketsDelay(Advanced.PacketsDelay));
            _syncingDelAck = false;
        }
    }

    /// <summary>Competitive Mode snapshots what it overrides so switching it OFF puts things back.</summary>
    private void OnCompetitiveModeToggled(bool on)
    {
        if (on)
        {
            _competitiveSnapshot = new CompetitiveSnapshot
            {
                SmartPackets = Settings.SmartPackets,
                Responsiveness = Settings.Responsiveness,
                GhastPriorityMode = Advanced.GhastPriorityMode,
                NetworkPowerSaving = Advanced.NetworkPowerSaving
            };
            Settings.SmartPackets = true;
            Settings.Responsiveness = 20; // slider max => SystemResponsiveness written as 0
            Advanced.GhastPriorityMode = true;
            Advanced.NetworkPowerSaving = true;
        }
        else if (_competitiveSnapshot is { } snap)
        {
            Settings.SmartPackets = snap.SmartPackets;
            Settings.Responsiveness = snap.Responsiveness;
            Advanced.GhastPriorityMode = snap.GhastPriorityMode;
            Advanced.NetworkPowerSaving = snap.NetworkPowerSaving;
            _competitiveSnapshot = null;
        }
    }

    /// <summary>Connection Type is [LOGIC]: it only seeds sensible defaults for the other controls.</summary>
    private void SeedDefaultsForType(string type)
    {
        switch (type)
        {
            case "Fiber":
                Settings.SmartPackets = true;
                Settings.Tuning = "Normal";
                Settings.Latency = 2; // ticks 0 — aggressive
                break;
            case "Cable":
                Settings.SmartPackets = true;
                Settings.Tuning = "Normal";
                Settings.Latency = 1;
                break;
            case "DSL":
                Settings.SmartPackets = true;
                Settings.Tuning = "Restricted";
                Settings.Latency = 1;
                break;
            case "Satellite":
                Settings.SmartPackets = false;
                Settings.Tuning = "Restricted";
                Settings.Latency = 0; // keep delayed ACKs — long fat link
                break;
            case "WiFi":
                Settings.SmartPackets = true;
                Settings.Tuning = "Normal";
                Settings.Latency = 1;
                Advanced.NetworkPowerSaving = true; // Wi-Fi power save causes the worst stutters
                break;
        }
    }
}
