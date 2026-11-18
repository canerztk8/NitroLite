using System.Text.Json;
using System.Text.Json.Serialization;

namespace NitroLite.Core;

/// <summary>
/// The list of palettes NitroLite ships, and the one place that decides whether a palette is dark.
/// </summary>
/// <remarks>
/// Every dark-specific decision in the app — the DWM window frame, the OSD, the chart ink — asks
/// <see cref="IsDarkTheme"/> rather than comparing against <see cref="AppTheme.Dark"/>. Adding a palette is then one
/// enum member, one dictionary and one entry in <see cref="All"/>; nothing else has to learn about it.
/// </remarks>
public static class ThemeCatalog
{
    /// <summary>Every palette, in the order the settings picker offers them.</summary>
    public static IReadOnlyList<AppTheme> All { get; } = new[]
    {
        AppTheme.System,
        AppTheme.WarmCream,
        AppTheme.Dark,
        AppTheme.MidnightOled,
        AppTheme.SlateSteel,
        AppTheme.AmberConsole,
        AppTheme.NordFrost,
        AppTheme.CleanWhite,
        AppTheme.Forest,
        AppTheme.NeonStreet,
        AppTheme.HighContrast,
        AppTheme.ViceSunset,
        AppTheme.CyberChrome
    };

    /// <summary>
    /// The palettes that are dark-based. <see cref="AppTheme.System"/> is not one of them: it is a preference, and
    /// what it resolves to is <see cref="ThemeManager.Resolve"/>'s answer.
    /// </summary>
    public static IReadOnlyList<AppTheme> DarkThemes { get; } = new[]
    {
        AppTheme.Dark,
        AppTheme.MidnightOled,
        AppTheme.SlateSteel,
        AppTheme.AmberConsole,
        AppTheme.NordFrost,
        AppTheme.Forest,
        AppTheme.NeonStreet,
        AppTheme.HighContrast,
        AppTheme.ViceSunset,
        AppTheme.CyberChrome
    };

    /// <summary>True when <paramref name="theme"/> is one of the dark-based palettes.</summary>
    public static bool IsDarkTheme(AppTheme theme) => DarkThemes.Contains(theme);

    /// <summary>
    /// True when the stored theme name is a dark-based palette. Accepts the enum member name in any casing, which is
    /// what a hand-edited settings file carries; an unknown name is not dark.
    /// </summary>
    public static bool IsDarkTheme(string? themeName) =>
        TryParse(themeName, out var theme) && IsDarkTheme(theme);

    /// <summary>
    /// The Turkish catalogue key that names <paramref name="theme"/> in the settings picker. The key is the source
    /// text, so the seven catalogues translate it like any other label.
    /// </summary>
    public static string DisplayNameKey(AppTheme theme) => theme switch
    {
        AppTheme.System => "Sistem varsayılanı",
        AppTheme.Dark => "Koyu",
        AppTheme.MidnightOled => "Saf Siyah (OLED)",
        AppTheme.SlateSteel => "Soğuk Antrasit",
        AppTheme.AmberConsole => "Kehribar Konsol",
        AppTheme.NordFrost => "Kuzey Buzulu",
        AppTheme.CleanWhite => "Temiz Beyaz",
        AppTheme.Forest => "Orman",
        AppTheme.NeonStreet => "Neon Sokak",
        AppTheme.HighContrast => "Yüksek Karşıtlık",
        AppTheme.ViceSunset => "Neon Gün Batımı",
        AppTheme.CyberChrome => "Krom Şehir",
        _ => "Sıcak Krem"
    };

    /// <summary>
    /// Reads a stored theme name. Accepts the member name in any casing and the legacy numeric form, so a settings
    /// file written by any version of NitroLite still loads.
    /// </summary>
    /// <remarks>
    /// <c>Light</c> is the name the warm-cream palette carried before it was renamed, and a settings file written by
    /// that build still says so. It is read as <see cref="AppTheme.WarmCream"/> rather than rejected, because a user
    /// who picked the light palette should not be moved to a different one by an upgrade.
    /// </remarks>
    public static bool TryParse(string? name, out AppTheme theme)
    {
        theme = AppTheme.System;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var trimmed = name.Trim();
        if (int.TryParse(trimmed, out int number))
        {
            if (!Enum.IsDefined(typeof(AppTheme), number)) return false;
            theme = (AppTheme)number;
            return true;
        }

        if (string.Equals(trimmed, "Light", StringComparison.OrdinalIgnoreCase))
        {
            theme = AppTheme.WarmCream;
            return true;
        }

        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                theme = candidate;
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Writes <see cref="AppTheme"/> as its member name and reads either that name or the number older builds wrote.
/// </summary>
/// <remarks>
/// The name is what makes a settings file readable and what the theme picker's stable keys are named after. Reading
/// the number as well is the backward-compatibility half: a config.json from before the extra palettes carries
/// <c>"theme": 1</c>, and that still has to mean Dark. An unrecognised value falls back to
/// <see cref="AppTheme.System"/> rather than leaving the window unthemed.
/// </remarks>
public sealed class AppThemeJsonConverter : JsonConverter<AppTheme>
{
    public override AppTheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number))
        {
            return Enum.IsDefined(typeof(AppTheme), number) ? (AppTheme)number : AppTheme.System;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return ThemeCatalog.TryParse(reader.GetString(), out var theme) ? theme : AppTheme.System;
        }

        // Null, an object, an array: nothing that names a palette.
        reader.Skip();
        return AppTheme.System;
    }

    public override void Write(Utf8JsonWriter writer, AppTheme value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
