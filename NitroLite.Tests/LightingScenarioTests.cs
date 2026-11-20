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
/// The lighting page walked the way a person walks it: turn this on, then that, press the button in the middle of the
/// sequence and check that nothing is left in a state the next click cannot get out of.
/// </summary>
/// <remarks>
/// Each test below is a sequence rather than a single setter, because the failures worth catching live between two
/// clicks — a colour written after the switch that cancelled it, a mode that comes back from the blackout still
/// switched off, an editor that shows stops the keyboard is not painting.
/// </remarks>
public class LightingScenarioTests
{
    // =============================================================================================================
    // 1. Fast hands: a colour that was asked for and then cancelled.
    // =============================================================================================================

    [Fact]
    public async Task TurningTheModeOffWhileColourWritesAreQueued_WritesNothingMore()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 78;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        hardware.LightingWrites.Clear();

        vm.ReactiveBrightness = 4;
        vm.ReactiveBrightness = 5;
        vm.ReactiveLightingEnabled = false;

        await Task.Delay(1200);

        // Neither level reached the keyboard. The slider's repaint waits for the hand to stop, and by the time it was
        // due the mode had been switched off, so it found nothing to paint and returned before the gate. Without that
        // check the pending write lands a moment after the switch — a flash of the temperature colour at a level the user
        // had just moved past, on a keyboard they had just handed back. The only write here is the one that hands the
        // keyboard back to the user's own colour.
        var colour = ReactiveLighting.ColorFor(78);
        Assert.DoesNotContain(hardware.LightingWrites,
            w => w.Contains(colour, StringComparison.OrdinalIgnoreCase));
        Assert.False(vm.ReactiveLightingEnabled);
    }

    [Fact]
    public async Task TurningTheModeOffPutsTheUsersOwnColourBack()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 78;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.PrimaryColor = "#123456";
        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        hardware.LightingWrites.Clear();
        vm.ReactiveLightingEnabled = false;

        await Phase3Fakes.WaitUntilAsync(() => hardware.LightingWrites.Count > 0, 3000);

        // The switch is the user's way out of the mode, and the keyboard has to agree with it on the same click.
        Assert.Contains("#123456", hardware.LightingWrites[^1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ReactiveLighting.ColorFor(78), hardware.LightingWrites[^1], StringComparison.OrdinalIgnoreCase);
    }

    // =============================================================================================================
    // 2. The blackout in the middle of a sequence.
    // =============================================================================================================

    [Fact]
    public async Task ModeOnThenBlackoutThenRestore_PutsTheModeBackOnTheKeys()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 78;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;
        var colour = ReactiveLighting.ColorFor(78);

        vm.TurnOffLightingCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => vm.IsLightingBlackedOut, 3000);

        // Dark keys, dark page, and the switch down with them — the behaviour the blackout exists for.
        Assert.False(vm.ReactiveLightingEnabled);
        Assert.False(vm.AutomaticColoursEnabled);

        hardware.LightingWrites.Clear();
        vm.ToggleLightingBlackoutCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => !vm.IsLightingBlackedOut && vm.ReactiveLightingEnabled, 3000);
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        Assert.True(vm.AutomaticColoursEnabled);
        Assert.Contains(hardware.LightingWrites, w => w.Contains(colour, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ASensorSwitchedWhileThePageIsBlackedOut_TakesEffectOnTheNextTickNotOnTheDarkKeyboard()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 58;
        hardware.Sample.GpuTemperature = 95;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.TurnOffLightingCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => vm.IsLightingBlackedOut, 3000);
        hardware.LightingWrites.Clear();

        // Automation, a shortcut or a stale click can still reach the setter: the switch is down, so the forced repaint
        // the setter asks for must find the mode off and write nothing to a dark keyboard.
        vm.ReactiveGpuSensor = true;
        await vm.ReactiveLightingTask;

        Assert.False(vm.ReactiveLightingEnabled);
        Assert.DoesNotContain(hardware.LightingWrites,
            w => w.Contains(ReactiveLighting.ColorFor(58), StringComparison.OrdinalIgnoreCase));
    }

    // =============================================================================================================
    // 3. The two automatic colours handing the keyboard back and forth.
    // =============================================================================================================

    [Fact]
    public async Task TheTemperatureColourTakesTheKeyboardBackFromTheChargingOne()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 78;

        using var vm = Create(hardware, powerState: () => (true, 80));
        await vm.InitializeAsync();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;
        Assert.True(vm.ChargingColorEnabled);

        hardware.LightingWrites.Clear();
        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        Assert.False(vm.ChargingColorEnabled);
        Assert.Contains(hardware.LightingWrites,
            w => w.Contains(ReactiveLighting.ColorFor(78), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SelectingAProfileWhileTheChargingColourIsOn_ClosesItAndKeepsTheCardOpen()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware, powerState: () => (true, 80));
        await vm.InitializeAsync();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;

        vm.SelectedRgbProfile = vm.RgbProfiles[1];
        await Phase3Fakes.WaitUntilAsync(() => !vm.ChargingColorEnabled, 3000);

        Assert.False(vm.ChargingColorEnabled);
        Assert.True(vm.AutomaticColoursEnabled);
    }

    [Fact]
    public async Task TheSensorPairSurvivesAManualColour()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 58;
        hardware.Sample.GpuTemperature = 95;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveGpuSensor = true;
        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        vm.SelectedRgbProfile = vm.RgbProfiles[1];
        await Phase3Fakes.WaitUntilAsync(() => !vm.ReactiveLightingEnabled, 3000);

        // The manual colour ends the mode, not the choice of sensors: switching it back on with the pair the user set
        // is one click, and the split is where they left it.
        Assert.True(vm.ReactiveGpuSensor);
        Assert.True(vm.ReactiveCpuSensor);

        hardware.LightingWrites.Clear();
        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        var left = ReactiveLighting.ColorFor(58);
        var right = ReactiveLighting.ColorFor(95);

        Assert.Contains(hardware.LightingWrites, w =>
            w.EndsWith($"|{left},{left},{right},{right}", StringComparison.OrdinalIgnoreCase));
    }

    // =============================================================================================================
    // 4. The charging colour around the plug.
    // =============================================================================================================

    [Fact]
    public async Task ChargingColourSetUpWhileUnplugged_WaitsQuietlyAndUsesThatLevelWhenPluggedIn()
    {
        var onAc = false;
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware, powerState: () => (onAc, 60));
        await vm.InitializeAsync();
        hardware.LightingWrites.Clear();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;
        vm.ChargingBrightness = 3;
        await vm.ChargingLightingTask;

        // Nothing to paint while the machine is on its battery, and the level is remembered for the plug rather than
        // written to a keyboard the mode does not own yet.
        Assert.Empty(hardware.LightingWrites);
        Assert.Equal(3, vm.ChargingBrightness);

        onAc = true;
        await vm.RefreshTelemetryAsync();
        await vm.ChargingLightingTask;

        var write = Assert.Single(hardware.LightingWrites,
            w => w.Contains(ChargingLighting.DefaultColor, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("STATIC|3|", write, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoredBlackoutWithTheChargingColourArmed_NeverLightsTheKeyboard()
    {
        // The state a user comes back to after pressing the blackout with the charging colour switched on: the keyboard
        // is meant to be dark, and the plug is in from the moment the app starts.
        var settings = new AppSettings
        {
            IsBacklightOff = true,
            KeyboardLighting = new LightingConfig { Effect = "OFF", Brightness = 0 },
            ChargingColorEnabled = true
        };

        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware, settings, powerState: () => (true, 80));
        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();
        await vm.ChargingLightingTask;

        // The card is closed with the page, so the mode is armed rather than running, and nothing of its colour
        // reaches the keys.
        Assert.True(vm.IsLightingBlackedOut);
        Assert.False(vm.ChargingColorEnabled);
        Assert.False(vm.AutomaticColoursEnabled);
        Assert.DoesNotContain(hardware.LightingWrites,
            w => w.Contains(ChargingLighting.DefaultColor, StringComparison.OrdinalIgnoreCase));
    }

    // =============================================================================================================
    // 5. The custom ramp editor.
    // =============================================================================================================

    [Fact]
    public async Task TypingATemperatureAnotherRowHolds_MovesTheRowOffIt()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);
        var first = vm.ReactiveStops[0];
        var second = vm.ReactiveStops[1];

        vm.SetReactiveStopTemperature(second, first.TemperatureText);

        // Two rows on one temperature collapse into one stop, and a ramp of fewer than two stops sends the keyboard
        // back to the built-in colours while the editor keeps showing the user's — a silent failure this nudge makes
        // visible instead.
        Assert.NotEqual(first.AtCelsius, second.AtCelsius);
        Assert.Equal(first.AtCelsius + 1, second.AtCelsius);

        var painted = vm.CustomRampPreview;
        Assert.True(painted.Count >= 2);
        Assert.Equal(painted.Count, painted.Select(s => s.AtCelsius).Distinct().Count());
    }

    [Fact]
    public async Task TypingIntoARowThatWasJustRemoved_ChangesNothing()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);
        var row = vm.ReactiveStops[1];
        var before = row.AtCelsius;

        row.RemoveCommand.Execute(null);

        // The box commits on losing focus, and a row can be deleted while its box holds the caret. Writing into a row
        // nothing reads would leave the number on screen with no stop behind it.
        vm.SetReactiveStopTemperature(row, "95");

        Assert.Equal(before, row.AtCelsius);
    }

    [Fact]
    public async Task ATypedStopIsWhatTheNextLaunchReads()
    {
        var path = Path.Combine(Path.GetTempPath(), "nitrolite-scenario-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var hardware = new Phase3Fakes.StubHardware();
            using (var vm = new MainViewModel(new AppSettings(), hardware, null, path))
            {
                await vm.InitializeAsync();
                vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);
                vm.SetReactiveStopTemperature(vm.ReactiveStops[1], "73 °C");
            }

            // The editor's changes reach the file on a debounce, so the assertion waits for the write rather than for a
            // fixed delay: the point is that the typed number is what the next launch reads.
            AppSettings reloaded = new();
            await Phase3Fakes.WaitUntilAsync(() =>
            {
                reloaded = SettingsManager.LoadSettingsWithFallback(path);
                return reloaded.ReactiveLightingCustomStops?.Any(s => Math.Abs(s.AtCelsius - 73) < 0.001) == true;
            }, 3000);

            Assert.Equal(ReactiveLightingProfiles.CustomId, reloaded.ReactiveLightingProfile);
            Assert.Contains(reloaded.ReactiveLightingCustomStops!, s => Math.Abs(s.AtCelsius - 73) < 0.001);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // =============================================================================================================
    // 6. The page's brightness, and the two figures that follow the language.
    // =============================================================================================================

    [Fact]
    public async Task SlidingThePagesBrightnessToZero_EndsTheModeInsteadOfFightingIt()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 78;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        vm.BrightnessLevel = 0;
        await Phase3Fakes.WaitUntilAsync(() => !vm.ReactiveLightingEnabled, 3000);

        // The slider is a manual write, and a manual write is the user taking the keyboard back. The switch goes down
        // rather than staying on over a keyboard that will not answer it.
        Assert.False(vm.ReactiveLightingEnabled);
        Assert.True(vm.IsLightingOff);
        Assert.True(vm.AutomaticColoursEnabled);
    }

    [Fact]
    public async Task TheTwoModeFiguresAndTheLiveLine_FollowALanguageChange()
    {
        var original = Loc.Language;
        try
        {
            var hardware = new Phase3Fakes.StubHardware();

            using var vm = Create(hardware);
            await vm.InitializeAsync();

            var raised = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != null) raised.Add(e.PropertyName);
            };

            vm.SelectedLanguage = vm.Languages.First(l => string.Equals(l.Code, "de", StringComparison.OrdinalIgnoreCase));

            // All three are sentences built from the catalogue rather than fixed text, so each has to be re-raised or
            // the page keeps the old language in three places until the next restart.
            Assert.Contains(nameof(vm.ReactiveBrightnessLabel), raised);
            Assert.Contains(nameof(vm.ChargingBrightnessLabel), raised);
            Assert.Contains(nameof(vm.ReactiveLightingNow), raised);

            Assert.Equal(vm.Labels.BrightnessValue(vm.ReactiveBrightness, vm.MaxBrightness), vm.ReactiveBrightnessLabel);
            Assert.Equal(vm.Labels.BrightnessValue(vm.ChargingBrightness, vm.MaxBrightness), vm.ChargingBrightnessLabel);
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    // =============================================================================================================
    // 9. The switch that told the keyboard but not the card.
    // =============================================================================================================

    [Fact]
    public async Task TheTemperatureSwitch_RaisesItsOwnPropertyName()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await Phase3Fakes.WaitUntilAsync(() => vm.ReactiveLightingEnabled);

        var raised = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) raised.Add(e.PropertyName);
        };

        vm.ReactiveLightingEnabled = false;

        // The switch lives in a method, not in the property's own setter, so the notification helper's caller-member
        // default named the method instead of the property and no binding heard the change: the keyboard stopped while
        // the card went on drawing a live reading and a live slider, and the switch was the only thing that looked off.
        Assert.Contains(nameof(vm.ReactiveLightingEnabled), raised);

        raised.Clear();
        vm.ReactiveLightingEnabled = true;
        Assert.Contains(nameof(vm.ReactiveLightingEnabled), raised);
    }

    [Fact]
    public async Task TheChargingSwitch_RaisesItsOwnPropertyName()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = Create(hardware, powerState: () => (true, 60));
        await vm.InitializeAsync();

        var raised = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) raised.Add(e.PropertyName);
        };

        vm.ChargingColorEnabled = true;
        Assert.Contains(nameof(vm.ChargingColorEnabled), raised);

        raised.Clear();
        vm.ChargingColorEnabled = false;
        Assert.Contains(nameof(vm.ChargingColorEnabled), raised);
    }

    // =============================================================================================================
    // harness
    // =============================================================================================================

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-scenario-" + Guid.NewGuid().ToString("N") + ".json");

    private static MainViewModel Create(
        Phase3Fakes.StubHardware hardware,
        AppSettings? settings = null,
        Func<(bool OnAc, int? BatteryPercent)>? powerState = null) =>
        new(settings ?? new AppSettings(), hardware, null, TempSettingsPath(),
            powerState: powerState ?? (() => (false, 50)));
}
