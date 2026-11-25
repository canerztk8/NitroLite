using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The diagnostics archive and the compatibility link both go into public issues. settings.json is written with the
/// default encoder, which stores every non-ASCII letter as a <c>\uXXXX</c> escape, and the profile-path rule used to
/// stop at the escape's backslash: <c>Çağrı</c> survived whole and <c>Güneş</c> half.
/// </summary>
public class CleanupCRedactionTests
{
    private static string JsonWithPath(string path) => JsonSerializer.Serialize(new { install_path = path });

    private static string ValueOf(string json) => JsonNode.Parse(json)!["install_path"]!.GetValue<string>();

    [Theory]
    [InlineData("Çağrı")]
    [InlineData("Güneş")]
    [InlineData("Иван Петров")]
    [InlineData("John Smith")]
    public void Scrub_RemovesAJsonEscapedWindowsProfileName(string name)
    {
        var json = JsonWithPath($@"C:\Users\{name}\Games\Game\game.exe");

        var scrubbed = Redaction.Scrub(json);

        // Still valid JSON, and the decoded value is the path with the name gone.
        Assert.Equal(@"C:\Users\<user>\Games\Game\game.exe", ValueOf(scrubbed));
        Assert.DoesNotContain(name, ValueOf(scrubbed), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultEncoderReallyEscapesTheName_SoTheTestAboveIsNotVacuous()
    {
        var json = JsonWithPath(@"C:\Users\Çağrı\x");
        Assert.Contains(@"\u00C7a\u011Fr\u0131", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Çağrı")]
    [InlineData("Güneş")]
    [InlineData("Иван")]
    [InlineData("John Smith")]
    public void Scrub_RemovesAPlainWindowsProfileName(string name)
    {
        var scrubbed = Redaction.Scrub($@"Saved to C:\Users\{name}\AppData\Local\NitroLite\settings.json");

        Assert.Equal(@"Saved to C:\Users\<user>\AppData\Local\NitroLite\settings.json", scrubbed);
    }

    [Theory]
    [InlineData("/home/John Smith/.config/nitrolite/settings.json", "/home/<user>/.config/nitrolite/settings.json")]
    [InlineData("/home/çağrı/.config/x", "/home/<user>/.config/x")]
    [InlineData("/Users/John Smith/Library/x", "/Users/<user>/Library/x")]
    [InlineData("/Users/иван/Library/x", "/Users/<user>/Library/x")]
    [InlineData("/home/caner", "/home/<user>")]
    public void Scrub_RemovesUnixAndMacProfileNames_IncludingOnesWithSpaces(string text, string expected)
    {
        Assert.Equal(expected, Redaction.Scrub(text));
    }

    [Fact]
    public void Scrub_KeepsTheProseAfterABareUnixProfilePath()
    {
        Assert.Equal("read /home/<user> because it was there", Redaction.Scrub("read /home/caner because it was there"));
    }

    [Theory]
    [InlineData("/home/John Smith/x")]
    [InlineData("/Users/Çağrı/x")]
    public void Scrub_RemovesAJsonEscapedUnixProfileName(string path)
    {
        var scrubbed = Redaction.Scrub(JsonWithPath(path));

        Assert.StartsWith(path.StartsWith("/home/", StringComparison.Ordinal) ? "/home/<user>/" : "/Users/<user>/",
            ValueOf(scrubbed), StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubJson_ScrubsEveryStringValueAndName_AndStaysValidJson()
    {
        var json = JsonSerializer.Serialize(new
        {
            language = "tr",
            fan = 55,
            enabled = true,
            nothing = (string?)null,
            games = new[]
            {
                new { InstallPath = @"C:\Users\Çağrı\Games\a.exe" },
                new { InstallPath = @"D:\Games\b.exe" }
            },
            paths = new Dictionary<string, int>
            {
                [@"C:\Users\Güneş\x"] = 1,
                [@"C:\Users\Çağrı\x"] = 2
            }
        });

        var scrubbed = Redaction.ScrubJson(json);
        var root = JsonNode.Parse(scrubbed)!;

        Assert.DoesNotContain("Çağrı", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("Güneş", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u00", scrubbed, StringComparison.Ordinal);
        Assert.Equal("tr", root["language"]!.GetValue<string>());
        Assert.Equal(55, root["fan"]!.GetValue<int>());
        Assert.True(root["enabled"]!.GetValue<bool>());
        Assert.Null(root["nothing"]);
        Assert.Equal(@"C:\Users\<user>\Games\a.exe", root["games"]![0]!["InstallPath"]!.GetValue<string>());
        Assert.Equal(@"D:\Games\b.exe", root["games"]![1]!["InstallPath"]!.GetValue<string>());

        // Two names that scrub to the same text both survive.
        var paths = root["paths"]!.AsObject();
        Assert.Equal(2, paths.Count);
        Assert.True(paths.ContainsKey(@"C:\Users\<user>\x"));
        Assert.Contains(paths, p => p.Key.StartsWith(@"C:\Users\<user>\x (", StringComparison.Ordinal));
    }

    [Fact]
    public void ScrubJson_FallsBackToTheTextScrub_ForAFileThatIsNotJson()
    {
        var broken = "{ \"install_path\": \"C:\\\\Users\\\\\\u00C7a\\u011Fr\\u0131\\\\x\", ";

        var scrubbed = Redaction.ScrubJson(broken);

        Assert.DoesNotContain(@"\u00C7", scrubbed, StringComparison.Ordinal);
        Assert.Contains("<user>", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubJson_PassesNullAndEmptyThrough()
    {
        Assert.Equal("", Redaction.ScrubJson(null));
        Assert.Equal("", Redaction.ScrubJson(""));
    }

    [Fact]
    public void TheRealSettingsFile_WithATurkishProfileName_LeavesNoTraceAfterEitherScrub()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NitroLite-cleanupc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var settings = new AppSettings();
            settings.GameProfiles.Add(new GameProfile
            {
                ExeName = "game.exe",
                InstallPath = @"C:\Users\Çağrı Güneş\Games\game.exe"
            });
            SettingsManager.SaveSettingsAtomic(settingsPath, settings);
            var text = File.ReadAllText(settingsPath);

            foreach (var scrubbed in new[] { Redaction.Scrub(text), Redaction.ScrubJson(text) })
            {
                Assert.DoesNotContain(@"\u00C7a\u011Fr\u0131", scrubbed, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(@"G\u00FCne\u015F", scrubbed, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Çağrı", scrubbed, StringComparison.Ordinal);
                Assert.DoesNotContain("Güneş", scrubbed, StringComparison.Ordinal);
                Assert.NotNull(JsonNode.Parse(scrubbed));
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TheCompatibilityLink_CarriesNoProfileName_EvenWhenTheReportDoes()
    {
        const string report = "### NitroLite hardware report\n| Log | C:\\Users\\Çağrı\\AppData\\Local\\NitroLite\\x.log |\n"
                              + "| Home | /home/John Smith/.config/nitrolite |\n";

        var url = CompatibilityReportUrl.Build("AN16-61", "EV9_SKF", report);
        var body = Uri.UnescapeDataString(url[(url.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);

        Assert.DoesNotContain("Çağrı", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Smith", body, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\<user>\AppData", body, StringComparison.Ordinal);
        Assert.Equal(Redaction.Scrub(report), CompatibilityReportUrl.SanitizeReport(report));
    }

    [Fact]
    public void TheLinuxCaptureLink_ScrubsItsSummary()
    {
        var url = CompatibilityReportUrl.BuildLinuxCapture("AN16-61", null, "capture.txt", "home: /home/John Smith/x");

        Assert.DoesNotContain("Smith", Uri.UnescapeDataString(url), StringComparison.Ordinal);
    }
}
