using System.IO;
using System.Text;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests.Scenarios;

/// <summary>
/// Category 7: hostile input. Every parser the app exposes to something it does not control — the command line,
/// a settings file, a backup, a release payload — fed a deterministic corpus of malformed values.
/// </summary>
/// <remarks>
/// The rule for every test here is the same: the parser may reject the input, but it must never throw. A throw
/// from a settings file happens before the window exists, so there is no toast and no log the user can reach;
/// the process simply does not start. A throw from the command line happens in a console window that closes
/// before anyone can read it.
/// </remarks>
public class HostileInputScenarios : IDisposable
{
    private readonly string _root;

    public HostileInputScenarios()
    {
        _root = Path.Combine(Path.GetTempPath(), "NitroLiteFuzz_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }

        // Nothing here owns a finalizer; the suppression is the shape the analyzer requires.
        GC.SuppressFinalize(this);
    }

    private string Path_(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    /// <summary>
    /// A deterministic corpus. Fixed seed so a failure is reproducible, and shaped like the things that actually
    /// arrive: a truncated write, a file from another program, a value someone typed.
    /// </summary>
    private static IEnumerable<string> NastyStrings()
    {
        yield return "";
        yield return " ";
        yield return "\0";
        yield return "\0\0\0";
        yield return "\t\r\n";
        yield return new string('x', 100_000);
        yield return new string('\0', 4096);
        yield return "İstanbul";
        yield return "ıIİi";
        yield return "ß";
        yield return "🎮🔥";
        yield return "\uD800";              // lone high surrogate
        yield return "\uDC00";              // lone low surrogate
        yield return "{\"a\":";
        yield return "[]";
        yield return "null";
        yield return "true";
        yield return "-1";
        yield return "999999999999999999999999";
        yield return "NaN";
        yield return "Infinity";
        yield return "-Infinity";
        yield return "1e999";
        yield return "../../etc/passwd";
        yield return @"..\..\Windows\System32\evil.dll";
        yield return "CON";
        yield return "\\\\?\\C:\\";
        yield return "%PATH%";
        yield return "$(whoami)";
        yield return "'; DROP TABLE--";
        yield return "--mode";
        yield return "--fan";
        yield return "--profile";

        var random = new Random(20261114);
        const string alphabet = "{}[]\",:0123456789.eE+-abcXYZ \t\n\\/%$'`\0\u00e7\u011f\u0131\u015f";
        for (int i = 0; i < 200; i++)
        {
            int length = random.Next(0, 64);
            var builder = new StringBuilder(length);
            for (int j = 0; j < length; j++) builder.Append(alphabet[random.Next(alphabet.Length)]);
            yield return builder.ToString();
        }
    }

    // ---------------------------------------------------------------- command line

    [Fact]
    public void CommandLine_NeverThrowsOnAnyArgument()
    {
        // A shortcut with a typo, a script that interpolated the wrong variable, a shell that split a path on a
        // space: all of it lands here, and none of it may take the process down before it can print why.
        foreach (var nasty in NastyStrings())
        {
            foreach (var args in new[]
                     {
                         new[] { nasty },
                         new[] { "--mode", nasty },
                         new[] { "--fan", nasty },
                         new[] { "--profile", nasty },
                         new[] { nasty, nasty, nasty },
                         new[] { "--mode", nasty, "--fan", nasty, "--profile", nasty }
                     })
            {
                var exception = Record.Exception(() => CliCommands.TryParse(args, out _, out _));
                Assert.Null(exception);
            }
        }
    }

    [Fact]
    public void CommandLine_NeverThrowsOnAHugeArgumentList()
    {
        // A shell glob that expanded to thousands of files. The parser walks the array, so the cost is linear, but
        // it must not recurse or allocate its way into a stack overflow.
        var args = Enumerable.Repeat("--mode", 5_000).ToArray();
        Assert.Null(Record.Exception(() => CliCommands.TryParse(args, out _, out _)));

        var nulls = new string?[1_000];
        Assert.Null(Record.Exception(() => CliCommands.TryParse(nulls!, out _, out _)));
    }

    // ---------------------------------------------------------------- settings file

    [Fact]
    public void SettingsFile_NeverThrowsAndAlwaysFallsBackToDefaults()
    {
        // The settings file is read before the window exists. A throw here is a process that does not start, with
        // no window to show the reason in.
        var path = Path_("settings.json");
        foreach (var nasty in NastyStrings())
        {
            File.WriteAllText(path, nasty, new UTF8Encoding(false));

            SettingsLoadResult result = null!;
            var exception = Record.Exception(() => result = SettingsManager.LoadSettings(path));
            Assert.Null(exception);
            Assert.NotNull(result);
            Assert.NotNull(result.Settings);
        }
    }

    [Fact]
    public void SettingsFile_WithHostileFieldValues_IsNormalizedRatherThanTrusted()
    {
        // A hand-edited or half-written file can carry any number in any field. Every one of them ends up driving
        // hardware, so the loader has to clamp rather than pass them through.
        var path = Path_("settings.json");
        var hostile = new[]
        {
            "{\"custom_fan_cpu_percent\":-99999,\"custom_fan_gpu_percent\":99999}",
            "{\"custom_fan_cpu_percent\":0,\"custom_fan_gpu_percent\":0}",
            "{\"fan_curve_points\":[]}",
            "{\"fan_curve_points\":null}",
            "{\"fan_curve_points\":[{\"temperature_c\":-500,\"percent\":-500}]}",
            "{\"fan_curve_points\":[{\"temperature_c\":99999,\"percent\":99999}]}",
            "{\"fan_curve_hysteresis_c\":9999,\"fan_curve_min_dwell_seconds\":-9999}",
            "{\"default_atk_mouse_profile\":9999}",
            "{\"theme\":9999}",
            "{\"language\":\"xx-YY\"}",
            "{\"window_left\":1e308,\"window_top\":-1e308}",
            "{\"last_operating_mode\":9999,\"dc_power_mode\":9999}"
        };

        foreach (var json in hostile)
        {
            File.WriteAllText(path, json, new UTF8Encoding(false));

            SettingsLoadResult result = null!;
            Assert.Null(Record.Exception(() => result = SettingsManager.LoadSettings(path)));
            var settings = result.Settings;

            Assert.InRange(settings.CustomFanCpuPercent, AppSettings.MinCustomFanPercent, 100);
            Assert.InRange(settings.CustomFanGpuPercent, AppSettings.MinCustomFanPercent, 100);
            Assert.InRange(settings.DefaultAtkMouseProfile, 0, 4);
            Assert.InRange(settings.FanCurveHysteresisC, 0, 15);
            Assert.InRange(settings.FanCurveMinDwellSeconds, 0, 120);
            Assert.NotNull(settings.FanCurvePoints);
            Assert.NotEmpty(settings.FanCurvePoints!);
            Assert.True(Enum.IsDefined(settings.Theme), $"theme after {json}");
            Assert.True(Enum.IsDefined(settings.DcPowerMode), $"dc mode after {json}");
            // null means "not chosen yet, follow Windows", which is a valid state.
            Assert.True(settings.Language == null || Loc.IsSupported(settings.Language), $"language after {json}");
        }
    }

    [Fact]
    public void SettingsFile_ThatIsADirectory_IsRejectedWithoutThrowing()
    {
        // A sync client or a botched restore can leave a folder where the file should be.
        var path = Path_("settings.json");
        Directory.CreateDirectory(path);

        SettingsLoadResult result = null!;
        Assert.Null(Record.Exception(() => result = SettingsManager.LoadSettings(path)));
        Assert.NotNull(result.Settings);
    }

    // ---------------------------------------------------------------- backup file

    [Fact]
    public void BackupFile_NeverThrowsOnAnyPayload()
    {
        // A backup is the one file a user carries between machines, so it arrives from a USB stick, a chat app and
        // a cloud folder. None of those preserve it faithfully.
        var backup = Path_("backup.json");
        var settingsPath = Path_("settings.json");

        foreach (var nasty in NastyStrings())
        {
            File.WriteAllText(backup, nasty, new UTF8Encoding(false));

            BackupResult result = null!;
            Assert.Null(Record.Exception(() => result = BackupService.Restore(backup, settingsPath, null, null)));
            Assert.NotNull(result);
            // A rejected restore must leave the live settings alone.
            Assert.NotNull(SettingsManager.LoadSettings(settingsPath).Settings);
        }
    }

    [Fact]
    public void BackupFile_WithDeeplyNestedJson_IsRejectedWithoutAStackOverflow()
    {
        // System.Text.Json has a depth limit, but the limit is only useful if the failure is caught. A stack
        // overflow cannot be caught at all, so this is the one shape that has to be refused cleanly.
        var backup = Path_("deep.json");
        var settingsPath = Path_("settings.json");
        var json = new StringBuilder("{\"app\":\"NitroLite\",\"schema\":1,\"settings\":");
        for (int i = 0; i < 5_000; i++) json.Append('[');
        for (int i = 0; i < 5_000; i++) json.Append(']');
        json.Append('}');
        File.WriteAllText(backup, json.ToString(), new UTF8Encoding(false));

        BackupResult result = null!;
        Assert.Null(Record.Exception(() => result = BackupService.Restore(backup, settingsPath, null, null)));
        Assert.False(result.Success);
    }

    // ---------------------------------------------------------------- release payload

    [Fact]
    public void ReleasePayload_NeverThrowsOnAnyShape()
    {
        // The release JSON comes off the network. A captive portal, a proxy error page or a truncated response all
        // arrive here as a JsonElement that is not a release.
        foreach (var nasty in NastyStrings())
        {
            JsonElement element;
            try
            {
                using var document = JsonDocument.Parse(nasty);
                element = document.RootElement.Clone();
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                continue;   // not JSON at all, so it never reaches the parser
            }

            Assert.Null(Record.Exception(() => UpdateService.ParseRelease(element, new Version(1, 2, 0))));
        }
    }

    [Fact]
    public void ReleasePayload_WithHostileAssetFields_IsIgnoredRatherThanTrusted()
    {
        var payloads = new[]
        {
            "{\"tag_name\":\"v999999999999999999999\",\"assets\":[]}",
            "{\"tag_name\":\"v1.3.0\",\"assets\":[{\"name\":null,\"browser_download_url\":null}]}",
            "{\"tag_name\":\"v1.3.0\",\"assets\":[{\"name\":\"NitroLite-v1.3.0-win-x64.zip\",\"digest\":\"sha256:\"}]}",
            "{\"tag_name\":\"v1.3.0\",\"assets\":[{\"name\":\"NitroLite-v1.3.0-win-x64.zip\",\"digest\":\"md5:abc\"}]}",
            "{\"tag_name\":\"v1.3.0\",\"assets\":[{\"name\":\"NitroLite-v1.3.0-win-x64.zip\",\"browser_download_url\":\"file:///C:/Windows/System32/evil.exe\"}]}",
            "{\"tag_name\":\"v1.3.0\",\"assets\":[{\"name\":\"../../evil.zip\",\"browser_download_url\":\"https://example.test/x.zip\"}]}",
            "{\"tag_name\":null,\"assets\":null}",
            "{\"assets\":[{\"name\":\"NitroLite-v1.3.0-win-x64.zip\"}]}"
        };

        foreach (var payload in payloads)
        {
            using var document = JsonDocument.Parse(payload);
            UpdateInfo? info = null;
            Assert.Null(Record.Exception(() => info = UpdateService.ParseRelease(document.RootElement, new Version(1, 2, 0))));

            // Whatever it decided, it must not hand back a download address that is not https.
            if (info?.DownloadUrl is { } url)
                Assert.StartsWith("https://", url, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- fan curve

    [Fact]
    public void FanCurve_NeverThrowsOnAnyPointList()
    {
        // The curve is edited by dragging sliders, but it is also restored from a settings file and from a game
        // profile. A malformed list must produce a usable curve, not an exception on the poll thread.
        var random = new Random(20261114);
        for (int i = 0; i < 500; i++)
        {
            var points = new List<FanCurvePoint>();
            int count = random.Next(0, 12);
            for (int j = 0; j < count; j++)
                points.Add(new FanCurvePoint(random.Next(-1000, 1000), random.Next(-1000, 1000)));

            FanCurve curve = null!;
            Assert.Null(Record.Exception(() => curve = new FanCurve(points)));
            Assert.NotNull(curve);

            // Whatever the input, the answer for any temperature has to be a speed the hardware accepts.
            foreach (var temperature in new[] { -273.0, 0.0, 25.0, 45.0, 85.0, 95.0, 150.0, 1e9 })
            {
                int percent = 0;
                Assert.Null(Record.Exception(() => percent = curve.PercentAt(temperature)));
                Assert.InRange(percent, FanCurve.MinPercent, FanCurve.MaxPercent);
            }
        }
    }

    [Fact]
    public void FanControlParameter_NeverAsksForAZeroSpeed()
    {
        // The single choke point every fan write goes through. A zero here stops the fans, and the firmware will
        // not take them back on its own while a custom speed is set.
        foreach (var percent in new[] { int.MinValue, -1, 0, 1, 29, 30, 100, 101, int.MaxValue })
        {
            var json = JsonSerializer.Serialize(AcerAgentClient.BuildFanControlParameter(FanMode.Custom, percent, percent));
            using var document = JsonDocument.Parse(json);
            foreach (var fan in document.RootElement.GetProperty("custom_fan_data").EnumerateArray())
            {
                int speed = fan.GetProperty("fan_custom_speed").GetInt32();
                Assert.InRange(speed, AppSettings.MinCustomFanPercent, 100);
            }
        }
    }

    // ---------------------------------------------------------------- formatting

    [Fact]
    public void TelemetryFormatting_NeverThrowsOnExtremeValues()
    {
        var values = new[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon,
            0.0, -0.0, 1e308, -1e308
        };
        var ints = new[] { int.MinValue, -1, 0, 1, int.MaxValue };

        foreach (var value in values)
        {
            Assert.Null(Record.Exception(() => TelemetryFormatter.FormatCpuTemperature(value)));
            Assert.Null(Record.Exception(() => TelemetryFormatter.FormatGpuTemperature(value, false, true)));
            Assert.Null(Record.Exception(() => TelemetryFormatter.FormatUsagePercent(value)));
        }

        foreach (var value in ints)
        {
            Assert.Null(Record.Exception(() => TelemetryFormatter.FormatFanRpm(value)));
            Assert.Null(Record.Exception(() => TelemetryFormatter.CalculateFanPercent(value, 0)));
        }
    }

    [Fact]
    public void Localization_NeverThrowsOnABrokenTemplate()
    {
        // A translation with a placeholder the call site does not pass, or a format specifier the value cannot
        // take, must still produce a message. Losing the text is worse than losing the formatting.
        foreach (var template in new[]
                 {
                     "{0} {1} {2}", "{0:F9}", "{0:Q}", "{{", "}}", "{", "}", "{0", "0}", "{999999}",
                     new string('{', 500), new string('}', 500)
                 })
        {
            Assert.Null(Record.Exception(() => Loc.Format(template, new object?[] { 1.5 }, null)));
            Assert.Null(Record.Exception(() => Loc.Format(template, Array.Empty<object?>(), null)));
            Assert.Null(Record.Exception(() => Loc.Format(template, new object?[] { null, "x", 3 }, null)));
        }
    }
}
