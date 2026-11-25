using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Regression cover for issue #3 item A: on an Acer Nitro AN16-61 with no Acer service, GPU usage stayed blank while a
/// game ran and Task Manager showed ~35 %.
/// </summary>
/// <remarks>
/// The only usage sources were the service's GPU1_USAGE and NVML, and NVML was held back whenever the GPU temperature
/// read 0 — which, on the direct-WMI path, is an unverified firmware sensor rather than the service. These tests pin
/// the engine-counter fallback (the counters Task Manager reads), the rule that only a busy discrete adapter may open
/// NVML, and the chart rule that a usage needs no temperature. Nothing here touches PDH, D3DKMT or NVML: the parsing
/// and the aggregation are pure, and the monitor is driven through its seams with NVML off.
/// </remarks>
public class Issue3GpuUsageTests
{
    private const long Igpu = 0x0000_C3A4;
    private const long Dgpu = 0x0000_0001_0001_2B40L;

    private static KeyValuePair<string, double> Sample(int pid, long luid, int engine, string type, double value)
    {
        uint high = (uint)(luid >> 32);
        uint low = (uint)(luid & 0xFFFF_FFFF);
        return new KeyValuePair<string, double>(
            $"pid_{pid}_luid_0x{high:X8}_0x{low:X8}_phys_0_eng_{engine}_engtype_{type}", value);
    }

    // ---------------------------------------------------------------- instance names

    [Fact]
    public void AnInstanceNameIsReadIntoAdapterPhysicalNodeAndEngineType()
    {
        Assert.True(WindowsTelemetryFallbacks.TryParseGpuEngineInstance(
            "pid_1234_luid_0x00000000_0x0000C3A4_phys_0_eng_0_engtype_3D", out var instance));

        Assert.Equal(0x0000_C3A4L, instance.Luid);
        Assert.Equal(0, instance.Phys);
        Assert.Equal("3D", instance.EngineType);
    }

    [Fact]
    public void TheHighPartOfTheLuidLandsInTheUpperBits()
    {
        Assert.True(WindowsTelemetryFallbacks.TryParseGpuEngineInstance(
            "pid_8_luid_0x00000001_0x00012B40_phys_1_eng_3_engtype_High Priority Compute", out var instance));

        Assert.Equal(Dgpu, instance.Luid);
        Assert.Equal(1, instance.Phys);
        Assert.Equal("High Priority Compute", instance.EngineType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("_Total")]
    [InlineData("pid_1_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0xZZZZ_0x0000C3A4_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x00000000_0x_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x00000000_0x0000C3A4_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x00000000_0x0000C3A4_phys_x_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x00000000_0x0000C3A4_phys_0_eng_0")]
    [InlineData("pid_1_luid_0x123456789_0x0000C3A4_phys_0_eng_0_engtype_3D")]
    public void AMalformedInstanceNameIsRefused(string? name)
    {
        Assert.False(WindowsTelemetryFallbacks.TryParseGpuEngineInstance(name, out _));
    }

    // ---------------------------------------------------------------- aggregation

    [Fact]
    public void EachEngineTypeIsSummedAcrossProcessesAndTheAdapterIsItsBusiestType()
    {
        var perAdapter = WindowsTelemetryFallbacks.AggregateGpuEngineUsage(new[]
        {
            // dGPU: a game and an overlay both on 3D (20 + 15 = 35), a copy engine at 12.
            Sample(100, Dgpu, 0, "3D", 20),
            Sample(200, Dgpu, 0, "3D", 15),
            Sample(100, Dgpu, 5, "Copy", 12),

            // iGPU: the compositor on 3D at 8, a video decode at 30 — the busiest type wins.
            Sample(300, Igpu, 0, "3D", 8),
            Sample(400, Igpu, 2, "VideoDecode", 30),
        });

        Assert.Equal(2, perAdapter.Count);
        Assert.Equal(35.0, perAdapter[Dgpu], 3);
        Assert.Equal(30.0, perAdapter[Igpu], 3);
        Assert.Equal(35.0, WindowsTelemetryFallbacks.BusiestAdapter(perAdapter)!.Value, 3);
    }

    [Fact]
    public void ASumAboveOneHundredIsClamped()
    {
        var perAdapter = WindowsTelemetryFallbacks.AggregateGpuEngineUsage(new[]
        {
            Sample(1, Dgpu, 0, "3D", 80),
            Sample(2, Dgpu, 0, "3D", 60),
            Sample(3, Igpu, 0, "3D", 150),
        });

        Assert.Equal(100.0, perAdapter[Dgpu]);
        Assert.Equal(100.0, perAdapter[Igpu]);
    }

    [Fact]
    public void MalformedNamesAndImpossibleValuesAreSkipped()
    {
        var perAdapter = WindowsTelemetryFallbacks.AggregateGpuEngineUsage(new[]
        {
            new KeyValuePair<string, double>("garbage", 90),
            new KeyValuePair<string, double>("pid_1_luid_0x00000000_0x0000C3A4_eng_0_engtype_3D", 90),
            Sample(1, Dgpu, 0, "3D", double.NaN),
            Sample(2, Dgpu, 0, "3D", double.PositiveInfinity),
            Sample(3, Dgpu, 0, "3D", -5),
            Sample(4, Dgpu, 0, "3D", 7),
        });

        Assert.Single(perAdapter);
        Assert.Equal(7.0, perAdapter[Dgpu], 3);
    }

    [Fact]
    public void AnIdleAdapterIsPresentAtZero()
    {
        var perAdapter = WindowsTelemetryFallbacks.AggregateGpuEngineUsage(new[] { Sample(1, Dgpu, 0, "3D", 0) });

        Assert.Equal(0.0, perAdapter[Dgpu]);
    }

    [Fact]
    public void AnEmptyCollectNamesNoAdapter()
    {
        var perAdapter = WindowsTelemetryFallbacks.AggregateGpuEngineUsage(Array.Empty<KeyValuePair<string, double>>());

        Assert.Empty(perAdapter);
        Assert.Null(WindowsTelemetryFallbacks.BusiestAdapter(perAdapter));
        Assert.Equal(new GpuEngineReading(null, false), WindowsTelemetryFallbacks.InterpretGpuEngineUsage(perAdapter, null));
    }

    // ---------------------------------------------------------------- which adapter is the dGPU

    [Theory]
    // The role is passed as an int: the enum is internal, and a public test method cannot take it as a parameter.
    [InlineData(0x0000_0013u, (int)GpuAdapterRole.Discrete)]   // render + display + hybrid discrete
    [InlineData(0x0000_0023u, (int)GpuAdapterRole.Integrated)] // render + display + hybrid integrated
    [InlineData(0x0000_0005u, (int)GpuAdapterRole.Software)]   // render + software device
    [InlineData(0x0000_0003u, (int)GpuAdapterRole.Other)]      // a plain adapter on a machine that is not hybrid
    public void TheAdapterTypeFlagsNameTheRole(uint flags, int expected)
    {
        Assert.Equal((GpuAdapterRole)expected, WindowsTelemetryFallbacks.RoleFromAdapterType(flags));
    }

    private static readonly Dictionary<long, GpuAdapterRole> Optimus = new()
    {
        [Igpu] = GpuAdapterRole.Integrated,
        [Dgpu] = GpuAdapterRole.Discrete,
    };

    [Fact]
    public void OnAnOptimusMachineABusyDgpuIsShownAndCountsAsAwake()
    {
        var reading = WindowsTelemetryFallbacks.InterpretGpuEngineUsage(
            new Dictionary<long, double> { [Igpu] = 60, [Dgpu] = 35 }, Optimus);

        Assert.Equal((double?)35.0, reading.Usage);
        Assert.True(reading.DiscreteBusy);
    }

    [Fact]
    public void OnAnOptimusMachineABusyIgpuNeverCountsAsTheDgpuAwake()
    {
        // A video in the browser: the iGPU is busy, the dGPU has no instance at all. Claiming "awake" here would open
        // NVML, and NVML on a sleeping dGPU wakes it on battery.
        var reading = WindowsTelemetryFallbacks.InterpretGpuEngineUsage(
            new Dictionary<long, double> { [Igpu] = 60 }, Optimus);

        Assert.Null(reading.Usage);
        Assert.False(reading.DiscreteBusy);
    }

    [Fact]
    public void AnAlmostIdleDgpuIsShownButDoesNotOpenNvml()
    {
        var reading = WindowsTelemetryFallbacks.InterpretGpuEngineUsage(
            new Dictionary<long, double> { [Igpu] = 60, [Dgpu] = 2 }, Optimus);

        Assert.Equal((double?)2.0, reading.Usage);
        Assert.False(reading.DiscreteBusy);
    }

    [Fact]
    public void WithoutAdapterRolesTheBusiestAdapterIsShownAndNothingIsClaimed()
    {
        var reading = WindowsTelemetryFallbacks.InterpretGpuEngineUsage(
            new Dictionary<long, double> { [Igpu] = 60, [Dgpu] = 35 }, null);

        Assert.Equal((double?)60.0, reading.Usage);
        Assert.False(reading.DiscreteBusy);
    }

    [Fact]
    public void ASingleAdapterOnAMachineThatIsNotHybridCountsAsAwakeWhenBusy()
    {
        var roles = new Dictionary<long, GpuAdapterRole> { [Dgpu] = GpuAdapterRole.Other, [0x99] = GpuAdapterRole.Software };

        var reading = WindowsTelemetryFallbacks.InterpretGpuEngineUsage(
            new Dictionary<long, double> { [Dgpu] = 40, [0x99] = 90 }, roles);

        Assert.Equal((double?)40.0, reading.Usage);
        Assert.True(reading.DiscreteBusy);
    }

    [Fact]
    public void TwoPlainAdaptersCannotBeToldApartSoNothingIsClaimed()
    {
        var roles = new Dictionary<long, GpuAdapterRole> { [Igpu] = GpuAdapterRole.Other, [Dgpu] = GpuAdapterRole.Other };

        var reading = WindowsTelemetryFallbacks.InterpretGpuEngineUsage(
            new Dictionary<long, double> { [Igpu] = 40, [Dgpu] = 10 }, roles);

        Assert.Equal((double?)40.0, reading.Usage);
        Assert.False(reading.DiscreteBusy);
    }

    // ---------------------------------------------------------------- the NVML gate

    [Fact]
    public void OnlyABusyDgpuOverrulesTheSleepGuess()
    {
        Assert.True(HardwareMonitorService.GpuAsleep(true, null));
        Assert.True(HardwareMonitorService.GpuAsleep(true, new GpuEngineReading(null, false)));
        Assert.True(HardwareMonitorService.GpuAsleep(true, new GpuEngineReading(60, false)));
        Assert.False(HardwareMonitorService.GpuAsleep(true, new GpuEngineReading(35, true)));
        Assert.False(HardwareMonitorService.GpuAsleep(false, null));
    }

    // ---------------------------------------------------------------- the monitor and the chart

    private static HardwareMonitorService Monitor() => new(initNvml: false) { ThermalZoneProvider = () => null };

    [Fact]
    public void AFirmwareGpuTemperatureOfZeroStillShowsTheEngineUsage()
    {
        using var monitor = Monitor();
        var data = new MonitorData { CpuUsage = 20, CpuCores = 8, GpuTemperature = 0 };

        monitor.EnrichMonitorData(data, gpuAsleepPerService: true, new GpuEngineReading(35.04, true));

        Assert.Equal(35.0, data.GpuUsage);
        Assert.Equal("PDH GPU Engine", data.Provenance["gpu-usage"]);
        Assert.Equal((double?)35.0, TelemetryMetrics.Value(TelemetryMetrics.GpuUsage, data));

        // The engine counters do not wake the card, and with the GPU taken as asleep NVML was never asked.
        Assert.False(data.GpuMetricsAvailable);
        Assert.Equal(0.0, data.GpuPowerWatts);
    }

    [Fact]
    public void ThePublicEnrichmentSamplesTheEngineCountersItself()
    {
        using var monitor = Monitor();
        monitor.GpuEngineProvider = () => new GpuEngineReading(41, false);
        var data = new MonitorData { CpuUsage = 20, CpuCores = 8, GpuTemperature = 0 };

        monitor.EnrichMonitorData(data, gpuAsleepPerService: true);

        Assert.Equal(41.0, data.GpuUsage);
    }

    [Fact]
    public void TheServiceUsageWinsOverTheEngineCounters()
    {
        using var monitor = Monitor();
        var data = new MonitorData { CpuUsage = 20, CpuCores = 8, GpuTemperature = 55, GpuUsage = 50 };

        monitor.EnrichMonitorData(data, gpuAsleepPerService: false, new GpuEngineReading(35, true));

        Assert.Equal(50.0, data.GpuUsage);
        Assert.Equal("firmware", data.Provenance["gpu-usage"]);
    }

    [Fact]
    public void WithNeitherATemperatureNorAUsageTheChartHasNoValue()
    {
        using var monitor = Monitor();
        var data = new MonitorData { CpuUsage = 20, CpuCores = 8, GpuTemperature = 0 };

        monitor.EnrichMonitorData(data, gpuAsleepPerService: true, null);

        Assert.Equal(0.0, data.GpuUsage);
        Assert.Equal("unavailable", data.Provenance["gpu-usage"]);
        Assert.Null(TelemetryMetrics.Value(TelemetryMetrics.GpuUsage, data));
    }

    [Fact]
    public void TheChartShowsAUsageWithoutATemperatureAndNoFakeZeroForASleepingGpu()
    {
        Assert.Equal((double?)35.0, TelemetryMetrics.Value(TelemetryMetrics.GpuUsage, new MonitorData { GpuTemperature = 0, GpuUsage = 35 }));
        Assert.Null(TelemetryMetrics.Value(TelemetryMetrics.GpuUsage, new MonitorData { GpuTemperature = 0, GpuUsage = 0 }));

        // An awake GPU idling at 0 % is a real reading, as before.
        Assert.Equal((double?)0.0, TelemetryMetrics.Value(TelemetryMetrics.GpuUsage, new MonitorData { GpuTemperature = 50, GpuUsage = 0 }));
    }
}
