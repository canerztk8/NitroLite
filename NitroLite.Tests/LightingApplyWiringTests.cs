using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The lighting page has no Apply button, so every control on it is the write. These tests pin that down: each one
/// changes a single control the way the view does and then waits for the debounced write to reach the backend.
/// </summary>
/// <remarks>
/// The bug this file exists for: only the brightness and speed sliders were wired to the device. Choosing an effect,
/// a direction, a colour or a zone colour changed the view model and nothing else, so the keyboard stayed as it was
/// and the page looked broken. "Turn the lights off" worked because it had its own command.
/// </remarks>
public class LightingApplyWiringTests
{
    /// <summary>Long enough for the 350 ms debounce plus the write itself.</summary>
    private const int SettleMilliseconds = 3000;

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-lighting-wiring-" + Guid.NewGuid().ToString("N") + ".json");

    private static MainViewModel Create(Phase3Fakes.StubHardware hardware) =>
        new(new AppSettings(), hardware, null, TempSettingsPath());

    private static EffectChoice Effect(MainViewModel vm, string id) =>
        vm.Effects.First(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds a view model that has finished its first read, which is what clears the write guard.</summary>
    private static async Task<MainViewModel> ReadyAsync(Phase3Fakes.StubHardware hardware)
    {
        var vm = Create(hardware);
        await vm.InitializeAsync();
        hardware.LightingWrites.Clear();
        return vm;
    }

    private static async Task<string> NextWriteAsync(Phase3Fakes.StubHardware hardware)
    {
        await Phase3Fakes.WaitUntilAsync(() => hardware.LightingWrites.Count > 0, SettleMilliseconds);
        return hardware.LightingWrites[^1];
    }

    [Fact]
    public async Task SelectedEffect_AppliesToTheDevice()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.SelectedEffect = Effect(vm, "WAVE");

        var write = await NextWriteAsync(hardware);
        Assert.StartsWith("WAVE|", write);
    }

    [Fact]
    public async Task PrimaryColor_AppliesToTheDevice()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.PrimaryColor = "#FF0000";

        var write = await NextWriteAsync(hardware);
        Assert.Contains("|#FF0000|", write);
    }

    [Fact]
    public async Task ZoneColor_AppliesToTheDevice()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.Zone3 = "#00FF00";

        var write = await NextWriteAsync(hardware);
        Assert.EndsWith("|#00E5FF,#00E5FF,#00FF00,#00E5FF", write);
    }

    [Fact]
    public async Task Direction_AppliesToTheDevice()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        // WAVE is the effect that takes a direction, so the row exists and the choice is meaningful.
        vm.SelectedEffect = Effect(vm, "WAVE");
        await NextWriteAsync(hardware);
        hardware.LightingWrites.Clear();

        vm.SelectedDirection = vm.DirectionChoices.First(d => d.Direction == 3);

        var write = await NextWriteAsync(hardware);
        Assert.StartsWith("WAVE|", write);
        Assert.Contains("|3|", write);
    }

    [Fact]
    public async Task ApplyAllZones_AppliesToTheDevice()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.PrimaryColor = "#123456";
        await NextWriteAsync(hardware);
        hardware.LightingWrites.Clear();

        vm.ApplyAllZonesCommand.Execute(null);

        var write = await NextWriteAsync(hardware);
        Assert.EndsWith("|#123456,#123456,#123456,#123456", write);
    }

    [Fact]
    public async Task PickedColor_AppliesToTheDevice()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        // The picker window has no Apply of its own beyond its own button: the colour it returns is the write.
        vm.ApplyPickedColor("#123456");

        var write = await NextWriteAsync(hardware);
        Assert.Contains("|#123456|", write);
    }

    [Fact]
    public async Task PickedColor_GoesToTheZoneTheDeckHasFocused()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        // The zone buttons under the palette are gone, so the deck is the only way to aim the picker. A colour picked
        // while zone 2 is focused has to land on zone 2, not on the primary colour.
        vm.SelectZoneForPicker(2);
        vm.ApplyPickedColor("#123456");

        var write = await NextWriteAsync(hardware);
        Assert.EndsWith("|#00E5FF,#123456,#00E5FF,#00E5FF", write);
    }

    [Fact]
    public async Task ActiveColor_FollowsTheDeck()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.Zone3 = "#00FF00";
        vm.SelectZoneForPicker(3);

        // The hex box and the picker both read this, so it has to be the focused zone's colour rather than the
        // primary one — otherwise the box would show a colour the next edit would not write.
        Assert.Equal("#00FF00", vm.ActiveColor);
    }

    [Fact]
    public async Task SelectedRgbProfile_AppliesWithoutAnApplyButton()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        // The card's Apply button is gone: choosing a profile is the write, the same way choosing an effect is. The
        // first read already selected the first preset, so a different one is what counts as a choice.
        Assert.True(vm.RgbProfiles.Count > 1);
        var preset = vm.RgbProfiles[1];
        vm.SelectedRgbProfile = preset;

        var write = await NextWriteAsync(hardware);
        Assert.False(string.IsNullOrEmpty(write));
        Assert.Equal(preset.Name, vm.SelectedRgbProfile?.Name);
    }

    [Fact]
    public async Task ReloadingTheProfiles_DoesNotWriteToTheDevice()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = Create(hardware);

        // The first read selects a profile so the card is not blank. That selection is not the user picking one, and
        // it must not bounce out as a write — the same rule the rest of the page follows.
        await vm.InitializeAsync();

        // Start-up puts the stored lighting back once, deliberately (the keyboard may have lost it over a power
        // cycle). What must not happen is the controls bouncing their first values back out as further writes.
        hardware.LightingWrites.Clear();
        await Task.Delay(600);

        Assert.Empty(hardware.LightingWrites);
    }

    [Fact]
    public async Task SwatchClick_AppliesToTheDevice()    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        // The palette writes into whichever zone the deck has selected; the write has to follow it.
        vm.SelectZoneForPicker(2);
        var swatch = vm.ColorPalette.First(s => !string.Equals(s.Hex, vm.Zone2, StringComparison.OrdinalIgnoreCase));
        swatch.SelectCommand.Execute(null);

        var write = await NextWriteAsync(hardware);
        Assert.EndsWith("|#00E5FF," + swatch.Hex + ",#00E5FF,#00E5FF", write);
    }

    [Fact]
    public async Task LightingChange_DoesNotRaiseStatus()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.SelectedEffect = Effect(vm, "BREATHING");
        await NextWriteAsync(hardware);

        // The banner at the foot of the window is for things the user has to read. A lighting change is visible on
        // the keyboard itself, so it must not raise one.
        Assert.False(vm.HasStatus);
        Assert.Equal("", vm.Status);
    }

    [Fact]
    public async Task TurnOffLighting_DoesNotRaiseStatus()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.TurnOffLightingCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => hardware.LightingWrites.Contains("OFF"), SettleMilliseconds);

        Assert.False(vm.HasStatus);
    }

    [Fact]
    public async Task ZonePickerClick_DoesNotRaiseStatus()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.SelectZoneForPicker(3);

        Assert.True(vm.ZonePickerOpen);
        Assert.False(vm.HasStatus);
    }

    [Fact]
    public async Task ReadingTheStateBack_DoesNotWriteToTheDevice()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = Create(hardware);

        // The first read fills every control from the hardware. Those assignments are not user actions, so none of
        // them may bounce back out as a write.
        await vm.InitializeAsync();

        // Start-up puts the stored lighting back once, deliberately (the keyboard may have lost it over a power
        // cycle). What must not happen is the controls bouncing their first values back out as further writes.
        hardware.LightingWrites.Clear();
        await Task.Delay(600);

        Assert.Empty(hardware.LightingWrites);
    }

    // ---------------------------------------------------------------- the profile list follows the editor

    [Fact]
    public async Task ApplyAllZones_TakesThePrimaryColourWithIt()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        // The deck is pointed at zone 2, so the colour being spread is that zone's and not the primary one. The
        // firmware's STATIC mode takes one colour and the payload carries both fields, so leaving the primary behind
        // is what made "apply to all zones" land as a colour the page was not showing.
        vm.SelectZoneForPicker(2);
        vm.ActiveColor = "#123456";
        await NextWriteAsync(hardware);
        hardware.LightingWrites.Clear();

        vm.ApplyAllZonesCommand.Execute(null);

        var write = await NextWriteAsync(hardware);
        Assert.Contains("|#123456|", write);
        Assert.EndsWith("|#123456,#123456,#123456,#123456", write);
        Assert.Equal("#123456", vm.PrimaryColor);
    }

    [Fact]
    public async Task EditingAColourByHand_MovesTheProfileListToCustom()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        // Start from a real preset, so what the assertion catches is the edit and not the initial state: a fresh
        // settings file has no stored preset, and the editor then holds whatever the keyboard was running.
        var preset = vm.RgbProfiles[1];
        vm.SelectedRgbProfile = preset;
        await NextWriteAsync(hardware);
        Assert.Equal(preset.Name, vm.SelectedRgbProfile?.Name);

        // The list is a statement about where the colours came from. Once one is set by hand it no longer came from
        // the preset that was selected, and leaving that preset selected is what made the page look like the profile
        // was fighting the picker.
        vm.ApplyPickedColor("#123456");

        Assert.Equal(vm.CustomProfile.Name, vm.SelectedRgbProfile?.Name);
    }

    [Fact]
    public async Task AColourSetByHand_ClearsTheStoredPresetName()
    {
        var path = TempSettingsPath();
        try
        {
            var hardware = new Phase3Fakes.StubHardware();
            using var vm = new MainViewModel(new AppSettings(), hardware, null, path);
            await vm.InitializeAsync();

            // The blackout preset is the one that used to come back: it is the preset a user picks to switch the lights
            // off, and its name is then in the settings file while the colours are whatever was on the keyboard next.
            var blackout = vm.RgbProfiles.First(p => string.Equals(p.Effect, "OFF", StringComparison.OrdinalIgnoreCase));
            vm.SelectedRgbProfile = blackout;
            await Phase3Fakes.WaitUntilAsync(
                () => hardware.LightingWrites.Any(w => w.StartsWith("OFF", StringComparison.Ordinal)),
                SettleMilliseconds);

            Assert.Equal(blackout.Name, SettingsManager.LoadSettingsWithFallback(path).SelectedRgbPreset);

            vm.ApplyPickedColor("#123456");

            // The list says Custom, and the file no longer names a preset: the next launch must not read "blackout"
            // back over a keyboard that is lit. The file write follows the hardware write, so it is waited for rather
            // than assumed.
            Assert.Equal(vm.CustomProfile.Name, vm.SelectedRgbProfile?.Name);
            await Phase3Fakes.WaitUntilAsync(
                () => SettingsManager.LoadSettingsWithFallback(path).SelectedRgbPreset == null,
                SettleMilliseconds);

            Assert.Null(SettingsManager.LoadSettingsWithFallback(path).SelectedRgbPreset);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task ReloadingTheProfileList_KeepsACustomColourOnTheCustomEntry()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        var blackout = vm.RgbProfiles.First(p => string.Equals(p.Effect, "OFF", StringComparison.OrdinalIgnoreCase));
        vm.SelectedRgbProfile = blackout;
        await Phase3Fakes.WaitUntilAsync(
            () => hardware.LightingWrites.Any(w => w.StartsWith("OFF", StringComparison.Ordinal)),
            SettleMilliseconds);

        // Away from black and into a colour that is nobody's preset.
        vm.SelectedEffect = Effect(vm, "STATIC");
        vm.BrightnessLevel = 5;
        vm.ApplyPickedColor("#0A0B0C");
        await NextWriteAsync(hardware);

        // Saving a profile rebuilds the whole list, which is the moment the stale name used to win: the editor held a
        // hand-set colour, the list re-selected "Lights Off (Blackout)", and the page claimed the lights were off over
        // a lit keyboard.
        vm.NewProfileName = "Gece";
        vm.SaveRgbProfileCommand.Execute(null);

        Assert.Equal("Gece", vm.SelectedRgbProfile?.Name);
        Assert.NotEqual(blackout.Name, vm.SelectedRgbProfile?.Name);

        // And with no preset selected at all, the list still describes the colours rather than a name from the file.
        vm.ApplyPickedColor("#0A0B0D");
        Assert.Equal(vm.CustomProfile.Name, vm.SelectedRgbProfile?.Name);
    }

    [Fact]
    public async Task AFactoryProfileCannotBeDeleted()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        var builtIn = vm.RgbProfiles.First(p => p.IsBuiltIn && p.Name != vm.CustomProfile.Name);
        vm.SelectedRgbProfile = builtIn;
        await NextWriteAsync(hardware);

        var before = vm.RgbProfiles.Count;

        // The factory profiles are the app's, not the user's: the button is disabled rather than firing into a refusal
        // that the list then has to explain.
        Assert.False(vm.CanDeleteRgbProfile);
        Assert.False(vm.DeleteRgbProfileCommand.CanExecute(null));

        vm.DeleteRgbProfileCommand.Execute(null);

        Assert.Equal(before, vm.RgbProfiles.Count);
        Assert.Contains(vm.RgbProfiles, p => p.Name == builtIn.Name);
    }

    [Fact]
    public async Task TheCustomEntryCannotBeDeleted()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.SelectedRgbProfile = vm.CustomProfile;

        Assert.False(vm.CanDeleteRgbProfile);
        Assert.False(vm.DeleteRgbProfileCommand.CanExecute(null));
    }

    [Fact]
    public async Task AProfileTheUserSaved_CanBeDeleted()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.NewProfileName = "Atölye";
        vm.SaveRgbProfileCommand.Execute(null);
        await NextWriteAsync(hardware);

        var saved = vm.RgbProfiles.First(p => p.Name == "Atölye");
        vm.SelectedRgbProfile = saved;

        Assert.True(vm.CanDeleteRgbProfile);
        Assert.True(vm.DeleteRgbProfileCommand.CanExecute(null));

        vm.DeleteRgbProfileCommand.Execute(null);

        Assert.DoesNotContain(vm.RgbProfiles, p => p.Name == "Atölye");
    }

    [Fact]
    public async Task SelectingTheCustomEntry_WritesNothing()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.SelectedRgbProfile = vm.CustomProfile;
        await Task.Delay(600);

        // It is a label, not a preset: there is nothing to apply.
        Assert.Empty(hardware.LightingWrites);
    }

    [Fact]
    public async Task ApplyingAPreset_LeavesThatPresetSelected()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        var preset = vm.RgbProfiles[1];
        vm.SelectedRgbProfile = preset;
        await NextWriteAsync(hardware);

        // Filling the editor from a preset is not the user editing anything, so the selection must survive it.
        Assert.Equal(preset.Name, vm.SelectedRgbProfile?.Name);
    }

    [Fact]
    public async Task TurningTheLightsOff_ShowsBlackZones()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.TurnOffLightingCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => hardware.LightingWrites.Contains("OFF"), SettleMilliseconds);

        // The deck shows the state the keyboard is in. A rainbow over a keyboard that is off is the page describing
        // an effect that is not running.
        Assert.True(vm.IsLightingOff);
        Assert.False(vm.ZoneRainbow);
        Assert.True(vm.ZoneSolid);
        Assert.Equal("#000000", vm.Zone1);
        Assert.Equal("#000000", vm.Zone2);
        Assert.Equal("#000000", vm.Zone3);
        Assert.Equal("#000000", vm.Zone4);
    }
}
