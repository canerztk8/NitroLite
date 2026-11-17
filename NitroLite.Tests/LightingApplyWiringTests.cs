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
    public async Task SwatchClick_AppliesToTheDevice()
    {
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
        await Task.Delay(600);

        Assert.Empty(hardware.LightingWrites);
    }
}
