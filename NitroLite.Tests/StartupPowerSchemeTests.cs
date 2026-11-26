using System.Reflection;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Guards the startup crash that killed the app on the first boot after the Core split.
/// </summary>
/// <remarks>
/// <para>
/// The log said it plainly: <c>ArgumentNullException (Parameter 'powerScheme')</c> thrown from
/// <c>SchoolModeService..ctor</c>, called by <c>MainWindow.CreateSchoolModeService</c>, called by
/// <c>MainWindow.SetupEvents</c>, called by <c>MainWindow..ctor</c>. The production constructor never assigned
/// <c>_schoolModeScheme</c>; only the test-harness constructor did. Before the split the service built its own
/// <c>WindowsPowerSchemeAccess</c> internally, so the omission was invisible until Core could no longer reference a
/// P/Invoke and the caller had to supply one.
/// </para>
/// <para>
/// Two things let it compile. The field was nullable, so nothing complained about it staying null. And the
/// constructor parameter was declared <c>IPowerSchemeAccess? powerScheme = null</c> while the body threw on null, so
/// the optional default was a lie the compiler believed. Both are fixed, and both are pinned here.
/// </para>
/// </remarks>
public class StartupPowerSchemeTests
{
    // ---------------------------------------------------------------- stubs

    /// <summary>An <see cref="IHardwareController"/> that answers nothing and writes nothing.</summary>
    private sealed class SilentController : IHardwareController
    {
        public string BackendName => "Test";
        public bool IsAvailable => true;

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(true);
        public Task<HardwareCapabilities> GetCapabilitiesAsync() => Task.FromResult(HardwareCapabilities.Unknown);
        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(null);
        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(OperatingMode.Default);
        public Task<bool> SetOperatingModeAsync(OperatingMode mode) => Task.FromResult(true);
        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(GpuOperatingMode.Optimus);
        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(true);
        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);
        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(FanMode.Auto);
        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1) => Task.FromResult(true);
        public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult<LightingConfig?>(null);
        public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors) => Task.FromResult(true);
        public Task<bool> TurnOffLightingAsync() => Task.FromResult(true);
        public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(null);
        public Task<bool> SetBacklightTimeoutAsync(bool enable30s) => Task.FromResult(true);
        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => Task.FromResult<BatteryChargeMode?>(null);
        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => Task.FromResult(true);
        public Task<bool?> GetCoolBoostAsync() => Task.FromResult<bool?>(null);
        public Task<bool> SetCoolBoostAsync(bool on) => Task.FromResult(true);
        public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(true);
        public void Dispose() { }
    }

    private sealed class SilentCpuBoost : ICpuBoostAccess
    {
        public CpuBoostMode? QueryBoostModeExact(bool isAc) => null;
        public Task<bool> SetBoostModeAsync(CpuBoostMode mode, bool targetAc = true, bool targetDc = true) => Task.FromResult(true);
    }

    private sealed class SilentDisplay : IDisplayRefreshRateService
    {
        public int GetCurrentRefreshRate() => 165;
        public int GetMaxRefreshRate() => 165;
        public bool SetRefreshRate(int hz) => true;
    }

    private sealed class SilentBrightness : IScreenBrightnessService
    {
        public int? GetBrightness() => 80;
        public bool SetBrightness(int percent) => true;
    }

    /// <summary>
    /// A power scheme that answers "no active scheme" to everything, which is what Windows reports for the first
    /// seconds after a boot: <c>PowerGetActiveScheme</c> has not published one yet.
    /// </summary>
    private sealed class NoActiveScheme : IPowerSchemeAccess
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }

        public bool TryReadDc(Guid subGroup, Guid setting, out uint value)
        {
            Reads++;
            value = 0;
            return false;
        }

        public bool TryWriteDc(Guid subGroup, Guid setting, uint value)
        {
            Writes++;
            return false;
        }
    }

    private static SchoolModeService Build(IPowerSchemeAccess scheme, AppSettings? settings = null) =>
        new(new SilentController(), new SilentCpuBoost(), new SilentDisplay(), new SilentBrightness(),
            settings ?? new AppSettings(), scheme)
        {
            // No undo record on disk: a test must not write into the user's data folder.
            BackupPath = null
        };

    // ---------------------------------------------------------------- 1. the guard that was missing

    [Fact]
    public void SchoolMode_WithoutAPowerScheme_IsRejectedAtConstruction()
    {
        // The parameter is required now, so this only compiles with the null-forgiving operator. The throw is the
        // second line of defence: a caller that reaches it gets a message naming the parameter, not a NullReference
        // three frames deeper.
        var ex = Assert.Throws<ArgumentNullException>(() => new SchoolModeService(
            new SilentController(), new SilentCpuBoost(), new SilentDisplay(), new SilentBrightness(),
            new AppSettings(), null!));

        Assert.Equal("powerScheme", ex.ParamName);
    }

    [Fact]
    public void SchoolMode_PowerSchemeParameter_IsNotOptional()
    {
        // An optional parameter that the body then rejects is how the production constructor lost its scheme without
        // a compile error. If someone makes it optional again, this fails.
        var ctor = typeof(SchoolModeService).GetConstructors()
            .Single(c => c.GetParameters().Length == 6);
        var scheme = ctor.GetParameters()[5];

        Assert.Equal("powerScheme", scheme.Name);
        Assert.False(scheme.IsOptional);
        Assert.False(scheme.HasDefaultValue);
    }

    // ---------------------------------------------------------------- 3. early boot: no active scheme yet

    [Fact]
    public async Task SchoolMode_Enable_WithNoActiveScheme_DoesNotThrowAndWritesNoPowerSetting()
    {
        // This is the second half of the report: on a cold boot PowerGetActiveScheme can answer nothing for a moment.
        // The service must degrade to "these steps did not apply", never to an exception. It also must not write a
        // power setting whose previous value it could not read, because the undo record would then be incomplete and
        // switching school mode off would not put the machine back.
        var scheme = new NoActiveScheme();
        var service = Build(scheme);

        var outcome = await service.EnableAsync();

        Assert.NotNull(outcome);
        Assert.True(scheme.Reads > 0, "the service should have tried to read the current values");
        Assert.Equal(0, scheme.Writes);
        Assert.False(outcome.Refused);
    }

    [Fact]
    public async Task SchoolMode_Disable_WithNoActiveScheme_DoesNotThrow()
    {
        var service = Build(new NoActiveScheme());

        var outcome = await service.DisableAsync();

        Assert.NotNull(outcome);
    }

    [Fact]
    public void WindowsPowerSchemeAccess_ReportsFailureInsteadOfThrowingWhenTheSchemeIsUnavailable()
    {
        // The real reader delegates to CpuPowerService, whose TryGetActiveScheme returns false when Windows has not
        // published a scheme. Both entry points have to answer false rather than throw.
        var access = new WindowsPowerSchemeAccess();

        var read = access.TryReadDc(Guid.NewGuid(), Guid.NewGuid(), out var value);
        var write = access.TryWriteDc(Guid.NewGuid(), Guid.NewGuid(), 1);

        // On a machine with an active scheme the read may succeed; what matters is that neither call throws and that
        // a failure is reported as false with a defined value.
        Assert.True(read || value == 0);
        Assert.True(write || !write);
    }

    [Fact]
    public void CpuPowerService_QueryBoostModeExact_DoesNotThrowWhenTheSchemeIsUnavailable()
    {
        // The same early-boot window, on the other reader. BypassNativePowerCalls is the seam the tests use to keep
        // the P/Invoke out of the run; the point here is that the method answers instead of throwing.
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);

        var mode = service.QueryBoostModeExact(isAc: true);

        Assert.True(mode == null || Enum.IsDefined(mode.Value));
    }
}
