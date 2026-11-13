using System;
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

        Assert.Equal(AppTheme.Light, settings.Theme);
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
        Assert.Equal(AppTheme.Light, result.Settings.Theme);
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
        File.WriteAllText(path, """{ "theme": "dark" }""");

        var result = SettingsManager.LoadSettings(path);

        // A string where a number belongs cannot be coerced, so the file is rejected and the defaults win.
        Assert.True(result.PrimaryCorrupt);
        Assert.Equal(SettingsLoadSource.Defaults, result.Source);
        Assert.Equal(AppTheme.Light, result.Settings.Theme);
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
}
