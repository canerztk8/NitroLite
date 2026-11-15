using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Guards the one gap the catalogue tests cannot see: a Turkish key that the code asks for but no catalogue carries.
/// Every other test compares the catalogues against each other, so a key that was never added to any of them passed
/// silently and the string stayed Turkish in all seven languages.
/// </summary>
public class LocalizationSourceCoverageTests
{
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

    private static HashSet<string> EnglishKeys()
    {
        var path = Path.Combine(RepoRoot(), "NitroLite", "Lang", "en.json");
        var json = File.ReadAllText(path, Encoding.UTF8);
        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        Assert.NotNull(map);
        return new HashSet<string>(map!.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every <c>Loc.T("…")</c>, <c>Loc.Ps("…")</c> and <c>Loc.TryT("…")</c> call whose argument is a literal, plus the
    /// interpolated form, whose template is the literal text with each hole replaced by <c>{n}</c>.
    /// </summary>
    private static IEnumerable<(string Key, string Where)> LiteralKeys(string source, string file)
    {
        var call = new Regex(@"Loc\.(?:T|Ps|TryT)\(\s*(?<arg>\$?""(?:[^""\\]|\\.)*"")", RegexOptions.Compiled);
        foreach (Match m in call.Matches(source))
        {
            var arg = m.Groups["arg"].Value;
            var line = source.Take(m.Index).Count(c => c == '\n') + 1;
            var where = $"{file}:{line}";

            if (!arg.StartsWith("$", StringComparison.Ordinal))
            {
                yield return (Unescape(arg[1..^1]), where);
                continue;
            }

            var template = InterpolatedTemplate(arg[2..^1]);
            if (template != null) yield return (template, where);
        }
    }

    /// <summary>
    /// Turns the body of an interpolated string into the format template the compiler would build. Returns null when
    /// the body contains a construct this simple scanner cannot follow, so the test never reports a false positive.
    /// </summary>
    private static string? InterpolatedTemplate(string body)
    {
        var sb = new StringBuilder();
        int index = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '{')
            {
                if (i + 1 < body.Length && body[i + 1] == '{')
                {
                    sb.Append('{');
                    i++;
                    continue;
                }

                int depth = 1;
                int start = i + 1;
                int j = start;
                for (; j < body.Length && depth > 0; j++)
                {
                    if (body[j] == '{') depth++;
                    else if (body[j] == '}') depth--;
                }
                if (depth != 0) return null;

                var hole = body[start..(j - 1)];
                // A hole that already carries a format specifier keeps it: "CPU {value:F0}" is "{0:F0}".
                int colon = hole.IndexOf(':');
                sb.Append('{').Append(index++);
                if (colon >= 0) sb.Append(hole[colon..]);
                sb.Append('}');
                i = j - 1;
                continue;
            }

            if (c == '}' && i + 1 < body.Length && body[i + 1] == '}')
            {
                sb.Append('}');
                i++;
                continue;
            }

            if (c == '\\')
            {
                if (i + 1 >= body.Length) return null;
                sb.Append(Unescape(body.Substring(i, 2)));
                i++;
                continue;
            }

            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Unescape(string text) => text
        .Replace("\\\"", "\"")
        .Replace("\\\\", "\\")
        .Replace("\\n", "\n")
        .Replace("\\r", "\r")
        .Replace("\\t", "\t");

    [Fact]
    public void EveryLiteralKeyUsedInCodeExistsInTheEnglishCatalogue()
    {
        var keys = EnglishKeys();
        var root = Path.Combine(RepoRoot(), "NitroLite");
        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            foreach (var (key, where) in LiteralKeys(source, Path.GetFileName(file)))
            {
                if (key.Length == 0) continue;
                if (!keys.Contains(key)) missing.Add($"{where}  \"{key}\"");
            }
        }

        Assert.True(missing.Count == 0,
            "These keys are used in code but no catalogue carries them, so they stay Turkish in every language:\n"
            + string.Join("\n", missing.Distinct().OrderBy(m => m, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The XAML keeps the Turkish source text, which is also the catalogue key, so a string edited in the markup
    /// stops being translated without anything failing: the window still opens, in Turkish, in all seven languages.
    /// The code-side scan above cannot see this, because the string never passes through <c>Loc.T</c>.
    /// </summary>
    [Fact]
    public void EveryTranslatableXamlStringExistsInTheEnglishCatalogue()
    {
        var keys = EnglishKeys();
        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "NitroLite"), "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var (value, where) in XamlStrings(File.ReadAllText(file), Path.GetFileName(file)))
            {
                if (!keys.Contains(value)) missing.Add($"{where}  \"{value}\"");
            }
        }

        Assert.True(missing.Count == 0,
            "These XAML strings are not catalogue keys, so they stay Turkish in every language:\n"
            + string.Join("\n", missing.Distinct().OrderBy(m => m, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The properties <see cref="NitroLite.Core.LocXaml"/> rewrites, minus the values that are not prose: a markup
    /// extension, a glyph entity, and anything without a run of two letters (<c>%80</c>, <c>60°</c>, <c>v1.2</c>).
    /// </summary>
    private static IEnumerable<(string Value, string Where)> XamlStrings(string source, string file)
    {
        var attribute = new Regex("\\b(?:Text|Content|Header|ToolTip|Title)=\"(?<value>[^\"]*)\"", RegexOptions.Compiled);
        var run = new Regex("<Run\\b[^>]*\\bText=\"(?<value>[^\"]*)\"", RegexOptions.Compiled);

        foreach (var regex in new[] { attribute, run })
        {
            foreach (Match match in regex.Matches(source))
            {
                var raw = match.Groups["value"].Value;
                if (raw.Contains("&#x", StringComparison.Ordinal)) continue;
                if (raw.StartsWith('{')) continue;

                var value = System.Net.WebUtility.HtmlDecode(raw);
                if (!Regex.IsMatch(value, "[A-Za-zÇĞİÖŞÜçğıöşü]{2}")) continue;

                var line = source.Take(match.Index).Count(c => c == '\n') + 1;
                yield return (value, $"{file}:{line}");
            }
        }
    }

    [Fact]
    public void TheScannerUnderstandsBothLiteralForms()
    {
        // The scanner is the only thing standing between a new string and a silent Turkish leak, so its own
        // behaviour is pinned: a plain literal, an interpolated template with a format specifier, and an escaped
        // brace.
        var source = "Loc.T(\"Düz metin\"); Loc.T($\"Sıcaklık {value:F0} °C\"); Loc.T($\"Süslü {{parantez}}\");";
        var keys = LiteralKeys(source, "sample.cs").Select(k => k.Key).ToList();

        Assert.Contains("Düz metin", keys);
        Assert.Contains("Sıcaklık {0:F0} °C", keys);
        Assert.Contains("Süslü {parantez}", keys);
    }
}
