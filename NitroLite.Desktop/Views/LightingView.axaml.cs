using Avalonia.Controls;
using Avalonia.Input;
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
}
