using System.Text.RegularExpressions;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Every catalogue has to be a complete, translated copy of the English one, and the lookup has to reach every row.
/// </summary>
/// <remarks>
/// <para>
/// The existing catalogue tests check one language at a time. These check the set as a whole, because the failure this
/// guards against is not a missing key in one file — it is a row that was copied from English into all six catalogues
/// and never translated. That is exactly what had happened to thirty-four rows: the English value was half Turkish
/// ("Active RGB durumu could not be read") and the six translations had copied it verbatim, so the bug was invisible
/// to a per-language test that only compared key sets.
/// </para>
/// <para>
/// The Turkish-letter check is the one that catches it. Turkish has ğ, ı, ş and İ, and none of the seven other
/// languages does, so a value in any of them that contains one of those letters is a value that was never translated.
/// </para>
/// </remarks>
public class LocalizationCompletenessTests
{
    /// <summary>Every language that ships a catalogue. Turkish is the source and has none.</summary>
    private static readonly string[] Catalogues = { "en", "de", "es", "hi", "id", "pt", "ru" };

    /// <summary>Letters Turkish has and none of the seven catalogue languages does.</summary>
    private static readonly char[] TurkishOnlyLetters = { 'ğ', 'ı', 'ş', 'Ğ', 'Ş', 'İ' };

    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    private static Dictionary<string, string> Load(string code) =>
        new(Loc.LoadCatalog(code), StringComparer.Ordinal);

    [Fact]
    public void EveryCatalogueHasExactlyTheSameKeySet()
    {
        var english = Load("en");
        Assert.NotEmpty(english);

        foreach (var code in Catalogues)
        {
            var catalogue = Load(code);

            var missing = english.Keys.Where(k => !catalogue.ContainsKey(k)).ToList();
            var extra = catalogue.Keys.Where(k => !english.ContainsKey(k)).ToList();

            Assert.True(missing.Count == 0,
                $"{code} is missing {missing.Count} keys, first: {string.Join(" / ", missing.Take(5))}");
            Assert.True(extra.Count == 0,
                $"{code} has {extra.Count} keys English does not, first: {string.Join(" / ", extra.Take(5))}");
            Assert.Equal(english.Count, catalogue.Count);
        }
    }

    [Fact]
    public void NoCatalogueLeavesTurkishTextInATranslation()
    {
        var offenders = new List<string>();

        foreach (var code in Catalogues)
        {
            foreach (var (key, value) in Load(code))
            {
                if (string.IsNullOrEmpty(value)) continue;
                if (value.IndexOfAny(TurkishOnlyLetters) >= 0) offenders.Add($"{code}: {key} -> {value}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} rows still carry Turkish text, first: {string.Join(" / ", offenders.Take(5))}");
    }

    [Fact]
    public void EveryCatalogueHasAValueForEveryKeyAndKeepsItsPlaceholders()
    {
        var english = Load("en");
        var problems = new List<string>();

        foreach (var code in Catalogues)
        {
            foreach (var (key, value) in Load(code))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    problems.Add($"{code}: empty value for {key}");
                    continue;
                }

                if (!english.TryGetValue(key, out var source)) continue;

                var expected = Placeholders(source);
                var actual = Placeholders(value);
                if (expected != actual) problems.Add($"{code}: {key} -> {value} (placeholders {expected} vs {actual})");
            }
        }

        Assert.True(problems.Count == 0,
            $"{problems.Count} rows are empty or lost a placeholder, first: {string.Join(" / ", problems.Take(5))}");
    }

    [Fact]
    public void EveryKeyIsReachableThroughTheLookupInEveryLanguage()
    {
        var english = Load("en");
        var original = Loc.Language;

        try
        {
            foreach (var code in Catalogues)
            {
                Loc.SetLanguage(code);
                Assert.Equal(code, Loc.Language);

                var catalogue = Load(code);
                foreach (var (key, expected) in catalogue)
                {
                    // The lookup has to answer with the catalogue's own value, not the source text and not the
                    // English fallback: a row that is present but unreachable is the same bug as a missing row.
                    Assert.Equal(expected, Loc.T(key));
                    Assert.True(Loc.Has(key), $"{code} reports no entry for {key}");
                }

                Assert.Equal(english.Count, catalogue.Count);
            }
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void SourceLanguageReturnsTheKeyItselfForEveryRow()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage(Loc.SourceLanguage);

            foreach (var key in Load("en").Keys)
            {
                // Turkish is the source: the key is the text, so the lookup is the identity. A row that came back
                // changed here would mean a tr catalogue had appeared without the tests being told about it.
                Assert.Equal(key, Loc.T(key));
            }
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void LoadCatalog_AnswersTheSameInstanceEveryTime()
    {
        // The catalogues are embedded resources that cannot change while the process runs. SetLanguage used to
        // re-read and re-parse the English one on every call; the cache is what makes that a dictionary lookup.
        foreach (var code in Catalogues)
        {
            Assert.Same(Loc.LoadCatalog(code), Loc.LoadCatalog(code));
        }
    }

    [Fact]
    public void SetLanguage_DoesNotReallocateTheCatalogue()
    {
        // Warm up: the first call is the one that reads the embedded resource.
        Loc.SetLanguage("de");
        Loc.SetLanguage("en");

        const int Switches = 200;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Switches; i++)
        {
            Loc.SetLanguage(i % 2 == 0 ? "de" : "en");
        }

        var perSwitch = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Switches;

        // What is left is the language record lookup and the dictionary assignment. Re-reading the English catalogue
        // on every call cost tens of kilobytes and a JSON parse; the budget is loose enough not to fail on a runtime
        // detail and tight enough to catch a return to that.
        Assert.True(perSwitch < 512, $"SetLanguage allocated {perSwitch:F0} bytes per switch");

        Loc.SetLanguage(Loc.SourceLanguage);
    }

    private static string Placeholders(string value) =>
        string.Join(",", Placeholder.Matches(value).Select(m => m.Groups[1].Value).OrderBy(v => v, StringComparer.Ordinal));
}
