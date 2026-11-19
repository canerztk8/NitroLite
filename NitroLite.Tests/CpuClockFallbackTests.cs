using NitroLite.Core;
using Xunit;
using Xunit.Abstractions;

namespace NitroLite.Tests;

/// <summary>
/// Regression cover for the blank CPU clock on the Acer Nitro AN16-61 report (issue 2026-11-19).
/// </summary>
/// <remarks>
/// The clock used to be filled from one place only: the <c>CPU_FREQUENCY</c> field of an AcerAgentService reply.
/// The reporting machine does not run that service, so the value stayed 0 and the dashboard showed
/// <c>-- GHz</c> for the whole session even though fans and modes worked over direct WMI. These tests pin the
/// Windows fallback in place, on a machine that does have the service as well as on one that does not.
/// </remarks>
public class CpuClockFallbackTests
{
    private readonly ITestOutputHelper _output;

    public CpuClockFallbackTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// The fallback source itself has to answer on a real Windows machine. A zero here means the
    /// <c>CallNtPowerInformation</c> marshalling is wrong, which is exactly the bug this file guards.
    /// </summary>
    [Fact]
    public void NativeFrequency_ReturnsARealClock()
    {
        double mhz = HardwareMonitorService.GetNativeCpuFrequencyMhz();
        _output.WriteLine($"Native CPU clock on this machine: {mhz} MHz");

        Assert.True(mhz > 100, $"Expected a running clock above 100 MHz, got {mhz}");
        Assert.True(mhz < 20000, $"Expected a clock below 20000 MHz, got {mhz}");
    }

    /// <summary>A refused or broken read must be 0, never a negative or absurd number that the UI would render.</summary>
    [Fact]
    public void NativeFrequency_IsNeverNegativeOrAbsurd()
    {
        for (int i = 0; i < 3; i++)
        {
            double mhz = HardwareMonitorService.GetNativeCpuFrequencyMhz();
            Assert.InRange(mhz, 0, 20000);
        }
    }

    /// <summary>The AN16-61 case: no service reply, so the value is still 0 when the monitor enriches the sample.</summary>
    [Fact]
    public void EnrichMonitorData_FillsTheClock_WhenTheServiceDidNotReportIt()
    {
        using var monitor = new HardwareMonitorService();
        var data = new MonitorData { CpuUsage = 25.0, CpuFrequencyMHz = 0 };

        // gpuAsleepPerService keeps NVML out of the test, the same way the caller does on battery.
        monitor.EnrichMonitorData(data, gpuAsleepPerService: true);

        Assert.True(data.CpuFrequencyMHz > 0, "A monitor without a service reply must still report a clock");
    }

    /// <summary>A service reading is a measured value and must win over the native fallback.</summary>
    [Fact]
    public void EnrichMonitorData_KeepsTheServiceReading()
    {
        using var monitor = new HardwareMonitorService();
        var data = new MonitorData { CpuUsage = 25.0, CpuFrequencyMHz = 3210 };

        monitor.EnrichMonitorData(data, gpuAsleepPerService: true);

        Assert.Equal(3210, data.CpuFrequencyMHz);
    }

    /// <summary>The estimate's frequency term used to be 1.0 whenever the clock was missing; it is now real.</summary>
    [Fact]
    public void EnrichMonitorData_EstimatedPowerStaysInRange_WithAFilledClock()
    {
        using var monitor = new HardwareMonitorService();
        var data = new MonitorData { CpuUsage = 100.0, CpuFrequencyMHz = 0 };

        monitor.EnrichMonitorData(data, gpuAsleepPerService: true);

        Assert.InRange(data.CpuPowerWatts, 7.0, 135.0);
    }
}
