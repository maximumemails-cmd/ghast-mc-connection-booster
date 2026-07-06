using CommunityToolkit.Mvvm.ComponentModel;

namespace Ghast.ViewModels;

public partial class AdvancedViewModel : ObservableObject
{
    public string[] CongestionOptions { get; } = { "Default", "CUBIC", "CTCP", "NewReno", "DCTCP" };

    [ObservableProperty] private bool _mtuAutomatic = true;

    /// <summary>576-1500, used only when MtuAutomatic is false. Clamped when the config is built.</summary>
    [ObservableProperty] private int _mtuValue = 1500;

    /// <summary>0-6. Authoritative half of the merged delayed-ACK control (see README).</summary>
    [ObservableProperty] private int _packetsDelay = 4;

    [ObservableProperty] private int _networkPriority = 1;

    [ObservableProperty] private string _congestionProvider = "Default";

    [ObservableProperty] private bool _ghastPriorityMode = true;

    /// <summary>true = adapter power saving DISABLED ("stop Windows sleeping the NIC").</summary>
    [ObservableProperty] private bool _networkPowerSaving = true;
}
