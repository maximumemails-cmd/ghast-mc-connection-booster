using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ghast.ViewModels;

namespace Ghast.Views;

public partial class PresetsView : UserControl
{
    public PresetsView() => InitializeComponent();

    private PresetsViewModel? ViewModel => DataContext as PresetsViewModel;

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PresetItemViewModel item } element)
        {
            DragDrop.DoDragDrop(element, new DataObject(typeof(PresetItemViewModel), item), DragDropEffects.Move);
            e.Handled = true;
        }
    }

    private void PresetRow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(PresetItemViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void PresetRow_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is null)
            return;
        if (e.Data.GetData(typeof(PresetItemViewModel)) is not PresetItemViewModel source)
            return;
        if (sender is not FrameworkElement { DataContext: PresetItemViewModel target })
            return;

        ViewModel.Move(source, target);
        e.Handled = true;
    }
}
