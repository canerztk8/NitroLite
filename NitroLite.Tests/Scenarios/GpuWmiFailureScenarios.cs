using System.Diagnostics;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests.Scenarios;

/// <summary>
/// Category 3: the two providers that can fail without saying so. NVML can wake a sleeping GPU and cost battery;
/// the Acer WMI provider can hang instead of returning an error.
/// </summary>
public class GpuWmiFailureScenarios
{
    #region S3-01 Sixty polls on battery must not wake the dGPU

    /// <summary>
    /// On battery with the dGPU in D3cold, every NVML query would wake it and keep it awake. Sixty polls must leave
    /// the GPU values untouched, which is what "NVML was never called" looks like from the outside.
    /// </summary>
    [Fact]
    public void SixtyPollsOnBatteryNeverTouchTheGpu()
    {
        using var service = new HardwareMonitorService(initNvml: false);

        for (int i = 0; i < 60; i++)
        {
            var data = service.CreateFallbackMonitorData(allowNvml: false);

            Assert.False(data.GpuMetricsAvailable, $"Poll {i} claimed GPU metrics while the dGPU was asleep.");
            Assert.Equal(0.0, data.GpuPowerWatts);
            Assert.Equal(0.0, data.VramUsedMB);
            Assert.Equal(0.0, data.VramTotalMB);
        }
    }

    /// <summary>
    /// The same loop with the adapter plugged in is allowed to ask NVML. On a machine without an NVIDIA GPU the
    /// answer is still "no metrics", but the call must not throw and must not invent a value.
    /// </summary>
    [Fact]
    public void SixtyPollsOnTheAdapterDoNotThrowAndDoNotInventValues()
    {
        using var service = new HardwareMonitorService(initNvml: false);

        for (int i = 0; i < 60; i++)
        {
            var data = service.CreateFallbackMonitorData(allowNvml: true);

            // No NVML session was opened, so there is nothing to report and nothing to make up.
            Assert.False(data.GpuMetricsAvailable);
            Assert.Equal(0.0, data.GpuPowerWatts);
        }
    }

    /// <summary>
    /// A sample that arrives with the GPU already marked asleep must keep it that way: the enrichment step is what
    /// decides whether to touch NVML, and it must honour the flag it was given.
    /// </summary>
    [Fact]
    public void AnAsleepGpuStaysAsleepThroughEnrichment()
    {
        using var service = new HardwareMonitorService(initNvml: false);
        var data = new MonitorData { CpuTemperature = 55, GpuTemperature = 0 };

        service.EnrichMonitorData(data, gpuAsleepPerService: true);

        Assert.Equal(0, data.GpuTemperature);
        Assert.False(data.GpuMetricsAvailable);
        Assert.Equal(0.0, data.GpuPowerWatts);
    }

    #endregion

    #region S3-02 A provider that hangs instead of failing

    /// <summary>
    /// The failure this probe exists to detect is a provider that hangs rather than returning an error. A
    /// synchronous call would hang the caller with it, and the caller is the settings page.
    /// </summary>
    [Fact]
    public void AProbeWithAnImpossibleDeadlineReturnsInsteadOfHanging()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = AcerWmiProbe.ReadWithTimeout(TimeSpan.FromMilliseconds(1));

        stopwatch.Stop();

        Assert.NotNull(result);
        // The probe either finished inside the millisecond or reported the timeout; either way it returned.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"The probe took {stopwatch.Elapsed.TotalSeconds:F1} s to return, which is not a bounded wait.");
    }

    /// <summary>
    /// A probe that cannot reach the provider must describe the failure rather than throw, because the hardware
    /// report prints the description.
    /// </summary>
    [Fact]
    public void AProbeThatCannotReachTheProviderDescribesTheFailure()
    {
        var result = AcerWmiProbe.ReadWithTimeout(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }

    #endregion

    #region S3-03 The GPU mode gate must fail closed

    /// <summary>
    /// A GPU mode query that failed is not evidence that the mode is supported. The gate has to refuse rather than
    /// let the user write a mode the machine may not have.
    /// </summary>
    [Theory]
    [InlineData(null, GpuOperatingMode.Optimus, false)]
    [InlineData(null, GpuOperatingMode.DiscreteGpu, false)]
    [InlineData(0, GpuOperatingMode.Optimus, false)]
    public void AnUnknownCapabilityRefusesEveryGpuMode(int? capability, GpuOperatingMode target, bool expected)
    {
        Assert.Equal(expected, MainWindow.CanSwitchGpuMode(capability, target));
    }

    #endregion
}
