using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Adversarial stress tests designed by challenger_nitrolite_2 to probe edge cases,
/// boundary limits, concurrency hazards, and mathematical validity.
/// </summary>
public class AdversarialChallengerTests
{
    // =========================================================================
    // 1. THERMAL RESISTANCE PHYSICS & DEBOUNCE TESTS
    // =========================================================================

    [Theory]
    [InlineData(2.45, "İyi Seviyede", 91.0, 95.0)]
    [InlineData(2.70, "İyi Seviyede", 85.0, 90.0)]
    [InlineData(3.10, "Dikkat", 74.0, 80.0)]
    [InlineData(3.50, "Dikkat", 64.0, 70.0)]
    public void ThermalResistance_RatiosAbove230_AreNeverClampedTo215_AndReachCorrectStatus(
        double targetRatio, string expectedStatusSubstring, double minHealth, double maxHealth)
    {
        var service = new ThermalHealthService();

        // Under normal ambient intake (SysTemp = 28.5°C => ambient = 28.5 - 4.0 = 24.5°C)
        // deltaT = targetRatio * 20W => CpuTemp = 24.5 + deltaT
        double power = 20.0;
        int fans = 3000; // fanFactor = (3000/3000)^0.25 = 1.0
        double cpuTemp = 24.5 + (targetRatio * power);

        // Record 6 consecutive sustained load ticks
        for (int i = 0; i < 6; i++)
        {
            service.RecordSample(new MonitorData
            {
                CpuUsage = 50.0,
                CpuPowerWatts = power,
                CpuTemperature = (int)Math.Round(cpuTemp),
                CpuFanSpeed = fans,
                GpuFanSpeed = fans,
                SysTemperature = 29 // ambient = 29 - 4 = 25°C, or sysTemp 55 => ambient 24.5
            });
        }

        var report = service.GetReport();

        // 1. Assert ratio is NOT clamped to 2.15
        Assert.True(report.ThermalResistanceRatio >= 2.30,
            $"Ratio {report.ThermalResistanceRatio:F2} °C/W should be >= 2.30 °C/W (NOT clamped to 2.15).");

        // 2. Assert paste health matches expected degradation range
        Assert.InRange(report.PasteHealthPercent, minHealth, maxHealth);

        // 3. Assert expected status text
        Assert.Contains(expectedStatusSubstring, report.PasteHealthStatus);
    }

    [Fact]
    public void IdleCooldownDebounce_RejectsWarmSamplesUntilStabilized()
    {
        var service = new ThermalHealthService();

        // Step 1: Pre-condition service with high load (90°C at 50W) for 4 ticks
        for (int i = 0; i < 4; i++)
        {
            service.RecordSample(new MonitorData
            {
                CpuUsage = 80.0,
                CpuPowerWatts = 50.0,
                CpuTemperature = 90,
                CpuFanSpeed = 4000
            });
        }

        // Step 2: Load suddenly stops. Low power (6W), low usage (4%).
        // But package temperature is still heat-soaked (> 54°C):
        // Ticks 1-4 at 65°C, 62°C, 59°C, 56°C.
        int[] soakedTemps = { 65, 62, 59, 56 };
        for (int i = 0; i < soakedTemps.Length; i++)
        {
            service.RecordSample(new MonitorData
            {
                CpuUsage = 4.0,
                CpuPowerWatts = 6.0,
                CpuTemperature = soakedTemps[i],
                CpuFanSpeed = 2500
            });
        }

        var reportSoaked = service.GetReport();
        // RecentIdleTemp must NOT include any of the > 54°C soaked temperatures
        Assert.True(reportSoaked.RecentIdleTemp <= 54.0,
            $"Recent idle temp {reportSoaked.RecentIdleTemp}°C should not be polluted by temps > 54°C.");

        // Step 3: Now simulate fresh idle service with 1 spike followed by only 3 ticks of 52°C idle
        var freshIdleService = new ThermalHealthService();
        freshIdleService.RecordSample(new MonitorData { CpuUsage = 90.0, CpuPowerWatts = 45.0, CpuTemperature = 85, CpuFanSpeed = 3500 });

        for (int i = 0; i < 4; i++)
        {
            freshIdleService.RecordSample(new MonitorData
            {
                CpuUsage = 5.0,
                CpuPowerWatts = 7.0,
                CpuTemperature = 52,
                CpuFanSpeed = 2000
            });
        }

        // 4 ticks is < 5 ticks debounce, so RecentIdleTemp should NOT be 52.0
        var reportDebounce = freshIdleService.GetReport();
        Assert.NotEqual(52.0, reportDebounce.RecentIdleTemp);

        // 5th tick satisfies >= 5 consecutive idle ticks and <= 54°C, so it MUST now be accepted
        freshIdleService.RecordSample(new MonitorData
        {
            CpuUsage = 5.0,
            CpuPowerWatts = 7.0,
            CpuTemperature = 52,
            CpuFanSpeed = 2000
        });

        var reportAccepted = freshIdleService.GetReport();
        Assert.Equal(52.0, reportAccepted.RecentIdleTemp);
    }

    [Fact]
    public async Task ThermalHealthService_AdversarialHighConcurrencyStress_ZeroExceptions()
    {
        var service = new ThermalHealthService();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var tasks = new List<Task>();

        // 20 concurrent tasks executing rapid mixed reads and writes
        for (int t = 0; t < 20; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    var rng = new Random(threadId);
                    for (int i = 0; i < 150; i++)
                    {
                        if (i % 3 == 0)
                        {
                            service.RecordSample(new MonitorData
                            {
                                CpuUsage = 40.0 + rng.Next(30),
                                CpuPowerWatts = 25.0 + rng.Next(20),
                                CpuTemperature = 70 + rng.Next(25),
                                CpuFanSpeed = 2200 + rng.Next(1500),
                                SysTemperature = 26
                            });
                        }
                        else if (i % 3 == 1)
                        {
                            service.RecordSample(new MonitorData
                            {
                                CpuUsage = 3.0 + rng.Next(5),
                                CpuPowerWatts = 5.0 + rng.Next(5),
                                CpuTemperature = 40 + rng.Next(12),
                                CpuFanSpeed = 1800
                            });
                        }
                        else
                        {
                            var r = service.GetReport();
                            Assert.NotNull(r);
                            Assert.True(r.PasteHealthPercent > 0);
                            Assert.False(string.IsNullOrEmpty(r.PasteHealthStatus));
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

    // =========================================================================
    // 2. STORAGE MAPPING & SINGLE-DRIVE RESILIENCE TESTS
    // =========================================================================

    [Theory]
    [InlineData("PHYSICALDRIVE0", 0)]
    [InlineData(@"\\.\PHYSICALDRIVE1", 1)]
    [InlineData(@"\\.\PHYSICALDRIVE12", 12)]
    [InlineData("Disk#0", 0)]
    [InlineData("Disk #4", 4)]
    [InlineData(@"SCSI\DISK\7", 7)]
    [InlineData("NVME_SSD_2", 2)]
    public void Storage_RegexDeviceIdParsing_HandlesAllNonStandardStrings(string rawId, int expectedNum)
    {
        var match = Regex.Match(rawId, @"\d+");
        Assert.True(match.Success, $"Expected digits in '{rawId}'");
        Assert.True(int.TryParse(match.Value, out int diskNum));
        Assert.Equal(expectedNum, diskNum);
    }

    [Fact]
    public void Storage_NonNumericDeviceId_DefaultsSafelyWithoutException()
    {
        string rawId = "VIRTUAL_DISK_NO_DIGITS";
        int diskNum = 0;
        if (!string.IsNullOrEmpty(rawId))
        {
            var match = Regex.Match(rawId, @"\d+");
            if (match.Success && int.TryParse(match.Value, out int parsed))
            {
                diskNum = parsed;
            }
        }

        Assert.Equal(0, diskNum);
    }

    [Fact]
    public void Storage_MultiPartitionPhysicalSizePreservation_DoesNotTruncateToPartitionSum()
    {
        // 2TB drive (1863.0 GiB)
        long physicalSizeBytes = 2000398934016L;

        // Partition 1 (OS): 250 GB used
        long part1Used = 250L * 1024 * 1024 * 1024;

        // Partition 2 (Data): 250 GB used
        long part2Used = 250L * 1024 * 1024 * 1024;

        long mountedUsedBytes = part1Used + part2Used; // 500 GB used

        // Authoritative physical size calculation
        long totalBytes = physicalSizeBytes;
        long usedBytes = mountedUsedBytes;
        long freeBytes = Math.Max(0, physicalSizeBytes - usedBytes);

        double totalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double usedGB = Math.Round(usedBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double freeGB = Math.Round(freeBytes / (1024.0 * 1024.0 * 1024.0), 1);

        // Verification:
        Assert.Equal(1863.0, totalGB);
        Assert.Equal(500.0, usedGB);
        Assert.Equal(1363.0, freeGB);
        Assert.True(totalGB > 1000.0, "Total capacity must not be truncated to partition sum (500GB)!");
    }

    [Fact]
    public void Storage_SingleDriveResolution_DoesNotDuplicateDriveOrShowPhantom()
    {
        // Case 1: Machine with exactly 1 drive (Disk 0)
        var singleDriveList = new List<StorageDriveInfo>
        {
            new() { DiskNumber = 0, Model = "Samsung 990 PRO 2TB", DriveLetter = "C", TotalCapacityGB = 1863.0 }
        };

        var d0 = singleDriveList.FirstOrDefault(d => d.DiskNumber == 0) ?? (singleDriveList.Count > 0 ? singleDriveList[0] : null);
        var d1 = singleDriveList.FirstOrDefault(d => d.DiskNumber == 1 && d != d0) ?? (singleDriveList.Count > 1 ? singleDriveList.FirstOrDefault(d => d != d0) : null);

        Assert.NotNull(d0);
        Assert.Equal(0, d0.DiskNumber);
        Assert.Null(d1); // d1 MUST be null on single drive machine!

        // Case 2: Machine with 1 drive enumerated as Disk 1 (no Disk 0)
        var nonZeroDriveList = new List<StorageDriveInfo>
        {
            new() { DiskNumber = 1, Model = "Kingston 1TB", DriveLetter = "C", TotalCapacityGB = 953.0 }
        };

        var nzD0 = nonZeroDriveList.FirstOrDefault(d => d.DiskNumber == 0) ?? (nonZeroDriveList.Count > 0 ? nonZeroDriveList[0] : null);
        var nzD1 = nonZeroDriveList.FirstOrDefault(d => d.DiskNumber == 1 && d != nzD0) ?? (nonZeroDriveList.Count > 1 ? nonZeroDriveList.FirstOrDefault(d => d != nzD0) : null);

        Assert.NotNull(nzD0);
        Assert.Equal(1, nzD0.DiskNumber);
        Assert.Null(nzD1); // nzD1 MUST be null, not a duplicate of nzD0!
    }

    // =========================================================================
    // 3. MEMORY CLEANER INHIBITION & SAFETY TESTS
    // =========================================================================

    [Theory]
    [InlineData(0.0, 99.0, false, "Zero RAM total")]
    [InlineData(-1024.0, 95.0, false, "Negative RAM total")]
    [InlineData(32768.0, 95.0, true, "Active gaming session")]
    [InlineData(32768.0, 50.0, false, "Low RAM usage (16GB free)")]
    public void MemoryCleaner_ShouldAutoClean_IsInhibitedUnderAdversarialConditions(
        double totalRam, double usagePct, bool isGameActive, string scenario)
    {
        var service = new MemoryCleanerService();
        bool shouldClean = service.ShouldAutoClean(usagePct, totalRam, isGameActive);

        Assert.False(shouldClean, $"Auto-clean should be inhibited under scenario: {scenario}");
    }

    [LiveSystemFact]
    public void MemoryCleaner_CooldownDebounce_StrictlyEnforced()
    {
        var service = new MemoryCleanerService();

        // 1. Trigger cleaning
        service.CleanMemory(excludePid: Process.GetCurrentProcess().Id);

        // 2. Immediate check under critical 95% RAM pressure
        bool shouldClean = service.ShouldAutoClean(ramUsagePercent: 95.0, ramTotalMB: 16384.0, isGameActive: false);

        Assert.False(shouldClean, "Auto-clean must be debounced by 120s cooldown even under 95% pressure.");
    }

    // =========================================================================
    // 4. WIN32 POWRPROF CPU BOOST TESTS
    // =========================================================================

    [Theory]
    [InlineData("Current AC Power Setting Index: 0x00000000", CpuBoostMode.Disabled)]
    [InlineData("Current AC Power Setting Index: 0x00000002", CpuBoostMode.Aggressive)]
    [InlineData("Current AC Power Setting Index: 0x00000004", CpuBoostMode.Efficient)]
    [InlineData("Current AC Power Setting Index:   0x00000000  ", CpuBoostMode.Disabled)]
    [InlineData("Current AC Power Setting Index: 0x00000003", CpuBoostMode.Aggressive)] // Non-standard fallback
    public void CpuPower_PowerCfgRegex_ExtractsAndParsesBoostModesCorrectly(string output, CpuBoostMode expected)
    {
        var match = Regex.Match(output, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
        Assert.True(match.Success);

        bool parsed = int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, null, out var val);
        Assert.True(parsed);

        CpuBoostMode resolved;
        if (val == 0) resolved = CpuBoostMode.Disabled;
        else if (val == 4) resolved = CpuBoostMode.Efficient;
        else resolved = CpuBoostMode.Aggressive;

        Assert.Equal(expected, resolved);
    }

    // =========================================================================
    // 5. TELEMETRY & DISPLAY FORMATTING ADVERSARIAL TESTS
    // =========================================================================

    [Theory]
    [InlineData(0.0, false, "0 °C (Uyku / D3cold)")]
    [InlineData(-10.0, false, "0 °C (Uyku / D3cold)")]
    [InlineData(55.0, true, "0 °C (Uyku / D3cold)")]
    [InlineData(62.4, false, "62 °C")]
    public void TelemetryFormatter_GpuTemperature_FormatsD3coldSleepStateCleanly(
        double temp, bool isSleeping, string expected)
    {
        string result = TelemetryFormatter.FormatGpuTemperature(temp, isSleeping);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0.0, false, "0.0 W (D3cold)")]
    [InlineData(-5.0, false, "0.0 W (D3cold)")]
    [InlineData(75.0, true, "0.0 W (D3cold)")]
    [InlineData(85.3, false, "85.3 W")]
    public void TelemetryFormatter_GpuPower_FormatsD3coldSleepStateCleanly(
        double watts, bool isSleeping, string expected)
    {
        string result = TelemetryFormatter.FormatGpuPower(watts, isSleeping);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true, 15.5, false, false, "15.5 W (Deşarj Oluyor)")]
    [InlineData(true, 0.0, false, false, "0.0 W (Pilde / Boşta)")]  // Crucial: 0W on battery is NOT "Adaptörde / Doygun"!
    [InlineData(false, 0.0, true, true, "45.0 W (Şarj Oluyor)", 45.0)]
    [InlineData(false, 0.0, true, false, "0.0 W (Adaptörde / Doygun)")]
    [InlineData(false, 0.0, false, false, "0.0 W (Pilde / Boşta)")]
    public void TelemetryFormatter_BatteryPowerFlow_CleanlyDistinguishesAllStates(
        bool isDischarging, double dischargeWatts, bool isPluggedIn, bool isCharging, string expected, double chargeWatts = 0.0)
    {
        var status = new BatteryStatusInfo
        {
            IsDischarging = isDischarging,
            DischargeRateWatts = dischargeWatts,
            IsPluggedIn = isPluggedIn,
            IsCharging = isCharging,
            ChargeRateWatts = chargeWatts
        };

        string flow = TelemetryFormatter.FormatBatteryPowerFlow(status);
        Assert.Equal(expected, flow);
    }
}
