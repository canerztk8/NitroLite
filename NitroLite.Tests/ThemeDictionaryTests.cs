using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Guards the palette files themselves: every theme carries the same keys, and every theme is legible.
/// </summary>
/// <remarks>
/// A missing key is not a compile error — it is a <c>ResourceReferenceKeyNotFoundException</c> at the moment the
/// window loads, on the one machine that picked that palette. The key-set test is what turns that into a red build.
/// The contrast test is the same idea for the other silent failure: a palette that loads but whose 1px rules
/// disappear into the page, or whose body text is too faint to read.
/// </remarks>
public class ThemeDictionaryTests
{
    // =============================================================================================================
    // Reading the dictionaries.
    // =============================================================================================================

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

    private static string ThemePath(AppTheme theme) =>
        Path.Combine(RepoRoot(), "NitroLite", ThemeCatalog.DictionaryPath(theme).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Every <c>SolidColorBrush</c> in a dictionary, key to colour.</summary>
    private static Dictionary<string, string> Brushes(AppTheme theme)
    {
        var path = ThemePath(theme);
        Assert.True(File.Exists(path), $"Missing theme dictionary: {path}");
        var text = File.ReadAllText(path, Encoding.UTF8);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, @"<SolidColorBrush\s+x:Key=""(?<key>[^""]+)""\s+Color=""(?<color>#[0-9A-Fa-f]{6,8})"""))
        {
            map[m.Groups["key"].Value] = m.Groups["color"].Value;
        }
        return map;
    }

    /// <summary>The concrete palettes: everything except the "follow Windows" preference.</summary>
    private static IEnumerable<AppTheme> ConcreteThemes =>
        ThemeCatalog.All.Where(t => t != AppTheme.System);

    // =============================================================================================================
    // 1. Key parity.
    // =============================================================================================================

    [Fact]
    public void EveryThemeCarriesExactlyTheSameKeys()
    {
        var reference = Brushes(AppTheme.Dark);
        Assert.NotEmpty(reference);

        foreach (var theme in ConcreteThemes)
        {
            var actual = Brushes(theme);
            var missing = reference.Keys.Except(actual.Keys, StringComparer.Ordinal).OrderBy(k => k).ToList();
            var extra = actual.Keys.Except(reference.Keys, StringComparer.Ordinal).OrderBy(k => k).ToList();

            Assert.True(missing.Count == 0, $"{theme} is missing: {string.Join(", ", missing)}");
            Assert.True(extra.Count == 0, $"{theme} has keys Dark does not: {string.Join(", ", extra)}");
        }
    }

    [Fact]
    public void EveryThemeDefinesTheKeysTheWindowAsksFor()
    {
        // The handful the code-behind resolves by name at runtime; a typo here is a crash, not a compile error.
        var required = new[]
        {
            "Paper", "PaperDeep", "CardBg", "CardBgHover", "PanelBg", "RailBg",
            "Ink", "InkSoft", "InkFaint", "Rule", "RuleStrong", "BorderDark", "BorderStrong",
            "Vermilion", "Live", "AccentRed", "AccentCyan", "AccentGreen", "AccentPurple",
            "TextPrimary", "TextSecondary", "TextMuted", "OnAccent", "WarnBg",
            "Slate", "Olive", "Link", "ChartFill", "BgDark"
        };

        foreach (var theme in ConcreteThemes)
        {
            var brushes = Brushes(theme);
            foreach (var key in required)
            {
                Assert.True(brushes.ContainsKey(key), $"{theme} does not define {key}");
            }
        }
    }

    [Fact]
    public void EveryColourIsAWellFormedHexValue()
    {
        foreach (var theme in ConcreteThemes)
        {
            foreach (var (key, colour) in Brushes(theme))
            {
                Assert.Matches("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", colour);
                Assert.True(colour.Length == 7 || colour.Length == 9, $"{theme}.{key} = {colour}");
            }
        }
    }

    // =============================================================================================================
    // 2. Contrast.
    // =============================================================================================================

    /// <summary>WCAG 2.1 relative luminance of an <c>#RRGGBB</c> colour.</summary>
    private static double Luminance(string hex)
    {
        var h = hex.TrimStart('#');
        double Channel(int offset)
        {
            double c = Convert.ToInt32(h.Substring(offset, 2), 16) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    /// <summary>WCAG 2.1 contrast ratio between two <c>#RRGGBB</c> colours.</summary>
    private static double Contrast(string a, string b)
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
            var b = Brushes(theme);
            double ratio = Contrast(b["Ink"], b["CardBg"]);
            Assert.True(ratio >= 4.5, $"{theme}: Ink on CardBg is {ratio:F2}:1, below the 4.5:1 AA floor");
        }
    }

    [Fact]
    public void CardRulesStayVisibleAgainstThePage()
    {
        foreach (var theme in ConcreteThemes)
        {
            var b = Brushes(theme);
            double ratio = Contrast(b["BorderDark"], b["Paper"]);
            Assert.True(ratio >= 1.45, $"{theme}: BorderDark on Paper is {ratio:F2}:1, below the 1.45:1 floor");
        }
    }

    [Fact]
    public void InfoBadgeAndActiveModeFrameStayVisible()
    {
        foreach (var theme in ConcreteThemes)
        {
            var b = Brushes(theme);

            // The (?) badge is a 1px RuleStrong ring with InkFaint type on a card.
            double ring = Contrast(b["RuleStrong"], b["CardBg"]);
            Assert.True(ring >= 1.45, $"{theme}: RuleStrong on CardBg is {ring:F2}:1, the (?) ring would vanish");

            double glyph = Contrast(b["InkFaint"], b["CardBg"]);
            Assert.True(glyph >= 3.0, $"{theme}: InkFaint on CardBg is {glyph:F2}:1, the (?) glyph would be unreadable");

            // The active mode card is marked by a Vermilion frame drawn over the card's Paper ground.
            double frame = Contrast(b["Vermilion"], b["Paper"]);
            Assert.True(frame >= 3.0, $"{theme}: Vermilion on Paper is {frame:F2}:1, the active mode frame would blur");
        }
    }

    [Fact]
    public void StrongRulesAreStrongerThanWeakOnes()
    {
        foreach (var theme in ConcreteThemes)
        {
            var b = Brushes(theme);
            // RuleStrong is the emphasis rule; if it is not further from the page than Rule, the hierarchy is flat.
            Assert.True(Contrast(b["RuleStrong"], b["Paper"]) > Contrast(b["Rule"], b["Paper"]),
                $"{theme}: RuleStrong is not stronger than Rule");
            Assert.True(Contrast(b["BorderStrong"], b["Paper"]) > Contrast(b["BorderDark"], b["Paper"]),
                $"{theme}: BorderStrong is not stronger than BorderDark");
        }
    }

    [Fact]
    public void CardToneSeparatesFromThePage()
    {
        foreach (var theme in ConcreteThemes)
        {
            var b = Brushes(theme);
            // A card that is the same colour as the page is a card you cannot see, whatever its border does.
            Assert.NotEqual(b["CardBg"], b["Paper"]);
            Assert.NotEqual(b["CardBgHover"], b["CardBg"]);
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
        // System is a preference, not a palette: what it resolves to is ThemeManager's answer.
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
    public void EveryConcretePaletteHasItsOwnDictionary()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in ConcreteThemes)
        {
            var path = ThemeCatalog.DictionaryPath(theme);
            Assert.True(paths.Add(path), $"{theme} shares a dictionary with another palette: {path}");
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "NitroLite", path.Replace('/', Path.DirectorySeparatorChar))),
                $"{theme} points at a file that does not exist: {path}");
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
}
