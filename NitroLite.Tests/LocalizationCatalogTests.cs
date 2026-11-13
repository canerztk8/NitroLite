using System.Globalization;
using System.Text.RegularExpressions;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Keeps the eight catalogues honest. English is the reference: every other language must carry every English
/// key, with the same placeholders, and no empty value. A gap here would show up as a half-translated window.
/// </summary>
public class LocalizationCatalogTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:,(-?\d+))?(?::([^}]*))?\}", RegexOptions.Compiled);

    private static readonly string[] TranslatedLanguages = { "de", "es", "ru", "pt", "hi", "id" };

    private static IReadOnlyDictionary<string, string> English => Loc.LoadCatalog("en");

    private static string Placeholders(string text) =>
        string.Join("|", Placeholder.Matches(text)
            .Select(m => $"{m.Groups[1].Value},{m.Groups[2].Value},{m.Groups[3].Value}")
            .OrderBy(s => s, StringComparer.Ordinal));

    [Fact]
    public void EnglishCatalogueIsEmbeddedAndNotEmpty()
    {
        Assert.True(English.Count > 500, $"English catalogue has only {English.Count} entries");
    }

    [Fact]
    public void EverySupportedLanguageIsListedOnce()
    {
        Assert.Equal(8, Loc.Languages.Count);
        Assert.Equal(Loc.Languages.Count, Loc.Languages.Select(l => l.Code).Distinct().Count());
        Assert.Contains(Loc.Languages, l => l.Code == Loc.SourceLanguage);
        Assert.Contains(Loc.Languages, l => l.Code == Loc.DefaultLanguage);
        foreach (var language in Loc.Languages)
        {
            Assert.False(string.IsNullOrWhiteSpace(language.NativeName));
            Assert.False(string.IsNullOrWhiteSpace(language.Culture));
            Assert.True(Loc.IsSupported(language.Code));
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    [InlineData("pt")]
    [InlineData("hi")]
    [InlineData("id")]
    public void CatalogueCarriesEveryEnglishKey(string code)
    {
        var catalogue = Loc.LoadCatalog(code);
        Assert.NotEmpty(catalogue);

        var missing = English.Keys.Where(k => !catalogue.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0,
            $"{code} is missing {missing.Count} keys, first: {string.Join(" / ", missing.Take(5))}");

        var extra = catalogue.Keys.Where(k => !English.ContainsKey(k)).ToList();
        Assert.True(extra.Count == 0,
            $"{code} has {extra.Count} keys English does not, first: {string.Join(" / ", extra.Take(5))}");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    [InlineData("pt")]
    [InlineData("hi")]
    [InlineData("id")]
    public void CatalogueHasNoEmptyValue(string code)
    {
        var empty = Loc.LoadCatalog(code).Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ToList();
        Assert.True(empty.Count == 0,
            $"{code} has {empty.Count} empty values, first: {string.Join(" / ", empty.Take(5))}");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    [InlineData("pt")]
    [InlineData("hi")]
    [InlineData("id")]
    public void CatalogueKeepsEveryPlaceholder(string code)
    {
        var broken = new List<string>();
        foreach (var (key, value) in Loc.LoadCatalog(code))
        {
            if (!English.TryGetValue(key, out var english)) continue;
            if (Placeholders(english) != Placeholders(value)) broken.Add($"{key} -> {value}");
        }

        Assert.True(broken.Count == 0,
            $"{code} has {broken.Count} placeholder mismatches, first: {string.Join(" / ", broken.Take(5))}");
    }

    [Fact]
    public void EveryTranslatedValueActuallyDiffersFromEnglishSomewhere()
    {
        // A catalogue that is a copy of the English file would pass every other test while translating nothing.
        foreach (var code in TranslatedLanguages)
        {
            var catalogue = Loc.LoadCatalog(code);
            var identical = catalogue.Count(p => English.TryGetValue(p.Key, out var en) && en == p.Value);
            Assert.True(identical < catalogue.Count / 4,
                $"{code} copies English for {identical} of {catalogue.Count} entries");
        }
    }

    [Fact]
    public void SetLanguageSwitchesTheTextAndRestoresTheSource()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage("de");
            Assert.Equal("de", Loc.Language);
            Assert.Equal("Aus", Loc.T("Kapalı"));
            Assert.True(Loc.Has("Kapalı"));

            Loc.SetLanguage(Loc.SourceLanguage);
            Assert.Equal("Kapalı", Loc.T("Kapalı"));
            Assert.True(Loc.Has("anything at all"));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void UnknownLanguageFallsBackToEnglish()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage("xx");
            Assert.Equal(Loc.DefaultLanguage, Loc.Language);
            Assert.Equal("Off", Loc.T("Kapalı"));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void InterpolatedTextUsesTheTranslatedTemplate()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage("en");
            // Real call sites: the percent sign moves in English, and the format specifier survives the lookup.
            Assert.Equal("NitroLite: Acer service not responding | CPU: 55%",
                Loc.T($"NitroLite: Acer servisi yanıt vermiyor | CPU: %{55.0:F0}"));
            Assert.Equal("Downloading update… 42%", Loc.T($"Güncelleme indiriliyor… %{42.0:F0}"));
            Assert.Equal("Custom fan speed: CPU 55%, GPU 60%.",
                Loc.T($"Özel fan hızı: CPU %{55}, GPU %{60}."));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void InterpolatedTextNeverThrowsOnABrokenTemplate()
    {
        // A template that cannot be formatted must never take the message away: the raw template comes back.
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal("50% {0} {1}", Loc.Format("50% {0} {1}", new object?[] { 1 }));
            Assert.Equal("", Loc.T(""));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void MissingKeyFallsBackToEnglishThenToTheSource()
    {
        var original = Loc.Language;
        try
        {
            // The source language has no catalogue of its own, so every lookup goes through English.
            Loc.SetLanguage(Loc.SourceLanguage);
            Assert.Equal("Off", Loc.Lookup("Kapalı"));
            Assert.Equal("a text no catalogue has", Loc.Lookup("a text no catalogue has"));

            // A complete catalogue answers from itself.
            Loc.SetLanguage("de");
            Assert.Equal("Aus", Loc.Lookup("Kapalı"));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void TryTReturnsNullInTheSourceLanguageAndATranslationElsewhere()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage(Loc.SourceLanguage);
            Assert.Null(Loc.TryT("Kapalı"));

            Loc.SetLanguage("en");
            Assert.Equal("Off", Loc.TryT("Kapalı"));
            Assert.Null(Loc.TryT("a text no catalogue has"));
            Assert.Null(Loc.TryT(null));
            Assert.Null(Loc.TryT(""));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void PsDoublesApostrophesSoThePowerShellStringSurvives()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal("the file''s path", Loc.Ps("the file's path"));
            Assert.Equal("no apostrophe", Loc.Ps("no apostrophe"));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Theory]
    [InlineData("tr-TR", "tr")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "de")]
    [InlineData("es-ES", "es")]
    [InlineData("ru-RU", "ru")]
    [InlineData("pt-BR", "pt")]
    [InlineData("hi-IN", "hi")]
    [InlineData("id-ID", "id")]
    [InlineData("ja-JP", "en")]
    [InlineData("fr-FR", "en")]
    public void DetectSystemLanguagePicksASupportedLanguageOrEnglish(string culture, string expected)
    {
        Assert.Equal(expected, Loc.DetectSystemLanguage(new CultureInfo(culture)));
    }

    [Fact]
    public void IsSupportedRejectsEmptyAndUnknownCodes()
    {
        Assert.False(Loc.IsSupported(null));
        Assert.False(Loc.IsSupported(""));
        Assert.False(Loc.IsSupported("   "));
        Assert.False(Loc.IsSupported("zz"));
        Assert.True(Loc.IsSupported("EN"));
    }

    [Fact]
    public void EveryCatalogueEntryIsReachableThroughTheLookup()
    {
        // Guards against a catalogue whose keys were normalised (trimmed, lower-cased) on the way in.
        var original = Loc.Language;
        try
        {
            foreach (var code in TranslatedLanguages)
            {
                Loc.SetLanguage(code);
                var catalogue = Loc.LoadCatalog(code);
                var unreachable = catalogue.Keys.Where(k => Loc.Lookup(k) != catalogue[k]).ToList();
                Assert.True(unreachable.Count == 0,
                    $"{code} has {unreachable.Count} unreachable keys, first: {string.Join(" / ", unreachable.Take(5))}");
            }
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }
}
