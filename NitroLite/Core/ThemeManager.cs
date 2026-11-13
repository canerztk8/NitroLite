using System;
using System.Windows;

namespace NitroLite.Core;

/// <summary>
/// Loads the chosen palette into <see cref="Application.Resources"/> before any window is built.
/// <para>
/// The XAML refers to brushes with <c>StaticResource</c>, which resolves once, when the element is loaded. That is
/// why a theme change restarts NitroLite instead of swapping the dictionary under a live window: the merged
/// dictionary has to be in place before <c>InitializeComponent</c> runs. The cost is one restart per theme change,
/// and the benefit is that nothing in the window has to be re-bound or re-coloured at runtime.
/// </para>
/// </summary>
public static class ThemeManager
{
    /// <summary>The dictionary file for a theme, relative to the application root.</summary>
    public static string DictionaryPath(AppTheme theme) =>
        theme == AppTheme.Dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";

    /// <summary>An unknown value falls back to the light surface rather than leaving the window unthemed.</summary>
    public static AppTheme Resolve(AppTheme? theme) => theme == AppTheme.Dark ? AppTheme.Dark : AppTheme.Light;

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
            Source = new Uri(DictionaryPath(Resolve(theme)), UriKind.Relative)
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
