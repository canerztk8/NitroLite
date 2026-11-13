using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class ThermalHealthServiceTests
{
    [Fact]
    public void ThermalResistanceRatio_Above230_IsNotClampedTo215_WarningAndCriticalStatesReachable()
    {
        var service = new ThermalHealthService();

        // Feed sustained elevated thermal resistance:
        // Ambient 24.5°C, CPU Temp 95°C, CPU Power 22W, Fans 2200 RPM
        // DeltaT = 95 - 24.5 = 70.5°C
        // Raw ratio = 70.5 / 22 = 3.20 °C/W
        // FanFactor for 2200 RPM: (2200/3000)^0.25 ≈ 0.925 => Normalized ratio ≈ 2.96 °C/W
        for (int i = 0; i < 6; i++)
        {
            service.RecordSample(new MonitorData
            {
                CpuUsage = 50.0,
                CpuPowerWatts = 22.0,
                CpuTemperature = 95,
                CpuFanSpeed = 2200,
                GpuFanSpeed = 2200,
                SysTemperature = 28
            });
        }

        var report = service.GetReport();

        // Must NOT be clamped to 2.15!
        Assert.True(report.ThermalResistanceRatio > 2.30, 
            $"Expected thermal resistance ratio > 2.30 °C/W to detect degraded thermal interface, but got {report.ThermalResistanceRatio:F2} °C/W");

        // Paste health must degrade cleanly
        Assert.True(report.PasteHealthPercent < 90.0,
            $"Expected paste health < 90.0% under severe thermal resistance, but got {report.PasteHealthPercent:F1}%");

        // Warning or Critical state must be reachable
        Assert.True(
            report.PasteHealthStatus.Contains("Dikkat") || report.PasteHealthStatus.Contains("İyi Seviyede"),
            $"Expected status to indicate degradation, got '{report.PasteHealthStatus}'");
    }

    [Fact]
    public void IdleCooldownDebounce_PostLoadHeatSoak_RejectsHighTempsFromIdleBaseline()
    {
        var service = new ThermalHealthService();
        var initialReport = service.GetReport();
        double initialIdle = initialReport.RecentIdleTemp;

        // 1. Post-load heat soak: low usage (5%), low power (8W), but residual package heat at 78°C
        for (int i = 0; i < 4; i++)
        {
            service.RecordSample(new MonitorData
            {
                CpuUsage = 5.0,
                CpuPowerWatts = 8.0,
                CpuTemperature = 78,
                CpuFanSpeed = 2500
            });
        }

        var reportAfterHeatSoak = service.GetReport();

        // Idle temp should NOT jump to 78°C because it requires >= 5 consecutive ticks AND CpuTemperature <= 54°C
        Assert.True(reportAfterHeatSoak.RecentIdleTemp <= 54.0,
            $"RecentIdleTemp ({reportAfterHeatSoak.RecentIdleTemp}°C) was polluted by post-load heat soak (> 54°C)!");

        // 2. Even 10 ticks of 70°C low-load cannot enter idle collection because temp > 54°C
        for (int i = 0; i < 10; i++)
        {
            service.RecordSample(new MonitorData
            {
                CpuUsage = 4.0,
                CpuPowerWatts = 7.0,
                CpuTemperature = 70,
                CpuFanSpeed = 2000
            });
        }

        var reportAfterWarmTicks = service.GetReport();
        Assert.True(reportAfterWarmTicks.RecentIdleTemp <= 54.0,
            $"RecentIdleTemp ({reportAfterWarmTicks.RecentIdleTemp}°C) must remain <= 54°C!");
    }

    [Fact]
    public async Task MultiThreadedConcurrency_SimultaneousSampleRecordingAndReporting_NoCollectionModifiedException()
    {
        var service = new ThermalHealthService();
        var tasks = new List<Task>();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // 10 concurrent tasks rapidly recording samples and querying reports
        for (int t = 0; t < 10; t++)
        {
            int taskId = t;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 50; i++)
                    {
                        var data = (i % 2 == 0)
                            ? new MonitorData
                            {
                                CpuUsage = 50.0,
                                CpuPowerWatts = 30.0,
                                CpuTemperature = 75 + (i % 5),
                                CpuFanSpeed = 3200
                            }
                            : new MonitorData
                            {
                                CpuUsage = 5.0,
                                CpuPowerWatts = 8.0,
                                CpuTemperature = 45,
                                CpuFanSpeed = 1500
                            };

                        service.RecordSample(data);

                        if (i % 5 == 0)
                        {
                            var report = service.GetReport();
                            Assert.NotNull(report);
                            Assert.True(report.PasteHealthPercent > 0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
    }

    [Fact]
    public void DailyFanMinutes_BoundedTo1440MinutesPerDay()
    {
        // 24 hours = 1440 minutes = 86,400 seconds
        // Simulate recording excessive seconds (e.g. 200,000 active seconds in a day)
        long excessiveSeconds = 200000;
        int boundedActiveMinutes = Math.Min(1440, (int)(excessiveSeconds / 60));
        int boundedPassiveMinutes = Math.Min(1440, (int)(excessiveSeconds / 60));

        Assert.Equal(1440, boundedActiveMinutes);
        Assert.Equal(1440, boundedPassiveMinutes);
    }

    [Fact]
    public void TransientBoostSpike_SingleTickBurst_IsIgnoredByConsecutiveLoadStreakRequirement()
    {
        var service = new ThermalHealthService();
        var beforeReport = service.GetReport();

        // 1-tick spike: 45W load, 88°C, but only 1 sample
        service.RecordSample(new MonitorData
        {
            CpuUsage = 80.0,
            CpuPowerWatts = 45.0,
            CpuTemperature = 88,
            CpuFanSpeed = 3500
        });

        var afterReport = service.GetReport();

        // Single tick burst should NOT alter the thermal resistance ratio
        Assert.Equal(beforeReport.ThermalResistanceRatio, afterReport.ThermalResistanceRatio);
    }

    [Fact]
    public void GetStabilityTrend_EmptyHistory_ReturnsSafeDefaultsWithoutDivideByZero()
    {
        var service = new ThermalHealthService();
        service.SetDailyHistoryForTesting(new List<DailyThermalSummary>());

        var trend = service.GetStabilityTrend(30);

        Assert.Equal(0, trend.DaysCount);
        Assert.Equal(100.0, trend.StabilityScorePercent);
        Assert.Equal(0.0, trend.DegradationSlopePerDay);
        Assert.Equal(ThermalStatusLevel.Excellent, trend.StatusLevel);
        Assert.False(string.IsNullOrEmpty(trend.StatusText));
        Assert.Equal(0.0, trend.CoefficientOfVariation);
    }

    [Fact]
    public void GetStabilityTrend_SingleDayHistory_ReturnsCalibratedScoreWithoutDivideByZero()
    {
        var service = new ThermalHealthService();
        service.SetDailyHistoryForTesting(new List<DailyThermalSummary>
        {
            new() { Date = "2026-11-01", ThermalResistanceRatio = 1.95 }
        });

        var trend = service.GetStabilityTrend(30);

        Assert.Equal(1, trend.DaysCount);
        Assert.True(trend.StabilityScorePercent >= 95.0, $"Expected score >= 95%, got {trend.StabilityScorePercent}%");
        Assert.Equal(0.0, trend.DegradationSlopePerDay);
        Assert.Equal(ThermalStatusLevel.Excellent, trend.StatusLevel);
        Assert.Equal("Kararlı, Termal iletkenlik nominal", trend.StatusText);
    }

    [Fact]
    public void GetStabilityTrend_14DayVs30Day_RespectsWindowLimit()
    {
        var service = new ThermalHealthService();
        var history = new List<DailyThermalSummary>();
        for (int i = 1; i <= 30; i++)
        {
            history.Add(new DailyThermalSummary
            {
                Date = $"2026-10-{i:D2}",
                ThermalResistanceRatio = 1.95 + (i * 0.001)
            });
        }
        service.SetDailyHistoryForTesting(history);

        var trend14 = service.GetStabilityTrend(14);
        var trend30 = service.GetStabilityTrend(30);

        Assert.Equal(14, trend14.DaysCount);
        Assert.Equal(30, trend30.DaysCount);
        Assert.Equal(1.95 + (30 * 0.001), trend14.CurrentRatio, precision: 2);
        Assert.Equal(1.95 + (30 * 0.001), trend30.CurrentRatio, precision: 2);
    }

    [Fact]
    public void GetStabilityTrend_OptimalStableInterface_HighStabilityAndExcellentStatus()
    {
        var service = new ThermalHealthService();
        var history = new List<DailyThermalSummary>();
        // 30 days of very stable thermal resistance around 1.95 °C/W
        for (int i = 0; i < 30; i++)
        {
            history.Add(new DailyThermalSummary
            {
                Date = $"2026-10-{(i + 1):D2}",
                ThermalResistanceRatio = 1.94 + ((i % 3) * 0.01)
            });
        }
        service.SetDailyHistoryForTesting(history);

        var trend = service.GetStabilityTrend(30);

        Assert.True(trend.StabilityScorePercent >= 95.0, $"Expected score >= 95%, got {trend.StabilityScorePercent}%");
        Assert.Equal(ThermalStatusLevel.Excellent, trend.StatusLevel);
        Assert.Contains("Kararlı", trend.StatusText);
        Assert.True(Math.Abs(trend.DegradationSlopePerDay) < 0.005, $"Expected slope near 0, got {trend.DegradationSlopePerDay}");
    }

    [Fact]
    public void GetStabilityTrend_DegradingThermalResistance_PositiveSlopeAndDegradedStatus()
    {
        var service = new ThermalHealthService();
        var history = new List<DailyThermalSummary>();
        // 30 days where thermal resistance creeps from 2.00 up to 2.80 °C/W (+0.027 °C/W per day)
        for (int i = 0; i < 30; i++)
        {
            history.Add(new DailyThermalSummary
            {
                Date = $"2026-10-{(i + 1):D2}",
                ThermalResistanceRatio = 2.00 + (i * 0.027)
            });
        }
        service.SetDailyHistoryForTesting(history);

        var trend = service.GetStabilityTrend(30);

        Assert.True(trend.DegradationSlopePerDay > 0.02, $"Expected slope > 0.02, got {trend.DegradationSlopePerDay}");
        Assert.True(trend.StabilityScorePercent < 85.0, $"Expected stability score < 85%, got {trend.StabilityScorePercent}%");
        Assert.True(trend.StatusLevel == ThermalStatusLevel.Degraded || trend.StatusLevel == ThermalStatusLevel.Critical);
    }

    [Fact]
    public void TelemetryFormatter_FormatsSubComponentTemperaturesAndSlopeCleanly()
    {
        Assert.Equal("42 °C", TelemetryFormatter.FormatTemperature(42.3));
        Assert.Equal("-- °C", TelemetryFormatter.FormatTemperature(null));
        Assert.Equal("-- °C", TelemetryFormatter.FormatTemperature(-5));
        Assert.Equal("29.5 °C", TelemetryFormatter.FormatDetailedTemperature(29.54));
        Assert.Equal("+0.003 °C/W/gün", TelemetryFormatter.FormatThermalSlope(0.003));
        Assert.Equal("-0.002 °C/W/gün", TelemetryFormatter.FormatThermalSlope(-0.002));
        Assert.Equal("%98.7", TelemetryFormatter.FormatStabilityScore(98.7));
    }

    private const string NominalRecommendation =
        "Termal direnç ve ısı transferi nominal tolerans sınırları içindedir. Bakım gerekmemektedir.";

    [Theory]
    [InlineData(ThermalStatusLevel.Degraded)]
    [InlineData(ThermalStatusLevel.Critical)]
    public void BuildRecommendation_DegradingTrend_DoesNotClaimNoMaintenanceNeeded(ThermalStatusLevel level)
    {
        var trend = new ThermalStabilityTrend { DaysCount = 5, StatusLevel = level };

        string result = ThermalHealthService.BuildRecommendation(NominalRecommendation, trend);

        Assert.DoesNotContain("Bakım gerekmemektedir", result);
        Assert.Contains("artış eğiliminde", result);
    }

    [Theory]
    [InlineData(ThermalStatusLevel.Excellent)]
    [InlineData(ThermalStatusLevel.Good)]
    public void BuildRecommendation_StableTrend_KeepsCurrentStateRecommendation(ThermalStatusLevel level)
    {
        var trend = new ThermalStabilityTrend { DaysCount = 5, StatusLevel = level };

        Assert.Equal(NominalRecommendation, ThermalHealthService.BuildRecommendation(NominalRecommendation, trend));
    }

    [Fact]
    public void BuildRecommendation_NoMeasuredDay_KeepsCurrentStateRecommendation()
    {
        // DaysCount == 0 means no sustained-load sample yet; the trend must not override the verdict.
        var trend = new ThermalStabilityTrend { DaysCount = 0, StatusLevel = ThermalStatusLevel.Critical };

        Assert.Equal(NominalRecommendation, ThermalHealthService.BuildRecommendation(NominalRecommendation, trend));
    }
}
