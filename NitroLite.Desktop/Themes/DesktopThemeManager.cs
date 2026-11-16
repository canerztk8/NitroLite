using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;
using NitroLite.Core;

namespace NitroLite.Desktop.Themes;

/// <summary>
/// Loads the chosen palette into <see cref="Application.Resources"/>.
/// </summary>
/// <remarks>
/// <para>
/// The views refer to brushes with <c>DynamicResource</c>, which re-resolves whenever the merged dictionary changes.
/// That is what lets a theme change happen under a live window: <see cref="Apply"/> swaps the dictionary and every
/// brush reference in the tree follows, with no restart and nothing re-bound by hand.
/// </para>
/// <para>
/// <see cref="AppTheme.System"/> is resolved to a concrete palette here, by asking the desktop rather than the
/// registry: Avalonia's <see cref="IPlatformSettings"/> answers the same question on every platform NitroLite runs on.
/// A desktop that cannot answer reads as light, which is the palette NitroLite shipped with.
/// </para>
/// <para>
/// The Fluent chrome — combo-box popups, text-box selection, scrollbars — is switched with
/// <see cref="Application.RequestedThemeVariant"/> so it follows the palette instead of staying dark under a light
/// one. The palette itself is not a Fluent theme variant: it is a dictionary of brushes, and the two are independent.
/// </para>
/// </remarks>
public sealed class DesktopThemeManager : IThemeApplier
{
    /// <summary>The one instance the composition root hands to the view model.</summary>
    public static DesktopThemeManager Instance { get; } = new();

    /// <summary>The dictionary currently merged into the application, so a swap can remove exactly that one.</summary>
    private static ResourceDictionary? _current;

    /// <summary>
    /// Turns a stored preference into the palette that is actually loaded. Every concrete palette is its own answer:
    /// the four extra dark ones are not aliases of <see cref="AppTheme.Dark"/>.
    /// </summary>
    public static AppTheme Resolve(AppTheme theme) => theme switch
    {
        AppTheme.System => SystemPrefersDark() ? AppTheme.Dark : AppTheme.Light,
        { } concrete when ThemeCatalog.All.Contains(concrete) => concrete,
        _ => AppTheme.Light
    };

    /// <summary>
    /// True when the desktop is set to a dark appearance. A platform that cannot answer, or an application that does
    /// not exist yet, reads as light.
    /// </summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            var settings = Application.Current?.PlatformSettings;
            return settings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The brushes for a theme, resolved. Public so a test can assert the palette without an
    /// <see cref="Application"/>: the dictionary is the whole answer, and merging it is the only part that needs one.
    /// </summary>
    public static ResourceDictionary BuildResources(AppTheme theme) => ThemePalette.For(Resolve(theme)).ToResources();

    /// <summary>
    /// Replaces the palette dictionary. Safe to call when there is no <see cref="Application"/> (the test harness
    /// builds view models without one): it then does nothing instead of throwing.
    /// </summary>
    public void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app == null) return;

        var resolved = Resolve(theme);

        var resources = app.Resources ??= new ResourceDictionary();
        var merged = resources.MergedDictionaries;

        // Only the palette is replaced; anything else merged into the application stays.
        if (_current != null) merged.Remove(_current);

        _current = ThemePalette.For(resolved).ToResources();
        merged.Add(_current);

        app.RequestedThemeVariant = ThemeCatalog.IsDarkTheme(resolved) ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
