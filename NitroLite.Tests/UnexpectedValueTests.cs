using System.IO;
using System.Text.Json.Nodes;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// What the app does when a value arrives that is not one of the ones it expects. Every setting here is a small
/// closed set (0/1, an enum, a percentage), and the sources are outside the app's control: a hand-edited settings
/// file, a half-written one, an Acer service that answers in a shape no model documented.
/// </summary>
public class UnexpectedValueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nl_unexpected_" + Guid.NewGuid().ToString("N"));

    public UnexpectedValueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- settings file: a number that is valid JSON but not a valid setting -------------------------------

    [Fact]
    public void Normalize_ReplacesEnumValuesThatHaveNoMember()
    {
        var settings = new AppSettings
        {
            Theme = (AppTheme)7,
            LastOperatingMode = (OperatingMode)99,
            DcPowerMode = (DcPowerProfilePreference)5
        };

        settings.Normalize();

        // An unknown palette falls back to following Windows rather than to a fixed one: the user's own light/dark
        // preference is a better guess than either palette.
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Null(settings.LastOperatingMode);
        Assert.Equal(DcPowerProfilePreference.Eco, settings.DcPowerMode);
    }

    [Fact]
    public void Normalize_KeepsEnumValuesThatDoHaveAMember()
    {
        var settings = new AppSettings
        {
            Theme = AppTheme.Dark,
            LastOperatingMode = OperatingMode.Turbo,
            DcPowerMode = DcPowerProfilePreference.Balanced
        };

        settings.Normalize();

        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal(OperatingMode.Turbo, settings.LastOperatingMode);
        Assert.Equal(DcPowerProfilePreference.Balanced, settings.DcPowerMode);
    }

    [Fact]
    public void Normalize_BringsIndicesAndPercentagesBackIntoRange()
    {
        var settings = new AppSettings
        {
            OsdCorner = 9,
            OsdMonitor = -3,
            DefaultAtkMouseProfile = 42,
            CustomFanCpuPercent = 400,
            CustomFanGpuPercent = -20
        };

        settings.Normalize();

        Assert.InRange(settings.OsdCorner, 0, 3);
        Assert.Equal(0, settings.OsdMonitor);
        Assert.InRange(settings.DefaultAtkMouseProfile, 0, 4);
        Assert.Equal(100, settings.CustomFanCpuPercent);
        Assert.Equal(AppSettings.MinCustomFanPercent, settings.CustomFanGpuPercent);
    }

    [Fact]
    public void Normalize_LeavesTheChartMetricAlone()
    {
        // A display preference: the graph falls back to CPU temperature for a metric it does not know, so
        // rewriting the stored choice would only lose what the user picked.
        var settings = new AppSettings { SelectedChartMetric = "NOT_A_METRIC" };
        settings.Normalize();
        Assert.Equal("NOT_A_METRIC", settings.SelectedChartMetric);
    }

    [Fact]
    public void Normalize_DropsALanguageTheAppCannotSpeak()
    {
        var settings = new AppSettings { Language = "xx" };
        settings.Normalize();
        Assert.Null(settings.Language);

        settings.Language = "de";
        settings.Normalize();
        Assert.Equal("de", settings.Language);
    }

    [Fact]
    public void LoadSettings_NormalizesAFileFullOfOutOfRangeNumbers()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              "theme": 7,
              "last_operating_mode": 99,
              "dc_power_mode": 5,
              "osd_corner": 9,
              "osd_monitor": -3,
              "default_atk_mouse_profile": 42,
              "custom_fan_cpu_percent": 400,
              "custom_fan_gpu_percent": -20,
              "selectedChartMetric": "NOT_A_METRIC",
              "language": "xx"
            }
            """);

        var result = SettingsManager.LoadSettings(path);

        // The file parses, so it is used rather than discarded; every unusable value is replaced by its default.
        Assert.Equal(SettingsLoadSource.Primary, result.Source);
        Assert.False(result.PrimaryCorrupt);
        Assert.Equal(AppTheme.System, result.Settings.Theme);
        Assert.Null(result.Settings.LastOperatingMode);
        Assert.Equal(DcPowerProfilePreference.Eco, result.Settings.DcPowerMode);
        Assert.InRange(result.Settings.OsdCorner, 0, 3);
        Assert.Equal(0, result.Settings.OsdMonitor);
        Assert.InRange(result.Settings.DefaultAtkMouseProfile, 0, 4);
        Assert.Equal(100, result.Settings.CustomFanCpuPercent);
        Assert.Equal(AppSettings.MinCustomFanPercent, result.Settings.CustomFanGpuPercent);
        Assert.Null(result.Settings.Language);
    }

    [Fact]
    public void LoadSettings_StillDiscardsAFileWithTheWrongType()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "OsdCorner": "nine" }""");

        var result = SettingsManager.LoadSettings(path);

        // A string where a number belongs cannot be coerced, so the file is rejected and the defaults win. The
        // default palette is still Light: only a theme *name* the app cannot read falls back to System.
        Assert.True(result.PrimaryCorrupt);
        Assert.Equal(SettingsLoadSource.Defaults, result.Source);
        Assert.Equal(AppTheme.WarmCream, result.Settings.Theme);
    }

    [Fact]
    public void LoadSettings_ReadsAThemeNameAndFallsBackForAnUnknownOne()
    {
        // The theme is stored by name, so a string is the expected shape here rather than the wrong type. An
        // unrecognised name costs the palette, not the rest of the file.
        var path = Path.Combine(_dir, "settings.json");

        File.WriteAllText(path, """{ "theme": "NordFrost", "language": "de" }""");
        var named = SettingsManager.LoadSettings(path);
        Assert.Equal(SettingsLoadSource.Primary, named.Source);
        Assert.Equal(AppTheme.NordFrost, named.Settings.Theme);
        Assert.Equal("de", named.Settings.Language);

        File.WriteAllText(path, """{ "theme": "nonsense", "language": "de" }""");
        var unknown = SettingsManager.LoadSettings(path);
        Assert.Equal(SettingsLoadSource.Primary, unknown.Source);
        Assert.Equal(AppTheme.System, unknown.Settings.Theme);
        Assert.Equal("de", unknown.Settings.Language);
    }

    // ---- values that come back from the Acer service ------------------------------------------------------

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("2", true)]
    [InlineData("", false)]
    [InlineData("yes", false)]
    public void ReadCapabilityFlag_ReadsAStringAnswerConsistently(string text, bool expected)
    {
        // "2" must mean the same as the number 2: the numeric form is already read as "non-zero is true".
        Assert.Equal(expected, AcerAgentClient.ReadCapabilityFlag(JsonValue.Create(text)!));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(-1, true)]
    public void ReadCapabilityFlag_ReadsANumericAnswerAsNonZero(int number, bool expected)
    {
        Assert.Equal(expected, AcerAgentClient.ReadCapabilityFlag(JsonValue.Create(number)!));
    }

    [Fact]
    public void ReadCapabilityFlag_ReadsAWrappedAnswer()
    {
        var wrapped = JsonNode.Parse("""{ "support": { "value": 1 } }""")!;
        Assert.True(AcerAgentClient.ReadCapabilityFlag(wrapped));

        var unknownShape = JsonNode.Parse("""{ "something_else": 1 }""")!;
        Assert.False(AcerAgentClient.ReadCapabilityFlag(unknownShape));
    }

    // ---- values that come back from Windows ---------------------------------------------------------------

    [Fact]
    public void DisplayName_ShowsAnUnknownOperatingModeByNumber()
    {
        Assert.Equal("Mod 99", ModePolicy.DisplayName((OperatingMode)99));
        Assert.Equal("Turbo", ModePolicy.DisplayName(OperatingMode.Turbo));
    }

    [Fact]
    public void BoostDisplayName_ShowsAWindowsValueItDoesNotSetByNumber()
    {
        // 3, 5 and 6 are real PerfBoostMode values NitroLite never writes; they must not be labelled "Aggressive".
        Assert.Contains("3", ModePolicy.BoostDisplayName((CpuBoostMode)3));
        Assert.Contains("6", ModePolicy.BoostDisplayName((CpuBoostMode)6));
        Assert.Equal("Agresif", ModePolicy.BoostDisplayName((CpuBoostMode)2));
    }

    [Fact]
    public void DescribeStatus_DoesNotClaimToKnowAnUnrecognisedStartType()
    {
        Assert.Equal("Bilinmiyor", DebloatService.DescribeStatus(null, "Unknown"));
        Assert.Equal("Çalışıyor", DebloatService.DescribeStatus("Running", "Unknown"));
        Assert.Equal("Devre dışı", DebloatService.DescribeStatus("Running", "Disabled"));
    }

    [Fact]
    public void ValidateStartupType_RejectsAnythingOutsideTheThreeWindowsValues()
    {
        Assert.Throws<ArgumentException>(() => DebloatService.ValidateStartupType("Boot"));
        Assert.Throws<ArgumentException>(() => DebloatService.ValidateStartupType("0"));
        Assert.Equal("Automatic", DebloatService.ValidateStartupType(" automatic "));
    }

    // ---- sensor values that are sentinels rather than readings --------------------------------------------

    [Theory]
    [InlineData(255.0)]
    [InlineData(999.0)]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    public void FormatCpuTemperature_TreatsAnImpossibleReadingAsNoReading(double temp)
    {
        Assert.Equal("-- °C", TelemetryFormatter.FormatCpuTemperature(temp));
    }

    [Fact]
    public void FormatCpuTemperature_StillShowsARealReading()
    {
        Assert.Equal("95 °C", TelemetryFormatter.FormatCpuTemperature(95.2));
    }

    [Fact]
    public void FormatGpuTemperature_TreatsAnImpossibleReadingAsNoReading()
    {
        Assert.Equal("-- °C", TelemetryFormatter.FormatGpuTemperature(255.0));
        Assert.Equal("-- °C", TelemetryFormatter.FormatGpuTemperature(255.0, available: false));
        Assert.Equal("0 °C (Uyku / D3cold)", TelemetryFormatter.FormatGpuTemperature(255.0, isSleeping: true));
    }

    // ---- the lighting block: strings and a list, not numbers ------------------------------------------------

    [Fact]
    public void Normalize_RepairsALightingBlockWithNullStrings()
    {
        // A hand-edited or half-written file can carry "Effect": null and "LEDs": null. Both used to reach
        // cfg.Effect.Equals(...) and cfg.LEDs.ToArray() in the startup refresh, and the exception aborted every
        // step after it: the window opened half-built on every launch.
        var settings = new AppSettings
        {
            KeyboardLighting = new LightingConfig { Effect = null!, Color = null!, LEDs = null! }
        };

        settings.Normalize();

        Assert.NotNull(settings.KeyboardLighting);
        Assert.Equal("STATIC", settings.KeyboardLighting!.Effect);
        Assert.Equal(LightingPolicy.DefaultColor, settings.KeyboardLighting.Color);
        Assert.Equal(4, settings.KeyboardLighting.LEDs.Count);
        Assert.All(settings.KeyboardLighting.LEDs, led => Assert.Equal(LightingPolicy.DefaultColor, led));
    }

    [Fact]
    public void Normalize_LeavesAMissingLightingBlockMissing()
    {
        // No lighting block means "read the keyboard's own state". Turning it into a default would make a fresh
        // install overwrite whatever the keyboard is currently doing.
        var settings = new AppSettings { KeyboardLighting = null, LastActiveLighting = null };

        settings.Normalize();

        Assert.Null(settings.KeyboardLighting);
        Assert.Null(settings.LastActiveLighting);
    }

    [Fact]
    public void Normalize_BringsLightingNumbersAndColoursIntoRange()
    {
        var settings = new AppSettings
        {
            KeyboardLighting = new LightingConfig
            {
                Effect = "static",
                Brightness = 99,
                Speed = 0,
                Direction = 42,
                Color = "not-a-colour",
                LEDs = new List<string> { "#GGGGGG", "#123456" }
            }
        };

        settings.Normalize();

        var lit = settings.KeyboardLighting!;
        Assert.Equal("STATIC", lit.Effect);
        Assert.Equal(9, lit.Brightness);
        Assert.Equal(1, lit.Speed);
        Assert.Equal(1, lit.Direction);
        Assert.Equal(LightingPolicy.DefaultColor, lit.Color);
        Assert.Equal(4, lit.LEDs.Count);
        Assert.Equal(LightingPolicy.DefaultColor, lit.LEDs[0]);
        Assert.Equal("#123456", lit.LEDs[1]);
    }

    [Fact]
    public void Sanitize_KeepsTheDirectionAnEffectActuallySupports()
    {
        var wave = LightingPolicy.Sanitize(new LightingConfig { Effect = "WAVE", Direction = 3 })!;
        Assert.Equal(3, wave.Direction);

        // STATIC has no direction at all, so whatever the file said is replaced rather than sent.
        var staticEffect = LightingPolicy.Sanitize(new LightingConfig { Effect = "STATIC", Direction = 3 })!;
        Assert.Equal(1, staticEffect.Direction);
    }

    [Fact]
    public void Sanitize_TruncatesExtraZonesInsteadOfSendingThem()
    {
        var cfg = LightingPolicy.Sanitize(new LightingConfig
        {
            Effect = "STATIC",
            LEDs = new List<string> { "#111111", "#222222", "#333333", "#444444", "#555555", "#666666" }
        })!;

        Assert.Equal(4, cfg.LEDs.Count);
        Assert.Equal("#444444", cfg.LEDs[3]);
    }

    [Fact]
    public void LoadSettings_SurvivesALightingBlockFullOfNulls()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              "KeyboardLighting": { "Effect": null, "Brightness": 99, "Speed": 0, "Direction": 9, "Color": null, "LEDs": null },
              "LastActiveLighting": { "Effect": null, "LEDs": null }
            }
            """);

        var result = SettingsManager.LoadSettings(path);

        Assert.Equal(SettingsLoadSource.Primary, result.Source);
        Assert.False(result.PrimaryCorrupt);
        Assert.Equal("STATIC", result.Settings.KeyboardLighting!.Effect);
        Assert.Equal(4, result.Settings.KeyboardLighting!.LEDs.Count);
        Assert.Equal("STATIC", result.Settings.LastActiveLighting!.Effect);
    }

    [Fact]
    public void RgbPreset_Sanitize_RepairsAPresetReadFromDisk()
    {
        var preset = new RgbPreset { Name = "Bozuk", Effect = null!, Brightness = 99, LEDs = null! };

        preset.Sanitize();

        Assert.Equal("STATIC", preset.Effect);
        Assert.Equal(9, preset.Brightness);
        Assert.Equal(4, preset.LEDs.Count);
    }
}
