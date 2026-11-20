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
/// The four changes a split keyboard needed: a slider per fan, a ramp per half, a charger state that survives the 80 %
/// limit, and a default face the first launch is actually drawn in.
/// </summary>
/// <remarks>
/// Each of these was a defect rather than a missing feature — the graphics fan ignored its own slider, both halves were
/// painted by one ramp, the charging colour had no notion of a capped pack, and the first launch depended on a fallback
/// rather than on a stored default — so the tests are written as "the thing the user did reaches the thing they expect".
/// </remarks>
public class RampTargetAndAdapterTests
{
    // =============================================================================================================
    // 1. The graphics fan has its own slider.
    // =============================================================================================================

    [Fact]
    public async Task TheGraphicsSliderReachesTheGraphicsFan()
    {
        var hardware = new Phase3Fakes.StubHardware();
        var settings = new AppSettings();
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(),
            powerState: () => (false, 50));

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await Phase3Fakes.WaitUntilAsync(() => hardware.FanWrites.Count > 0);

        hardware.FanWrites.Clear();

        // Two different numbers, both above the thermal floor for a 58 °C die: the write has to carry each to its own fan.
        vm.CustomCpuPercent = 64;
        vm.CustomGpuPercent = 37;

        await Phase3Fakes.WaitUntilAsync(() =>
            hardware.FanWrites.Exists(w => w.CpuPercent == 64 && w.GpuPercent == 37));

        // The stored pair follows the sliders, so a restart opens on what the user set.
        Assert.Equal(64, settings.CustomFanCpuPercent);
        Assert.Equal(37, settings.CustomFanGpuPercent);
    }

    [Fact]
    public async Task TheTwoSliders_AreNotOneSliderTwice()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath(),
            powerState: () => (false, 50));

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await Phase3Fakes.WaitUntilAsync(() => hardware.FanWrites.Count > 0);

        hardware.FanWrites.Clear();

        // Only the graphics slider moves. The processor's fan must stay where it was, which is the whole bug: the write
        // used to send the processor's percentage twice.
        int cpu = vm.CustomCpuPercent;
        vm.CustomGpuPercent = 31;

        // Wait for the write that carries the new number rather than for any write: the mode change above is still
        // allowed to land, and a test that took the last write of the pair would be racing it.
        await Phase3Fakes.WaitUntilAsync(() => hardware.FanWrites.Exists(w => w.GpuPercent == 31));

        var write = hardware.FanWrites.Last(w => w.GpuPercent == 31);
        Assert.Equal(cpu, write.CpuPercent);
    }

    // =============================================================================================================
    // 2. One ramp per half.
    // =============================================================================================================

    [Fact]
    public void TwoRamps_PaintOneKeyboard()
    {
        var warm = new[]
        {
            new ReactiveLightingStop { AtCelsius = 40, Color = "#FF0000" },
            new ReactiveLightingStop { AtCelsius = 90, Color = "#FF0000" }
        };
        var cool = new[]
        {
            new ReactiveLightingStop { AtCelsius = 40, Color = "#0000FF" },
            new ReactiveLightingStop { AtCelsius = 90, Color = "#0000FF" }
        };

        var pair = ReactiveLighting.ColorsForSensors(warm, cool, 40, 40, useCpu: true, useGpu: true);

        // Same temperature, two ramps: the split is visible even when both sensors agree on the number, which is what
        // made the single ramp look broken.
        Assert.True(pair.HasValue);
        Assert.Equal("#FF0000", pair!.Value.Left);
        Assert.Equal("#0000FF", pair.Value.Right);
    }

    [Fact]
    public void OneRampStillPaintsTheWholeKeyboard()
    {
        var pair = ReactiveLighting.ColorsForSensors(null, 58, 95, useCpu: true, useGpu: true);

        // The single-ramp call is the one every existing caller uses, and it still means "the same ramp for both halves".
        Assert.True(pair.HasValue);
        Assert.Equal(ReactiveLighting.ColorFor(58), pair!.Value.Left);
        Assert.Equal(ReactiveLighting.ColorFor(95), pair.Value.Right);
    }

    [Fact]
    public async Task TheGraphicsHalfCanFollowItsOwnRamp()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 45;
        hardware.Sample.GpuTemperature = 45;

        var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = true };
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(),
            powerState: () => (false, 50));

        await vm.InitializeAsync();
        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        hardware.LightingWrites.Clear();

        // Both sensors are on, and the ramp card is pointed at a half by that half's own tile: switching the graphics
        // tile off and on again leaves both halves painting and hands the card to the graphics ramp.
        vm.ReactiveGpuSensor = false;
        await vm.ReactiveLightingTask;
        vm.ReactiveGpuSensor = true;
        await vm.ReactiveLightingTask;

        Assert.True(vm.ReactiveGpuSensor);
        Assert.True(vm.ReactiveRampTargetGpu);
        hardware.LightingWrites.Clear();

        // The graphics half gets the custom entry, and its cold end is given a colour no processor ramp uses.
        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);
        var coldest = vm.ReactiveStops[0];
        vm.ApplyReactiveStopColor(coldest, "#123456");

        Assert.Equal("#123456", vm.ReactiveStops[0].Color);

        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        var write = Assert.Single(hardware.LightingWrites);
        var zones = write.Split('|')[^1].Split(',');

        // Left two zones are the processor's ramp at 45 °C, right two the graphics ramp the user just built.
        Assert.Equal(4, zones.Length);
        Assert.Equal(ReactiveLighting.ColorFor(45), zones[0]);
        Assert.Equal(ReactiveLighting.ColorFor(45), zones[1]);
        Assert.Equal("#123456", zones[2]);
        Assert.Equal("#123456", zones[3]);

        // And the processor's own ramp was not touched by editing the graphics one.
        Assert.NotEqual("#123456", settings.ReactiveLightingCustomStops?[0].Color);
        Assert.Equal("#123456", settings.ReactiveLightingCustomStopsGpu?[0].Color);
    }

    [Fact]
    public async Task TheGraphicsHalfInheritsUntilItIsGivenItsOwnRamp()
    {
        var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = false };

        using var vm = new MainViewModel(settings, new Phase3Fakes.StubHardware(), null, TempSettingsPath(),
            powerState: () => (false, 50));

        await vm.InitializeAsync();

        // No graphics ramp in the file: the tile still works, it both enables the half and points the card at it, and
        // the picker opens on the processor's ramp. Nothing is written to the graphics keys until a ramp is chosen.
        Assert.False(vm.ReactiveRampTargetGpu);
        vm.ReactiveGpuSensor = true;

        Assert.True(vm.ReactiveGpuSensor);
        Assert.True(vm.ReactiveRampTargetGpu);
        Assert.Null(settings.ReactiveLightingProfileGpu);
        Assert.NotNull(vm.SelectedReactiveProfile);
    }

    [Fact]
    public async Task TheRampCaption_NamesTheHalfTheCardIsEditing()
    {
        var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = true };

        using var vm = new MainViewModel(settings, new Phase3Fakes.StubHardware(), null, TempSettingsPath(),
            powerState: () => (false, 50));

        await vm.InitializeAsync();

        // Both halves live: the caption carries the half the chips and the stop rows below it belong to, which is where
        // the CPU/GPU chips used to say it. Opening the app edits the processor's ramp.
        Assert.Equal($"{vm.Labels.ReactiveProfiles} · CPU", vm.ReactiveRampCaption);

        // With one sensor there is nothing to disambiguate, so the title stands alone.
        vm.ReactiveGpuSensor = false;
        Assert.Equal(vm.Labels.ReactiveProfiles, vm.ReactiveRampCaption);

        // And the tile that switches a half on is the tile that hands the card to it.
        vm.ReactiveGpuSensor = true;
        Assert.Equal($"{vm.Labels.ReactiveProfiles} · GPU", vm.ReactiveRampCaption);
    }

    [Fact]
    public async Task TheRampTarget_FallsBackToTheHalfThatStillPaints()
    {
        var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = true };

        using var vm = new MainViewModel(settings, new Phase3Fakes.StubHardware(), null, TempSettingsPath(),
            powerState: () => (false, 50));

        await vm.InitializeAsync();

        vm.ReactiveGpuSensor = false;
        vm.ReactiveGpuSensor = true;
        Assert.True(vm.ReactiveRampTargetGpu);

        // Switching the graphics half off leaves one ramp on the keyboard, so the card returns to it rather than
        // editing a ramp nothing follows.
        vm.ReactiveGpuSensor = false;
        Assert.False(vm.ReactiveRampTargetGpu);

        // The processor's half going away with the graphics half still live hands the card to the graphics ramp.
        vm.ReactiveGpuSensor = true;
        vm.ReactiveCpuSensor = false;
        Assert.True(vm.ReactiveRampTargetGpu);
    }

    [Fact]
    public async Task TheRampTarget_IsStoredPerHalfAndReadBackOnTheNextLaunch()
    {
        var path = TempSettingsPath();

        try
        {
            var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = true };

            using (var vm = new MainViewModel(settings, null, null, path, powerState: () => (false, 50)))
            {
                // The startup read fills the controls with the machine's own state; until it has, every setter is
                // suppressed so the window cannot write its shipped defaults back to the firmware.
                await vm.InitializeAsync();

                // Both halves are live; tapping the graphics tile off and on is how the ramp card is pointed at it.
                vm.ReactiveGpuSensor = false;
                vm.ReactiveGpuSensor = true;
                Assert.True(vm.ReactiveRampTargetGpu);

                vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.Id == "inferno");

                Assert.Equal("inferno", settings.ReactiveLightingProfileGpu);
            }

            var reloaded = SettingsManager.LoadSettingsWithFallback(path);
            Assert.Equal("inferno", reloaded.ReactiveLightingProfileGpu);

            // The processor's half kept the ramp it had, which is the point of two settings rather than one.
            Assert.NotEqual("inferno", reloaded.ReactiveLightingProfile);
        }
        finally
        {
            Delete(path);
        }
    }

    // =============================================================================================================
    // 3. The adapter colour survives the 80 % charge limit.
    // =============================================================================================================

    [Theory]
    [InlineData(true, 55, false, true, false)]
    [InlineData(true, 80, true, true, true)]
    [InlineData(true, 100, false, true, false)]
    [InlineData(false, 55, false, false, false)]
    public void TheAdapterState_KnowsTheChargerFromTheChargeCurrent(
        bool onAc, int percent, bool limitOn, bool expectOnAdapter, bool expectAtLimit)
    {
        var state = ChargingLighting.Read(onAc, percent, limitOn);

        Assert.Equal(expectOnAdapter, state.OnAdapter);
        Assert.Equal(expectAtLimit, state.AtChargeLimit);

        // The colour follows the adapter whatever the pack is doing about it.
        Assert.Equal(expectOnAdapter, ChargingLighting.ShouldPaint(state));

        if (expectAtLimit) Assert.False(state.Charging);
    }

    [Theory]
    [InlineData(true, 55, false, true)]
    [InlineData(true, 79, true, true)]
    [InlineData(true, 80, true, false)]
    [InlineData(true, 100, false, false)]
    [InlineData(false, 55, false, false)]
    public void Charging_IsOnlyTrueWhileThePackIsActuallyTakingACharge(
        bool onAc, int percent, bool limitOn, bool expectCharging)
    {
        // A capped machine at 80 % and a full machine at 100 % both report "plugged in, not charging"; only the pack
        // below its limit is charging, and the colour follows the plug either way.
        var state = ChargingLighting.Read(onAc, percent, limitOn);

        Assert.Equal(expectCharging, state.Charging);
        Assert.Equal(onAc, ChargingLighting.ShouldPaint(state));
    }

    [Fact]
    public void AnUnknownAdapter_IsNeitherAChargerNorABattery()
    {
        // Null is "the platform cannot say". A machine with no adapter node would otherwise paint the charging colour
        // for the rest of its life, so the colour waits — the same answer the page gave before the state was modelled.
        var state = ChargingLighting.Read(null, null, false);
        Assert.False(state.OnAdapter);
        Assert.False(state.AtChargeLimit);
        Assert.False(ChargingLighting.ShouldPaint(state));
    }

    [Fact]
    public async Task ACappedPack_StillPaintsTheAdapterColour()
    {
        var hardware = new Phase3Fakes.StubHardware();
        var settings = new AppSettings();
        bool onAdapter = true;

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(),
            powerState: () => (onAdapter, 80));

        await vm.InitializeAsync();

        // The 80 % limit is armed, so the pack rests at the cap and the firmware reports "plugged in, not charging".
        vm.BatteryLimitOn = true;
        await vm.RefreshTelemetryAsync();

        hardware.LightingWrites.Clear();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;

        // The keys still show the adapter colour: the plug is what the mode is about.
        var write = Assert.Single(hardware.LightingWrites);
        Assert.Contains(ChargingLighting.DefaultColor, write, StringComparison.Ordinal);

        // And the card says which of the two adapter situations this is rather than claiming a charge.
        Assert.Equal(vm.Labels.ChargingColorAtLimit(ChargingLighting.ChargeLimitPercent), vm.ChargingColorStateText);

        // Unplugging still hands the keyboard back.
        hardware.LightingWrites.Clear();
        onAdapter = false;
        await vm.RefreshTelemetryAsync();
        await vm.ChargingLightingTask;

        Assert.DoesNotContain(ChargingLighting.DefaultColor,
            Assert.Single(hardware.LightingWrites), StringComparison.Ordinal);
        Assert.Equal(vm.Labels.ChargingColorWaiting, vm.ChargingColorStateText);
    }

    [Fact]
    public async Task AChargingPack_GetsThePlainAdapterSentence()
    {
        using var vm = new MainViewModel(new AppSettings(), new Phase3Fakes.StubHardware(), null, TempSettingsPath(),
            powerState: () => (true, 42));

        await vm.InitializeAsync();

        Assert.Equal(vm.Labels.ChargingColorActive, vm.ChargingColorStateText);
    }

    [Fact]
    public async Task TheTwoAutomaticColours_StayMutuallyExclusive()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath(),
            powerState: () => (true, 60));

        await vm.InitializeAsync();

        vm.ChargingColorEnabled = true;
        Assert.True(vm.ChargingColorEnabled);

        // Asking for the temperature colour is asking for the other one to stand down: two modes cannot own four zones.
        vm.ReactiveLightingEnabled = true;
        Assert.False(vm.ChargingColorEnabled);

        vm.ChargingColorEnabled = true;
        Assert.False(vm.ReactiveLightingEnabled);
    }

    // =============================================================================================================
    // 4. Quicksand is the stored default, not a fallback.
    // =============================================================================================================

    [Fact]
    public void AFreshSettingsObjectNamesTheDefaultFace()
    {
        // The first launch has to be drawn in Quicksand because the settings say so, not because a null happened to
        // resolve to the first entry in the catalogue.
        Assert.Equal(UiFontCatalog.DefaultId, new AppSettings().UiFont);
        Assert.Equal(UiFontCatalog.BundledSans, UiFontCatalog.Resolve(new AppSettings().UiFont).Stack.Split(',')[0]);
    }

    [Fact]
    public void ASettingsFileFromBeforeTheKeyExisted_StillOpensInQuicksand()
    {
        var path = TempSettingsPath();

        try
        {
            File.WriteAllText(path, "{ \"theme\": \"WarmCream\" }");

            var settings = SettingsManager.LoadSettingsWithFallback(path);
            Assert.Equal(UiFontCatalog.DefaultId, UiFontCatalog.Resolve(settings.UiFont).Id);
        }
        finally
        {
            Delete(path);
        }
    }

    // =============================================================================================================
    // 5. The page itself.
    // =============================================================================================================

    [Fact]
    public void TheTemperatureTiles_AreTheSensorSwitches()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml"));

        // The row of chips that said the same two words again is gone, and the tiles carry the switches instead.
        Assert.DoesNotContain("Labels.ReactiveSensors}", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveCpuReading", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveGpuReading", markup, StringComparison.Ordinal);
        // One-way on purpose: a two-way tile cannot tell "point the card at this half" from "switch this half off",
        // so the click goes through the view model, which knows how many halves are live and which one the card is on.
        Assert.Contains("IsChecked=\"{Binding ReactiveCpuSensor, Mode=OneWay}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding ReactiveGpuSensor, Mode=OneWay}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnReactiveCpuTile\"", markup, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnReactiveGpuTile\"", markup, StringComparison.Ordinal);
        Assert.Contains("Classes=\"tempchip\"", markup, StringComparison.Ordinal);

        // And the tiles are on screen whether or not the mode is on: they are the only control that picks a sensor, and
        // hiding them behind the switch meant the choice could only be made after the mode was already running.
        var tiles = markup.IndexOf("AutomationProperties.AutomationId=\"ReactiveSensorCpu\"", StringComparison.Ordinal);
        var gate = markup.LastIndexOf("IsVisible=\"{Binding ReactiveLightingEnabled}\"", tiles, StringComparison.Ordinal);
        Assert.True(gate < 0 || markup.IndexOf("</StackPanel>", gate, StringComparison.Ordinal) < tiles,
            "the sensor tiles must not be inside a ReactiveLightingEnabled panel");

        // The ramp card can be pointed at either half, which is what lets the two halves look different — and the only
        // control that names a half is the tile above, so the chips that repeated it are gone for good.
        Assert.DoesNotContain("ReactiveRampTargetCpu", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ReactiveRampTargetGpu", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveRampCaption", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTileClick_IsDecidedByTheViewModel()
    {
        var codeBehind = File.ReadAllText(
            Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml.cs"));

        // The handler is the one place that knows a click on a live half means "point the card at it".
        Assert.Contains("ToggleReactiveSensor", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTemperatureTileStyle_DimsWhatIsOff()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "App.axaml"));

        // A ToggleButton with a class no style answers draws as the theme's default button; the dimmed state is the
        // selected/unselected signal the report asked for.
        Assert.Contains("ToggleButton.tempchip", app, StringComparison.Ordinal);
        Assert.Contains("ToggleButton.tempchip:checked", app, StringComparison.Ordinal);
        Assert.Contains("ToggleButton.tempchip:disabled", app, StringComparison.Ordinal);

        // The dimming reaches the figure as well, so the half that is off reads as greyed out rather than as a lighter
        // version of the half that is on.
        Assert.Contains("ToggleButton.tempchip TextBlock.figurebig", app, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // harness
    // =============================================================================================================

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-ramp-target-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A test that cannot clean up is still a test that passed.
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
}
