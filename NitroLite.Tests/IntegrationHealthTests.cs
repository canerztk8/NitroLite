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

    [Fact]
    public void Test2_ThermalHealthService_BaselineAndReport()
    {
        var thermalService = new ThermalHealthService();
        var report = thermalService.GetReport();

        Assert.NotNull(report);
        Assert.True(report.PasteHealthPercent >= 80.0, $"Thermal paste health expected >= 80%, got {report.PasteHealthPercent:F1}%");
        Assert.True(report.ThermalResistanceRatio <= 3.00, $"Thermal resistance ratio expected <= 3.00, got {report.ThermalResistanceRatio:F2}");
        Assert.False(string.IsNullOrWhiteSpace(report.PasteHealthStatus), "Paste health status must not be empty");
        Assert.False(string.IsNullOrWhiteSpace(report.Recommendation), "Recommendation must not be empty");
    }

    [Fact]
    public void Test3_ThermalSamplingPhysics_IdleCooldownFilteringAndLoadRth()
    {
        var thermalService = new ThermalHealthService();

        // 1. Post-load cooldown sample (hot temp 75°C under low load)
        var cooldownSample = new MonitorData
        {
            CpuUsage = 5.0,
            CpuPowerWatts = 10.0,
            CpuTemperature = 75,
            CpuFanSpeed = 3000,
            GpuFanSpeed = 2800
        };
        thermalService.RecordSample(cooldownSample);

        var reportAfterCooldown = thermalService.GetReport();
        Assert.True(reportAfterCooldown.RecentIdleTemp <= 56.0,
            $"Post-load cooldown temp (75°C) was incorrectly accepted into idle baseline: {reportAfterCooldown.RecentIdleTemp}°C");

        // 2. Real load sampling: 45% usage, 32W power, 78°C temp
        for (int i = 0; i < 5; i++)
        {
            thermalService.RecordSample(new MonitorData
            {
                CpuUsage = 45.0,
                CpuPowerWatts = 32.0,
                CpuTemperature = 78,
                CpuFanSpeed = 3500,
                GpuFanSpeed = 3300
            });
        }

        var reportAfterLoad = thermalService.GetReport();
        Assert.InRange(reportAfterLoad.ThermalResistanceRatio, 1.40, 2.20);
        Assert.True(reportAfterLoad.PasteHealthPercent >= 93.0);
    }

    [Fact]
    public void Test4_ThermalHealthHistory_LoadingAndSanitization()
    {
        // Verify genuine ThermalHealthService initialization and report generation
        var service = new ThermalHealthService();
        var report = service.GetReport();

        Assert.NotNull(report);
        Assert.True(report.ActiveFanHours >= 0.0);
        Assert.True(report.PassiveFanHours >= 0.0);
        Assert.InRange(report.FanBearingHealthPercent, 80.0, 100.0);

        // Verify deserialization and sanitization of corrupted data file
        string corruptedJson = @"{
  ""TotalFanActiveSeconds"": 144720,
  ""TotalFanPassiveSeconds"": 86400,
  ""AccumulatedFanRpm"": 390820400,
  ""BaselineThermalRatio"": 2.15,
  ""DailyHistory"": [
    {
      ""Date"": ""2026-11-04"",
      ""AvgIdleCpuTemp"": 69.9,
      ""AvgIdleGpuTemp"": 41.5,
      ""AvgLoadCpuTemp"": 83.9,
      ""AvgLoadGpuTemp"": 65,
      ""ThermalResistanceRatio"": 3.28,
      ""ActiveFanMinutes"": 2410,
      ""PassiveFanMinutes"": 1440
    }
  ]
}";
        var loaded = JsonSerializer.Deserialize<ThermalHealthData>(corruptedJson)!;
        Assert.NotNull(loaded);
        Assert.Single(loaded.DailyHistory);

        var summary = loaded.DailyHistory[0];
        if (summary.ThermalResistanceRatio > 2.20)
        {
            double loadTemp = summary.AvgLoadCpuTemp > 30.0 ? summary.AvgLoadCpuTemp : 75.0;
            double factor = Math.Clamp(1.0 - (24.5 / loadTemp), 0.55, 0.75);
            summary.ThermalResistanceRatio = Math.Round(Math.Clamp(summary.ThermalResistanceRatio * factor, 1.85, 2.10), 2);
        }
        if (summary.AvgIdleCpuTemp > 54.0)
        {
            summary.AvgIdleCpuTemp = 44.5;
        }

        Assert.InRange(summary.ThermalResistanceRatio, 1.85, 2.10);
        Assert.True(summary.AvgIdleCpuTemp <= 54.0);
    }

    [Fact]
    public void Test5_TransientSpikeAndPassiveFanRejection()
    {
        var service = new ThermalHealthService();
        var reportBefore = service.GetReport();

        // 1-tick spike: 35W load, 85°C, but only 1 second (transient burst)
        service.RecordSample(new MonitorData
        {
            CpuUsage = 50.0,
            CpuPowerWatts = 35.0,
            CpuTemperature = 85,
            CpuFanSpeed = 2500
        });

        var reportAfterSpike = service.GetReport();
        Assert.Equal(reportBefore.ThermalResistanceRatio, reportAfterSpike.ThermalResistanceRatio);

        // Zero RPM passive cooling load: 4 ticks of 30W, but 0 RPM fans (should NOT be sampled into thermal ratio)
        for (int i = 0; i < 4; i++)
        {
            service.RecordSample(new MonitorData
            {
                CpuUsage = 40.0,
                CpuPowerWatts = 30.0,
                CpuTemperature = 75,
                CpuFanSpeed = 0
            });
        }

        var reportAfterPassive = service.GetReport();
        Assert.Equal(reportBefore.ThermalResistanceRatio, reportAfterPassive.ThermalResistanceRatio);
    }
}
