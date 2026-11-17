using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NitroLite.Core;
using NitroLite.Desktop.Themes;

namespace NitroLite.Desktop.Converters;

/// <summary>
/// Turns a palette into the screenshot the theme card shows for it.
/// </summary>
/// <remarks>
/// <para>
/// A converter rather than a property on the view model, because a <see cref="Bitmap"/> needs the Avalonia platform to
/// decode and the view model is deliberately free of Avalonia types: that is what lets a test construct it and assert
/// that picking a theme applied and saved it, with no display attached. The converter only runs when a card is
/// actually rendered, which is exactly when a platform exists.
/// </para>
/// <para>
/// <see cref="AppTheme.System"/> has no picture of its own. It shows whatever it currently resolves to, which is the
/// window the user is actually looking at — the same rule the WPF front end used.
/// </para>
/// <para>
/// The bitmaps are cached per palette: a card is re-rendered on every selection change, and decoding six JPEGs each
/// time would be work for nothing. A palette whose asset is missing answers null, and the card falls back to its
/// swatch strip rather than showing an empty frame.
/// </para>
/// </remarks>
public sealed class ThemePreviewConverter : IValueConverter
{
    /// <summary>The shared instance the views bind to.</summary>
    public static readonly ThemePreviewConverter Instance = new();

    private static readonly Dictionary<AppTheme, Bitmap?> Cache = new();
    private static readonly object Gate = new();

    /// <summary>
    /// The asset name for a palette. Not the enum member lowercased: the warm-cream palette is
    /// <c>WarmCream</c> in code and <c>light.jpg</c> on disk, because the file was captured before the rename.
    /// </summary>
    public static string AssetName(AppTheme theme) => DesktopThemeManager.Resolve(theme) switch
    {
        AppTheme.Dark => "dark",
        AppTheme.MidnightOled => "midnightoled",
        AppTheme.SlateSteel => "slatesteel",
        AppTheme.AmberConsole => "amberconsole",
        AppTheme.NordFrost => "nordfrost",
        _ => "light"
    };

    /// <summary>The <c>avares://</c> address of a palette's screenshot.</summary>
    public static Uri AssetUri(AppTheme theme) =>
        new($"avares://NitroLite.Desktop/Assets/themes/{AssetName(theme)}.jpg");

    /// <summary>
    /// The decoded screenshot, or null when the asset is missing or there is no platform to decode it with. Cached, so
    /// the second card that asks for the same palette gets the same bitmap.
    /// </summary>
    public static Bitmap? Load(AppTheme theme)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(theme, out var cached)) return cached;

            Bitmap? bitmap = null;
            try
            {
                using var stream = AssetLoader.Open(AssetUri(theme));
                bitmap = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                // A build without the assets, or a test with no display: the card falls back to its swatches.
                AppLog.Warn(Loc.T("Tema önizlemesi yüklenemedi"), $"{theme}: {ex.Message}");
            }

            Cache[theme] = bitmap;
            return bitmap;
        }
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AppTheme theme ? Load(theme) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A screenshot is not a palette.");
}
