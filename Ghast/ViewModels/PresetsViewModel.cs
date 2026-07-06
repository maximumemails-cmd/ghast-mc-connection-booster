using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghast.Models;
using Ghast.Services;

namespace Ghast.ViewModels;

public partial class PresetsViewModel : ObservableObject
{
    private readonly PresetService _presetService;
    private readonly Func<GhastConfig> _snapshotConfig;
    private readonly Func<Preset, Task> _applyPreset;
    private readonly Func<string, string?> _promptName;
    private readonly Func<string, IEnumerable<string>, string, bool> _confirm;

    public PresetsViewModel(
        PresetService presetService,
        Func<GhastConfig> snapshotConfig,
        Func<Preset, Task> applyPreset,
        Func<string, string?> promptName,
        Func<string, IEnumerable<string>, string, bool> confirm)
    {
        _presetService = presetService;
        _snapshotConfig = snapshotConfig;
        _applyPreset = applyPreset;
        _promptName = promptName;
        _confirm = confirm;

        _presetService.EnsureSeeded();
        foreach (var preset in _presetService.LoadAll())
            Items.Add(new PresetItemViewModel(preset));

        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => string.IsNullOrWhiteSpace(SearchText)
                           || ((PresetItemViewModel)o).Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    public ObservableCollection<PresetItemViewModel> Items { get; } = new();

    public ICollectionView View { get; }

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private bool _selectMode;

    partial void OnSearchTextChanged(string value) => View.Refresh();

    partial void OnSelectModeChanged(bool value)
    {
        if (!value)
            foreach (var item in Items)
                item.IsSelected = false;
    }

    [RelayCommand]
    private void ToggleSelectMode() => SelectMode = !SelectMode;

    [RelayCommand]
    private async Task ApplyPresetAsync(PresetItemViewModel? item)
    {
        if (item is not null)
            await _applyPreset(item.Preset);
    }

    [RelayCommand]
    private void CreatePreset()
    {
        var name = _promptName("Name your preset");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var preset = new Preset { Name = name.Trim(), Config = _snapshotConfig() };

        var existing = Items.FirstOrDefault(i =>
            i.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!_confirm("Overwrite preset?", new[] { $"A preset named \"{preset.Name}\" already exists." }, "Overwrite"))
                return;
            Items.Remove(existing);
            _presetService.Delete(existing.Preset);
        }

        _presetService.Save(preset);
        Items.Add(new PresetItemViewModel(preset));
        PersistOrder();
    }

    [RelayCommand]
    private void ImportPreset()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Ghast preset",
            Filter = "Ghast presets (*.ghast;*.json)|*.ghast;*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var preset = _presetService.Import(dialog.FileName);
            Items.Add(new PresetItemViewModel(preset));
            PersistOrder();
        }
        catch (Exception ex)
        {
            Logger.Error("importing preset", ex);
            _confirm("Import failed", new[] { ex.Message }, "OK");
        }
    }

    [RelayCommand]
    private async Task BulkApplyAsync()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
            return;
        if (!_confirm("Apply selected presets?",
                selected.Select(s => s.Name).Append("They run in order — the last one wins where they overlap."),
                "Apply"))
            return;

        foreach (var item in selected)
            await _applyPreset(item.Preset);
        SelectMode = false;
    }

    [RelayCommand]
    private void BulkDelete()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
            return;
        if (!_confirm("Delete selected presets?", selected.Select(s => s.Name), "Delete"))
            return;

        foreach (var item in selected)
        {
            _presetService.Delete(item.Preset);
            Items.Remove(item);
        }
        PersistOrder();
        SelectMode = false;
    }

    /// <summary>Called by the view's drag-handle drop handler.</summary>
    public void Move(PresetItemViewModel source, PresetItemViewModel target)
    {
        var from = Items.IndexOf(source);
        var to = Items.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
            return;
        Items.Move(from, to);
        PersistOrder();
    }

    private void PersistOrder() => _presetService.SaveOrder(Items.Select(i => i.Name));
}
