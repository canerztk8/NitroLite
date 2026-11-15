using System.Windows;
using Microsoft.Win32;

namespace NitroLite.Core;

/// <summary>
/// Loads the chosen palette into <see cref="Application.Resources"/>.
/// <para>
/// The XAML refers to brushes with <c>DynamicResource</c>, which re-resolves whenever the merged dictionary changes.
/// That is what lets a theme change happen under a live window: <see cref="Apply"/> swaps the dictionary and every
/// brush reference in the tree follows, with no restart and nothing re-bound by hand. The few places that paint
/// themselves in code (the fan curve, the wear trend, the status colours) are re-run by the window after the swap.
/// </para>
/// <para>
/// <see cref="AppTheme.System"/> is resolved to a concrete palette here. Windows can flip its own theme while
/// NitroLite runs; the window keeps the palette it was built with until the next launch, which is the same rule the
/// explicit Light and Dark choices follow.
/// </para>
/// </summary>
public static class ThemeManager
{
    /// <summary>Windows stores the app theme here: 1 = light, 0 = dark.</summary>
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>The dictionary file for a theme, relative to the application root.</summary>
    public static string DictionaryPath(AppTheme theme) => ThemeCatalog.DictionaryPath(Resolve(theme));

    /// <summary>
    /// Turns a stored preference into the palette that is actually loaded. <see cref="AppTheme.System"/> asks
    /// Windows; an unknown value falls back to the light surface rather than leaving the window unthemed.
    /// </summary>
    public static AppTheme Resolve(AppTheme? theme) => theme switch
    {
        AppTheme.System => SystemPrefersDark() ? AppTheme.Dark : AppTheme.Light,
        // Every concrete palette is its own answer: the four extra dark ones are not aliases of Dark.
        { } concrete when ThemeCatalog.All.Contains(concrete) => concrete,
        _ => AppTheme.Light
    };

    /// <summary>
    /// True when the palette that will actually be loaded is dark-based. This is what the window frame, the OSD and
    /// the chart ask, so a new dark palette needs no change at any of those call sites.
    /// </summary>
    public static bool IsDark(AppTheme? theme) => ThemeCatalog.IsDarkTheme(Resolve(theme));

    /// <summary>
    /// True when Windows is set to the dark app theme. A missing value or a locked-down registry reads as light,
    /// which is the Windows default and the palette NitroLite shipped with.
    /// </summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // AppsUseLightTheme is a DWORD; anything other than an explicit 0 means light.
            return key?.GetValue(AppsUseLightThemeValue) is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces the merged theme dictionary. Safe to call when there is no <see cref="Application"/> (the test
    /// harness builds windows without one): it then does nothing instead of throwing.
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;

        var dictionary = new ResourceDictionary
        {
            Source = new Uri(DictionaryPath(theme), UriKind.Relative)
        };

        // Only the theme dictionary is replaced; anything else merged into the application stays.
        var merged = app.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var source = merged[i].Source?.OriginalString;
            if (source != null && source.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) &&
                source.Contains("Themes/", StringComparison.OrdinalIgnoreCase))
            {
                merged.RemoveAt(i);
            }
        }

        merged.Add(dictionary);
    }

    /// <summary>
    /// The five colours that identify a palette at a glance: the page, the card, the frame, the body ink and the
    /// accent. The settings picker draws them as swatches so the choice is made by looking rather than by reading
    /// seven names that mean nothing until the window has already changed.
    /// </summary>
    public readonly record struct ThemePreview(
        System.Windows.Media.Color Paper,
        System.Windows.Media.Color Card,
        System.Windows.Media.Color Border,
        System.Windows.Media.Color Ink,
        System.Windows.Media.Color Accent);

    /// <summary>
    /// Reads the preview colours out of a palette's dictionary. A dictionary that cannot be loaded, or one missing a
    /// key, falls back to the light palette rather than throwing: the picker is decoration, and a swatch is never a
    /// reason to lose the settings page.
    /// </summary>
    public static ThemePreview Preview(AppTheme theme)
    {
        var resolved = Resolve(theme);
        try
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri(ThemeCatalog.DictionaryPath(resolved), UriKind.Relative)
            };

            return new ThemePreview(
                Read(dictionary, "Paper"),
                Read(dictionary, "CardBg"),
                Read(dictionary, "BorderDark"),
                Read(dictionary, "Ink"),
                Read(dictionary, "Vermilion"));
        }
        catch
        {
            return new ThemePreview(
                System.Windows.Media.Color.FromRgb(0xF2, 0xED, 0xE3),
                System.Windows.Media.Color.FromRgb(0xF8, 0xF4, 0xEB),
                System.Windows.Media.Color.FromRgb(0xCF, 0xC4, 0xAF),
                System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x17),
                System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));
        }
    }

    private static System.Windows.Media.Color Read(ResourceDictionary dictionary, string key) =>
        dictionary[key] is System.Windows.Media.SolidColorBrush brush
            ? brush.Color
            : System.Windows.Media.Colors.Transparent;
}
