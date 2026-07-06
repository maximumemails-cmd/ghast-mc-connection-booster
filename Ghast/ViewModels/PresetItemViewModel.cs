using CommunityToolkit.Mvvm.ComponentModel;
using Ghast.Models;

namespace Ghast.ViewModels;

public partial class PresetItemViewModel : ObservableObject
{
    public PresetItemViewModel(Preset preset) => Preset = preset;

    public Preset Preset { get; }

    public string Name => Preset.Name;

    [ObservableProperty] private bool _isSelected;
}
