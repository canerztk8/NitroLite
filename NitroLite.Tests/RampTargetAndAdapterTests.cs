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
    public async Task TheRampHeader_CarriesTheHalfTheCardIsEditing()
    {
        var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = true };

        using var vm = new MainViewModel(settings, new Phase3Fakes.StubHardware(), null, TempSettingsPath(),
            powerState: () => (false, 50));

        await vm.InitializeAsync();

        // The title is only a title: the half is named by the two chips in the same row, so a suffix would say it twice.
        Assert.Equal(vm.Labels.ReactiveProfiles, vm.ReactiveRampCaption);

        // Opening the app edits the processor's ramp, and its chip is the one drawn as selected.
        Assert.True(vm.ReactiveRampForCpu);
        Assert.False(vm.ReactiveRampForGpu);

        // The chips are the control: picking one moves the editor and nothing else.
        vm.ReactiveRampForGpu = true;

        Assert.True(vm.ReactiveRampTargetGpu);
        Assert.True(vm.ReactiveRampForGpu);
        Assert.False(vm.ReactiveRampForCpu);
        Assert.True(vm.ReactiveGpuSensor);
        Assert.True(vm.ReactiveCpuSensor);

        // Clicking the chip that is already selected is what a ToggleButton does before the setter runs: it arrives as
        // false. Neither value may leave the pair with no chip selected, because there is always a half being edited.
        vm.ReactiveRampForGpu = false;
        Assert.True(vm.ReactiveRampForGpu);
        Assert.False(vm.ReactiveRampForCpu);

        // Switching the processor's half on hands the card back to it, and the chips follow without being touched.
        vm.ReactiveCpuSensor = false;
        vm.ReactiveCpuSensor = true;

        Assert.True(vm.ReactiveRampForCpu);
        Assert.False(vm.ReactiveRampForGpu);
    }

    [Fact]
    public async Task TheSplitChips_BehaveLikeARadioPair()
    {
        var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = true };

        using var vm = new MainViewModel(settings, new Phase3Fakes.StubHardware(), null, TempSettingsPath(),
            powerState: () => (false, 50));

        await vm.InitializeAsync();

        // One of the two shapes is always selected.
        Assert.True(vm.ReactiveSplitEven);
        Assert.False(vm.ReactiveSplitBlend);

        vm.ReactiveSplitBlend = true;
        Assert.True(vm.ReactiveSplitBlend);
        Assert.False(vm.ReactiveSplitEven);

        // A false has nowhere to go, because one shape is always chosen: the write only re-announces both sides.
        vm.ReactiveSplitBlend = false;
        Assert.True(vm.ReactiveSplitBlend);
        Assert.False(vm.ReactiveSplitEven);

        vm.ReactiveSplitEven = true;
        Assert.True(vm.ReactiveSplitEven);
        Assert.False(vm.ReactiveSplitBlend);

        vm.ReactiveSplitEven = false;
        Assert.True(vm.ReactiveSplitEven);
        Assert.False(vm.ReactiveSplitBlend);

        // What the chips on the page actually do: a command, not a toggle. The row shows the pair by reading it back,
        // so no click can leave it with neither shape drawn.
        vm.SelectReactiveSplitBlendCommand.Execute(null);
        Assert.True(vm.ReactiveSplitBlend);
        Assert.False(vm.ReactiveSplitEven);

        vm.SelectReactiveSplitEvenCommand.Execute(null);
        Assert.True(vm.ReactiveSplitEven);
        Assert.False(vm.ReactiveSplitBlend);
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
    public void TheSensorRows_CarryTheirOwnSwitch()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml"));

        // The reading is a reading again. The tile that used to be both the switch and the pointer at the ramp card is
        // gone, which is what made a click on a live half look like a bug: it meant "point here" once and "switch this
        // off" the next time, and nothing on screen said which.
        Assert.DoesNotContain("Classes=\"tempchip\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ReactiveCpuSensor, Mode=OneWay", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("OnReactiveCpuTile", markup, StringComparison.Ordinal);

        Assert.Contains("ReactiveCpuReading", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveGpuReading", markup, StringComparison.Ordinal);

        // One row per sensor, each with a plain switch bound to that sensor. The switch is the only thing that decides
        // whether the half paints, and it says so by itself.
        Assert.Contains("Classes=\"sensor\"", markup, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ReactiveSensorCpu\"", markup, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ReactiveSensorGpu\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding ReactiveCpuSensor}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding ReactiveGpuSensor}\"", markup, StringComparison.Ordinal);

        // The dimming of the half that is off reaches the figure as well.
        Assert.Contains("Classes.off=\"{Binding !ReactiveCpuSensor}\"", markup, StringComparison.Ordinal);

        // The ramp card names its half in its own header, and both the ramp chips and the split chips stay on the page
        // while there is only one sensor: a row that comes and goes moves everything under it.
        Assert.Contains("AutomationProperties.AutomationId=\"ReactiveRampForCpu\"", markup, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ReactiveRampForGpu\"", markup, StringComparison.Ordinal);
        Assert.Contains("Classes.selected=\"{Binding ReactiveRampForGpu}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SelectReactiveRampForGpuCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SelectReactiveSplitBlendCommand}\"", markup, StringComparison.Ordinal);

        // No ToggleButton wears the segmented clothes: a toggle owns its own checked state, and a click on the chip
        // that is already down had to be undone again, which is how the chosen chip came out drawn as unchosen.
        Assert.DoesNotContain("ToggleButton Classes=\"seg\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding ReactiveSplit}\"", markup, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ReactiveSplitEven\"", markup, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ReactiveSplitBlend\"", markup, StringComparison.Ordinal);

        // And the brightness slider is on the card whether the mode is on or not: it is the control the user comes here
        // for, so it is disabled while the mode is off rather than hidden.
        var slider = markup.IndexOf("AutomationProperties.AutomationId=\"ReactiveBrightness\"", StringComparison.Ordinal);
        Assert.True(slider > 0, "the brightness slider is missing from the card");
        var gate = markup.LastIndexOf("IsVisible=\"{Binding ReactiveLightingEnabled}\"", slider, StringComparison.Ordinal);
        Assert.True(gate < 0 || markup.IndexOf("</StackPanel>", gate, StringComparison.Ordinal) < slider,
            "the brightness slider must not be inside a ReactiveLightingEnabled panel");

        // Nothing else gates it: the fade is a look, not a lock.
        var row = markup.LastIndexOf("<StackPanel", slider, StringComparison.Ordinal);
        Assert.True(row > 0, "the brightness slider is not inside a panel");
        var opening = markup[row..markup.IndexOf('>', row)];
        Assert.Contains("IsEnabled=\"{Binding ReactiveLightingEnabled}\"", opening, StringComparison.Ordinal);

        // While the mode is off the row fades as a whole: a slider draws its track from the same resources whether it
        // takes input or not, so a full-strength track with a dead thumb reads as broken rather than as paused.
        Assert.Contains("Classes=\"gated\"", opening, StringComparison.Ordinal);
        Assert.Contains("Classes.off=\"{Binding !ReactiveLightingEnabled}\"", opening, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGatedRow_FadesWhileItsModeIsOff()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "App.axaml"));

        // A row wearing a class that no style answers looks exactly like an enabled one. Fluent fades its own buttons
        // when they are disabled, but a slider draws its track the same way either way, so the row carries the fade
        // itself and the accent figure inside it fades with the rest.
        // A class, not a property selector: [IsEnabled=False] names a property on a type that does not own it, so
        // the selector resolved to nothing and the row never faded at all.
        var gated = StyleBlock(app, "StackPanel.gated.off\"");
        Assert.Contains("Opacity", gated, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSensorRowStyle_DimsWhatIsOff()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "App.axaml"));

        // A Border with a class no style answers draws as nothing at all, so the row style has to exist for the page to
        // look like a page.
        Assert.Contains("Border.sensor", app, StringComparison.Ordinal);
        Assert.Contains("Border.sensor.off", app, StringComparison.Ordinal);
        Assert.Contains("Border.sensor.off TextBlock.figurebig", app, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChosenSegment_CarriesAFrame()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "App.axaml"));

        // The chosen chip has to be unmistakable: the ink frame, the ink text and a wash of the ink, on the chip and
        // on the presenter inside it. Fluent's own button styles that presenter directly, and a style on the element
        // outranks the template binding that carries the chip's own brush into it, so the wash has to be set in both
        // places or the chosen chip is the one chip in the row without one.
        var checkedStyle = StyleBlock(app, "Button.seg.selected\"");
        Assert.Contains("BorderBrush\" Value=\"{DynamicResource AccentBrush}\"", checkedStyle, StringComparison.Ordinal);
        Assert.Contains("Foreground\" Value=\"{DynamicResource AccentBrush}\"", checkedStyle, StringComparison.Ordinal);
        Assert.Contains("FontWeight\" Value=\"Bold\"", checkedStyle, StringComparison.Ordinal);

        var checkedPresenter = StyleBlock(app, "Button.seg.selected /template/ ContentPresenter\"");
        Assert.Contains("{DynamicResource AccentSoftBrush}", checkedPresenter, StringComparison.Ordinal);
        Assert.Contains("BorderBrush\" Value=\"{DynamicResource AccentBrush}\"", checkedPresenter, StringComparison.Ordinal);
    }

    /// <summary>The body of the one style whose selector is exactly <paramref name="selector"/>, quotes and all.</summary>
    private static string StyleBlock(string markup, string selector)
    {
        var bearer = "<Style Selector=\"" + selector;
        var start = markup.IndexOf(bearer, StringComparison.Ordinal);
        Assert.True(start >= 0, "no style for " + selector);

        var end = markup.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.True(end > start, "unterminated style for " + selector);

        return markup[start..end];
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
