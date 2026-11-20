using System;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The two places where the lighting page tells the user something without writing it: the four bars that stand in for
/// the keyboard, and the temperature tiles that are both the sensor switch and the pointer at the half being edited.
/// </summary>
/// <remarks>
/// Both are read-only pictures of state the page already holds, which is exactly why they can be wrong without a test
/// failing anywhere else: the write goes to the keyboard, the picture says something else, and nobody notices until a
/// user does. These tests compare the picture with the payload.
/// </remarks>
public class DeckPreviewAndTileClickTests
{
    // =============================================================================================================
    // 1. The deck.
    // =============================================================================================================

    [Fact]
    public async Task TheDeckShowsTheTemperatureColourWhileThatModeOwnsTheKeys()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 78;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        var painted = vm.ReactiveLightingColor;

        // One sensor paints the whole keyboard, so all four bars carry the one colour — and it is the colour the write
        // used, not the palette's. The page used to draw the palette here, so a keyboard painting orange for 78 °C sat
        // under a deck showing the user's saved blue.
        Assert.All(Bars(vm), bar => Assert.Equal(painted, bar, ignoreCase: true));
        Assert.True(vm.ZonePreviewAutomatic);
        Assert.Contains(vm.Labels.ZonePreviewAutomatic, vm.ZonePreviewNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeckSplitsLeftAndRightOnTheSensorBoundary()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 52;
        hardware.Sample.GpuTemperature = 84;

        var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = true };

        using var vm = Create(hardware, settings);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        var bars = Bars(vm);

        // Zones 1 and 2 are the processor's half and 3 and 4 the graphics chip's. The two halves are twenty-odd degrees
        // apart, so their colours differ: a bar on the wrong side of the split is a bar the payload does not match.
        Assert.Equal(bars[0], bars[1], ignoreCase: true);
        Assert.Equal(bars[2], bars[3], ignoreCase: true);
        Assert.False(string.Equals(bars[0], bars[2], StringComparison.OrdinalIgnoreCase));
        Assert.Equal(vm.ReactiveLightingColor, bars[0], ignoreCase: true);
        Assert.Equal(vm.ReactiveLightingColorRight, bars[3], ignoreCase: true);
    }

    [Fact]
    public async Task TheDeckFallsBackToThePaletteWhenNoAutomaticModePaints()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        Assert.False(vm.ReactiveLightingEnabled);
        Assert.False(vm.ZonePreviewAutomatic);
        Assert.Equal("", vm.ZonePreviewNote);

        var bars = Bars(vm);
        Assert.Equal(vm.Zone1, bars[0]);
        Assert.Equal(vm.Zone2, bars[1]);
        Assert.Equal(vm.Zone3, bars[2]);
        Assert.Equal(vm.Zone4, bars[3]);
    }

    [Fact]
    public async Task TheDeckShowsTheChargingColourWhileTheAdapterIsIn()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware, powerState: () => (true, 55));
        await vm.InitializeAsync();

        vm.ChargingColor = "#F59E0B";
        vm.ChargingColorEnabled = true;
        await vm.RefreshTelemetryAsync();

        // The charging colour is the second automatic mode, and the deck answers "what is on the keys now" for both.
        Assert.All(Bars(vm), bar => Assert.Equal("#F59E0B", bar, ignoreCase: true));
        Assert.True(vm.ZonePreviewAutomatic);

        // Unplugged it paints nothing, so the deck goes back to the palette on the next tick.
        using var onBattery = Create(new Phase3Fakes.StubHardware(), powerState: () => (false, 55));
        await onBattery.InitializeAsync();
        onBattery.ChargingColor = "#F59E0B";
        onBattery.ChargingColorEnabled = true;
        await onBattery.RefreshTelemetryAsync();

        Assert.Equal(onBattery.Zone1, Bars(onBattery)[0]);
        Assert.False(onBattery.ZonePreviewAutomatic);
    }

    // =============================================================================================================
    // 2. The sensor switches.
    // =============================================================================================================

    [Fact]
    public async Task SwitchingAHalfOn_HandsTheRampCardToIt()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        Assert.True(vm.ReactiveCpuSensor);
        Assert.False(vm.ReactiveGpuSensor);
        Assert.True(vm.ReactiveRampForCpu);

        // The switch does one thing: it paints with that half. Pointing the ramp card at the half is what follows from
        // lighting it, because a half that is on is the half the user just looked at — and it is said by the chips in
        // the ramp header rather than by the switch, so one control never carries two answers.
        vm.ReactiveGpuSensor = true;

        Assert.True(vm.ReactiveGpuSensor);
        Assert.True(vm.ReactiveCpuSensor);
        Assert.True(vm.ReactiveRampForGpu);
        Assert.False(vm.ReactiveRampForCpu);
    }

    [Fact]
    public async Task SwitchingAHalfOff_LeavesTheCardOnTheHalfThatStillPaints()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveGpuSensor = true;
        Assert.True(vm.ReactiveRampForGpu);

        // The graphics half goes dark: one ramp is left on the keyboard, so the card edits that one rather than a ramp
        // nothing follows.
        vm.ReactiveGpuSensor = false;

        Assert.False(vm.ReactiveGpuSensor);
        Assert.True(vm.ReactiveRampForCpu);
        Assert.Equal(vm.Labels.ReactiveProfiles, vm.ReactiveRampCaption);
    }

    // =============================================================================================================
    // 3. The switch and the settings file cannot disagree.
    // =============================================================================================================

    [Fact]
    public async Task AManualApplyLeavesBothAutomaticModesOffOnDisk()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 74;
        var path = Phase3Fakes.TempSettingsPath();
        var settings = new AppSettings();

        using var vm = new MainViewModel(settings, hardware, null, path, powerState: () => (true, 60));
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        Assert.True(vm.ReactiveLightingEnabled);

        // Reaching for a colour is the user taking the keys back: the write carries their colour and the two automatic
        // modes stand down. What the file said afterwards was the bug — the page showed both switches off while
        // settings.json still claimed the temperature ramp was on, so the next launch came back to a mode nobody chose.
        vm.ApplyLightingCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => !vm.ReactiveLightingEnabled);

        var reloaded = SettingsManager.LoadSettingsWithFallback(path);
        Assert.False(reloaded.ReactiveLightingEnabled);
        Assert.False(reloaded.ChargingColorEnabled);
        Assert.False(vm.ChargingColorEnabled);
    }

    // =============================================================================================================
    // fixtures
    // =============================================================================================================

    private static string[] Bars(MainViewModel vm) =>
        new[] { vm.Zone1Preview, vm.Zone2Preview, vm.Zone3Preview, vm.Zone4Preview };

    private static MainViewModel Create(
        Phase3Fakes.StubHardware hardware,
        AppSettings? settings = null,
        Func<(bool OnAc, int? BatteryPercent)>? powerState = null) =>
        new(settings ?? new AppSettings(), hardware, null, Phase3Fakes.TempSettingsPath(),
            powerState: powerState ?? (() => (false, 50)));
}
