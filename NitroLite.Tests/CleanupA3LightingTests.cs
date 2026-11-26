using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Regression tests for clean-up round 3, lighting (L1-L8): the WAVE direction, the hex box, school mode's dark
/// keyboard, the blackout against a write in flight, the re-assertion against the automatic colours, the elevation
/// switch on automatic writes, the preset status, and a charging paint that outlives the plug.
/// </summary>
/// <remarks>
/// Hardware goes to <see cref="A3Hardware"/>, which records every write and touches nothing.
/// </remarks>
public class CleanupA3LightingTests
{
    private const int Settle = 3000;

    // ---------------------------------------------------------------- L1. WAVE up/down

    [Fact]
    public async Task WaveUpPickedOnTheWmiPath_IsWrittenAsUp()
    {
        var hardware = new A3Hardware();
        using var vm = await ReadyAsync(hardware, new AppSettings());

        vm.SelectedEffect = vm.Effects.First(e => e.Id == "WAVE");
        await WaitForLightingWriteAsync(hardware);
        hardware.ClearLighting();

        vm.SelectedDirection = vm.DirectionChoices.First(d => d.Direction == 3);

        var write = await WaitForLightingWriteAsync(hardware);
        Assert.Equal("WAVE", write.Effect);
        Assert.Equal(3, write.Direction);
        Assert.Equal(3, vm.DirectionLevel);
    }

    [Fact]
    public async Task AStoredDownDirection_SurvivesTheFirstRead()
    {
        // A direction set in NitroSense (or stored by an earlier session) used to be read back as "left", and the next
        // edit then wrote left.
        var settings = new AppSettings
        {
            KeyboardLighting = new LightingConfig
            {
                Effect = "WAVE", Brightness = 5, Speed = 3, Direction = 4, Color = "#00E5FF",
                LEDs = new List<string> { "#00E5FF", "#00E5FF", "#00E5FF", "#00E5FF" }
            }
        };
        var hardware = new A3Hardware { ReportedLighting = settings.KeyboardLighting };
        using var vm = await ReadyAsync(hardware, settings);

        Assert.Equal(4, vm.DirectionLevel);
        Assert.Equal(4, vm.SelectedDirection?.Direction);

        hardware.ClearLighting();
        vm.SpeedLevel = 6;
        var write = await WaitForLightingWriteAsync(hardware);
        Assert.Equal(4, write.Direction);
    }

    // ---------------------------------------------------------------- L2. the hex box

    [Theory]
    [InlineData("#FF00")]
    [InlineData("red")]
    [InlineData("#GGGGGG")]
    public async Task AnIncompleteHex_WritesNothingAndKeepsTheColour(string typed)
    {
        var hardware = new A3Hardware();
        using var vm = await ReadyAsync(hardware, new AppSettings());
        var before = vm.PrimaryColor;

        vm.ActiveColorText = typed;
        await Task.Delay(900);

        Assert.Empty(hardware.LightingSnapshot());
        Assert.Equal(before, vm.PrimaryColor);

        // The box is not rewritten under the user's fingers.
        Assert.Equal(typed, vm.ActiveColorText);
    }

    [Fact]
    public async Task ACompleteHex_IsWrittenAndTheBoxKeepsWhatWasTyped()
    {
        var hardware = new A3Hardware();
        using var vm = await ReadyAsync(hardware, new AppSettings());

        vm.ActiveColorText = "#FF0";
        vm.ActiveColorText = "#FF00";
        vm.ActiveColorText = "ff0000";

        var write = await WaitForLightingWriteAsync(hardware);
        Assert.Equal("#FF0000", write.Color);
        Assert.Equal("#FF0000", vm.PrimaryColor);
        Assert.Equal("ff0000", vm.ActiveColorText);

        // A colour set anywhere else replaces the box's text.
        vm.ApplyPickedColor("#00FF00");
        Assert.Equal("#00FF00", vm.ActiveColorText);
    }

    // ---------------------------------------------------------------- L3. school mode's dark keyboard

    [Fact]
    public async Task WithSchoolModeOn_TheStartAndTheTrayRestoreKeepTheKeysDark()
    {
        var settings = LitSettings();
        settings.SchoolModeEnabled = true;
        var hardware = new A3Hardware();
        var school = School(hardware, settings);

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), schoolMode: school,
            powerState: () => (false, 60));
        await vm.InitializeAsync();

        Assert.DoesNotContain(hardware.LightingSnapshot(), w => w.Effect != "OFF");
        Assert.Contains(hardware.LightingSnapshot(), w => w.Effect == "OFF");

        hardware.ClearLighting();
        await vm.ReassertKeyboardLightingAsync();

        var write = Assert.Single(hardware.LightingSnapshot());
        Assert.Equal("OFF", write.Effect);
    }

    [Fact]
    public async Task ABlackoutDuringSchoolMode_SurvivesSwitchingSchoolModeOff()
    {
        var settings = LitSettings();
        var hardware = new A3Hardware { ReportedLighting = settings.KeyboardLighting };
        var school = School(hardware, settings);

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), schoolMode: school,
            powerState: () => (false, 60));
        await vm.InitializeAsync();

        vm.SchoolModeOn = true;
        await WaitUntilAsync(() => settings.SchoolModeEnabled && hardware.ReportedLighting?.Effect == "OFF");

        await vm.ToggleLightingBlackoutCommand.ExecuteAsync(null);
        Assert.True(vm.IsLightingBlackedOut);
        hardware.ClearLighting();

        vm.SchoolModeOn = false;
        await WaitUntilAsync(() => !settings.SchoolModeEnabled);
        await Task.Delay(300);

        Assert.DoesNotContain(hardware.LightingSnapshot(), w => w.Effect != "OFF");
        Assert.True(vm.IsLightingBlackedOut);
        Assert.True(settings.IsBacklightOff);
    }

    // ---------------------------------------------------------------- L4. a colour in flight against the blackout

    [Fact]
    public async Task AColourWriteInFlight_DoesNotUndoTheBlackout()
    {
        var settings = new AppSettings();
        var hardware = new A3Hardware();
        using var vm = await ReadyAsync(hardware, settings);

        hardware.LightingDelay = TimeSpan.FromMilliseconds(400);
        vm.PrimaryColor = "#FF0000";
        await WaitUntilAsync(() => hardware.LightingInFlight > 0);

        await vm.ToggleLightingBlackoutCommand.ExecuteAsync(null);
        await Task.Delay(300);

        Assert.True(vm.IsLightingBlackedOut);
        Assert.True(settings.IsBacklightOff);
        Assert.Equal("OFF", settings.KeyboardLighting?.Effect);
        Assert.Equal("OFF", hardware.LightingSnapshot()[^1].Effect);
    }

    // ---------------------------------------------------------------- L5. the re-assertion and the charging colour

    [Fact]
    public async Task ATrayRestore_PutsThePaintedChargingColourBack()
    {
        var settings = LitSettings();
        settings.ChargingColorEnabled = true;
        settings.ChargingColor = "#FF00FF";
        var hardware = new A3Hardware { ReportedLighting = settings.KeyboardLighting };

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), powerState: () => (true, 80));
        await vm.InitializeAsync();
        await vm.ChargingLightingTask;
        Assert.Equal("#FF00FF", hardware.LightingSnapshot()[^1].Color);

        hardware.ClearLighting();
        await vm.ReassertKeyboardLightingAsync();
        await vm.ChargingLightingTask;

        // The user's colour went back first (that is the re-assertion), and the charging colour over it.
        Assert.Equal("#FF00FF", hardware.LightingSnapshot()[^1].Color);
    }

    // ---------------------------------------------------------------- L6. automatic writes never elevate

    [Fact]
    public async Task TheChargingPaintAndItsHandBack_NeverAllowElevation_ButAUserColourDoes()
    {
        var settings = LitSettings();
        settings.ChargingColorEnabled = true;
        bool onAdapter = true;
        var hardware = new A3Hardware { ReportedLighting = settings.KeyboardLighting };

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(),
            powerState: () => (onAdapter, 80));
        await vm.InitializeAsync();
        await vm.ChargingLightingTask;

        onAdapter = false;
        await vm.RefreshTelemetryAsync();
        await vm.ChargingLightingTask;

        var automatic = hardware.LightingSnapshot();
        Assert.NotEmpty(automatic);
        Assert.All(automatic, w => Assert.False(w.ElevationAllowed));
        Assert.True(hardware.AllowElevationForLighting);

        hardware.ClearLighting();
        vm.PrimaryColor = "#123456";
        var user = await WaitForLightingWriteAsync(hardware);
        Assert.True(user.ElevationAllowed);
    }

    [Fact]
    public async Task TheTemperatureTick_NeverAllowsElevation()
    {
        var settings = LitSettings();
        var hardware = new A3Hardware { ReportedLighting = settings.KeyboardLighting };
        using var vm = await ReadyAsync(hardware, settings);

        vm.ReactiveLightingEnabled = true;
        hardware.ClearLighting();

        hardware.Sample = new MonitorData
        {
            CpuTemperature = 95, GpuTemperature = 90, CpuFanSpeed = 4000, GpuFanSpeed = 4000,
            IsFanRpmMeasured = true, IsTemperatureMeasured = true
        };
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        Assert.NotEmpty(hardware.LightingSnapshot());
        Assert.All(hardware.LightingSnapshot(), w => Assert.False(w.ElevationAllowed));
        Assert.True(hardware.AllowElevationForLighting);
    }

    [Fact]
    public async Task SchoolModesLightingWrites_NeverAllowElevation()
    {
        var settings = LitSettings();
        var hardware = new A3Hardware { ReportedLighting = settings.KeyboardLighting };
        var school = School(hardware, settings);
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), schoolMode: school,
            powerState: () => (false, 60));
        await vm.InitializeAsync();
        hardware.ClearLighting();

        vm.SchoolModeOn = true;
        await WaitUntilAsync(() => settings.SchoolModeEnabled && hardware.LightingSnapshot().Count > 0);
        vm.SchoolModeOn = false;
        await WaitUntilAsync(() => !settings.SchoolModeEnabled && hardware.LightingSnapshot().Count > 1);

        Assert.All(hardware.LightingSnapshot(), w => Assert.False(w.ElevationAllowed));
        Assert.True(hardware.AllowElevationForLighting);
    }

    // ---------------------------------------------------------------- L7. a preset that did not land

    [Fact]
    public async Task APresetTheKeyboardRefused_IsNeitherStoredNorAnnouncedAsApplied()
    {
        var settings = new AppSettings();
        var hardware = new A3Hardware();
        using var vm = await ReadyAsync(hardware, settings);
        var stored = settings.SelectedRgbPreset;

        hardware.LightingSucceeds = false;
        var preset = vm.RgbProfiles.First(p => !ReferenceEquals(p, vm.CustomProfile));
        vm.SelectedRgbProfile = preset;
        await WaitUntilAsync(() => vm.HasRgbProfileStatus);

        Assert.Equal(stored, settings.SelectedRgbPreset);
        Assert.DoesNotContain(vm.Labels.Applied, vm.RgbProfileStatus, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- L8. the plug pulled during the paint

    [Fact]
    public async Task APlugPulledWhileTheChargingPaintIsInFlight_PutsTheOwnColourBack()
    {
        var settings = LitSettings();
        bool onAdapter = true;
        var hardware = new A3Hardware { ReportedLighting = settings.KeyboardLighting };
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(),
            powerState: () => (onAdapter, 80));
        await vm.InitializeAsync();
        hardware.ClearLighting();

        // The paint is sent and is slow to answer; the plug comes out meanwhile.
        hardware.LightingDelay = TimeSpan.FromMilliseconds(400);
        vm.ChargingColorEnabled = true;
        var paint = vm.ChargingLightingTask;
        await WaitUntilAsync(() => hardware.LightingInFlight > 0);

        onAdapter = false;
        await vm.RefreshTelemetryAsync();
        await paint;
        await vm.ChargingLightingTask;
        await WaitUntilAsync(() => hardware.LightingInFlight == 0);

        Assert.Equal(settings.KeyboardLighting!.Color, hardware.LightingSnapshot()[^1].Color);
    }

    // ---------------------------------------------------------------- helpers

    internal static AppSettings LitSettings()
    {
        var lit = new LightingConfig
        {
            Effect = "STATIC", Brightness = 5, Speed = 3, Direction = 1, Color = "#00E5FF",
            LEDs = new List<string> { "#00E5FF", "#00E5FF", "#00E5FF", "#00E5FF" }
        };
        return new AppSettings { KeyboardLighting = lit, LastActiveLighting = LightingPolicy.Clone(lit) };
    }

    /// <summary>School mode with the same lighting hooks the composition root wires, minus its own guards.</summary>
    internal static SchoolModeService School(A3Hardware hardware, AppSettings settings)
    {
        return new SchoolModeService(hardware, new NoBoost(), new NoDisplay(), new NoBrightness(), settings,
            new NoScheme())
        {
            BackupPath = Path.Combine(Path.GetTempPath(), "nitrolite-a3-school-" + Guid.NewGuid().ToString("N") + ".json"),
            GetCurrentOperatingMode = () => hardware.ReportedMode,
            GetCurrentFanMode = () => hardware.ReportedFan,
            TurnLightingOffAsync = async () =>
            {
                var current = await hardware.GetLightingConfigAsync();
                if (current == null || LightingPolicy.IsOff(current)) return false;
                return await hardware.TurnOffLightingAsync();
            },
            RestoreLightingAsync = async () =>
            {
                var last = settings.LastActiveLighting ?? settings.KeyboardLighting;
                if (last == null) return;
                await hardware.SetLightingAsync(last.Effect, last.Brightness, last.Speed, last.Direction, last.Color,
                    last.LEDs.ToArray());
            }
        };
    }

    internal static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-a3-" + Guid.NewGuid().ToString("N") + ".json");

    private static async Task<MainViewModel> ReadyAsync(A3Hardware hardware, AppSettings settings)
    {
        var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), powerState: () => (false, 60));
        await vm.InitializeAsync();
        hardware.ClearLighting();
        return vm;
    }

    internal static async Task<LightingWrite> WaitForLightingWriteAsync(A3Hardware hardware)
    {
        await WaitUntilAsync(() => hardware.LightingSnapshot().Count > 0);
        return hardware.LightingSnapshot()[^1];
    }

    internal static Task WaitUntilAsync(Func<bool> condition, int timeoutMs = Settle) =>
        CleanupA1Tests.WaitUntilAsync(condition, timeoutMs);

    internal sealed record LightingWrite(string Effect, int Brightness, int Direction, string Color, bool ElevationAllowed);

    /// <summary>A backend that records every write and touches no hardware.</summary>
    internal sealed class A3Hardware : IHardwareController, ILightingElevationControl
    {
        private readonly object _lock = new();
        private readonly List<LightingWrite> _lighting = new();
        private readonly List<OperatingMode> _modeWrites = new();
        private readonly List<FanMode> _fanWrites = new();
        private int _inFlight;

        public string BackendName => "Test";
        public bool IsAvailable => true;
        public bool AllowElevationForLighting { get; set; } = true;

        public HardwareCapabilities Capabilities { get; set; } = new()
        {
            Backend = "Test",
            ServiceAvailable = true,
            FanControl = true,
            OperatingModeControl = true,
            KeyboardLighting = true,
            SupportedOperatingModes = new[]
            {
                OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo, OperatingMode.Eco
            }
        };

        public MonitorData Sample { get; set; } = new()
        {
            CpuTemperature = 55, GpuTemperature = 50, CpuFanSpeed = 2000, GpuFanSpeed = 2000,
            IsFanRpmMeasured = true, IsTemperatureMeasured = true
        };

        /// <summary>What a lighting read answers; follows the writes.</summary>
        public LightingConfig? ReportedLighting { get; set; }

        public OperatingMode ReportedMode { get; set; } = OperatingMode.Default;
        public FanMode ReportedFan { get; set; } = FanMode.Auto;
        public bool LightingSucceeds { get; set; } = true;
        public TimeSpan LightingDelay { get; set; } = TimeSpan.Zero;
        public int LightingInFlight => System.Threading.Volatile.Read(ref _inFlight);

        public List<LightingWrite> LightingSnapshot()
        {
            lock (_lock) return _lighting.ToList();
        }

        public List<OperatingMode> ModeWrites()
        {
            lock (_lock) return _modeWrites.ToList();
        }

        public List<FanMode> FanWrites()
        {
            lock (_lock) return _fanWrites.ToList();
        }

        public void ClearLighting()
        {
            lock (_lock) _lighting.Clear();
        }

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(true);
        public Task<HardwareCapabilities> GetCapabilitiesAsync() => Task.FromResult(Capabilities);
        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(Sample);
        /// <summary>False: the mode read answers nothing, like a backend that cannot report it.</summary>
        public bool ReportsMode { get; set; } = true;

        public Task<OperatingMode?> GetOperatingModeAsync() =>
            Task.FromResult<OperatingMode?>(ReportsMode ? ReportedMode : null);

        public Task<bool> SetOperatingModeAsync(OperatingMode mode)
        {
            lock (_lock) _modeWrites.Add(mode);
            ReportedMode = mode;
            return Task.FromResult(true);
        }

        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(null);
        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(true);
        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);
        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(ReportedFan);

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
        {
            lock (_lock) _fanWrites.Add(mode);
            ReportedFan = mode;
            return Task.FromResult(true);
        }

        public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult(ReportedLighting);

        public async Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction,
            string primaryColor, string[]? zoneColors)
        {
            bool elevation = AllowElevationForLighting;
            System.Threading.Interlocked.Increment(ref _inFlight);
            try
            {
                if (LightingDelay > TimeSpan.Zero) await Task.Delay(LightingDelay).ConfigureAwait(false);
                if (!LightingSucceeds) return false;

                lock (_lock) _lighting.Add(new LightingWrite(effect, brightness, direction, primaryColor, elevation));
                ReportedLighting = new LightingConfig
                {
                    Effect = effect, Brightness = brightness, Speed = speed, Direction = direction, Color = primaryColor,
                    LEDs = (zoneColors ?? Array.Empty<string>()).ToList()
                };
                return true;
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<bool> TurnOffLightingAsync()
        {
            lock (_lock) _lighting.Add(new LightingWrite("OFF", 0, 1, "#000000", AllowElevationForLighting));
            ReportedLighting = new LightingConfig { Effect = "OFF", Brightness = 0, Color = "#000000" };
            return Task.FromResult(true);
        }

        public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(false);
        public Task<bool> SetBacklightTimeoutAsync(bool enable30s) => Task.FromResult(true);
        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => Task.FromResult<BatteryChargeMode?>(null);
        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => Task.FromResult(true);
        public Task<bool?> GetCoolBoostAsync() => Task.FromResult<bool?>(false);
        public Task<bool> SetCoolBoostAsync(bool on) => Task.FromResult(true);
        public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(true);

        public void Dispose()
        {
        }
    }

    internal sealed class NoBoost : ICpuBoostAccess
    {
        public bool IsSupported => false;
        public CpuBoostMode? QueryBoostModeExact(bool isAc) => null;
        public Task<bool> SetBoostModeAsync(CpuBoostMode mode, bool targetAc = true, bool targetDc = true) =>
            Task.FromResult(false);
    }

    internal sealed class NoDisplay : IDisplayRefreshRateService
    {
        public bool IsSupported => false;
        public int GetCurrentRefreshRate() => 0;
        public int GetMaxRefreshRate() => 0;
        public bool SetRefreshRate(int hz) => false;
    }

    internal sealed class NoBrightness : IScreenBrightnessService
    {
        public int? GetBrightness() => null;
        public bool SetBrightness(int percent) => false;
    }

    internal sealed class NoScheme : IPowerSchemeAccess
    {
        public bool IsSupported => false;

        public bool TryReadDc(Guid subGroup, Guid setting, out uint value)
        {
            value = 0;
            return false;
        }

        public bool TryWriteDc(Guid subGroup, Guid setting, uint value) => false;
    }
}
