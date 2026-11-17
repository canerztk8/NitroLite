using System.Globalization;
using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The four gaps the parity audit found in the first wave: the command line the shipped scripts use, the placeholder
/// a missing sensor has to show, the lighting speed floor, and the per-game rule the window could not edit.
/// </summary>
/// <remarks>
/// Each of these is a case where the Avalonia window quietly did something different from the Windows one, and none of
/// them threw: a script exited 2, a dead sensor read "0 °C", a slider reached a level the firmware rejects, and a rule
/// was always applied automatically because nothing could turn it off.
/// </remarks>
public class Wave1CriticalParityTests
{
    // ---------------------------------------------------------------- package A: the command line

    [Theory]
    [InlineData("--fix-lighting", "fix-lighting")]
    [InlineData("--fix-services", "fix-services")]
    [InlineData("--restore-mux", "restore-mux")]
    [InlineData("--optimize-pc", "optimize")]
    [InlineData("--restore-optimize", "restore-optimize")]
    [InlineData("--uninstall-cleanup", "uninstall-cleanup")]
    public void ALegacyFlag_IsACliTaskAndNamesItsCommand(string flag, string command)
    {
        Assert.True(CliTaskRunner.IsCliTask(new[] { flag }));
        Assert.Equal(command, CliTaskRunner.ParseCommand(new[] { flag }));
        Assert.Equal(flag, CliTaskRunner.ParseLegacyFlag(new[] { flag }));
    }

    [Theory]
    [InlineData("--FIX-LIGHTING")]
    [InlineData(" --fix-lighting ")]
    public void ALegacyFlag_IsMatchedWithoutCaseOrSurroundingSpace(string flag)
    {
        Assert.True(CliTaskRunner.IsCliTask(new[] { flag }));
        Assert.Equal("fix-lighting", CliTaskRunner.ParseCommand(new[] { flag }));
    }

    [Fact]
    public void TheExplicitForm_StillWorks()
    {
        Assert.True(CliTaskRunner.IsCliTask(new[] { "--cli", "fix-lighting" }));
        Assert.Equal("fix-lighting", CliTaskRunner.ParseCommand(new[] { "--cli", "fix-lighting" }));

        // Nothing was spelled the old way, so the confirmation the legacy form asks for is not owed.
        Assert.Equal("", CliTaskRunner.ParseLegacyFlag(new[] { "--cli", "fix-lighting" }));
        Assert.False(CliTaskRunner.RequiresConfirmation(new[] { "--cli", "fix-lighting" }));
    }

    [Fact]
    public void AnExplicitCommand_WinsOverALegacyFlagOnTheSameLine()
    {
        var args = new[] { "--fix-lighting", "--cli", "restore-mux" };

        Assert.Equal("restore-mux", CliTaskRunner.ParseCommand(args));
        Assert.False(CliTaskRunner.RequiresConfirmation(args));
    }

    [Theory]
    [InlineData("--yes")]
    [InlineData("-y")]
    [InlineData("--force")]
    [InlineData("-Y")]
    [InlineData(" --yes ")]
    public void AConfirmationFlag_IsRecognised(string flag)
    {
        Assert.True(CliTaskRunner.IsConfirmed(new[] { "--fix-lighting", flag }));
    }

    [Theory]
    [InlineData("--fix-lighting")]
    [InlineData("--fix-lighting --cli")]
    [InlineData("")]
    public void WithoutAConfirmationFlag_NothingIsConfirmed(string line)
    {
        var args = line.Length == 0 ? Array.Empty<string>() : line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.False(CliTaskRunner.IsConfirmed(args));
    }

    [Fact]
    public void TheLegacyForm_AsksForConfirmationAndTheExplicitFormDoesNot()
    {
        // The split is the whole contract: a stray shortcut carries the old spelling, a person typing --cli does not.
        Assert.True(CliTaskRunner.RequiresConfirmation(new[] { "--optimize-pc" }));
        Assert.True(CliTaskRunner.RequiresConfirmation(new[] { "--optimize-pc", "--yes" }));
        Assert.False(CliTaskRunner.RequiresConfirmation(new[] { "--cli", "optimize" }));
    }

    [Theory]
    [InlineData("--autostart")]
    [InlineData("--minimized")]
    [InlineData("--hardware-report")]
    [InlineData("--mode")]
    [InlineData("")]
    public void AnythingElse_IsNotACliTask(string arg)
    {
        Assert.False(CliTaskRunner.IsCliTask(new[] { arg }));
        Assert.Equal("", CliTaskRunner.ParseCommand(new[] { arg }));
    }

    [Fact]
    public void NoArguments_IsNotACliTask()
    {
        Assert.False(CliTaskRunner.IsCliTask(null));
        Assert.False(CliTaskRunner.IsCliTask(Array.Empty<string>()));
        Assert.Equal("", CliTaskRunner.ParseCommand(null));
        Assert.False(CliTaskRunner.IsConfirmed(null));
        Assert.False(CliTaskRunner.RequiresConfirmation(null));
    }

    [Fact]
    public void EveryLegacyFlag_NamesACommandTheRunnerAccepts()
    {
        // A flag that mapped to a command the runner does not know would exit 2 after asking for confirmation.
        foreach (var (flag, command) in CliTaskRunner.LegacyFlags)
        {
            Assert.Contains(command, CliTaskRunner.Commands);
            Assert.StartsWith("--", flag);
        }
    }

    // ---------------------------------------------------------------- package B: the missing sensor

    [Fact]
    public void WithNoSensorReading_EveryReadoutSaysSoInsteadOfZero()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());

        // A machine with no backend reports nothing, and nothing is not 0 °C with the fans stopped.
        Assert.Equal("-- °C", vm.CpuTemperatureText);
        Assert.Equal("-- °C", vm.GpuTemperatureText);
        Assert.Equal("-- °C", vm.SysTemperatureText);
        Assert.Equal("-- RPM", vm.CpuFanRpmText);
        Assert.Equal("-- RPM", vm.GpuFanRpmText);
        Assert.Equal("-- %", vm.CpuUsageText);
        Assert.Equal("-- %", vm.GpuUsageText);
    }

    [Theory]
    [InlineData(0, "-- RPM")]
    [InlineData(-100, "-- RPM")]
    [InlineData(2436, "2436 RPM")]
    [InlineData(6000, "6000 RPM")]
    public void FormatFanRpm_ShowsThePlaceholderForNoReading(int rpm, string expected)
    {
        Assert.Equal(expected, TelemetryFormatter.FormatFanRpm(rpm));
    }

    [Theory]
    [InlineData(0.0, "-- %")]
    [InlineData(-1.0, "-- %")]
    [InlineData(double.NaN, "-- %")]
    [InlineData(double.PositiveInfinity, "-- %")]
    public void FormatUsagePercent_ShowsThePlaceholderForNoReading(double percent, string expected)
    {
        Assert.Equal(expected, TelemetryFormatter.FormatUsagePercent(percent));
    }

    [Fact]
    public void FormatUsagePercent_KeepsARealReading()
    {
        // The decimal separator follows the active culture, because the number sits beside the temperature and the
        // clock in the same readout and the three have to agree. Pinned here so the assertion does not depend on the
        // machine the suite runs on.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("13.1 %", TelemetryFormatter.FormatUsagePercent(13.1));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TheRawFanFormatter_KeepsItsOwnContract()
    {
        // FormatFanSpeedRpm is the raw figure and still prints 0: the window uses FormatFanRpm, but a caller that
        // wants the number itself must not be silently given a placeholder.
        Assert.Equal("0 RPM", TelemetryFormatter.FormatFanSpeedRpm(0));
        Assert.Equal("2436 RPM", TelemetryFormatter.FormatFanSpeedRpm(2436));
    }

    // ---------------------------------------------------------------- package C: the lighting speed floor

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(9, 9)]
    [InlineData(10, 9)]
    public void SpeedLevel_NeverLeavesTheRangeTheProtocolAccepts(int assigned, int expected)
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());

        vm.SpeedLevel = assigned;

        Assert.Equal(expected, vm.SpeedLevel);
        Assert.InRange(vm.SpeedLevel, LightingLimits.MinSpeed, LightingLimits.MaxSpeed);
    }

    [Fact]
    public void AStoredSpeedOfZero_IsRaisedToTheFloorWhenItIsApplied()
    {
        var settings = new AppSettings
        {
            KeyboardLighting = new LightingConfig { Effect = "STATIC", Brightness = 5, Speed = 0, Direction = 1 }
        };

        using var vm = new MainViewModel(settings, null, null, TempSettingsPath());

        // ApplyLightingState assigns the stored value straight into the property, so the clamp is what protects the
        // keyboard from a settings file written before the floor existed.
        vm.SpeedLevel = settings.KeyboardLighting!.Speed;

        Assert.Equal(LightingLimits.MinSpeed, vm.SpeedLevel);

        // And the policy that builds the write clamps too, so neither layer can send a level the firmware rejects.
        Assert.Equal(1, LightingPolicy.Sanitize(settings.KeyboardLighting)!.Speed);
    }

    // ---------------------------------------------------------------- package D: the per-game rule

    [Fact]
    public async Task GameAutoApply_IsReadFromTheSelectedRule()
    {
        var settings = new AppSettings
        {
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "cs2", DisplayName = "Counter-Strike 2", AutoApply = false }
            }
        };

        using var vm = new MainViewModel(settings, null, null, TempSettingsPath());
        await vm.InitializeAsync();

        Assert.NotNull(vm.SelectedGameProfile);
        Assert.False(vm.GameAutoApply);
    }

    [Fact]
    public async Task GameAutoApply_WritesThroughToTheRuleAndSurvivesAReload()
    {
        var path = TempSettingsPath();
        var settings = new AppSettings
        {
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "cs2", DisplayName = "Counter-Strike 2", AutoApply = true }
            }
        };

        try
        {
            using (var vm = new MainViewModel(settings, null, null, path))
            {
                await vm.InitializeAsync();
                Assert.True(vm.GameAutoApply);

                vm.GameAutoApply = false;

                // The service reads the settings object on every poll, so the rule itself has to carry the change.
                Assert.False(settings.GameProfiles[0].AutoApply);
            }

            var reloaded = SettingsManager.LoadSettingsWithFallback(path);
            Assert.False(reloaded.GameProfiles[0].AutoApply);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task GameCurveAnchors_AreTheSixTemperaturesTheEditorOffers()
    {
        var settings = new AppSettings
        {
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "cs2", DisplayName = "Counter-Strike 2", TargetFanCurveEnabled = true }
            }
        };

        using var vm = new MainViewModel(settings, null, null, TempSettingsPath());
        await vm.InitializeAsync();

        Assert.Equal(FanCurve.AnchorCount, vm.GameCurveAnchors.Count);
        Assert.Equal(
            FanCurve.AnchorTemperatures.ToList(),
            vm.GameCurveAnchors.Select(a => a.TemperatureC).ToList());
    }

    [Fact]
    public async Task GameCurveAnchors_FallBackToTheDesktopCurveWhenTheRuleHasNoShape()
    {
        var settings = new AppSettings
        {
            FanCurvePoints = new List<FanCurvePoint>
            {
                new(40, 30), new(50, 35), new(60, 40), new(70, 55), new(80, 75), new(90, 100)
            },
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "cs2", DisplayName = "Counter-Strike 2", TargetFanCurvePoints = null }
            }
        };

        using var vm = new MainViewModel(settings, null, null, TempSettingsPath());
        await vm.InitializeAsync();

        Assert.Equal(6, vm.GameCurveAnchors.Count);
        Assert.Equal(55, vm.GameCurveAnchors.Single(a => a.TemperatureC == 70).Percent);
    }

    [Fact]
    public async Task GameCurveAnchor_WritesAllSixPointsIncludingTheMovedAnchorWithoutASaveButton()
    {
        var path = TempSettingsPath();
        var settings = new AppSettings
        {
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "cs2", DisplayName = "Counter-Strike 2", TargetFanCurveEnabled = true }
            }
        };

        try
        {
            using (var vm = new MainViewModel(settings, null, null, path))
            {
                await vm.InitializeAsync();

                // Moving an anchor is the write. There is no save button left to press, so the rule has to be on disk
                // the moment the slider moves.
                vm.GameCurveAnchors.Single(a => a.TemperatureC == 70).Percent = 58;

                var points = settings.GameProfiles[0].TargetFanCurvePoints;
                Assert.NotNull(points);
                Assert.Equal(6, points!.Count);
                Assert.Equal(58, points.Single(p => p.TemperatureC == 70).Percent);

                // The rule is in memory at once; the file write waits for the drag to stop, because a drag raises a
                // change per pixel and each one is a whole settings file. The pause is what the wait below covers.
                await Phase3Fakes.WaitUntilAsync(() =>
                    SettingsManager.LoadSettingsWithFallback(path).GameProfiles[0].TargetFanCurvePoints != null);
            }

            var reloaded = SettingsManager.LoadSettingsWithFallback(path);
            var stored = reloaded.GameProfiles[0].TargetFanCurvePoints;
            Assert.NotNull(stored);
            Assert.Equal(6, stored!.Count);
            Assert.Equal(58, stored.Single(p => p.TemperatureC == 70).Percent);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task GameCurveAnchors_FollowTheSelection()
    {
        var settings = new AppSettings
        {
            GameProfiles = new List<GameProfile>
            {
                new()
                {
                    ExeName = "cs2", DisplayName = "Counter-Strike 2", TargetFanCurveEnabled = true,
                    TargetFanCurvePoints = new List<FanCurvePoint>
                    {
                        new(40, 30), new(50, 30), new(60, 30), new(70, 30), new(80, 30), new(90, 100)
                    }
                },
                new()
                {
                    ExeName = "Balatro", DisplayName = "Balatro", TargetFanCurveEnabled = true,
                    TargetFanCurvePoints = new List<FanCurvePoint>
                    {
                        new(40, 30), new(50, 30), new(60, 30), new(70, 90), new(80, 95), new(90, 100)
                    }
                }
            }
        };

        using var vm = new MainViewModel(settings, null, null, TempSettingsPath());
        await vm.InitializeAsync();

        vm.SelectedGameProfile = vm.GameProfiles.Single(r => r.ExeName == "Balatro");

        Assert.Equal(90, vm.GameCurveAnchors.Single(a => a.TemperatureC == 70).Percent);
    }

    // ---------------------------------------------------------------- fixtures

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-wave1-" + Guid.NewGuid().ToString("N"), "settings.json");

    private static void Delete(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
