using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The fan-stall watchdog is the only thing between a failed fan and a cooked die, so its edges are pinned: it must
/// not fire on one bad sample, must not stay silent once the condition is real, and must not turn a broken machine
/// into a toast every second.
/// </summary>
public class FanStallWatchdogTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double seconds) => Start.AddSeconds(seconds);

    [Fact]
    public void AHotDieWithAStoppedFanIsNotAFaultUntilItHasHeldForTenSeconds()
    {
        var watchdog = new FanStallWatchdog();

        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(0)));
        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(9.9)));
        Assert.False(watchdog.HasAlerted);

        var verdict = watchdog.Observe(90, 60, 0, 2000, At(10));
        Assert.NotNull(verdict);
        Assert.Equal(FanStallTarget.Cpu, verdict!.Target);
        Assert.Equal(90, verdict.TemperatureC);
        Assert.Equal(0, verdict.FanRpm);
        Assert.Equal(TimeSpan.FromSeconds(10), verdict.Sustained);
        Assert.True(watchdog.HasAlerted);
    }

    [Theory]
    [InlineData(87)]
    [InlineData(60)]
    [InlineData(0)]
    public void ACoolDieNeverCountsEvenWithAStoppedFan(int temperatureC)
    {
        var watchdog = new FanStallWatchdog();

        for (int second = 0; second <= 60; second++)
        {
            Assert.Null(watchdog.Observe(temperatureC, temperatureC, 0, 0, At(second)));
        }

        Assert.False(watchdog.HasAlerted);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(2400)]
    public void AFanThatTurnsFastEnoughIsNeverAFault(int rpm)
    {
        var watchdog = new FanStallWatchdog();

        for (int second = 0; second <= 60; second++)
        {
            Assert.Null(watchdog.Observe(95, 95, rpm, rpm, At(second)));
        }

        Assert.False(watchdog.HasAlerted);
    }

    [Fact]
    public void TheThresholdIsInclusiveOnBothSides()
    {
        // 88 °C is hot and 399 RPM is stopped: the boundary belongs to the fault, not to the safe side.
        var watchdog = new FanStallWatchdog();

        Assert.Null(watchdog.Observe(FanStallWatchdog.HotTemperatureC, 60, FanStallWatchdog.StallRpm - 1, 2000, At(0)));
        Assert.NotNull(watchdog.Observe(FanStallWatchdog.HotTemperatureC, 60, FanStallWatchdog.StallRpm - 1, 2000, At(10)));
    }

    [Fact]
    public void AConditionThatBreaksRestartsTheClock()
    {
        var watchdog = new FanStallWatchdog();

        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(0)));
        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(8)));

        // One good sample in between: the fan turned, so the ten seconds start over from that sample.
        Assert.Null(watchdog.Observe(90, 60, 1500, 2000, At(9)));
        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(10)));
        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(19.9)));
        Assert.NotNull(watchdog.Observe(90, 60, 0, 2000, At(20)));
    }

    [Fact]
    public void TheGpuIsJudgedOnItsOwnFanAndItsOwnTemperature()
    {
        var watchdog = new FanStallWatchdog();

        // The CPU is cool with a stopped fan; the GPU is hot with a stopped fan. Only the GPU is at fault.
        Assert.Null(watchdog.Observe(50, 92, 0, 0, At(0)));
        var verdict = watchdog.Observe(50, 92, 0, 0, At(10));

        Assert.NotNull(verdict);
        Assert.Equal(FanStallTarget.Gpu, verdict!.Target);
        Assert.Equal(92, verdict.TemperatureC);
    }

    [Fact]
    public void BothDiesStalledReportsBothAndNamesTheOneStalledLongest()
    {
        var watchdog = new FanStallWatchdog();

        // The GPU stalls at once; the CPU only gets hot five seconds later.
        Assert.Null(watchdog.Observe(50, 92, 0, 0, At(0)));
        Assert.Null(watchdog.Observe(90, 92, 0, 0, At(5)));
        var verdict = watchdog.Observe(90, 92, 0, 0, At(15));

        Assert.NotNull(verdict);
        Assert.Equal(FanStallTarget.Both, verdict!.Target);
        // The GPU has been stalled for fifteen seconds, the CPU for ten: the message points at the GPU.
        Assert.Equal(92, verdict.TemperatureC);
        Assert.Equal(TimeSpan.FromSeconds(15), verdict.Sustained);
    }

    [Fact]
    public void ASecondAlertWaitsForTheCooldownAndThenFiresWithoutAnotherTenSeconds()
    {
        var watchdog = new FanStallWatchdog();

        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(0)));
        Assert.NotNull(watchdog.Observe(90, 60, 0, 2000, At(10)));

        // Still broken, but the user has just been told.
        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(60)));
        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(10 + FanStallWatchdog.Cooldown.TotalSeconds - 1)));

        // The cooldown expires while the fault never went away: the next alert is due at once, not ten seconds later.
        Assert.NotNull(watchdog.Observe(90, 60, 0, 2000, At(10 + FanStallWatchdog.Cooldown.TotalSeconds)));
    }

    [Fact]
    public void ResetForgetsTheCooldownSoTheNextStallAlertsImmediately()
    {
        var watchdog = new FanStallWatchdog();

        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(0)));
        Assert.NotNull(watchdog.Observe(90, 60, 0, 2000, At(10)));

        watchdog.Reset();
        Assert.False(watchdog.HasAlerted);

        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(11)));
        Assert.NotNull(watchdog.Observe(90, 60, 0, 2000, At(21)));
    }

    [Fact]
    public void AClockThatJumpsBackwardsDoesNotFireEarly()
    {
        // The poll loop feeds DateTime.UtcNow, which an NTP correction can move backwards. A negative elapsed time
        // must read as "not yet", never as "ten seconds have passed".
        var watchdog = new FanStallWatchdog();

        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(0)));
        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(-3600)));
        Assert.Null(watchdog.Observe(90, 60, 0, 2000, At(5)));
        Assert.NotNull(watchdog.Observe(90, 60, 0, 2000, At(10)));
    }

    [Fact]
    public void TheConstantsMatchTheDocumentedContract()
    {
        Assert.Equal(88, FanStallWatchdog.HotTemperatureC);
        Assert.Equal(400, FanStallWatchdog.StallRpm);
        Assert.Equal(TimeSpan.FromSeconds(10), FanStallWatchdog.SustainFor);
        Assert.Equal(TimeSpan.FromMinutes(5), FanStallWatchdog.Cooldown);
    }
}
