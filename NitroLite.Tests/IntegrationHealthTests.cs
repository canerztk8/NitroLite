using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class IntegrationHealthTests
{
    [Fact]
    public void Test0_HardwareMonitorService_EnrichMonitorData_ComputesSafeTelemetry()
    {
        using var hwService = new HardwareMonitorService();
        var monData = new MonitorData
        {
            CpuTemperature = 55,
            GpuTemperature = 48,
            CpuFanSpeed = 2400,
            CpuUsage = 35.0,
            CpuFrequencyMHz = 3800
        };

        hwService.EnrichMonitorData(monData);

        Assert.True(monData.CpuTemperature >= 0, "CPU Temperature must be non-negative");
        Assert.True(monData.GpuTemperature >= 0, "GPU Temperature must be non-negative");
        Assert.True(monData.CpuFanSpeed >= 0, "CPU Fan speed must be non-negative");
        Assert.True(monData.CpuPowerWatts > 0.0, "CPU Power Watts must be positively calculated");
        Assert.True(monData.CpuCores >= 1, "CPU Core count must be at least 1");
    }

    [Fact]
    public void EnrichMonitorData_DoesNotQueryNvmlWhenServiceReportsGpuAsleep()
    {
        using var hwService = new HardwareMonitorService();
        var monData = new MonitorData { CpuUsage = 10.0, GpuTemperature = 0 };

        hwService.EnrichMonitorData(monData, gpuAsleepPerService: true);

        // Nothing GPU-related may be filled in from NVML (which could wake the dGPU from D3cold).
        Assert.Equal(0.0, monData.GpuPowerWatts);
        Assert.Equal(0.0, monData.VramUsedMB);
        Assert.Equal(0.0, monData.VramTotalMB);
        Assert.Equal(0, monData.GpuTemperature);
        Assert.True(monData.CpuPowerWatts > 0.0);
    }

    [Fact]
    public void Test1_StorageHealthService_GetDrives_SingleOrMultiDriveSupport()
    {
        var storageService = new StorageHealthService();
        var drives = storageService.GetDrives();

        Assert.NotNull(drives);
        // Remediated: Do not require at least 2 drives; support single-drive machines (>= 1)
        Assert.True(drives.Count >= 1, $"Expected at least 1 drive, got {drives.Count}");

        var d0 = drives[0];
        Assert.True(d0.DriveLetter == "C" || d0.DriveLetter.Contains("C") || !string.IsNullOrEmpty(d0.DriveLetter),
            $"Disk 0 drive letter expected non-empty, got '{d0.DriveLetter}'");
        Assert.True(d0.TotalCapacityGB > 0.0, $"Disk 0 total capacity must be > 0, got {d0.TotalCapacityGB}");
        Assert.True(d0.UsedCapacityGB >= 0.0, "Disk 0 used capacity must be non-negative");
        Assert.True(d0.FreeCapacityGB >= 0.0, "Disk 0 free capacity must be non-negative");

        if (drives.Count >= 2)
        {
            var d1 = drives[1];
            Assert.True(d1.TotalCapacityGB > 0.0, $"Disk 1 total capacity must be > 0, got {d1.TotalCapacityGB}");
            Assert.True(d1.UsedCapacityGB >= 0.0, "Disk 1 used capacity must be non-negative");
        }
    }
}
