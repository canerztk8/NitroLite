using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The two sensors under the temperature colour, the level each automatic colour paints at, and the box a stop's
/// temperature is typed into.
/// </summary>
/// <remarks>
/// The split is the interesting half: with both chips on, the left two zones follow the processor and the right two the
/// graphics chip, so one write carries two colours. The tests assert the zone pattern rather than only the primary
/// colour, because a write that agreed with itself would otherwise look correct.
/// </remarks>
public class ReactiveSensorTests
{
    // =============================================================================================================
    // 1. Which sensor paints which half.
    // =============================================================================================================

    [Fact]
    public void NoSensor_HasNoColour()
    {
        // A mode with no source is armed and paints nothing; the card says so in words rather than leaving the keys on
        // a colour that stopped meaning anything.
        Assert.Null(ReactiveLighting.ColorsForSensors(null, 58, 51, useCpu: false, useGpu: false));
    }

    [Fact]
    public void TheProcessorAlone_PaintsTheWholeKeyboardItsColour()
    {
        var pair = ReactiveLighting.ColorsForSensors(null, 58, 51, useCpu: true, useGpu: false);
        Assert.True(pair.HasValue);

        // One sensor means one colour across all four zones: a keyboard split by a choice the user did not make reads
        // as a fault.
        Assert.Equal(ReactiveLighting.ColorFor(58), pair!.Value.Left);
        Assert.Equal(pair.Value.Left, pair.Value.Right);
    }

    [Fact]
    public void TheGraphicsChipAlone_PaintsTheWholeKeyboardItsColour()
    {
        var pair = ReactiveLighting.ColorsForSensors(null, 58, 95, useCpu: false, useGpu: true);
        Assert.True(pair.HasValue);

        Assert.Equal(ReactiveLighting.ColorFor(95), pair!.Value.Left);
        Assert.Equal(pair.Value.Left, pair.Value.Right);
    }

    [Fact]
    public void BothSensors_SplitTheKeyboardDownTheMiddle()
    {
        var pair = ReactiveLighting.ColorsForSensors(null, 58, 95, useCpu: true, useGpu: true);
        Assert.True(pair.HasValue);

        Assert.Equal(ReactiveLighting.ColorFor(58), pair!.Value.Left);
        Assert.Equal(ReactiveLighting.ColorFor(95), pair.Value.Right);

        // The temperatures were chosen to be far apart, so an implementation that used one sensor twice fails here
        // rather than passing on two colours that happened to be equal.
        Assert.NotEqual(pair.Value.Left, pair.Value.Right);
    }

    [Fact]
    public void ADeadReading_FallsBackToTheOtherSensorRatherThanTheColdEnd()
    {
        // Zero is what a backend that could not read the sensor returns. It is not a cold machine, so the colour must
        // not be the ramp's cold end.
        var pair = ReactiveLighting.ColorsForSensors(null, 0, 95, useCpu: true, useGpu: true);
        Assert.True(pair.HasValue);

        Assert.Equal(ReactiveLighting.ColorFor(95), pair!.Value.Left);
        Assert.Equal(pair.Value.Left, pair.Value.Right);
        Assert.NotEqual(ReactiveLighting.ColorFor(ReactiveLighting.MinStopCelsius), pair.Value.Left);
    }

    [Fact]
    public void BothReadingsDead_HasNoColour()
    {
        Assert.Null(ReactiveLighting.ColorsForSensors(null, 0, double.NaN, useCpu: true, useGpu: true));
        Assert.Null(ReactiveLighting.ColorsForSensors(null, 0, 0, useCpu: true, useGpu: false));
    }

    // =============================================================================================================
    // 2. The view model's sensors.
    // =============================================================================================================

    [Fact]
    public async Task TheCardStartsWithTheProcessorAndNoGraphicsChip()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        Assert.True(vm.ReactiveCpuSensor);
        Assert.False(vm.ReactiveGpuSensor);
        Assert.False(vm.ReactiveSplit);
        Assert.True(vm.HasReactiveSensors);

        // The number under the chip is the number the colour came from.
        Assert.Equal($"CPU {vm.CpuTemperature:F0} °C", vm.ReactiveCpuChip);
        Assert.Equal(vm.ReactiveCpuChip, vm.ReactiveLightingNow);
    }

    [Fact]
    public async Task TurningTheGraphicsChipOn_WritesTwoHalves()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 58;
        hardware.Sample.GpuTemperature = 95;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;
        hardware.LightingWrites.Clear();

        vm.ReactiveGpuSensor = true;
        await vm.ReactiveLightingTask;

        Assert.True(vm.ReactiveSplit);
        Assert.Contains(" · ", vm.ReactiveLightingNow, StringComparison.Ordinal);

        var left = ReactiveLighting.ColorFor(58);
        var right = ReactiveLighting.ColorFor(95);

        var write = Assert.Single(hardware.LightingWrites);

        // Four zones, the left pair the processor's colour and the right pair the graphics chip's. The shape is the
        // contract: an effect that takes one colour cannot address a half, so this has to be the zone pattern.
        Assert.EndsWith($"|{left},{left},{right},{right}", write, StringComparison.Ordinal);
        Assert.Equal(left, vm.ReactiveLightingColor);
        Assert.Equal(right, vm.ReactiveLightingColorRight);
    }

    [Fact]
    public async Task TurningTheProcessorOff_LeavesTheGraphicsChipInChargeOfTheWholeKeyboard()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 58;
        hardware.Sample.GpuTemperature = 95;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveGpuSensor = true;
        vm.ReactiveCpuSensor = false;

        Assert.False(vm.ReactiveCpuSensor);
        Assert.False(vm.ReactiveSplit);

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        var expected = ReactiveLighting.ColorFor(95);
        var write = hardware.LightingWrites.Last(w => w.Contains(expected, StringComparison.Ordinal));

        Assert.EndsWith($"|{expected},{expected},{expected},{expected}", write, StringComparison.Ordinal);
        Assert.Equal(vm.ReactiveGpuChip, vm.ReactiveLightingNow);
    }

    [Fact]
    public async Task MovingASensor_RepaintsAtOnceRatherThanAtTheNextBucket()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 58;
        hardware.Sample.GpuTemperature = 95;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;
        hardware.LightingWrites.Clear();

        // The temperature has not moved, so without the forced repaint the throttle would sit on the colour it already
        // wrote and the switch would appear to do nothing until the chip warmed up.
        vm.ReactiveGpuSensor = true;
        await vm.ReactiveLightingTask;

        var write = Assert.Single(hardware.LightingWrites);

        // Two halves means four zones: the pair that disagrees is what the split is for.
        Assert.Equal(4, write.Split('|')[^1].Split(',').Length);
        Assert.Equal(2, write.Split('|')[^1].Split(',').Distinct().Count());
    }

    [Fact]
    public async Task TurningTheModeOnWithNoSensor_PicksTheProcessorUp()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        vm.ReactiveCpuSensor = false;
        Assert.False(vm.HasReactiveSensors);
        Assert.Equal(vm.Labels.ReactiveNoSensor, vm.ReactiveLightingNow);

        vm.ReactiveLightingEnabled = true;

        // A switch that does nothing is worse than a wrong default, so enabling the mode always leaves it with a
        // source the user can see.
        Assert.True(vm.ReactiveCpuSensor);
        Assert.True(vm.HasReactiveSensors);
    }

    [Fact]
    public async Task TheSensorsComeBackOnTheNextLaunch()
    {
        var path = Path.Combine(Path.GetTempPath(), "nitrolite-sensors-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var hardware = new Phase3Fakes.StubHardware();
            var settings = new AppSettings { ReactiveLightingUseCpu = false, ReactiveLightingUseGpu = true };

            using (var vm = new MainViewModel(settings, hardware, null, path))
            {
                await vm.InitializeAsync();
                Assert.False(vm.ReactiveCpuSensor);
                Assert.True(vm.ReactiveGpuSensor);
            }

            // The switches are the saved pair, not the shipped default: a graphics-chip-only machine would otherwise
            // go back to the processor after every restart.
            var reloaded = SettingsManager.LoadSettingsWithFallback(path);
            Assert.False(reloaded.ReactiveLightingUseCpu);
            Assert.True(reloaded.ReactiveLightingUseGpu);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // =============================================================================================================
    // 3. The mode's own brightness.
    // =============================================================================================================

    [Fact]
    public async Task TheModePaintsAtItsOwnLevel_NotThePages()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 78;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.BrightnessLevel = 9;

        // The page's slider is debounced, and its write would be the manual apply: let it land before the mode is
        // switched on, so the write counted below is the mode's.
        await Phase3Fakes.WaitUntilAsync(() => hardware.LightingWrites.Count > 0, 3000);
        hardware.LightingWrites.Clear();

        vm.ReactiveBrightness = 3;
        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        var write = Assert.Single(hardware.LightingWrites,
            w => w.Contains(ReactiveLighting.ColorFor(78), StringComparison.Ordinal));

        // The mode's level, not the page's: the page's slider belongs to the colour the user chose.
        Assert.StartsWith("STATIC|3|", write, StringComparison.Ordinal);
        Assert.Equal(9, vm.BrightnessLevel);
        Assert.Equal(vm.Labels.BrightnessValue(3, vm.MaxBrightness), vm.ReactiveBrightnessLabel);
    }

    [Fact]
    public async Task MovingTheModesSlider_RepaintsAtOnce()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 78;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;
        hardware.LightingWrites.Clear();

        vm.ReactiveBrightness = 2;
        await vm.ReactiveLightingTask;

        var write = Assert.Single(hardware.LightingWrites);
        Assert.StartsWith("STATIC|2|", write, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLevelIsNeverZero()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        // Zero is the firmware's "stay dark", so a slider that reached it would look like a broken mode rather than a
        // dim one.
        vm.ReactiveBrightness = 0;
        Assert.Equal(ChargingLighting.MinBrightness, vm.ReactiveBrightness);

        vm.ReactiveBrightness = 99;
        Assert.Equal(vm.MaxBrightness, vm.ReactiveBrightness);
    }

    [Fact]
    public async Task TheChargingColourPaintsAtItsOwnLevelToo()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware, powerState: () => (true, 80));
        await vm.InitializeAsync();

        vm.ChargingBrightness = 2;
        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;

        var write = Assert.Single(hardware.LightingWrites,
            w => w.Contains(ChargingLighting.DefaultColor, StringComparison.Ordinal));

        Assert.StartsWith("STATIC|2|", write, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MovingTheChargingSliderWhileThePlugIsIn_RepaintsAtOnce()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware, powerState: () => (true, 80));
        await vm.InitializeAsync();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;
        hardware.LightingWrites.Clear();

        vm.ChargingBrightness = 4;
        await vm.ChargingLightingTask;

        var write = Assert.Single(hardware.LightingWrites);
        Assert.StartsWith("STATIC|4|", write, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 4. A manual choice ends both automatic colours, without the off/on dance.
    // =============================================================================================================

    [Fact]
    public async Task PickingAnRgbProfile_TurnsBothAutomaticColoursOff()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        Assert.True(vm.ReactiveLightingEnabled);
        Assert.True(vm.AutomaticColoursEnabled);

        hardware.LightingWrites.Clear();
        vm.SelectedRgbProfile = vm.RgbProfiles[1];

        // The profile's own write is the manual apply, and it ends both automatic colours as it starts. The user no
        // longer has to toggle the switch off and on to get the page's colour back.
        await Phase3Fakes.WaitUntilAsync(() => !vm.ReactiveLightingEnabled, 3000);

        Assert.False(vm.ReactiveLightingEnabled);
        Assert.False(vm.ChargingColorEnabled);

        // The cards stay open: switching a mode off is not switching the page off.
        Assert.True(vm.AutomaticColoursEnabled);
    }

    [Fact]
    public async Task TheSwitchesGoBackOnAfterTheManualColour()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        vm.SelectedRgbProfile = vm.RgbProfiles[1];
        await Phase3Fakes.WaitUntilAsync(() => !vm.ReactiveLightingEnabled, 3000);

        hardware.LightingWrites.Clear();
        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        // The mode takes the keys back on the next write, which is what "turn it on again" is supposed to mean.
        Assert.True(vm.ReactiveLightingEnabled);
        Assert.NotEmpty(hardware.LightingWrites);
    }

    // =============================================================================================================
    // 5. A stop's temperature, typed.
    // =============================================================================================================

    [Fact]
    public async Task AStopTakesTheTemperatureTypedIntoItsBox()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);
        var row = vm.ReactiveStops[1];

        vm.SetReactiveStopTemperature(row, "73 °C");

        // The box shows a unit, so the same text the user can see has to parse back.
        Assert.Equal(73, row.AtCelsius);
        Assert.Equal("73 °C", row.TemperatureText);
    }

    [Fact]
    public async Task ATypedTemperatureIsClampedToTheRange()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);
        var row = vm.ReactiveStops[1];

        vm.SetReactiveStopTemperature(row, "500");
        Assert.Equal(ReactiveLighting.MaxStopCelsius, row.AtCelsius);

        vm.SetReactiveStopTemperature(row, "5");
        Assert.Equal(ReactiveLighting.MinStopCelsius, row.AtCelsius);
    }

    [Fact]
    public async Task AHalfTypedNumber_IsIgnored()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);
        var row = vm.ReactiveStops[1];
        var before = row.AtCelsius;

        // A box that committed on every keystroke could not be typed in: "7" would clamp to the cold end before the
        // "2" arrived.
        vm.SetReactiveStopTemperature(row, "");
        vm.SetReactiveStopTemperature(row, "   ");
        vm.SetReactiveStopTemperature(row, "abc");
        vm.SetReactiveStopTemperature(row, null);

        Assert.Equal(before, row.AtCelsius);
    }

    [Fact]
    public async Task ADecimalWithAComma_IsUnderstood()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);
        var row = vm.ReactiveStops[1];

        // A Turkish keyboard's decimal separator is a comma.
        vm.SetReactiveStopTemperature(row, "73,5");

        Assert.Equal(73.5, row.AtCelsius);
    }

    [Fact]
    public async Task TheRampPreviewFollowsTheEditor()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);
        vm.ReactiveStops[0].Color = "#123456";

        // The picture above the rows is what the keyboard would show while it is edited, so it has to be built from
        // the same stops rather than from the profile the editor started on.
        Assert.Equal("#123456", vm.CustomRampPreview[0].Color);
        Assert.Equal(vm.ReactiveStops.Count, vm.CustomRampPreview.Count);
    }

    // =============================================================================================================
    // 6. The settings file.
    // =============================================================================================================

    [Fact]
    public void TheSensorsAndTheTwoLevelsSurviveARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "nitrolite-sensors-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings
            {
                ReactiveLightingUseCpu = false,
                ReactiveLightingUseGpu = true,
                ReactiveLightingBrightness = 4,
                ChargingColorBrightness = 8
            };

            SettingsManager.SaveSettingsAtomic(path, settings);
            var reloaded = SettingsManager.LoadSettingsWithFallback(path);

            Assert.False(reloaded.ReactiveLightingUseCpu);
            Assert.True(reloaded.ReactiveLightingUseGpu);
            Assert.Equal(4, reloaded.ReactiveLightingBrightness);
            Assert.Equal(8, reloaded.ChargingColorBrightness);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void AFreshFile_KeepsTheShippedDefaults()
    {
        var fresh = new AppSettings();

        // A file written by a build that had never heard of the sensors must not leave both of them off, and neither
        // mode may start at the firmware's "stay dark".
        Assert.True(fresh.ReactiveLightingUseCpu);
        Assert.False(fresh.ReactiveLightingUseGpu);
        Assert.Equal(6, fresh.ReactiveLightingBrightness);
        Assert.Equal(6, fresh.ChargingColorBrightness);
    }

    [Fact]
    public void AStoredZeroBrightness_ComesBackAtTheFloor()
    {
        var settings = new AppSettings { ReactiveLightingBrightness = 0, ChargingColorBrightness = -3 };

        settings.Normalize();

        Assert.Equal(ChargingLighting.MinBrightness, settings.ReactiveLightingBrightness);
        Assert.Equal(ChargingLighting.MinBrightness, settings.ChargingColorBrightness);
    }

    // =============================================================================================================
    // 7. The page itself.
    // =============================================================================================================

    [Fact]
    public void TheCardCarriesTheSensorsTheTwoLevelsAndTheEditableStop()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml"));

        Assert.Contains("ReactiveSensorCpu", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveSensorGpu", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveSplit", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveBrightness", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveBrightnessLabel", markup, StringComparison.Ordinal);
        Assert.Contains("ChargingBrightness", markup, StringComparison.Ordinal);
        Assert.Contains("CustomRampPreview", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveStopTemperature", markup, StringComparison.Ordinal);
        Assert.Contains("OnReactiveStopTemperatureKeyDown", markup, StringComparison.Ordinal);
        Assert.Contains("OnReactiveStopTemperatureLostFocus", markup, StringComparison.Ordinal);

        // The sensors sit under the temperature card, not under the charging one: the charging colour follows the plug
        // and has no sensor to choose.
        var sensors = markup.IndexOf("ReactiveSensorCpu", StringComparison.Ordinal);
        var charging = markup.IndexOf("Labels.ChargingColor", StringComparison.Ordinal);
        Assert.True(sensors > 0 && sensors < charging, "the sensors are not in the temperature card");
    }

    [Fact]
    public void TheSegmentedChipsHaveAStyleOfTheirOwn()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "App.axaml"));

        // A button with a class that no style answers renders as the theme's default button, which is a different shape
        // and colour from every other segmented control on the page. The chips are plain buttons carrying the chosen
        // state as a class: a ToggleButton owns its own checked state, and a click on the chip that was already down had
        // to be undone again, which is how the chosen chip came out drawn as the one chip without a frame.
        Assert.Contains("Button.seg\"", app, StringComparison.Ordinal);
        Assert.Contains("Button.seg.selected", app, StringComparison.Ordinal);
        Assert.Contains("Button.seg /template/ ContentPresenter", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleButton.seg", app, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // harness
    // =============================================================================================================

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-reactive-sensor-" + Guid.NewGuid().ToString("N") + ".json");

    private static MainViewModel Create(
        Phase3Fakes.StubHardware hardware,
        AppSettings? settings = null,
        Func<(bool OnAc, int? BatteryPercent)>? powerState = null) =>
        new(settings ?? new AppSettings(), hardware, null, TempSettingsPath(),
            powerState: powerState ?? (() => (false, 50)));

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
}
