using System;
using System.Windows;
using Microsoft.Win32;

namespace NitroLite.Core;

/// <summary>
/// Loads the chosen palette into <see cref="Application.Resources"/> before any window is built.
/// <para>
/// The XAML refers to brushes with <c>StaticResource</c>, which resolves once, when the element is loaded. That is
/// why a theme change restarts NitroLite instead of swapping the dictionary under a live window: the merged
/// dictionary has to be in place before <c>InitializeComponent</c> runs. The cost is one restart per theme change,
/// and the benefit is that nothing in the window has to be re-bound or re-coloured at runtime.
/// </para>
/// <para>
/// <see cref="AppTheme.System"/> is resolved to a concrete palette here, at startup. Windows can flip its own theme
/// while NitroLite runs; the window keeps the palette it was built with until the next launch, which is the same
/// rule the explicit Light and Dark choices follow.
/// </para>
/// </summary>
public static class ThemeManager
{
    /// <summary>Windows stores the app theme here: 1 = light, 0 = dark.</summary>
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>The dictionary file for a theme, relative to the application root.</summary>
    public static string DictionaryPath(AppTheme theme) =>
        Resolve(theme) == AppTheme.Dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";

    /// <summary>
    /// Turns a stored preference into the palette that is actually loaded. <see cref="AppTheme.System"/> asks
    /// Windows; an unknown value falls back to the light surface rather than leaving the window unthemed.
    /// </summary>
    public static AppTheme Resolve(AppTheme? theme) => theme switch
    {
        AppTheme.Dark => AppTheme.Dark,
        AppTheme.System => SystemPrefersDark() ? AppTheme.Dark : AppTheme.Light,
        _ => AppTheme.Light
    };

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
}
