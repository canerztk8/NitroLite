using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The 30-second idle timeout, and the switch that is supposed to control it.
/// </summary>
/// <remarks>
/// The bug this file exists for: the switch showed "off" while the keyboard still went dark after 30 seconds. The
/// startup read races the elevated helper — the window opens before the worker has connected, the read falls through
/// to the Acer service, and a machine running on direct WMI has no service to answer. The read returned nothing, the
/// switch kept its default "off", and nothing ever wrote the off state because the user never touched a switch that
/// already looked off. The firmware's timeout stayed on.
/// <para>
/// The fix has two halves, and both are pinned here: a write is read back so the switch cannot claim a state the
/// keyboard did not take, and opening the page re-reads so a read that lost the race is corrected.
/// </para>
/// </remarks>
public class BacklightTimeoutTests
{
    /// <summary>Long enough for the write and the read-back that follows it.</summary>
    private const int SettleMilliseconds = 3000;

    /// <summary>
    /// A backend whose timeout can be made to refuse, and to lie: <see cref="AcceptsWrites"/> false is a firmware that
    /// says no, <see cref="AppliesWrites"/> false is one that says yes and does nothing.
    /// </summary>
    private sealed class StubHardware : IHardwareController
    {
        public string BackendName => "Test";

        public bool IsAvailable => true;

        public HardwareCapabilities Capabilities { get; set; } = new()
        {
            Backend = "Test",
            ServiceAvailable = true,
            KeyboardLighting = true,
            BacklightTimeout = true
        };

        /// <summary>What the firmware reports. Null is a backend that cannot answer at all.</summary>
        public bool? Timeout { get; set; }

        /// <summary>False is a write the firmware refuses outright.</summary>
        public bool AcceptsWrites { get; set; } = true;

        /// <summary>False is a write the firmware accepts and does not apply.</summary>
        public bool AppliesWrites { get; set; } = true;

        public int Writes { get; private set; }

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

        public Task<HardwareCapabilities> GetCapabilitiesAsync() => Task.FromResult(Capabilities);

        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(null);

        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(OperatingMode.Default);

        public Task<bool> SetOperatingModeAsync(OperatingMode mode) => Task.FromResult(true);

        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(null);

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(true);

        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);

        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(FanMode.Auto);

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1) =>
            Task.FromResult(true);

        public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult<LightingConfig?>(null);

        public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction,
            string primaryColor, string[]? zoneColors) => Task.FromResult(true);

        public Task<bool> TurnOffLightingAsync() => Task.FromResult(true);

        public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult(Timeout);

        public Task<bool> SetBacklightTimeoutAsync(bool enable30s)
        {
            Writes++;
            if (!AcceptsWrites) return Task.FromResult(false);
            if (AppliesWrites) Timeout = enable30s;
            return Task.FromResult(true);
        }

        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => Task.FromResult<BatteryChargeMode?>(null);

        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => Task.FromResult(true);

        public Task<bool?> GetCoolBoostAsync() => Task.FromResult<bool?>(false);

        public Task<bool> SetCoolBoostAsync(bool on) => Task.FromResult(true);

        public Task<bool?> GetLcdOverdriveAsync() => Task.FromResult<bool?>(false);

        public Task<bool> SetLcdOverdriveAsync(bool on) => Task.FromResult(true);

        public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(true);

        public void Dispose() { }
    }

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-timeout-" + Guid.NewGuid().ToString("N") + ".json");

    private static MainViewModel Create(StubHardware hardware, AppSettings? settings, string path) =>
        new(settings ?? new AppSettings(), hardware, null, path);

    /// <summary>Builds a view model that has finished its first read, which is what clears the write guard.</summary>
    private static async Task<MainViewModel> ReadyAsync(StubHardware hardware, AppSettings? settings = null,
        string? path = null)
    {
        var vm = Create(hardware, settings, path ?? TempSettingsPath());
        await vm.InitializeAsync();
        return vm;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(SettleMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException("The expected state was not reached in time.");
    }

    // ---------------------------------------------------------------- the switch is the write

    [Fact]
    public async Task TurningTheSwitchOff_WritesOffToTheKeyboard()
    {
        var hardware = new StubHardware { Timeout = true };
        using var vm = await ReadyAsync(hardware);

        Assert.True(vm.BacklightTimeoutOn);

        vm.BacklightTimeoutOn = false;

        await WaitUntilAsync(() => hardware.Timeout == false);
        Assert.False(vm.BacklightTimeoutOn);

        // The Extras card's other switches report their writes, so this one does too.
        Assert.Contains(vm.Labels.BacklightTimeout, vm.Status);
    }

    [Fact]
    public async Task TurningTheSwitchOn_WritesOnToTheKeyboard()
    {
        var hardware = new StubHardware { Timeout = false };
        using var vm = await ReadyAsync(hardware);

        vm.BacklightTimeoutOn = true;

        await WaitUntilAsync(() => hardware.Timeout == true);
        Assert.True(vm.BacklightTimeoutOn);
    }

    // ---------------------------------------------------------------- a write that does not take

    [Fact]
    public async Task ARefusedWrite_PutsTheSwitchBackAndSaysSo()
    {
        var hardware = new StubHardware { Timeout = true, AcceptsWrites = false };
        using var vm = await ReadyAsync(hardware);

        vm.BacklightTimeoutOn = false;

        // The switch must not keep a state the keyboard never took: the page would promise the light stays on while
        // the keyboard goes dark 30 seconds later.
        await WaitUntilAsync(() => vm.BacklightTimeoutOn);
        Assert.True(vm.BacklightTimeoutOn);
        Assert.True(vm.HasStatus);
        Assert.Contains(vm.Labels.BacklightTimeout, vm.Status);
    }

    [Fact]
    public async Task AWriteTheFirmwareIgnores_PutsTheSwitchBackAndSaysSo()
    {
        // The firmware answers "accepted" and leaves the timeout on. Only the read-back catches this.
        var hardware = new StubHardware { Timeout = true, AppliesWrites = false };
        using var vm = await ReadyAsync(hardware);

        vm.BacklightTimeoutOn = false;

        await WaitUntilAsync(() => vm.BacklightTimeoutOn);
        Assert.True(vm.BacklightTimeoutOn);
        Assert.True(vm.HasStatus);
        Assert.Contains(vm.Labels.BacklightTimeout, vm.Status);
    }

    // ---------------------------------------------------------------- the read that lost the race

    [Fact]
    public async Task OpeningThePage_AdoptsTheStateTheKeyboardActuallyHolds()
    {
        // The startup read ran before the helper connected and got nothing, so the switch kept its default "off".
        var hardware = new StubHardware { Timeout = null };
        using var vm = await ReadyAsync(hardware);

        Assert.False(vm.BacklightTimeoutOn);

        // The helper is up by the time the page is opened, and the firmware says the timeout is on.
        hardware.Timeout = true;
        await vm.RefreshBacklightTimeoutAsync();

        Assert.True(vm.BacklightTimeoutOn);
    }

    [Fact]
    public async Task OpeningThePage_WhenNothingAnswers_ReassertsTheStoredChoice()
    {
        // Nothing answers at all, so the stored choice is the only statement of intent there is. Leaving it to a
        // firmware default the page cannot see is exactly the bug.
        var hardware = new StubHardware { Timeout = null };
        var settings = new AppSettings { BacklightTimeoutEnabled = false };
        using var vm = await ReadyAsync(hardware, settings);

        await vm.RefreshBacklightTimeoutAsync();

        Assert.Equal(1, hardware.Writes);
        Assert.False(vm.BacklightTimeoutOn);
    }

    [Fact]
    public async Task OpeningThePage_WhenNothingAnswersAndTheUserWantedItOn_WritesOn()
    {
        var hardware = new StubHardware { Timeout = null };
        var settings = new AppSettings { BacklightTimeoutEnabled = true };
        using var vm = await ReadyAsync(hardware, settings);

        await vm.RefreshBacklightTimeoutAsync();

        Assert.Equal(1, hardware.Writes);
        Assert.True(vm.BacklightTimeoutOn);
    }

    [Fact]
    public async Task OpeningThePage_WhenTheUserNeverTouchedTheSwitch_MakesTheDefaultTrue()
    {
        // The switch shows "off" and the hardware cannot be asked, so "off" is a claim the page is making. Writing it
        // is what turns the claim into a fact — leaving it unwritten is the reported bug, where the switch read "off"
        // over a firmware timeout that was still on and the keyboard went dark after 30 seconds.
        var hardware = new StubHardware { Timeout = null };
        using var vm = await ReadyAsync(hardware);

        await vm.RefreshBacklightTimeoutAsync();

        Assert.Equal(1, hardware.Writes);
        Assert.False(vm.BacklightTimeoutOn);
    }

    [Fact]
    public async Task OpeningThePage_WhenTheWriteIsRefused_LeavesTheSwitchAlone()
    {
        // Nobody touched the switch, so snapping it to the opposite state would be the page arguing with itself.
        var hardware = new StubHardware { Timeout = null, AcceptsWrites = false };
        using var vm = await ReadyAsync(hardware);

        await vm.RefreshBacklightTimeoutAsync();

        Assert.False(vm.BacklightTimeoutOn);
        Assert.False(vm.HasStatus);
    }

    // ---------------------------------------------------------------- the choice survives a restart

    [Fact]
    public async Task TheChoice_IsPersisted()
    {
        var path = TempSettingsPath();
        var hardware = new StubHardware { Timeout = true };
        using var vm = await ReadyAsync(hardware, path: path);

        vm.BacklightTimeoutOn = false;
        await WaitUntilAsync(() => hardware.Timeout == false);

        // The next start reads this back, which is what lets the page re-assert the choice when the firmware cannot
        // be asked.
        var saved = SettingsManager.LoadSettings(path).Settings;
        Assert.False(saved.BacklightTimeoutEnabled);
    }

    [Fact]
    public async Task ARefusedWrite_DoesNotPersistTheChoice()
    {
        var path = TempSettingsPath();
        var hardware = new StubHardware { Timeout = true, AcceptsWrites = false };
        using var vm = await ReadyAsync(hardware, path: path);

        vm.BacklightTimeoutOn = false;
        await WaitUntilAsync(() => vm.HasStatus);

        // Storing a choice the keyboard refused would make the next start re-assert it and fail again.
        var saved = SettingsManager.LoadSettings(path).Settings;
        Assert.Null(saved.BacklightTimeoutEnabled);
    }
}
