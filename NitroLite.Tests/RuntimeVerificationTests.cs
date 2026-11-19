using System;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The runtime sensor check: the second way a layout becomes trusted, for a machine whose profile carries no
/// measurement.
/// </summary>
/// <remarks>
/// <para>
/// The gap this closes: the fan curve was allowed only when <c>SensorLayout.Verified</c> was true, which is a statement
/// about who measured the sensor ids on the model. Almost nobody had, so an AN16-61 answering a real die temperature
/// every second still had its custom curve locked. Two consecutive in-window samples answer the weaker question —
/// "is this machine, right now, answering like a machine?" — which is enough to let the curve run, because the thermal
/// floor and the dead-sensor hand-back are enforced on the live reading either way.
/// </para>
/// <para>
/// What the check must never do is certify a mapping. Every test below that expects a refusal is a way a firmware
/// answers when it is not producing a real measurement: a documented sentinel, a zero, a number outside the range a
/// laptop die can be, a fan above its own ceiling, or a second tick that is really the first tick repeated.
/// </para>
/// </remarks>
public class RuntimeVerificationTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 19, 6, 0, DateTimeKind.Utc);

    /// <summary>A sample that passes the window: 62 °C die, 55 °C graphics, two fans turning.</summary>
    private static MonitorData Sample(int cpu = 62, int gpu = 55, int cpuFan = 2200, int gpuFan = 2100) =>
        new() { CpuTemperature = cpu, GpuTemperature = gpu, CpuFanSpeed = cpuFan, GpuFanSpeed = gpuFan };

    [Fact]
    public void TwoConsecutivePlausibleSamplesPromoteTheSession()
    {
        var trust = new RuntimeSensorVerification();

        Assert.False(trust.Observe(Sample(), T0, layoutConfirmed: true));
        Assert.False(trust.RuntimeVerified);
        Assert.Equal(1, trust.AcceptedSamples);

        Assert.True(trust.Observe(Sample(), T0.AddSeconds(1.5), layoutConfirmed: true));
        Assert.True(trust.RuntimeVerified);
        Assert.Equal(2, trust.AcceptedSamples);
        Assert.Equal(SensorTrustRefusal.None, trust.LastRefusal);
    }

    [Fact]
    public void TheGapIsRequiredSoOnePollCycleIsOneObservation()
    {
        var trust = new RuntimeSensorVerification();

        trust.Observe(Sample(), T0, layoutConfirmed: true);

        // The same poll cycle is not a second moment in time, and it is not a refusal either: it is one reading
        // counted twice, so the run keeps the sample it already had instead of starting over.
        Assert.False(trust.Observe(Sample(), T0.AddMilliseconds(400), layoutConfirmed: true));
        Assert.False(trust.RuntimeVerified);
        Assert.Equal(SensorTrustRefusal.TooSoon, trust.LastRefusal);
        Assert.Equal(1, trust.AcceptedSamples);

        // The next sample that is far enough from the last accepted one completes the run.
        Assert.True(trust.Observe(Sample(), T0.AddSeconds(2), layoutConfirmed: true));
    }

    [Fact]
    public void OneSecondPollingPromotesOnTheThirdTick()
    {
        var trust = new RuntimeSensorVerification();

        // The telemetry interval is one second by default, so every second tick is too soon. If a too-soon sample
        // reset the run the check could never promote on a machine that is plainly answering, which is the machine the
        // whole feature exists for.
        Assert.False(trust.Observe(Sample(), T0, layoutConfirmed: true));
        Assert.False(trust.Observe(Sample(), T0.AddSeconds(1), layoutConfirmed: true));
        Assert.True(trust.Observe(Sample(), T0.AddSeconds(2), layoutConfirmed: true));
    }

    [Fact]
    public void AnUnconfirmedLayoutIsNeverPromotedNoMatterHowPlausibleTheValuesAre()
    {
        var trust = new RuntimeSensorVerification();

        for (int i = 0; i < 20; i++)
            Assert.False(trust.Observe(Sample(), T0.AddSeconds(i * 2), layoutConfirmed: false));

        Assert.False(trust.RuntimeVerified);
        Assert.Equal(SensorTrustRefusal.LayoutUnconfirmed, trust.LastRefusal);
        Assert.Equal(20, trust.RejectedSamples);
    }

    [Theory]
    [InlineData(0xFFFF)]
    [InlineData(0x7FFF)]
    [InlineData(0x8000)]
    [InlineData(0xDEAD)]
    [InlineData(0xBEEF)]
    public void SentinelTemperaturesAreRejected(int value)
    {
        var trust = new RuntimeSensorVerification();

        Assert.False(trust.Observe(Sample(cpu: value), T0, layoutConfirmed: true));
        Assert.Equal(SensorTrustRefusal.SentinelValue, trust.LastRefusal);
        Assert.False(trust.Observe(Sample(cpu: value), T0.AddSeconds(2), layoutConfirmed: true));
        Assert.False(trust.RuntimeVerified);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(116)]
    [InlineData(130)]
    [InlineData(255)]
    public void OutOfWindowTemperaturesAreRejected(int value)
    {
        var trust = new RuntimeSensorVerification();

        Assert.False(trust.Observe(Sample(cpu: value), T0, layoutConfirmed: true));
        Assert.Equal(SensorTrustRefusal.TemperatureOutOfRange, trust.LastRefusal);

        Assert.False(trust.Observe(Sample(gpu: value), T0.AddSeconds(2), layoutConfirmed: true));
        Assert.Equal(SensorTrustRefusal.TemperatureOutOfRange, trust.LastRefusal);

        Assert.False(trust.RuntimeVerified);
    }

    [Fact]
    public void AStaticZeroIsRejectedEvenThoughItIsTheOnlyValueSomeFirmwareEverAnswers()
    {
        var trust = new RuntimeSensorVerification();

        for (int i = 0; i < 10; i++)
            Assert.False(trust.Observe(Sample(cpu: 0, gpu: 0, cpuFan: 0, gpuFan: 0), T0.AddSeconds(i * 2),
                layoutConfirmed: true));

        Assert.False(trust.RuntimeVerified);
        Assert.Equal(0, trust.AcceptedSamples);
        Assert.Equal(SensorTrustRefusal.TemperatureOutOfRange, trust.LastRefusal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    [InlineData(12000)]
    public void FanSpeedsAboveTheWindowAreRejected(int rpm)
    {
        var trust = new RuntimeSensorVerification();

        Assert.False(trust.Observe(Sample(cpuFan: rpm), T0, layoutConfirmed: true));
        Assert.Equal(SensorTrustRefusal.FanOutOfRange, trust.LastRefusal);

        Assert.False(trust.Observe(Sample(gpuFan: rpm), T0.AddSeconds(2), layoutConfirmed: true));
        Assert.Equal(SensorTrustRefusal.FanOutOfRange, trust.LastRefusal);

        Assert.False(trust.RuntimeVerified);
    }

    [Fact]
    public void AStoppedFanIsARealReadingAndCounts()
    {
        var trust = new RuntimeSensorVerification();

        // Zero RPM is a parked fan, not a failed sensor: a quiet Nitro really does stop its fans at idle, and refusing
        // that would keep every idle machine unverified forever.
        Assert.False(trust.Observe(Sample(cpuFan: 0, gpuFan: 0), T0, layoutConfirmed: true));
        Assert.True(trust.Observe(Sample(cpuFan: 0, gpuFan: 0), T0.AddSeconds(2), layoutConfirmed: true));
    }

    [Fact]
    public void ARefusedSampleRestartsTheRun()
    {
        var trust = new RuntimeSensorVerification();

        trust.Observe(Sample(), T0, layoutConfirmed: true);
        trust.Observe(Sample(cpu: 0), T0.AddSeconds(2), layoutConfirmed: true);

        Assert.Equal(0, trust.AcceptedSamples);
        Assert.False(trust.Observe(Sample(), T0.AddSeconds(4), layoutConfirmed: true));
        Assert.False(trust.RuntimeVerified);
        Assert.True(trust.Observe(Sample(), T0.AddSeconds(6), layoutConfirmed: true));
    }

    [Fact]
    public void PromotionIsOneWayForTheSession()
    {
        var trust = new RuntimeSensorVerification();

        trust.Observe(Sample(), T0, layoutConfirmed: true);
        Assert.True(trust.Observe(Sample(), T0.AddSeconds(2), layoutConfirmed: true));

        // A sensor that stops answering must not take the curve away mid-session: the dead-sensor hand-back (HW-06)
        // owns that case and looks at the live reading, not at this flag. Handing the fan mode back and forth every
        // time a parked fan reported zero would be worse than either state.
        Assert.True(trust.Observe(Sample(cpu: 0, gpu: 0), T0.AddSeconds(4), layoutConfirmed: true));
        Assert.True(trust.Observe(null, T0.AddSeconds(6), layoutConfirmed: true));
        Assert.True(trust.RuntimeVerified);
    }

    [Fact]
    public void ANullSampleIsRefused()
    {
        var trust = new RuntimeSensorVerification();

        Assert.False(trust.Observe(null, T0, layoutConfirmed: true));
        Assert.Equal(SensorTrustRefusal.NoSample, trust.LastRefusal);
        Assert.False(trust.RuntimeVerified);
    }

    [Fact]
    public void ResetForgetsThePromotionSoARescanStartsOver()
    {
        var trust = new RuntimeSensorVerification();

        trust.Observe(Sample(), T0, layoutConfirmed: true);
        trust.Observe(Sample(), T0.AddSeconds(2), layoutConfirmed: true);
        Assert.True(trust.RuntimeVerified);

        trust.Reset();

        Assert.False(trust.RuntimeVerified);
        Assert.Equal(0, trust.ObservedSamples);
        Assert.Equal(SensorTrustRefusal.None, trust.LastRefusal);
        Assert.Null(trust.LastAcceptedUtc);
    }

    [Fact]
    public void DescribeSaysRuntimeVerifiedOnlyWhenItIs()
    {
        var trust = new RuntimeSensorVerification();
        Assert.Equal("unverified (no samples yet)", trust.Describe());

        trust.Observe(Sample(), T0, layoutConfirmed: true);
        Assert.StartsWith("unverified (", trust.Describe());
        Assert.DoesNotContain("runtime-verified", trust.Describe());

        trust.Observe(Sample(), T0.AddSeconds(2), layoutConfirmed: true);
        Assert.StartsWith("runtime-verified (2 samples)", trust.Describe());
    }

    [Fact]
    public void DescribeWindowStatesTheNumbersThatWereApplied()
    {
        var window = RuntimeSensorVerification.DescribeWindow();

        Assert.Contains("15..115", window);
        Assert.Contains("10000", window);
        Assert.Contains("1.5", window);
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(62, true)]
    [InlineData(115, true)]
    [InlineData(14, false)]
    [InlineData(116, false)]
    [InlineData(0, false)]
    public void TheTemperatureWindowIsInclusiveAtBothEnds(int value, bool usable) =>
        Assert.Equal(usable, RuntimeSensorVerification.IsUsableTemperature(value));

    [Theory]
    [InlineData(0, true)]
    [InlineData(6200, true)]
    [InlineData(10000, true)]
    [InlineData(10001, false)]
    [InlineData(-1, false)]
    public void TheFanWindowStartsAtZero(int value, bool usable) =>
        Assert.Equal(usable, RuntimeSensorVerification.IsUsableFanRpm(value));

    [Fact]
    public void ClassifyNamesTheFirstRefusalInAStableOrder()
    {
        Assert.Equal(SensorTrustRefusal.None, RuntimeSensorVerification.Classify(62, 55, 2200, 2100));
        Assert.Equal(SensorTrustRefusal.SentinelValue, RuntimeSensorVerification.Classify(0xFFFF, 55, 2200, 2100));
        Assert.Equal(SensorTrustRefusal.TemperatureOutOfRange, RuntimeSensorVerification.Classify(5, 55, 2200, 2100));
        Assert.Equal(SensorTrustRefusal.FanOutOfRange, RuntimeSensorVerification.Classify(62, 55, 11000, 2100));
    }
}
