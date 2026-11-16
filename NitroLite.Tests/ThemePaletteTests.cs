using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using NitroLite.Core;
using NitroLite.Desktop.Themes;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Guards the palettes themselves: every theme carries every colour, and every theme is legible.
/// </summary>
/// <remarks>
/// A missing colour is not a compile error — it is a transparent surface at the moment the window loads, on the one
/// machine that picked that palette. The completeness test is what turns that into a red build. The contrast test is
/// the same idea for the other silent failure: a palette that loads but whose 1px rules disappear into the page, or
/// whose body text is too faint to read.
/// </remarks>
public class ThemePaletteTests
{
    // =============================================================================================================
    // Reading the palettes.
    // =============================================================================================================

    /// <summary>The concrete palettes: everything except the "follow the desktop" preference.</summary>
    private static IEnumerable<AppTheme> ConcreteThemes =>
        ThemeCatalog.All.Where(t => t != AppTheme.System);

    /// <summary>Every colour a palette carries, keyed by the property name the views ask for.</summary>
    private static Dictionary<string, Color> Colours(AppTheme theme)
    {
        var palette = ThemePalette.For(theme);
        return typeof(ThemePalette)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(Color) && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, p => (Color)p.GetValue(palette)!, StringComparer.Ordinal);
    }

    private static string Hex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    // =============================================================================================================
    // 1. Completeness.
    // =============================================================================================================

    [Fact]
    public void EveryPaletteCarriesExactlyTheSameColours()
    {
        var reference = Colours(AppTheme.Dark);
        Assert.NotEmpty(reference);

        foreach (var theme in ConcreteThemes)
        {
            var actual = Colours(theme);
            var missing = reference.Keys.Except(actual.Keys, StringComparer.Ordinal).OrderBy(k => k).ToList();
            var extra = actual.Keys.Except(reference.Keys, StringComparer.Ordinal).OrderBy(k => k).ToList();

            Assert.True(missing.Count == 0, $"{theme} is missing: {string.Join(", ", missing)}");
            Assert.True(extra.Count == 0, $"{theme} has colours Dark does not: {string.Join(", ", extra)}");
        }
    }

    [Fact]
    public void EveryPaletteDefinesEveryColourTheViewsAskFor()
    {
        // The handful the styles resolve by name at runtime; a typo here is an invisible surface, not a compile error.
        var required = new[]
        {
            "Page", "Card", "CardHover", "Panel", "Rail", "CardBorder", "CardBorderStrong",
            "TextPrimary", "TextSecondary", "TextMuted", "Accent", "AccentHover", "AccentSoft",
            "OnAccent", "Success", "Warning", "Danger", "ChartFill"
        };

        foreach (var theme in ConcreteThemes)
        {
            var colours = Colours(theme);
            foreach (var key in required)
            {
                Assert.True(colours.ContainsKey(key), $"{theme} does not define {key}");
            }
        }
    }

    [Fact]
    public void EveryColourIsOpaqueExceptTheDocumentedWashes()
    {
        // A surface that lost its alpha channel is a card you can see through; a wash that gained one is a badge that
        // hides the text under it. Both are silent, so both are pinned.
        var washes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ChartFill", "AccentSoft", "SuccessSoft", "WarningSoft", "DangerSoft"
        };

        foreach (var theme in ConcreteThemes)
        {
            foreach (var (key, colour) in Colours(theme))
            {
                if (washes.Contains(key))
                {
                    Assert.True(colour.A is > 0 and < 255, $"{theme}.{key} is not a wash: alpha {colour.A}");
                }
                else
                {
                    Assert.True(colour.A == 255, $"{theme}.{key} is translucent: alpha {colour.A}");
                }
            }
        }
    }

    // =============================================================================================================
    // 2. Contrast.
    // =============================================================================================================

    /// <summary>WCAG 2.1 relative luminance of a colour.</summary>
    private static double Luminance(Color colour)
    {
        double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);
    }

    /// <summary>WCAG 2.1 contrast ratio between two colours.</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void BodyTextMeetsWcagAaOnEveryCard()
    {
        foreach (var theme in ConcreteThemes)
        {
            var c = Colours(theme);
            double ratio = Contrast(c["TextPrimary"], c["Card"]);
            Assert.True(ratio >= 4.5, $"{theme}: TextPrimary on Card is {ratio:F2}:1, below the 4.5:1 AA floor");
        }
    }

    [Fact]
    public void CardRulesStayVisibleAgainstThePage()
    {
        foreach (var theme in ConcreteThemes)
        {
            var c = Colours(theme);
            double ratio = Contrast(c["CardBorder"], c["Page"]);
            Assert.True(ratio >= 1.45, $"{theme}: CardBorder on Page is {ratio:F2}:1, below the 1.45:1 floor");
        }
    }

    [Fact]
    public void InfoBadgeAndActiveModeFrameStayVisible()
    {
        foreach (var theme in ConcreteThemes)
        {
            var c = Colours(theme);

            // The (?) badge is a 1px CardBorderStrong ring with TextMuted type on a card.
            double ring = Contrast(c["CardBorderStrong"], c["Card"]);
            Assert.True(ring >= 1.45, $"{theme}: CardBorderStrong on Card is {ring:F2}:1, the (?) ring would vanish");

            double glyph = Contrast(c["TextMuted"], c["Card"]);
            Assert.True(glyph >= 3.0, $"{theme}: TextMuted on Card is {glyph:F2}:1, the (?) glyph would be unreadable");

            // The active mode card is marked by an Accent frame drawn over the page.
            double frame = Contrast(c["Accent"], c["Page"]);
            Assert.True(frame >= 3.0, $"{theme}: Accent on Page is {frame:F2}:1, the active mode frame would blur");
        }
    }

    [Fact]
    public void StrongRulesAreStrongerThanWeakOnes()
    {
        foreach (var theme in ConcreteThemes)
        {
            var c = Colours(theme);
            // CardBorderStrong is the emphasis rule; if it is not further from the page than CardBorder, the hierarchy
            // is flat and a focused field looks like an unfocused one.
            Assert.True(Contrast(c["CardBorderStrong"], c["Page"]) > Contrast(c["CardBorder"], c["Page"]),
                $"{theme}: CardBorderStrong is not stronger than CardBorder");
        }
    }

    [Fact]
    public void CardToneSeparatesFromThePage()
    {
        foreach (var theme in ConcreteThemes)
        {
            var c = Colours(theme);
            // A card that is the same colour as the page is a card you cannot see, whatever its border does.
            Assert.NotEqual(Hex(c["Card"]), Hex(c["Page"]));
            Assert.NotEqual(Hex(c["CardHover"]), Hex(c["Card"]));
        }
    }

    // =============================================================================================================
    // 3. The dark classification.
    // =============================================================================================================

    [Theory]
    [InlineData(AppTheme.Dark, true)]
    [InlineData(AppTheme.MidnightOled, true)]
    [InlineData(AppTheme.SlateSteel, true)]
    [InlineData(AppTheme.AmberConsole, true)]
    [InlineData(AppTheme.NordFrost, true)]
    [InlineData(AppTheme.Light, false)]
    public void IsDarkTheme_ClassifiesEveryConcretePalette(AppTheme theme, bool expected)
    {
        Assert.Equal(expected, ThemeCatalog.IsDarkTheme(theme));
    }

    [Fact]
    public void IsDarkTheme_DoesNotClassifyTheSystemPreference()
    {
        // System is a preference, not a palette: what it resolves to is the desktop's answer.
        Assert.False(ThemeCatalog.IsDarkTheme(AppTheme.System));
    }

    [Theory]
    [InlineData("Dark", true)]
    [InlineData("dark", true)]
    [InlineData("MidnightOled", true)]
    [InlineData("SlateSteel", true)]
    [InlineData("AmberConsole", true)]
    [InlineData("NordFrost", true)]
    [InlineData("Light", false)]
    [InlineData("System", false)]
    [InlineData("Nonsense", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDarkTheme_ReadsAStoredName(string? name, bool expected)
    {
        Assert.Equal(expected, ThemeCatalog.IsDarkTheme(name));
    }

    [Fact]
    public void EveryDarkPaletteIsInTheDarkList()
    {
        foreach (var theme in ConcreteThemes)
        {
            Assert.Equal(ThemeCatalog.IsDarkTheme(theme), ThemeCatalog.DarkThemes.Contains(theme));
        }
    }

    [Fact]
    public void EveryConcretePaletteHasItsOwnPalette()
    {
        var seen = new HashSet<ThemePalette>();
        foreach (var theme in ConcreteThemes)
        {
            Assert.True(seen.Add(ThemePalette.For(theme)), $"{theme} shares a palette with another theme");
        }
    }

    [Fact]
    public void EveryPaletteHasADisplayName()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in ThemeCatalog.All)
        {
            var key = ThemeCatalog.DisplayNameKey(theme);
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.True(keys.Add(key), $"{theme} reuses another palette's label: {key}");
        }
    }

    [Fact]
    public void EveryPaletteLabelIsTranslated()
    {
        var path = Path.Combine(RepoRoot(), "NitroLite.Core", "Lang", "en.json");
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8));
        Assert.NotNull(english);

        foreach (var theme in ThemeCatalog.All)
        {
            var key = ThemeCatalog.DisplayNameKey(theme);
            Assert.True(english!.ContainsKey(key), $"en.json has no label for {theme} (key: {key})");
        }
    }

    // =============================================================================================================
    // 4. Stored names and the settings round trip.
    // =============================================================================================================

    [Theory]
    [InlineData("System", AppTheme.System)]
    [InlineData("Light", AppTheme.Light)]
    [InlineData("Dark", AppTheme.Dark)]
    [InlineData("MidnightOled", AppTheme.MidnightOled)]
    [InlineData("SlateSteel", AppTheme.SlateSteel)]
    [InlineData("AmberConsole", AppTheme.AmberConsole)]
    [InlineData("NordFrost", AppTheme.NordFrost)]
    [InlineData("nordfrost", AppTheme.NordFrost)]
    [InlineData("0", AppTheme.Light)]
    [InlineData("1", AppTheme.Dark)]
    [InlineData("2", AppTheme.System)]
    [InlineData("6", AppTheme.NordFrost)]
    public void TryParse_AcceptsNamesAndLegacyNumbers(string stored, AppTheme expected)
    {
        Assert.True(ThemeCatalog.TryParse(stored, out var theme));
        Assert.Equal(expected, theme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Nonsense")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData(null)]
    public void TryParse_RejectsAnythingElse(string? stored)
    {
        Assert.False(ThemeCatalog.TryParse(stored, out _));
    }

    [Fact]
    public void Settings_RoundTripEveryPalette()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var json = JsonSerializer.Serialize(new AppSettings { Theme = theme });
            var back = JsonSerializer.Deserialize<AppSettings>(json);
            Assert.NotNull(back);
            Assert.Equal(theme, back!.Theme);
        }
    }

    [Fact]
    public void Settings_WriteTheThemeAsItsName()
    {
        var json = JsonSerializer.Serialize(new AppSettings { Theme = AppTheme.NordFrost });
        // A name, not the ordinal: the file stays readable and the picker's stable keys are the same strings.
        Assert.Contains("\"theme\":\"NordFrost\"", json.Replace(" ", ""));
    }

    [Theory]
    [InlineData("{\"theme\": 1}", AppTheme.Dark)]
    [InlineData("{\"theme\": 0}", AppTheme.Light)]
    [InlineData("{\"theme\": 2}", AppTheme.System)]
    [InlineData("{\"theme\": \"Dark\"}", AppTheme.Dark)]
    [InlineData("{\"theme\": \"MidnightOled\"}", AppTheme.MidnightOled)]
    [InlineData("{\"theme\": \"Nonsense\"}", AppTheme.System)]
    [InlineData("{\"theme\": 99}", AppTheme.System)]
    [InlineData("{\"theme\": null}", AppTheme.System)]
    public void Settings_ReadOldNumbersAndUnknownNames(string json, AppTheme expected)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(settings);
        Assert.Equal(expected, settings!.Theme);
    }

    [Fact]
    public void Normalize_SendsAnUnknownThemeToSystem()
    {
        var settings = new AppSettings { Theme = (AppTheme)99 };
        settings.Normalize();
        Assert.Equal(AppTheme.System, settings.Theme);
    }

    [Fact]
    public void Normalize_LeavesAKnownThemeAlone()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var settings = new AppSettings { Theme = theme };
            settings.Normalize();
            Assert.Equal(theme, settings.Theme);
        }
    }

    [Fact]
    public void SettingsFile_SurvivesTheDiskRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nitrolite_theme_{Guid.NewGuid():N}.json");
        try
        {
            SettingsManager.SaveSettingsAtomic(path, new AppSettings { Theme = AppTheme.AmberConsole });
            var loaded = SettingsManager.LoadSettings(path);
            Assert.Equal(SettingsLoadSource.Primary, loaded.Source);
            Assert.Equal(AppTheme.AmberConsole, loaded.Settings.Theme);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".bak"); } catch { }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
