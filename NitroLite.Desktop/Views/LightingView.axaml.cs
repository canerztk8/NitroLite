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
    /// The picker button on one row of the temp-reactive ramp: opens the window on that stop's colour and writes
    /// whatever comes back into the stop. The row is the button's tag, so the handler works for every row without a
    /// command per item, and the view model only ever sees the finished <c>#RRGGBB</c>.
    /// </summary>
    /// <remarks>
    /// Wrapped for the same reason as the palette's picker: an <c>async void</c> handler has nowhere to put an
    /// exception, and one thrown out of the dialog would be raised on the thread pool and take the process down
    /// instead of leaving the page usable.
    /// </remarks>
    private async void OnPickReactiveStopColor(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Control { Tag: ReactiveStopViewModel row }) return;
            if (DataContext is not MainViewModel vm) return;
            if (TopLevel.GetTopLevel(this) is not Window owner) return;

            var dialog = new ColorPickerDialog(row.Color, vm.Labels.CustomColor);
            if (!await dialog.ShowDialog<bool>(owner)) return;

            vm.ApplyReactiveStopColor(row, dialog.PickedHex);
        }
        catch (Exception ex)
        {
            AppLog.Error("The colour picker could not be opened", ex.ToString());
        }
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
            AppLog.Error("The colour picker could not be opened", ex.ToString());
        }
    }

    /// <summary>
    /// The charging card's picker: opens the window on the colour the card holds and writes the answer into it. The
    /// setter is the one place that validates and stores a charging colour, so the view hands it the finished hex.
    /// </summary>
    /// <remarks>Wrapped for the same reason as the palette's picker above.</remarks>
    private async void OnPickChargingColorClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (TopLevel.GetTopLevel(this) is not Window owner) return;

            var dialog = new ColorPickerDialog(vm.ChargingColorPreview, vm.Labels.CustomColor);
            if (!await dialog.ShowDialog<bool>(owner)) return;

            vm.ChargingColor = dialog.PickedHex;
        }
        catch (Exception ex)
        {
            AppLog.Error("The colour picker could not be opened", ex.ToString());
        }
    }

    /// <summary>
    /// The swatch beside the solid entry's hex box: opens the picker on the colour that half paints and writes whatever
    /// comes back. The view model only ever sees the finished <c>#RRGGBB</c>.
    /// </summary>
    /// <remarks>Wrapped for the same reason as the two pickers above.</remarks>
    private async void OnPickReactiveSolidColor(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (TopLevel.GetTopLevel(this) is not Window owner) return;

            var dialog = new ColorPickerDialog(vm.ReactiveSolidPreview, vm.Labels.CustomColor);
            if (!await dialog.ShowDialog<bool>(owner)) return;

            vm.ReactiveSolidColor = dialog.PickedHex;
        }
        catch (Exception ex)
        {
            AppLog.Error("The colour picker could not be opened", ex.ToString());
        }
    }

    /// <summary>
    /// Enter in a ramp row's temperature box: the typed number is committed, and the box is rewritten to what the stop
    /// actually holds so a value the ramp clamped cannot be left on screen looking accepted.
    /// </summary>
    private void OnReactiveStopTemperatureKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        if (sender is not TextBox box) return;

        CommitReactiveStopTemperature(box);
        e.Handled = true;
    }

    /// <summary>
    /// Leaving a temperature box commits it too. The row's own buttons take the pointer, and a number typed there and
    /// then abandoned should still be the number the stop keeps.
    /// </summary>
    private void OnReactiveStopTemperatureLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;

        CommitReactiveStopTemperature(box);
    }

    /// <summary>
    /// Hands the typed text to the view model and puts the canonical value back in the box. Both entry points share it,
    /// so the two cannot disagree about what a half-typed number means — the view model ignores it.
    /// </summary>
    private void CommitReactiveStopTemperature(TextBox box)
    {
        if (DataContext is not MainViewModel vm) return;
        if (box.DataContext is not ReactiveStopViewModel row) return;

        vm.SetReactiveStopTemperature(row, box.Text);

        // The binding is one-way on purpose: the box is an editor, and the stop is the truth. Rewriting the text here
        // is what makes "8" become "20 °C" the moment the ramp clamps it.
        box.Text = row.TemperatureText;
        box.CaretIndex = box.Text?.Length ?? 0;
    }
}
