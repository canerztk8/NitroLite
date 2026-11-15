using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NitroLite.Core;
// WinForms is referenced for the tray icon, so these three names exist twice.
using WpfButton = System.Windows.Controls.Button;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace NitroLite;

public partial class MainWindow
{
    // ---------------------------------------------------------------------------------------------------------------
    // Theme: preview cards, and a palette swap that happens under the live window instead of restarting the app.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds one preview card per palette. The cards are generated from <see cref="ThemeCatalog.All"/> rather than
    /// written into the XAML, so adding a palette is still one enum member and one dictionary.
    /// </summary>
    private void BuildThemeCards()
    {
        if (PanelThemeCards == null) return;

        PanelThemeCards.Children.Clear();
        foreach (var theme in ThemeCatalog.All)
        {
            var preview = ThemeManager.Preview(theme);

            var swatches = new StackPanel { Orientation = WpfOrientation.Horizontal };
            foreach (var colour in new[] { preview.Paper, preview.Card, preview.Border, preview.Ink, preview.Accent })
            {
                swatches.Children.Add(new Border
                {
                    Width = 13,
                    Height = 13,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(colour),
                    BorderBrush = new SolidColorBrush(preview.Border),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 3, 0),
                    SnapsToDevicePixels = true
                });
            }

            var label = new TextBlock
            {
                Text = Loc.T(ThemeCatalog.DisplayNameKey(theme)),
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

            var card = new WpfButton
            {
                Style = TryFindResource("ThemeCard") as Style,
                Tag = theme,
                MinWidth = 104,
                Content = new StackPanel { Children = { swatches, label } },
                ToolTip = Loc.T(ThemeCatalog.DisplayNameKey(theme))
            };
            card.Click += ThemeCard_Click;
            PanelThemeCards.Children.Add(card);
        }

        RefreshThemeCards();
    }

    /// <summary>
    /// Marks the stored preference, not the resolved palette: with "System" the window is showing one of the concrete
    /// themes, but the card that has to look chosen is the System one.
    /// </summary>
    private void RefreshThemeCards()
    {
        if (PanelThemeCards == null) return;

        var accent = TryFindResource("Vermilion") as SolidColorBrush ?? WpfBrushes.Gray;
        var none = WpfBrushes.Transparent;

        foreach (var card in PanelThemeCards.Children.OfType<WpfButton>())
        {
            var active = card.Tag is AppTheme theme && theme == _appSettings.Theme;
            card.BorderBrush = active ? accent : none;
        }
    }

    private void ThemeCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: AppTheme theme }) return;
        if (theme == _appSettings.Theme) return;

        // Drive the combo box rather than duplicating its logic: it is the control the rest of the code reads, and
        // its handler is what saves the choice and applies the palette.
        if (CmbTheme != null)
        {
            var match = CmbTheme.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag is AppTheme t && t == theme);
            if (match != null)
            {
                CmbTheme.SelectedItem = match;
                return;
            }
        }

        // No combo box (a harness-built window): apply directly so the card still works.
        ApplyThemeLive(theme);
    }

    /// <summary>
    /// Swaps the palette under the running window. The XAML brushes are <c>DynamicResource</c>, so the tree follows
    /// the dictionary on its own; what is left is the handful of places that paint themselves in code.
    /// </summary>
    private void ApplyThemeLive(AppTheme theme)
    {
        _appSettings.Theme = theme;
        SaveSettings();

        ThemeManager.Apply(theme);

        // The DWM frame is a native attribute, not a brush: it has to be told separately or a dark window keeps a
        // light 1px border around the whole app.
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) WindowChromeHelper.ApplyDarkFrame(hwnd, ThemeManager.IsDark(theme));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Tema çerçevesi güncellenemedi", ex.Message);
        }

        RepaintThemeDependentVisuals();
        RefreshThemeCards();
    }

    /// <summary>
    /// Re-runs the code paths that write brushes by hand. Everything bound with <c>DynamicResource</c> has already
    /// followed the swap; these are the canvases and status labels that were painted once and would otherwise keep
    /// the previous palette's ink.
    /// </summary>
    private void RepaintThemeDependentVisuals()
    {
        try
        {
            UpdateModeUi(_currentMode);
            UpdateFanButtonsUi(_currentFanMode);
            UpdateServiceButtons();
            UpdateSchoolModeUi();
            if (_currentGpuMode is { } gpu) UpdateGpuModeUi(gpu);
            RepaintSegmentedControls();
            RenderFanCurve();
            RenderWearTrend();
            RenderModeTrace();
            RenderTelemetryChart();
        }
        catch (Exception ex)
        {
            // A repaint that fails must not take the window down: the palette is already applied, and the next
            // telemetry tick repaints the same surfaces anyway.
            AppLog.Warn("Tema sonrası yeniden çizim başarısız", ex.ToString());
        }
    }

    /// <summary>
    /// Restores the rail width from settings. Called once the window is built, before the first layout pass, so the
    /// content area is never briefly the wrong size.
    /// </summary>
    private void RestoreSidebarWidth()
    {
        if (ColSidebar == null) return;
        ColSidebar.Width = new GridLength(AppSettings.ClampSidebarWidth(_appSettings.SidebarWidth));
    }

    private void RailSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ColSidebar == null) return;

        var width = AppSettings.ClampSidebarWidth(ColSidebar.ActualWidth);
        ColSidebar.Width = new GridLength(width);
        _appSettings.SidebarWidth = width;
        SaveSettings();
    }

    /// <summary>
    /// Double-clicking the rail's edge puts it back to the default width. A drag is the only way to change the
    /// width, so without this there is no way back to the layout the window shipped with short of editing the
    /// settings file.
    /// </summary>
    /// <remarks>
    /// This is the preview event on purpose: <see cref="GridSplitter"/> is a <see cref="System.Windows.Controls.Primitives.Thumb"/>,
    /// which marks the mouse-down handled and captures the mouse to start a drag, so the bubbling
    /// <c>MouseDoubleClick</c> never arrives. Marking the second click handled also keeps that click from starting a
    /// drag on top of the reset.
    /// </remarks>
    private void RailSplitter_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || ColSidebar == null) return;

        ColSidebar.Width = new GridLength(AppSettings.DefaultSidebarWidth);
        _appSettings.SidebarWidth = AppSettings.DefaultSidebarWidth;
        SaveSettings();
        e.Handled = true;
    }
}
