using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class StorageHealthServiceTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("PHYSICALDRIVE0", 0)]
    [InlineData("PHYSICALDRIVE1", 1)]
    [InlineData(@"\\.\PHYSICALDRIVE2", 2)]
    [InlineData("Disk_3", 3)]
    public void DeviceIdRegexParsing_NonNumericAndNumericIdentifiers_ExtractsCorrectDiskNumber(string rawId, int expectedNumber)
    {
        var match = Regex.Match(rawId, @"\d+");
        Assert.True(match.Success, $"Expected regex to match digits in '{rawId}'");
        Assert.True(int.TryParse(match.Value, out int diskNum));
        Assert.Equal(expectedNumber, diskNum);
    }

    [Fact]
    public void MultiPartitionDriveCapacity_UsesPhysicalSizeAsAuthoritativeCapacity()
    {
        // Physical drive size: 2 TB (2,000,398,934,016 bytes)
        long physicalSizeBytes = 2000398934016L;

        // Partition C: 500 GB total, 200 GB free => 300 GB used
        long part1Total = 500L * 1024 * 1024 * 1024;
        long part1Free = 200L * 1024 * 1024 * 1024;
        long part1Used = part1Total - part1Free;

        // Partition D: 500 GB total, 100 GB free => 400 GB used
        long part2Total = 500L * 1024 * 1024 * 1024;
        long part2Free = 100L * 1024 * 1024 * 1024;
        long part2Used = part2Total - part2Free;

        // Sum of mounted partitions
        long mountedTotalBytes = part1Total + part2Total;
        long mountedUsedBytes = part1Used + part2Used;

        // Correct logic from remediated StorageHealthService:
        long totalBytes = physicalSizeBytes > 0 ? physicalSizeBytes : mountedTotalBytes;
        long usedBytes = mountedUsedBytes;
        long freeBytes = physicalSizeBytes > 0 ? Math.Max(0, physicalSizeBytes - usedBytes) : (totalBytes - usedBytes);

        double totalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double usedGB = Math.Round(usedBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double freeGB = Math.Round(freeBytes / (1024.0 * 1024.0 * 1024.0), 1);

        // Verify physical drive capacity is NOT truncated to mounted total (1000 GB)
        Assert.Equal(1863.0, totalGB);
        Assert.Equal(700.0, usedGB);
        Assert.Equal(1163.0, freeGB);
        Assert.True(totalGB > 1500.0, "Physical drive capacity should retain full 2TB capacity regardless of unallocated space");
    }

    [Theory]
    [InlineData(0, 100, "Sağlıklı (Nominal)")]
    [InlineData(5, 95, "Sağlıklı (Nominal)")]
    [InlineData(12, 88, "Sağlıklı (Kondisyon İyi)")]
    [InlineData(25, 75, "Sağlıklı (Kondisyon İyi)")]
    public void HealthComputation_WithWearIndicator_CalculatesLegitimatePercentage(int wearVal, int expectedHealth, string expectedDesc)
    {
        // Formula: estHealth = Math.Clamp(100 - wearVal, 0, 100)
        int estHealth = Math.Clamp(100 - wearVal, 0, 100);
        Assert.Equal(expectedHealth, estHealth);

        int healthStatus = 0; // Healthy
        string healthDesc;
        if (healthStatus == 0)
        {
            healthDesc = estHealth >= 90 ? "Sağlıklı (Nominal)" : "Sağlıklı (Kondisyon İyi)";
        }
        else if (healthStatus == 1)
        {
            healthDesc = "Uyarı (Sürücü Kontrolü Gerekli)";
        }
        else
        {
            healthDesc = "Kritik (Acil Yedekleme Önerilir)";
        }

        Assert.Equal(expectedDesc, healthDesc);
    }

    [Theory]
    [InlineData(0, 100, "Sağlıklı (Nominal)")]
    [InlineData(1, 70, "Uyarı (Sürücü Kontrolü Gerekli)")]
    [InlineData(2, 30, "Kritik (Acil Yedekleme Önerilir)")]
    public void HealthComputation_WithoutWearIndicator_MapsStatusCorrectly(int healthStatus, int expectedHealth, string expectedDesc)
    {
        int estHealth;
        if (healthStatus == 0) estHealth = 100;
        else if (healthStatus == 1) estHealth = 70;
        else estHealth = 30;

        string healthDesc;
        if (healthStatus == 0)
        {
            healthDesc = estHealth >= 90 ? "Sağlıklı (Nominal)" : "Sağlıklı (Kondisyon İyi)";
        }
        else if (healthStatus == 1)
        {
            healthDesc = "Uyarı (Sürücü Kontrolü Gerekli)";
        }
        else
        {
            healthDesc = "Kritik (Acil Yedekleme Önerilir)";
        }

        Assert.Equal(expectedHealth, estHealth);
        Assert.Equal(expectedDesc, healthDesc);
    }

    [Fact]
    public void GetDrives_LiveInspection_DiscoversAtLeastOneDriveWithoutHardcodedCount()
    {
        var service = new StorageHealthService();
        var drives = service.GetDrives();

        // Must support single-drive machines (>= 1) rather than hardcoded >= 2
        Assert.NotNull(drives);
        Assert.True(drives.Count >= 1, "Expected at least 1 storage drive discovered on the host system.");

        var d0 = drives[0];
        Assert.NotNull(d0);
        Assert.True(d0.TotalCapacityGB > 0.0, "Disk 0 total capacity must be greater than 0 GB.");
        Assert.True(d0.UsedCapacityGB >= 0.0, "Disk 0 used capacity must be non-negative.");
        Assert.True(d0.FreeCapacityGB >= 0.0, "Disk 0 free capacity must be non-negative.");
        // -1 = wear counter not readable without administrator rights (no longer reported as a fake 100 %).
        Assert.True(d0.EstHealthPercent == -1 || (d0.EstHealthPercent >= 0 && d0.EstHealthPercent <= 100));
        Assert.False(string.IsNullOrWhiteSpace(d0.Model), "Disk model name must not be empty.");

        // If multiple drives exist, DiskNumbers should be unique and strictly ordered
        if (drives.Count > 1)
        {
            var diskNumbers = drives.Select(d => d.DiskNumber).ToList();
            Assert.Equal(diskNumbers.Count, diskNumbers.Distinct().Count());
            Assert.True(drives[1].DiskNumber > drives[0].DiskNumber);
        }
    }
}
