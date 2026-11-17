using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// "Turn the lights off" is a preference, not a moment: it has to survive a restart, and it must not be undone by the
/// re-assert that runs when the window comes back from the tray.
/// </summary>
/// <remarks>
/// The bug this file exists for: the button wrote the off state to the keyboard and nothing else. The settings file
/// kept the last lit configuration, <c>active_rgb_state.json</c> kept it too, and the re-assert after a resume put it
/// back — so the lights came on again on the next launch and every time the window was raised.
/// </remarks>
public class LightingOffPersistenceTests
{
    /// <summary>Long enough for the 350 ms debounce plus the write itself.</summary>
    private const int SettleMilliseconds = 3000;

    /// <summary>
    /// A folder of its own per test. <see cref="ActiveRgbStateStore"/> writes beside the settings file, so a shared
    /// temp folder would let one test's recovery copy decide another test's re-assert.
    /// </summary>
    private static string TempDirectory() =>
        Directory.CreateTempSubdirectory("nitrolite-lighting-off-").FullName;

    private static async Task<MainViewModel> ReadyAsync(
        Phase3Fakes.StubHardware hardware, AppSettings settings, string settingsPath)
    {
        var vm = new MainViewModel(settings, hardware, null, settingsPath);
        await vm.InitializeAsync();
        hardware.LightingWrites.Clear();
        return vm;
    }

    [Fact]
    public async Task TurnOffLighting_WritesTheOffStateToTheSettingsFile()
    {
        var dir = TempDirectory();
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var settings = new AppSettings();
            var hardware = new Phase3Fakes.StubHardware();
            using var vm = await ReadyAsync(hardware, settings, settingsPath);

            vm.TurnOffLightingCommand.Execute(null);
            await Phase3Fakes.WaitUntilAsync(() => settings.IsBacklightOff, SettleMilliseconds);

            Assert.True(settings.IsBacklightOff);
            Assert.NotNull(settings.KeyboardLighting);
            Assert.Equal("OFF", settings.KeyboardLighting!.Effect);
            Assert.Equal(0, settings.KeyboardLighting.Brightness);

            // The file is what the next launch reads, so the assertion has to be made against it rather than against
            // the object the view model is holding.
            var reloaded = SettingsManager.LoadSettingsWithFallback(settingsPath);
            Assert.True(reloaded.IsBacklightOff);
            Assert.Equal("OFF", reloaded.KeyboardLighting?.Effect);
            Assert.Equal(0, reloaded.KeyboardLighting?.Brightness);
        }
        finally
        {
            TestDataIsolation.DeleteFolder(dir);
        }
    }

    [Fact]
    public async Task TurnOffLighting_OverwritesTheRecoveryCopyWithTheOffState()
    {
        var dir = TempDirectory();
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var settings = new AppSettings();
            var hardware = new Phase3Fakes.StubHardware();
            using var vm = await ReadyAsync(hardware, settings, settingsPath);

            // A lit write first, so the recovery copy holds something a re-assert could wrongly put back.
            vm.BrightnessLevel = 7;
            await Phase3Fakes.WaitUntilAsync(() => hardware.LightingWrites.Count > 0, SettleMilliseconds);
            Assert.False(LightingPolicy.IsOff(ActiveRgbStateStore.Load(settingsPath)!));

            vm.TurnOffLightingCommand.Execute(null);
            await Phase3Fakes.WaitUntilAsync(() => settings.IsBacklightOff, SettleMilliseconds);

            var recovery = ActiveRgbStateStore.Load(settingsPath);
            Assert.NotNull(recovery);
            Assert.True(LightingPolicy.IsOff(recovery!));
        }
        finally
        {
            TestDataIsolation.DeleteFolder(dir);
        }
    }

    [Fact]
    public async Task ReassertKeyboardLighting_AfterTurnOff_KeepsTheLightsOff()
    {
        var dir = TempDirectory();
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var settings = new AppSettings();
            var hardware = new Phase3Fakes.StubHardware();
            using var vm = await ReadyAsync(hardware, settings, settingsPath);

            vm.BrightnessLevel = 7;
            await Phase3Fakes.WaitUntilAsync(() => hardware.LightingWrites.Count > 0, SettleMilliseconds);

            vm.TurnOffLightingCommand.Execute(null);
            await Phase3Fakes.WaitUntilAsync(() => settings.IsBacklightOff, SettleMilliseconds);
            hardware.LightingWrites.Clear();

            await vm.ReassertKeyboardLightingAsync();

            // The last lit configuration is still in LastActiveLighting, which is what raising the brightness after
            // "off" restores. The re-assert must not read it as the state to put back.
            Assert.Equal(new[] { "OFF" }, hardware.LightingWrites);
        }
        finally
        {
            TestDataIsolation.DeleteFolder(dir);
        }
    }

    [Fact]
    public async Task Startup_WithAStoredOffState_PutsTheLightsBackOff()
    {
        var dir = TempDirectory();
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var settings = new AppSettings
            {
                IsBacklightOff = true,
                KeyboardLighting = new LightingConfig
                {
                    Effect = "OFF",
                    Brightness = 0,
                    Color = "#000000",
                    LEDs = new() { "#000000", "#000000", "#000000", "#000000" }
                }
            };
            var hardware = new Phase3Fakes.StubHardware();

            using var vm = new MainViewModel(settings, hardware, null, settingsPath);
            await vm.InitializeAsync();

            Assert.Contains("OFF", hardware.LightingWrites);
            Assert.Equal("OFF", vm.SelectedEffect?.Id);
            Assert.Equal(0, vm.BrightnessLevel);
        }
        finally
        {
            TestDataIsolation.DeleteFolder(dir);
        }
    }

    [Fact]
    public async Task Startup_WithALitState_DoesNotWriteToTheKeyboard()
    {
        var dir = TempDirectory();
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var settings = new AppSettings
            {
                IsBacklightOff = false,
                KeyboardLighting = new LightingConfig
                {
                    Effect = "STATIC",
                    Brightness = 6,
                    Color = "#00E5FF",
                    LEDs = new() { "#00E5FF", "#00E5FF", "#00E5FF", "#00E5FF" }
                }
            };
            var hardware = new Phase3Fakes.StubHardware();

            using var vm = new MainViewModel(settings, hardware, null, settingsPath);
            await vm.InitializeAsync();

            Assert.Empty(hardware.LightingWrites);
        }
        finally
        {
            TestDataIsolation.DeleteFolder(dir);
        }
    }

    [Fact]
    public async Task RaisingTheBrightness_AfterTurnOff_ResumesTheLastLitConfiguration()
    {
        var dir = TempDirectory();
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var settings = new AppSettings();
            var hardware = new Phase3Fakes.StubHardware();
            using var vm = await ReadyAsync(hardware, settings, settingsPath);

            vm.TurnOffLightingCommand.Execute(null);
            await Phase3Fakes.WaitUntilAsync(() => settings.IsBacklightOff, SettleMilliseconds);

            // The page shows the state the keyboard is in, so the controls went to "off" with it.
            Assert.Equal("OFF", vm.SelectedEffect?.Id);
            Assert.Equal(0, vm.BrightnessLevel);
            hardware.LightingWrites.Clear();

            vm.BrightnessLevel = 6;
            await Phase3Fakes.WaitUntilAsync(() => hardware.LightingWrites.Count > 0, SettleMilliseconds);
            await Phase3Fakes.WaitUntilAsync(() => !settings.IsBacklightOff, SettleMilliseconds);

            // The write has to be a lit one: sending OFF with brightness 6 is what the firmware reads as "stay off".
            Assert.DoesNotContain("OFF", hardware.LightingWrites);
            Assert.StartsWith("STATIC|6|", hardware.LightingWrites[^1]);
            Assert.False(settings.IsBacklightOff);
            Assert.Equal(6, settings.KeyboardLighting?.Brightness);
        }
        finally
        {
            TestDataIsolation.DeleteFolder(dir);
        }
    }

    [Fact]
    public async Task PickingAnEffect_AfterTurnOff_ResumesTheLastLitConfiguration()
    {
        var dir = TempDirectory();
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var settings = new AppSettings();
            var hardware = new Phase3Fakes.StubHardware();
            using var vm = await ReadyAsync(hardware, settings, settingsPath);

            vm.TurnOffLightingCommand.Execute(null);
            await Phase3Fakes.WaitUntilAsync(() => settings.IsBacklightOff, SettleMilliseconds);
            hardware.LightingWrites.Clear();

            vm.SelectedEffect = vm.Effects.First(e => e.Id == "WAVE");
            await Phase3Fakes.WaitUntilAsync(() => hardware.LightingWrites.Count > 0, SettleMilliseconds);
            await Phase3Fakes.WaitUntilAsync(() => !settings.IsBacklightOff, SettleMilliseconds);

            Assert.StartsWith("WAVE|", hardware.LightingWrites[^1]);
            Assert.False(settings.IsBacklightOff);
        }
        finally
        {
            TestDataIsolation.DeleteFolder(dir);
        }
    }

    [Fact]
    public async Task BrightnessZero_IsStoredAsTheOffState()
    {
        var dir = TempDirectory();
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var settings = new AppSettings();
            var hardware = new Phase3Fakes.StubHardware();
            using var vm = await ReadyAsync(hardware, settings, settingsPath);

            vm.BrightnessLevel = 0;
            await Phase3Fakes.WaitUntilAsync(() => hardware.LightingWrites.Count > 0, SettleMilliseconds);
            await Phase3Fakes.WaitUntilAsync(() => settings.IsBacklightOff, SettleMilliseconds);

            Assert.True(settings.IsBacklightOff);
            Assert.Equal(0, settings.KeyboardLighting?.Brightness);
        }
        finally
        {
            TestDataIsolation.DeleteFolder(dir);
        }
    }

    [Fact]
    public async Task FixLightingCommand_WithAStoredOffState_TurnsTheLightsOff()
    {
        var hardware = new Phase3Fakes.StubHardware();
        var settings = new AppSettings
        {
            IsBacklightOff = true,
            LastActiveLighting = new LightingConfig
            {
                Effect = "STATIC",
                Brightness = 7,
                Color = "#FF0000",
                LEDs = new() { "#FF0000", "#FF0000", "#FF0000", "#FF0000" }
            }
        };

        int exitCode = await CliTaskRunner.RunAsync("fix-lighting", hardware, settings);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { "OFF" }, hardware.LightingWrites);
    }
}
