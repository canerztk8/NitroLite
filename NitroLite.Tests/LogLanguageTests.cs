using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// A bug report arrived with an English title and a Turkish detail under it: the title went through the catalogue, so
/// it followed the window, and the detail was a literal written in Turkish. A log line is read by whoever triages the
/// report rather than by the user, so it is English at the call site; these tests hold that line.
/// </summary>
public class LogLanguageTests
{
    // ------------------------------------------------------------------ Loc.English

    [Fact]
    public void EnglishAnswersWithTheEnglishRowWhateverTheWindowShows()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage("tr");
            Assert.Equal("Nitro key detected", Loc.English("Nitro tuşu algılandı"));

            Loc.SetLanguage("de");
            Assert.Equal("Nitro key detected", Loc.English("Nitro tuşu algılandı"));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void EnglishTakesAnAlreadyTranslatedLabelBackToItsKey()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage("de");
            Assert.Equal("Aus", Loc.T("Kapalı"));
            Assert.Equal("Off", Loc.English(Loc.T("Kapalı")));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void EnglishLeavesTextItDoesNotKnowAlone()
    {
        Assert.Equal(@"C:\WinTools\NitroLite.exe", Loc.English(@"C:\WinTools\NitroLite.exe"));
        Assert.Equal("", Loc.English(""));
        Assert.Equal("", Loc.English((string?)null));
    }

    [Fact]
    public void EnglishWorksBeforeTheLanguageIsSet()
    {
        // The app logs while it starts, before the stored language is read, so the answer must not depend on that order.
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage(Loc.SourceLanguage);
            Assert.Equal("Off", Loc.English("Kapalı"));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void EnglishFormatsNumbersWithADotWhateverTheCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("About 55.5% per year", Loc.English($"Yılda yaklaşık %{55.5:F1}"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // ------------------------------------------------------------------ the source scan

    private static readonly string[] Projects =
    {
        "NitroLite.Core", "NitroLite.Desktop", "NitroLite.Windows", "NitroLite.Linux"
    };

    private static readonly Regex LogCall = new(@"AppLog\.(?:Info|Warn|Error)\s*\(", RegexOptions.Compiled);
    private static readonly Regex Localized = new(@"Loc\.(?:T|TInv|Ps)\s*\(", RegexOptions.Compiled);
    private static readonly Regex EnglishCall = new(@"Loc\.English\s*\(", RegexOptions.Compiled);

    /// <summary>The letters only Turkish uses, so a German or Portuguese translation cannot trip the scan.</summary>
    private static readonly SearchValues<char> TurkishLetters = SearchValues.Create("çğıöşüÇĞİÖŞÜ");

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

    private static IEnumerable<string> Sources()
    {
        foreach (var project in Projects)
        {
            var root = Path.Combine(RepoRoot(), project);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var separator = Path.DirectorySeparatorChar;
                if (file.Contains($"{separator}obj{separator}") || file.Contains($"{separator}bin{separator}")) continue;
                yield return file;
            }
        }
    }

    /// <summary>Skips a regular string literal that starts at <paramref name="i"/>; returns the index after it.</summary>
    private static int SkipString(string s, int i)
    {
        var n = s.Length;
        i++;
        while (i < n)
        {
            var c = s[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '"') return i + 1;
            i++;
        }

        return n;
    }

    /// <summary>
    /// Skips an interpolated string literal, holes and all: a <c>"</c> inside a hole opens another literal, and a
    /// <c>}</c> inside one closes nothing.
    /// </summary>
    private static int SkipInterpolated(string s, int i)
    {
        var n = s.Length;
        var verbatim = false;
        var j = i - 1;
        while (j >= 0 && (s[j] == '$' || s[j] == '@'))
        {
            if (s[j] == '@') verbatim = true;
            j--;
        }

        i++;
        var depth = 0;
        while (i < n)
        {
            var c = s[i];
            if (depth == 0)
            {
                if (!verbatim && c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    if (verbatim && i + 1 < n && s[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                if (c == '{')
                {
                    if (i + 1 < n && s[i + 1] == '{')
                    {
                        i += 2;
                        continue;
                    }

                    depth = 1;
                    i++;
                    continue;
                }

                i++;
                continue;
            }

            if (c == '"')
            {
                var k = i - 1;
                i = k >= 0 && (s[k] == '$' || s[k] == '@') ? SkipInterpolated(s, i) : SkipString(s, i);
                continue;
            }

            if (c == '{')
            {
                depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                i++;
                continue;
            }

            i++;
        }

        return n;
    }

    /// <summary>The index just past the <c>)</c> that closes the call opened at <paramref name="openParen"/>.</summary>
    private static int FindCallEnd(string s, int openParen)
    {
        var n = s.Length;
        var depth = 0;
        var i = openParen;
        while (i < n)
        {
            var c = s[i];
            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                var j = s.IndexOf('\n', i);
                i = j < 0 ? n : j;
                continue;
            }

            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                var j = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = j < 0 ? n : j + 2;
                continue;
            }

            if (c == '\'')
            {
                var j = i + 1;
                while (j < n)
                {
                    if (s[j] == '\\')
                    {
                        j += 2;
                        continue;
                    }

                    if (s[j] == '\'')
                    {
                        j++;
                        break;
                    }

                    j++;
                }

                i = j;
                continue;
            }

            if (c == '"')
            {
                var k = i - 1;
                var interpolated = false;
                while (k >= 0 && (s[k] == '$' || s[k] == '@'))
                {
                    if (s[k] == '$') interpolated = true;
                    k--;
                }

                i = interpolated ? SkipInterpolated(s, i) : SkipString(s, i);
                continue;
            }

            if (c == '(')
            {
                depth++;
                i++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                i++;
                if (depth == 0) return i;
                continue;
            }

            i++;
        }

        return n;
    }

    /// <summary>True when the call carries a Turkish letter outside the <c>Loc.English(…)</c> arguments.</summary>
    private static bool HasTurkishOutsideEnglish(string span)
    {
        var stripped = new StringBuilder(span);
        foreach (var m in EnglishCall.Matches(span).Cast<Match>().Reverse())
        {
            var open = span.IndexOf('(', m.Index + m.Length - 1);
            var end = FindCallEnd(span, open);
            stripped.Remove(m.Index, end - m.Index);
        }

        return stripped.ToString().AsSpan().IndexOfAny(TurkishLetters) >= 0;
    }

    private static string Flatten(string span)
    {
        var flat = Regex.Replace(span, @"\s+", " ").Trim();
        return flat.Length <= 160 ? flat : flat[..157] + "…";
    }

    /// <summary>
    /// Every <c>AppLog</c> call is English at the call site. A title that goes through <c>Loc.T</c> follows the window,
    /// which is how a Turkish user's report came back with an English title over a Turkish detail; a Turkish literal
    /// does the same in reverse. A label that is already a catalogue key goes through <c>Loc.English</c> instead.
    /// </summary>
    [Fact]
    public void NoLogCallIsLocalized()
    {
        var problems = new List<string>();

        foreach (var file in Sources())
        {
            var source = File.ReadAllText(file, Encoding.UTF8);
            foreach (Match call in LogCall.Matches(source))
            {
                var open = source.IndexOf('(', call.Index + call.Length - 1);
                var end = FindCallEnd(source, open);
                var span = source.Substring(call.Index, end - call.Index);
                var line = 1 + source.Take(call.Index).Count(c => c == '\n');
                var where = $"{Path.GetFileName(file)}:{line}";

                if (Localized.IsMatch(span))
                {
                    problems.Add($"{where}  localized: {Flatten(span)}");
                    continue;
                }

                if (HasTurkishOutsideEnglish(span))
                {
                    problems.Add($"{where}  Turkish: {Flatten(span)}");
                }
            }
        }

        Assert.True(problems.Count == 0,
            "The log is English, so these calls have to be too — write the text in English, and send a label that is " +
            "already a catalogue key through Loc.English:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems));
    }
}
