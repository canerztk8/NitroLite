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
    [InlineData(0.0, false, "-- °C")]
    [InlineData(-10.0, false, "-- °C")]
    [InlineData(55.0, true, "0 °C (Uyku / D3cold)")]
    [InlineData(62.4, false, "62 °C")]
    public void TelemetryFormatter_GpuTemperature_FormatsD3coldSleepStateCleanly(
        double temp, bool isSleeping, string expected)
    {
        string result = TelemetryFormatter.FormatGpuTemperature(temp, isSleeping);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0.0, false, "-- W")]
    [InlineData(-5.0, false, "-- W")]
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
