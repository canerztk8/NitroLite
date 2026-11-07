using System.Collections.Generic;
using System.Linq;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class InternalPanelTests
{
    [Theory]
    [InlineData(11u, true)]          // DisplayPort embedded (eDP) – the AN16-41 panel
    [InlineData(6u, true)]           // LVDS
    [InlineData(0x80000000u, true)]  // INTERNAL
    [InlineData(10u, false)]         // external DisplayPort – the Lenovo monitor on this machine
    [InlineData(5u, false)]          // HDMI
    public void OutputTechnology_DistinguishesBuiltInPanel(uint technology, bool expected)
    {
        Assert.Equal(expected, DisplayRefreshRateService.IsInternalOutputTechnology(technology));
    }

    [Fact]
    public void LiveQuery_FindsAGdiDeviceNameOrNothing()
    {
        // Read-only QueryDisplayConfig; on a laptop with the lid open it returns e.g. "\\.\DISPLAY1".
        var name = DisplayRefreshRateService.FindInternalPanelDeviceName();
        Assert.True(name == null || name.StartsWith(@"\\.\DISPLAY"), $"Unexpected device name '{name}'");
    }
}

public class BatteryIdentityTests
{
    [Theory]
    [InlineData("34643LGCAP21A8T", "AP21A8T", "LGC")]
    [InlineData("AP21A8T", "AP21A8T", "")]        // nothing besides the name
    [InlineData("12345SMPXYZ", "ABC", "")]        // name not at the end
    [InlineData(null, "AP21A8T", "")]
    public void ManufacturerIsParsedFromDeviceId(string? deviceId, string name, string expected)
    {
        Assert.Equal(expected, BatteryWatchdogService.ParseBatteryManufacturer(deviceId, name));
    }

    [Theory]
    [InlineData(6, "Lityum-iyon")]
    [InlineData(8, "Lityum polimer")]
    [InlineData(0, "")]
    public void ChemistryCodesMapToNames(int code, string expected)
    {
        Assert.Equal(expected, BatteryWatchdogService.BatteryChemistryName(code));
    }
}

public class ThermalSeedCleanupTests
{
    [Fact]
    public void LegacySeed_IsRemovedOnce()
    {
        var data = new ThermalHealthData
        {
            DataVersion = 0,
            TotalFanActiveSeconds = 144000 + 3600,
            TotalFanPassiveSeconds = 86400 + 1800,
            AccumulatedFanRpm = 388800000 + 1000,
            DailyHistory = new List<DailyThermalSummary>
            {
                new() { Date = "2026-11-01", AvgIdleCpuTemp = 44.0, AvgIdleGpuTemp = 41.5, AvgLoadCpuTemp = 68.5, AvgLoadGpuTemp = 65.0, ThermalResistanceRatio = 1.95 },
                new() { Date = "2026-11-02", AvgIdleCpuTemp = 47.3, AvgLoadCpuTemp = 81.0, ThermalResistanceRatio = 2.31 }
            }
        };

        Assert.True(ThermalHealthService.RemoveLegacySeed(data));
        Assert.Equal(3600, data.TotalFanActiveSeconds);
        Assert.Equal(1800, data.TotalFanPassiveSeconds);
        Assert.Equal(0, data.DailyHistory[0].ThermalResistanceRatio);   // invented day → no measurement
        Assert.Equal(0, data.DailyHistory[0].AvgIdleCpuTemp);
        Assert.Equal(2.31, data.DailyHistory[1].ThermalResistanceRatio); // real day kept
        Assert.Equal(47.3, data.DailyHistory[1].AvgIdleCpuTemp);
        Assert.Equal(2, data.DataVersion);

        Assert.False(ThermalHealthService.RemoveLegacySeed(data));       // second run changes nothing
        Assert.Equal(3600, data.TotalFanActiveSeconds);
    }

    [Fact]
    public void WithoutAnyLoadSample_ReportSaysNoMeasurement()
    {
        // History holds only "no measurement" days and this instance has no in-memory samples.
        var service = new ThermalHealthService();
        service.SetDailyHistoryForTesting(new List<DailyThermalSummary>
        {
            new() { Date = "2026-11-01", ThermalResistanceRatio = 0, AvgIdleCpuTemp = 0 }
        });
        var report = service.GetReport();
        Assert.False(report.HasLoadMeasurement);
        Assert.False(report.HasIdleMeasurement);
        Assert.Contains("Henüz yük altında ölçüm yok", report.Recommendation);
    }

    [Fact]
    public void NewDataFile_HasNoInventedRuntimeOrDays()
    {
        var fresh = new ThermalHealthData { DataVersion = 2 };
        Assert.Equal(0, fresh.TotalFanActiveSeconds);
        Assert.Equal(0, fresh.TotalFanPassiveSeconds);
        Assert.Empty(fresh.DailyHistory);
    }

    [Fact]
    public void Trend_IgnoresDaysWithoutALoadMeasurement()
    {
        var service = new ThermalHealthService();
        service.SetDailyHistoryForTesting(new List<DailyThermalSummary>
        {
            new() { Date = "2026-11-01", ThermalResistanceRatio = 0 },
            new() { Date = "2026-11-02", ThermalResistanceRatio = 2.0 },
            new() { Date = "2026-11-03", ThermalResistanceRatio = 0 }
        });
        Assert.Equal(1, service.GetStabilityTrend(30).DaysCount);
    }
}
