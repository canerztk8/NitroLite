using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Lighting;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Runtime sensor verification on a machine whose dGPU is asleep: the GPU answers 0 °C for as long as the iGPU
/// drives the display, and that used to keep the session unverified (and the custom fan curve locked) forever.
/// </summary>
public class AuditG5SensorTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);

    private static MonitorData Sample(int cpu = 62, int gpu = 55, int cpuFan = 2200, int gpuFan = 2100) =>
        new() { CpuTemperature = cpu, GpuTemperature = gpu, CpuFanSpeed = cpuFan, GpuFanSpeed = gpuFan };

    [Fact]
    public void ASleepingDgpuDoesNotBlockPromotion()
    {
        var trust = new RuntimeSensorVerification();

        Assert.False(trust.Observe(Sample(gpu: 0, gpuFan: 0), T0, layoutConfirmed: true));
        Assert.True(trust.Observe(Sample(gpu: 0, gpuFan: 0), T0.AddSeconds(2), layoutConfirmed: true));
        Assert.True(trust.RuntimeVerified);
    }

    [Fact]
    public void GpuZeroIsNoSampleOnlyWhenTheCpuIsGood()
    {
        Assert.Equal(SensorTrustRefusal.None, RuntimeSensorVerification.Classify(62, 0, 2200, 0));

        // The CPU checks are not relaxed: a CPU zero, a CPU out of window and a CPU sentinel still refuse.
        Assert.Equal(SensorTrustRefusal.TemperatureOutOfRange, RuntimeSensorVerification.Classify(0, 0, 2200, 0));
        Assert.Equal(SensorTrustRefusal.TemperatureOutOfRange, RuntimeSensorVerification.Classify(0, 55, 2200, 2100));
        Assert.Equal(SensorTrustRefusal.TemperatureOutOfRange, RuntimeSensorVerification.Classify(120, 0, 2200, 0));
        Assert.Equal(SensorTrustRefusal.SentinelValue, RuntimeSensorVerification.Classify(0xFFFF, 0, 2200, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(116)]
    [InlineData(0xFFFF)]
    public void AnyOtherBadGpuReadingStillRefuses(int gpu)
    {
        Assert.NotEqual(SensorTrustRefusal.None, RuntimeSensorVerification.Classify(62, gpu, 2200, 2100));
    }

    [Fact]
    public void AnAllZeroSampleStillNeverPromotes()
    {
        var trust = new RuntimeSensorVerification();

        for (int i = 0; i < 5; i++)
            Assert.False(trust.Observe(Sample(0, 0, 0, 0), T0.AddSeconds(i * 2), layoutConfirmed: true));

        Assert.False(trust.RuntimeVerified);
        Assert.Equal(SensorTrustRefusal.TemperatureOutOfRange, trust.LastRefusal);
    }
}

/// <summary>
/// The wear history's "no persistence" form. A test that passed null used to get the real AppData file, and its
/// <c>Clear()</c> deleted the user's battery history.
/// </summary>
public class AuditG5BatteryTests
{
    private static BatteryHealthDetails Details(double health) => new()
    {
        DesignCapacityMWh = 90000,
        FullChargeCapacityMWh = 90000 * health / 100.0,
        HealthPercent = health,
        WearLevelPercent = 100 - health
    };

    [Fact]
    public void InMemoryHistoryHasNoPathAndKeepsReadings()
    {
        var history = BatteryWearHistory.InMemory();

        Assert.Null(history.Path);
        Assert.True(history.TryRecord(Details(95), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Single(history.GetSamples());
        Assert.True(history.Clear());
        Assert.Empty(history.GetSamples());
    }

    [Fact]
    public void ForFileWithNullMeansNoPersistence()
    {
        Assert.Null(BatteryWearHistory.ForFile(null).Path);
        Assert.Null(BatteryWearHistory.ForFile("").Path);
    }

    [Fact]
    public void ForFileWithAPathPersists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NitroLiteG5Wear_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, BatteryWearHistory.DefaultFileName);
            var first = BatteryWearHistory.ForFile(path);
            Assert.True(first.TryRecord(Details(95), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.True(File.Exists(path));

            Assert.Single(BatteryWearHistory.ForFile(path).GetSamples());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// WAVE up/down on a HID keyboard. The ENE KB5130 report documents direction bytes only for right and left, and the
/// frame sends anything else as right, so the HID transport must not offer up or down.
/// </summary>
public class AuditG5LightingTests
{
    [Fact]
    public void HidTransportOffersOnlyLeftAndRightForWave()
    {
        Assert.Equal(new[] { 1, 2 }, LightingPolicy.AllowedDirections("WAVE", hidTransport: true));
        Assert.Equal(new[] { 1, 2, 3, 4 }, LightingPolicy.AllowedDirections("WAVE", hidTransport: false));
        Assert.Equal(new[] { 1, 2 }, LightingPolicy.AllowedDirections("SHIFTING", hidTransport: true));
        Assert.Empty(LightingPolicy.AllowedDirections("STATIC", hidTransport: true));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void TheHidFrameHasNoUpOrDown(int direction)
    {
        // The reason for the restriction above: up and down reach the HID report as "right".
        var frame = KeyboardLightingFrame.Create("WAVE", 100, 4, direction, "#FF0000", null);
        Assert.True(EneKb5130Protocol.TryBuild(frame, out var reports, out _));
        Assert.Equal(1, reports[0][5]);
    }
}
