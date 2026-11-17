using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;

namespace NitroLite.Desktop.Views;

public partial class LightingView : UserControl
{
    public LightingView() => InitializeComponent();

    /// <summary>
    /// A click on one of the four bars. The tag carries the zone number, so the same handler serves all four and the
    /// XAML stays a deck rather than a list of commands. Zone 0 is the primary colour, 1..4 the bars.
    /// </summary>
    private void OnZonePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string tag }) return;
        if (DataContext is not MainViewModel vm) return;
        if (!int.TryParse(tag, out int zone)) return;

        vm.SelectZoneForPicker(zone);
    }

    /// <summary>
    /// The swatch beside the hex box: opens the picker on the active target's colour and writes whatever comes back.
    /// The dialog is the view's business, so the view model only ever sees the finished <c>#RRGGBB</c>.
    /// </summary>
    /// <remarks>
    /// Wrapped because an <c>async void</c> handler has nowhere to put an exception: one thrown out of the dialog
    /// would be raised on the thread pool and take the process down instead of leaving the page usable.
    /// </remarks>
    private async void OnPickColorClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (TopLevel.GetTopLevel(this) is not Window owner) return;

            var dialog = new ColorPickerDialog(vm.ActiveColor, vm.Labels.CustomColor);
            if (!await dialog.ShowDialog<bool>(owner)) return;

            vm.ApplyPickedColor(dialog.PickedHex);
        }
        catch (Exception ex)
        {
            AppLog.Error("Renk seçici açılamadı", ex.ToString());
        }
    }
}
