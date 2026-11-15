using System.Diagnostics;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Covers the two performance fixes from the resource audit: the 60 s cache in front of the
/// <c>powercfg.exe</c> fallback, and the safe-discharge load that used to burn two pool threads at full speed.
/// </summary>
public class PerformancePolishTests
{
    private static CpuPowerService NewService() =>
        new(autoInitialize: false, bypassNativePowerCalls: true);

    private static ProcessStartInfo ExitZero(string args) => new()
    {
        FileName = "cmd.exe",
        Arguments = "/c exit 0",
        UseShellExecute = false,
        CreateNoWindow = true
    };

    // ---------------------------------------------------------------- boost-mode cache

    [Fact]
    public void PowerCfgCache_ReturnsTheStoredValueInsideTheTtl()
    {
        var service = NewService();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        service.UtcNowFactory = () => now;

        service.StorePowerCfgMode(isAc: true, CpuBoostMode.Efficient);

        Assert.True(service.TryGetCachedPowerCfgMode(isAc: true, out var mode));
        Assert.Equal(CpuBoostMode.Efficient, mode);
    }

    [Fact]
    public void PowerCfgCache_ExpiresAfterSixtySeconds()
    {
        var service = NewService();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        service.UtcNowFactory = () => now;

        service.StorePowerCfgMode(isAc: true, CpuBoostMode.Efficient);

        now = now.AddSeconds(59);
        Assert.True(service.TryGetCachedPowerCfgMode(isAc: true, out _));

        now = now.AddSeconds(2);
        Assert.False(service.TryGetCachedPowerCfgMode(isAc: true, out _));
    }

    [Fact]
    public void PowerCfgCache_KeepsAcAndDcApart()
    {
        var service = NewService();
        service.StorePowerCfgMode(isAc: true, CpuBoostMode.Aggressive);
        service.StorePowerCfgMode(isAc: false, CpuBoostMode.Disabled);

        Assert.True(service.TryGetCachedPowerCfgMode(isAc: true, out var ac));
        Assert.Equal(CpuBoostMode.Aggressive, ac);

        Assert.True(service.TryGetCachedPowerCfgMode(isAc: false, out var dc));
        Assert.Equal(CpuBoostMode.Disabled, dc);
    }

    [Fact]
    public void PowerCfgCache_CachesAFailedReadToo()
    {
        // A hung powercfg must not be retried every 5 s; the null result is the answer for the whole TTL.
        var service = NewService();
        service.StorePowerCfgMode(isAc: true, null);

        Assert.True(service.TryGetCachedPowerCfgMode(isAc: true, out var mode));
        Assert.Null(mode);
    }

    [Fact]
    public void PowerCfgCache_InvalidateDropsBothSlots()
    {
        var service = NewService();
        service.StorePowerCfgMode(isAc: true, CpuBoostMode.Aggressive);
        service.StorePowerCfgMode(isAc: false, CpuBoostMode.Disabled);

        service.InvalidatePowerCfgCache();

        Assert.False(service.TryGetCachedPowerCfgMode(isAc: true, out _));
        Assert.False(service.TryGetCachedPowerCfgMode(isAc: false, out _));
    }

    [Fact]
    public void SetBoostModePowerCfg_SuccessInvalidatesTheCache()
    {
        var service = NewService();
        service.ProcessInfoFactory = ExitZero;
        service.StorePowerCfgMode(isAc: true, CpuBoostMode.Disabled);
        service.StorePowerCfgMode(isAc: false, CpuBoostMode.Disabled);

        Assert.True(service.SetBoostModePowerCfg(CpuBoostMode.Aggressive));

        Assert.False(service.TryGetCachedPowerCfgMode(isAc: true, out _));
        Assert.False(service.TryGetCachedPowerCfgMode(isAc: false, out _));
    }

    [Fact]
    public async Task SetBoostModeAsync_SuccessInvalidatesTheCache()
    {
        var service = NewService();
        service.StorePowerCfgMode(isAc: true, CpuBoostMode.Disabled);

        Assert.True(await service.SetBoostModeAsync(CpuBoostMode.Aggressive));

        Assert.False(service.TryGetCachedPowerCfgMode(isAc: true, out _));
    }

    [Fact]
    public void QueryCurrentBoostModePowerCfg_IgnoresTheCacheWhenAProcessSeamIsInjected()
    {
        // The cache exists to avoid spawning a real process. A test seam has no process to avoid, and caching it
        // would hide a value the test changed between two calls.
        var service = NewService();
        service.ProcessInfoFactory = args => new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 1",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        service.StorePowerCfgMode(isAc: true, CpuBoostMode.Aggressive);

        Assert.Null(service.QueryCurrentBoostModePowerCfg(isAc: true));
    }

    // ---------------------------------------------------------------- safe discharge

    private static BatteryWatchdogService NewWatchdog(byte percent, byte acLine = 0) =>
        new(attachSystemEvents: false)
        {
            SuppressSounds = true,
            PowerStatusSeam = () => (true, acLine, percent)
        };

    [Fact]
    public void SafeDischarge_RunsOnOneLowestPriorityBackgroundThread()
    {
        using var watchdog = NewWatchdog(90);
        try
        {
            Assert.True(watchdog.StartSafeDischarge());

            var worker = watchdog.DischargeWorker;
            Assert.NotNull(worker);
            Assert.Equal(ThreadPriority.Lowest, worker!.Priority);
            Assert.True(worker.IsBackground);
            Assert.Equal("NitroLite.SafeDischarge", worker.Name);
        }
        finally
        {
            watchdog.StopSafeDischarge();
        }
    }

    [Fact]
    public void SafeDischarge_DoesNotLowerThePriorityOfTheCallingThread()
    {
        // The old code lowered nothing, but a pool thread that did would carry the change back into the pool. The
        // dedicated thread must leave the caller alone.
        using var watchdog = NewWatchdog(90);
        var before = Thread.CurrentThread.Priority;
        try
        {
            Assert.True(watchdog.StartSafeDischarge());
            Assert.Equal(before, Thread.CurrentThread.Priority);
        }
        finally
        {
            watchdog.StopSafeDischarge();
        }
    }

    [Fact]
    public void SafeDischarge_RefusesOnAcPower()
    {
        using var watchdog = NewWatchdog(90, acLine: 1);
        Assert.False(watchdog.StartSafeDischarge());
        Assert.False(watchdog.IsDischarging);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(79)]
    [InlineData(40)]
    public void SafeDischarge_RefusesAtOrBelowTheEightyPercentLimit(byte percent)
    {
        using var watchdog = NewWatchdog(percent);
        Assert.False(watchdog.StartSafeDischarge());
        Assert.False(watchdog.IsDischarging);
    }

    [Fact]
    public void SafeDischarge_StopRaisesTheEventAndClearsTheWorker()
    {
        using var watchdog = NewWatchdog(90);
        var states = new List<bool>();
        watchdog.DischargeStateChanged += active => { lock (states) states.Add(active); };

        Assert.True(watchdog.StartSafeDischarge());
        Assert.True(watchdog.IsDischarging);

        watchdog.StopSafeDischarge();

        Assert.False(watchdog.IsDischarging);
        lock (states)
        {
            Assert.Equal(new[] { true, false }, states);
        }
    }

    [Fact]
    public async Task SafeDischarge_ReachesTheTargetAndStopsItself()
    {
        byte percent = 90;
        using var watchdog = new BatteryWatchdogService(attachSystemEvents: false)
        {
            SuppressSounds = true,
            PowerStatusSeam = () => (true, 0, percent)
        };

        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        watchdog.DischargeTargetReached += () => reached.TrySetResult(true);

        Assert.True(watchdog.StartSafeDischarge());
        percent = 80;

        var finished = await Task.WhenAny(reached.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(reached.Task, finished);
        Assert.False(watchdog.IsDischarging);
    }

    [Fact]
    public void SafeDischarge_ReconnectingTheAdapterStopsTheLoad()
    {
        byte acLine = 0;
        using var watchdog = new BatteryWatchdogService(attachSystemEvents: false)
        {
            SuppressSounds = true,
            PowerStatusSeam = () => (true, acLine, 90)
        };

        Assert.True(watchdog.StartSafeDischarge());
        acLine = 1;

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (watchdog.IsDischarging && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.False(watchdog.IsDischarging);
    }
}
