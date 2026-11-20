using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The charging keyboard colour — the second automatic colour, driven by the adapter rather than by a sensor — and the
/// page's blackout, which is the state both automatic colours have to step out of the way for.
/// </summary>
/// <remarks>
/// The policy half is a pure function of a colour and a configuration, so it needs no backend. The view-model half uses
/// <see cref="Phase3Fakes.StubHardware"/>, whose <c>LightingWrites</c> list is the record of what reached the keyboard,
/// and a power-state delegate that a test can move between the adapter and the battery.
/// </remarks>
public class ChargingLightingTests
{
    // =============================================================================================================
    // 1. The colour and the shape it is written in.
    // =============================================================================================================

    [Theory]
    [InlineData("#00E5FF", true)]
    [InlineData("00e5ff", true)]
    [InlineData(" #F59E0B ", true)]
    [InlineData("#F59E0", false)]
    [InlineData("F59E0B1", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAWholeColourIsAColour(string? value, bool expected) =>
        Assert.Equal(expected, ChargingLighting.IsColor(value));

    [Fact]
    public void AColourThatIsNotOneFallsBackToTheDefault()
    {
        // The store answers a malformed colour with WRONG_PARAMETERS, so nothing unvalidated may reach it.
        Assert.Equal(ChargingLighting.DefaultColor, ChargingLighting.Normalize(null));
        Assert.Equal(ChargingLighting.DefaultColor, ChargingLighting.Normalize("F5"));
        Assert.Equal("#00e5ff", ChargingLighting.Normalize("#00e5ff").ToLowerInvariant());
    }

    [Fact]
    public void TheChargingColourIsOneSolidColourOnEveryZone()
    {
        var baseline = new LightingConfig
        {
            Effect = "WAVE",
            Brightness = 6,
            Speed = 4,
            Direction = 2,
            Color = "#123456",
            LEDs = new List<string> { "#111111", "#222222", "#333333", "#444444" }
        };

        var charging = ChargingLighting.ConfigFor("#FF0000", baseline);

        // The adapter is one fact, not four: a charging pattern that quietly replaced a deck the user arranged would
        // be a second arrangement they never asked for.
        Assert.Equal("STATIC", charging.Effect);
        Assert.Equal("#FF0000", charging.Color);
        Assert.All(charging.LEDs, zone => Assert.Equal("#FF0000", zone));

        // The brightness and speed are the page's, so the keys do not jump to full brightness when the plug goes in.
        Assert.Equal(6, charging.Brightness);
        Assert.Equal(4, charging.Speed);
        Assert.Equal(2, charging.Direction);
    }

    [Fact]
    public void ADarkKeyboardIsNotPainted()
    {
        var lit = new LightingConfig { Effect = "STATIC", Brightness = 5 };
        Assert.True(ChargingLighting.CanPaint(lit));

        // The firmware reads a colour written at brightness zero as "stay dark", so there is nothing to paint.
        Assert.False(ChargingLighting.CanPaint(new LightingConfig { Effect = "STATIC", Brightness = 0 }));
        Assert.False(ChargingLighting.CanPaint(new LightingConfig { Effect = "OFF", Brightness = 5 }));
        Assert.False(ChargingLighting.CanPaint(null));
    }

    // =============================================================================================================
    // 2. The view model: the plug is the trigger.
    // =============================================================================================================

    [Fact]
    public async Task EnablingItOnTheCharger_PaintsTheChargingColour()
    {
        var hardware = new Phase3Fakes.StubHardware();
        var settings = new AppSettings();

        using var vm = Create(hardware, settings, powerState: () => (true, 80));
        await vm.InitializeAsync();
        hardware.LightingWrites.Clear();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;

        var write = Assert.Single(hardware.LightingWrites);
        // The mode's own level, not the page's: the charging colour has a brightness slider of its own so the plug
        // cannot change the brightness the user set for the colour they chose.
        Assert.StartsWith($"STATIC|{vm.ChargingBrightness}|", write, StringComparison.Ordinal);
        Assert.Contains(ChargingLighting.DefaultColor, write, StringComparison.Ordinal);

        // The colour the page holds is untouched: this mode paints the keyboard, it does not become the user's
        // configuration, so unplugging has something to put back.
        Assert.Null(settings.KeyboardLighting);
        Assert.Equal("#00E5FF", vm.PrimaryColor);
    }

    [Fact]
    public async Task PullingThePlug_PutsTheUsersOwnColourBack()
    {
        var hardware = new Phase3Fakes.StubHardware();
        var settings = new AppSettings();
        bool onAdapter = true;

        using var vm = Create(hardware, settings, powerState: () => (onAdapter, 80));
        await vm.InitializeAsync();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;
        Assert.NotEmpty(hardware.LightingWrites);

        hardware.LightingWrites.Clear();
        onAdapter = false;
        await vm.RefreshTelemetryAsync();
        await vm.ChargingLightingTask;

        var write = Assert.Single(hardware.LightingWrites);
        Assert.DoesNotContain(ChargingLighting.DefaultColor, write, StringComparison.Ordinal);
        Assert.Contains("#00E5FF", write, StringComparison.Ordinal);

        // The mode stays armed: the plug is a state, and the card says it is waiting rather than switching itself off.
        Assert.True(vm.ChargingColorEnabled);
        Assert.True(vm.HasChargingColorState);
    }

    [Fact]
    public async Task OnBattery_EnablingIt_ArmsTheColourAndWritesNothing()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware, new AppSettings(), powerState: () => (false, 60));
        await vm.InitializeAsync();
        hardware.LightingWrites.Clear();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;

        Assert.Empty(hardware.LightingWrites);
        Assert.Equal(vm.Labels.ChargingColorWaiting, vm.ChargingColorStateText);
    }

    [Fact]
    public async Task TheTwoAutomaticColours_CannotBothBeOn()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware, new AppSettings(), powerState: () => (true, 80));
        await vm.InitializeAsync();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;

        // Both modes paint all four zones, so two of them on at once would take turns writing over each other.
        vm.ReactiveLightingEnabled = true;
        Assert.False(vm.ChargingColorEnabled);

        vm.ChargingColorEnabled = true;
        Assert.False(vm.ReactiveLightingEnabled);
    }

    // =============================================================================================================
    // 3. The blackout: the page goes dark with the keyboard.
    // =============================================================================================================

    [Fact]
    public async Task TheBlackoutTurnsThePageOffAndTheButtonBringsItBack()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware, new AppSettings(), powerState: () => (true, 80));
        await vm.InitializeAsync();

        // A configuration the page holds before the blackout, so the restore can be shown to bring it back rather
        // than the shipped default: the brightness the user chose is the one that has to come back.
        vm.BrightnessLevel = 7;

        Assert.False(vm.IsLightingBlackedOut);
        Assert.True(vm.LightingControlsEnabled);
        Assert.Equal(vm.Labels.TurnOffLighting, vm.LightingBlackoutButtonText);

        vm.ToggleLightingBlackoutCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => vm.IsLightingBlackedOut, 3000);

        // Every control on the page is disabled, and the button says what it will do next.
        Assert.False(vm.LightingControlsEnabled);
        Assert.Equal(vm.Labels.TurnOnLighting, vm.LightingBlackoutButtonText);

        hardware.LightingWrites.Clear();
        vm.ToggleLightingBlackoutCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => !vm.IsLightingBlackedOut, 3000);

        // The last lit configuration comes back rather than a colour at zero, and at the brightness it was left on.
        Assert.Contains(hardware.LightingWrites, w => w.StartsWith("STATIC|7|", StringComparison.Ordinal));
        Assert.True(vm.LightingControlsEnabled);
        Assert.Equal(7, vm.BrightnessLevel);
    }

    [Fact]
    public async Task TheBlackoutSwitchesTheChargingColourOffAndTheRestoreArmsItAgain()
    {
        var hardware = new Phase3Fakes.StubHardware();
        var settings = new AppSettings();

        using var vm = Create(hardware, settings, powerState: () => (true, 80));
        await vm.InitializeAsync();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;

        vm.TurnOffLightingCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => vm.IsLightingBlackedOut, 3000);

        // The switch goes down with the keyboard, and the preference stays in the settings file: the blackout is a
        // moment, not a change of mind.
        Assert.False(vm.ChargingColorEnabled);
        Assert.True(settings.ChargingColorEnabled);

        hardware.LightingWrites.Clear();
        vm.ChargingColor = "#22D3EE";

        // Nothing is painted over a dark keyboard, whatever the adapter and the colour do.
        await vm.ChargingLightingTask;
        Assert.Empty(hardware.LightingWrites);

        vm.ToggleLightingBlackoutCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => !vm.IsLightingBlackedOut, 3000);
        await Phase3Fakes.WaitUntilAsync(() => vm.ChargingColorEnabled, 3000);
        await vm.ChargingLightingTask;

        // The mode is armed again and, on the adapter, the new colour is on the keys.
        Assert.Contains(hardware.LightingWrites, w => w.Contains("#22D3EE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBlackoutSurvivesARestart_AndAZeroBrightnessDoesNot()
    {
        var off = new LightingConfig
        {
            Effect = "OFF",
            Brightness = 0,
            Color = "#000000",
            LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" }
        };

        using (var restarted = Create(new Phase3Fakes.StubHardware(),
                   new AppSettings { IsBacklightOff = true, KeyboardLighting = off, LastActiveLighting = off },
                   powerState: () => (true, 80)))
        {
            await restarted.InitializeAsync();
            Assert.True(restarted.IsLightingBlackedOut);
        }

        // A brightness the user slid to zero is a dark keyboard with a page that still works: they may be on their way
        // to sliding it back up, and locking the page over it would be the page arguing with the slider.
        var dimmed = new LightingConfig
        {
            Effect = "STATIC",
            Brightness = 0,
            Color = "#00E5FF",
            LEDs = new List<string> { "#00E5FF", "#00E5FF", "#00E5FF", "#00E5FF" }
        };

        using var battery = Create(new Phase3Fakes.StubHardware(),
            new AppSettings { IsBacklightOff = true, KeyboardLighting = dimmed, LastActiveLighting = dimmed },
            powerState: () => (false, 40));

        await battery.InitializeAsync();
        Assert.False(battery.IsLightingBlackedOut);
        Assert.True(battery.LightingControlsEnabled);
    }

    [Fact]
    public void TheChargingColourAndTheBlackoutAreOnThePage()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml"));

        // The card is not hidden behind a capability flag: the plug is on every machine, and a card that only appears
        // after the probe answers is a card the user cannot find on the machine whose probe needs elevation.
        Assert.Contains("Labels.ChargingColor}", markup, StringComparison.Ordinal);
        Assert.Contains("ChargingColorEnabled", markup, StringComparison.Ordinal);
        Assert.Contains("ChargingColorChoices", markup, StringComparison.Ordinal);
        Assert.Contains("ChargingColorPreview", markup, StringComparison.Ordinal);
        Assert.Contains("OnPickChargingColorClick", markup, StringComparison.Ordinal);

        // The blackout is a cover over the page and a button in the header, both bound to the same state and command.
        Assert.Contains("IsLightingBlackedOut", markup, StringComparison.Ordinal);
        Assert.Contains("LightingControlsEnabled", markup, StringComparison.Ordinal);
        Assert.Contains("Labels.LightingBlackoutTitle", markup, StringComparison.Ordinal);
        Assert.Contains("ToggleLightingBlackoutCommand", markup, StringComparison.Ordinal);
        Assert.Contains("Labels.RestoreLighting", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeaderCarriesTheTimeoutAndTheBlackout()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml"));

        // The 30-second switch moved out of the extras card, to the left of the blackout button: both are keyboard
        // switches, and the extras category itself is gone.
        var title = markup.IndexOf("Labels.LightingPageTitle", StringComparison.Ordinal);
        var timeout = markup.IndexOf("Labels.BacklightTimeoutShort", StringComparison.Ordinal);
        var blackout = markup.IndexOf("LightingBlackoutButtonText", StringComparison.Ordinal);

        Assert.True(title > 0, "the page has no title");
        Assert.True(timeout > title, "the timeout switch is not in the header");
        Assert.True(blackout > timeout, "the blackout button is not to the right of the timeout switch");

        Assert.DoesNotContain("Labels.Extras", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtrasCard", markup, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 4. The settings half.
    // =============================================================================================================

    [Fact]
    public void ThePreferenceAndTheColourSurviveARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "nitrolite-charging-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings { ChargingColorEnabled = true, ChargingColor = "#22D3EE" };
            SettingsManager.SaveSettingsAtomic(path, settings);

            var reloaded = SettingsManager.LoadSettingsWithFallback(path);
            Assert.True(reloaded.ChargingColorEnabled);
            Assert.Equal("#22D3EE", reloaded.ChargingColor);

            // The opt-in defaults matter as much as the values: a file written by a build that never heard of the
            // feature must not switch it on.
            var fresh = new AppSettings();
            Assert.False(fresh.ChargingColorEnabled);
            Assert.Null(fresh.ChargingColor);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // =============================================================================================================
    // 5. OFF in the effect list: the keyboard takes no colour, so both automatic colours close.
    // =============================================================================================================

    [Fact]
    public async Task ChoosingOFF_ClosesBothAutomaticColours_AndKeepsThePreference()
    {
        var hardware = new Phase3Fakes.StubHardware();
        var settings = new AppSettings();

        using var vm = Create(hardware, settings, powerState: () => (true, 80));
        await vm.InitializeAsync();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;
        Assert.True(vm.AutomaticColoursEnabled);

        vm.SelectedEffect = vm.Effects.First(e => e.Id == "OFF");

        // The switch goes off with the keyboard, immediately — not a second later, when the write lands.
        Assert.False(vm.ChargingColorEnabled);
        Assert.False(vm.ReactiveLightingEnabled);
        Assert.False(vm.AutomaticColoursEnabled);

        // The preference is not the switch: the next lit effect is what brings the mode back.
        Assert.True(settings.ChargingColorEnabled);
    }

    [Fact]
    public async Task ChoosingALitEffectAgain_ReopensThem_AndPaintsTheMode()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware, new AppSettings(), powerState: () => (true, 80));
        await vm.InitializeAsync();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;

        vm.SelectedEffect = vm.Effects.First(e => e.Id == "OFF");
        await Phase3Fakes.WaitUntilAsync(() => !vm.AutomaticColoursEnabled, 3000);

        hardware.LightingWrites.Clear();
        vm.SelectedEffect = vm.Effects.First(e => e.Id == "STATIC");

        // The card opens as soon as the list moves, and the mode is put back on the keys once the write is accepted.
        Assert.True(vm.AutomaticColoursEnabled);
        await Phase3Fakes.WaitUntilAsync(() => vm.ChargingColorEnabled, 3000);
        await vm.ChargingLightingTask;

        Assert.Contains(hardware.LightingWrites,
            w => w.Contains(ChargingLighting.DefaultColor, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AZeroBrightness_ClosesNothing_EvenThoughTheKeysAreDark()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware, new AppSettings(), powerState: () => (true, 80));
        await vm.InitializeAsync();

        vm.ChargingColorEnabled = true;
        await vm.ChargingLightingTask;

        hardware.LightingWrites.Clear();
        vm.BrightnessLevel = 0;
        await Phase3Fakes.WaitUntilAsync(() => hardware.LightingWrites.Count > 0, 3000);

        // Both states are dark, but only one of them is the user asking for no colour at all. A slider at zero is a
        // dark keyboard with a page around it, so nothing closes — the user is on their way back up. The page write
        // does end the automatic colours, the same way a swatch does: that is the colour rule, not the dark one.
        Assert.True(vm.IsLightingOff);
        Assert.False(vm.IsLightingBlackedOut);
        Assert.True(vm.AutomaticColoursEnabled);
    }

    [Fact]
    public void BothAutomaticColourCardsCloseWithTheEffectList()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml"));

        // Two cards, one binding each: the ramp and the charging colour are closed together or not at all.
        Assert.Equal(2, markup.Split("AutomaticColoursEnabled").Length - 1);
    }

    // =============================================================================================================
    // harness
    // =============================================================================================================

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

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-charging-" + Guid.NewGuid().ToString("N"), "settings.json");

    private static MainViewModel Create(
        Phase3Fakes.StubHardware hardware,
        AppSettings settings,
        Func<(bool OnAc, int? BatteryPercent)>? powerState = null) =>
        new(settings, hardware, null, TempSettingsPath(),
            powerState: powerState ?? (() => (false, 50)));
}
