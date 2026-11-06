using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace NitroLite.Core;

public class StorageDriveInfo
{
    public int DiskNumber { get; set; }
    public string Model { get; set; } = "";
    public string DriveLetter { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string BusType { get; set; } = "NVMe";
    public double TotalCapacityGB { get; set; }
    public double UsedCapacityGB { get; set; }
    public double FreeCapacityGB { get; set; }
    public double UsedPercent { get; set; }
    public int HealthStatus { get; set; } // 0 = Healthy, 1 = Warning, 2 = Unhealthy
    public string HealthDesc { get; set; } = "Sağlıklı (Nominal)";
    public int EstHealthPercent { get; set; } = 100;
    public int? Temperature { get; set; }
}

public class StorageHealthService
{
    public List<StorageDriveInfo> GetDrives()
    {
        var list = new List<StorageDriveInfo>();

        try
        {
            // 1. Get partition letters mapped to disk numbers via MSFT_Partition
            var partitionLetters = new Dictionary<int, List<string>>();
            try
            {
                using var partSearcher = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    "SELECT DiskNumber, DriveLetter FROM MSFT_Partition WHERE DriveLetter IS NOT NULL");
                using var partResults = partSearcher.Get();
                foreach (ManagementObject part in partResults)
                {
                    using (part)
                    {
                        if (part["DiskNumber"] != null && part["DriveLetter"] != null)
                        {
                            int diskNum = Convert.ToInt32(part["DiskNumber"]);
                            char letterChar = '\0';
                            if (part["DriveLetter"] is char c)
                            {
                                letterChar = c;
                            }
                            else if (part["DriveLetter"] is string s && s.Length > 0)
                            {
                                letterChar = s[0];
                            }
                            else
                            {
                                try { letterChar = Convert.ToChar(part["DriveLetter"]); } catch { }
                            }

                            if (char.IsLetter(letterChar) && letterChar != '\0')
                            {
                                string letter = char.ToUpperInvariant(letterChar).ToString();
                                if (!partitionLetters.ContainsKey(diskNum))
                                {
                                    partitionLetters[diskNum] = new List<string>();
                                }
                                if (!partitionLetters[diskNum].Contains(letter))
                                {
                                    partitionLetters[diskNum].Add(letter);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // Secondary partition mapping via Win32_LogicalDiskToPartition if MSFT_Partition had no results
            if (partitionLetters.Count == 0)
            {
                try
                {
                    using var cimSearcher = new ManagementObjectSearcher(
                        @"root\cimv2",
                        "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition");
                    using var cimResults = cimSearcher.Get();
                    foreach (ManagementObject rel in cimResults)
                    {
                        using (rel)
                        {
                            string? ant = rel["Antecedent"]?.ToString();
                            string? dep = rel["Dependent"]?.ToString();
                            if (!string.IsNullOrEmpty(ant) && !string.IsNullOrEmpty(dep))
                            {
                                var diskMatch = Regex.Match(ant, @"Disk #(\d+)");
                                var letterMatch = Regex.Match(dep, @"DeviceID=""([A-Za-z]):""");
                                if (diskMatch.Success && letterMatch.Success && int.TryParse(diskMatch.Groups[1].Value, out int diskIdx))
                                {
                                    string let = letterMatch.Groups[1].Value.ToUpperInvariant();
                                    if (!partitionLetters.ContainsKey(diskIdx))
                                    {
                                        partitionLetters[diskIdx] = new List<string>();
                                    }
                                    if (!partitionLetters[diskIdx].Contains(let))
                                    {
                                        partitionLetters[diskIdx].Add(let);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // 2. Query Storage Reliability Counters for genuine SMART wear percentage
            var wearMap = new Dictionary<int, int>();
            var tempMap = new Dictionary<int, int>();
            try
            {
                using var relSearcher = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    "SELECT DeviceId, Wear, Temperature FROM MSFT_StorageReliabilityCounter");
                using var relResults = relSearcher.Get();
                foreach (ManagementObject rel in relResults)
                {
                    using (rel)
                    {
                        string? devId = rel["DeviceId"]?.ToString();
                        if (!string.IsNullOrEmpty(devId))
                        {
                            var match = Regex.Match(devId, @"\d+");
                            if (match.Success && int.TryParse(match.Value, out int parsedId))
                            {
                                if (rel["Wear"] != null)
                                {
                                    wearMap[parsedId] = Convert.ToInt32(rel["Wear"]);
                                }
                                if (rel["Temperature"] != null)
                                {
                                    int t = Convert.ToInt32(rel["Temperature"]);
                                    if (t > 0 && t < 120)
                                    {
                                        tempMap[parsedId] = t;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Query physical disks via MSFT_PhysicalDisk
            using var diskSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName, FirmwareVersion, HealthStatus, Size, BusType FROM MSFT_PhysicalDisk");
            using var diskResults = diskSearcher.Get();
            foreach (ManagementObject disk in diskResults)
            {
                using (disk)
                {
                    try
                    {
                        // Robust DeviceId parsing: handles "0", "1", "PHYSICALDRIVE0", etc. without FormatException
                        int diskNum = 0;
                        string? devIdRaw = disk["DeviceId"]?.ToString();
                        if (!string.IsNullOrEmpty(devIdRaw))
                        {
                            var numMatch = Regex.Match(devIdRaw, @"\d+");
                            if (numMatch.Success && int.TryParse(numMatch.Value, out int parsedNum))
                            {
                                diskNum = parsedNum;
                            }
                        }

                        string friendlyName = disk["FriendlyName"]?.ToString() ?? "NVMe SSD";
                        string firmware = disk["FirmwareVersion"]?.ToString() ?? "";
                        int health = Convert.ToInt32(disk["HealthStatus"] ?? 0);
                        long sizeBytes = Convert.ToInt64(disk["Size"] ?? 0);
                        int busType = Convert.ToInt32(disk["BusType"] ?? 0);

                        string busDesc = busType == 17 ? "NVMe Gen 4x4" : (busType == 11 ? "SATA SSD" : "SSD");

                        List<string> candidateLetters = new();
                        if (partitionLetters.TryGetValue(diskNum, out var mappedLetters) && mappedLetters.Count > 0)
                        {
                            candidateLetters.AddRange(mappedLetters);
                        }

                        long mountedTotalBytes = 0;
                        long mountedUsedBytes = 0;
                        long mountedFreeBytes = 0;
                        var resolvedLetters = new List<string>();

                        foreach (var letter in candidateLetters)
                        {
                            try
                            {
                                string cleanLetter = letter.Trim().TrimEnd('\\', ':');
                                if (cleanLetter.Length == 1 && char.IsLetter(cleanLetter[0]))
                                {
                                    var di = new DriveInfo(cleanLetter);
                                    if (di.IsReady)
                                    {
                                        mountedTotalBytes += di.TotalSize;
                                        mountedFreeBytes += di.AvailableFreeSpace;
                                        mountedUsedBytes += Math.Max(0, di.TotalSize - di.AvailableFreeSpace);
                                        resolvedLetters.Add(cleanLetter.ToUpperInvariant());
                                    }
                                }
                            }
                            catch { }
                        }

                        // Physical size is the authoritative total capacity of the drive
                        long totalBytes = sizeBytes > 0 ? sizeBytes : mountedTotalBytes;
                        long usedBytes;
                        long freeBytes;

                        if (resolvedLetters.Count > 0)
                        {
                            usedBytes = mountedUsedBytes;
                            freeBytes = mountedFreeBytes;
                        }
                        else
                        {
                            // Unpartitioned or unmounted drive: 0 used, all space available
                            usedBytes = 0;
                            freeBytes = 0;
                        }

                        double totalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
                        double freeGB = Math.Round(freeBytes / (1024.0 * 1024.0 * 1024.0), 1);
                        double usedGB = Math.Round(usedBytes / (1024.0 * 1024.0 * 1024.0), 1);
                        double usedPercent = totalGB > 0 ? Math.Round((usedGB / totalGB) * 100.0, 1) : 0.0;
                        string displayLetter = resolvedLetters.Count > 0
                            ? string.Join(", ", resolvedLetters)
                            : "";

                        // Legitimate health status and endurance calculation
                        int estHealth;
                        if (wearMap.TryGetValue(diskNum, out int wearVal) && wearVal >= 0 && wearVal <= 100)
                        {
                            estHealth = Math.Clamp(100 - wearVal, 0, 100);
                        }
                        else if (health == 0)
                        {
                            estHealth = 100;
                        }
                        else if (health == 1)
                        {
                            estHealth = 70;
                        }
                        else
                        {
                            estHealth = 30;
                        }

                        string healthDesc;
                        if (health == 0)
                        {
                            healthDesc = estHealth >= 90 ? "Sağlıklı (Nominal)" : "Sağlıklı (Kondisyon İyi)";
                        }
                        else if (health == 1)
                        {
                            healthDesc = "Uyarı (Sürücü Kontrolü Gerekli)";
                        }
                        else
                        {
                            healthDesc = "Kritik (Acil Yedekleme Önerilir)";
                        }

                        int? driveTemp = null;
                        if (tempMap.TryGetValue(diskNum, out int measuredTemp) && measuredTemp > 0 && measuredTemp < 120)
                        {
                            driveTemp = measuredTemp;
                        }

                        list.Add(new StorageDriveInfo
                        {
                            DiskNumber = diskNum,
                            Model = friendlyName,
                            DriveLetter = displayLetter,
                            Firmware = firmware,
                            BusType = busDesc,
                            TotalCapacityGB = totalGB,
                            UsedCapacityGB = usedGB,
                            FreeCapacityGB = freeGB,
                            UsedPercent = usedPercent,
                            HealthStatus = health,
                            HealthDesc = healthDesc,
                            EstHealthPercent = estHealth,
                            Temperature = driveTemp
                        });
                    }
                    catch { }
                }
            }
        }
        catch { }

        // Secondary fallback via Win32_DiskDrive from root\cimv2 if MSFT_PhysicalDisk returned nothing
        if (list.Count == 0)
        {
            try
            {
                using var win32Searcher = new ManagementObjectSearcher(
                    @"root\cimv2",
                    "SELECT Index, Model, InterfaceType, Size FROM Win32_DiskDrive");
                using var win32Results = win32Searcher.Get();
                foreach (ManagementObject disk in win32Results)
                {
                    using (disk)
                    {
                        try
                        {
                            int index = Convert.ToInt32(disk["Index"] ?? 0);
                            string model = disk["Model"]?.ToString() ?? $"Disk {index}";
                            long sizeBytes = Convert.ToInt64(disk["Size"] ?? 0);

                            // Dynamically map partition letters for this index
                            var resolvedLetters = new List<string>();
                            long mountedUsed = 0;
                            long mountedFree = 0;
                            try
                            {
                                using var partSearcher = new ManagementObjectSearcher(
                                    @"root\cimv2",
                                    $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\.\\\\PHYSICALDRIVE{index}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                                using var partitions = partSearcher.Get();
                                foreach (ManagementObject part in partitions)
                                {
                                    using (part)
                                    {
                                        string? partId = part["DeviceID"]?.ToString();
                                        if (!string.IsNullOrEmpty(partId))
                                        {
                                            using var logSearcher = new ManagementObjectSearcher(
                                                @"root\cimv2",
                                                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                                            using var logDisks = logSearcher.Get();
                                            foreach (ManagementObject logDisk in logDisks)
                                            {
                                                using (logDisk)
                                                {
                                                    string? devId = logDisk["DeviceID"]?.ToString();
                                                    if (!string.IsNullOrEmpty(devId) && devId.Length >= 2 && char.IsLetter(devId[0]))
                                                    {
                                                        string let = devId.Substring(0, 1).ToUpperInvariant();
                                                        var di = new DriveInfo(let);
                                                        if (di.IsReady)
                                                        {
                                                            mountedFree += di.AvailableFreeSpace;
                                                            mountedUsed += Math.Max(0, di.TotalSize - di.AvailableFreeSpace);
                                                            if (!resolvedLetters.Contains(let))
                                                                resolvedLetters.Add(let);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }

                            long totalBytes = sizeBytes > 0 ? sizeBytes : (mountedUsed + mountedFree);
                            long usedBytes = mountedUsed;
                            long freeBytes = mountedFree;

                            double totalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
                            double freeGB = Math.Round(freeBytes / (1024.0 * 1024.0 * 1024.0), 1);
                            double usedGB = Math.Round(usedBytes / (1024.0 * 1024.0 * 1024.0), 1);
                            double usedPercent = totalGB > 0 ? Math.Round((usedGB / totalGB) * 100.0, 1) : 0;
                            string displayLetter = resolvedLetters.Count > 0 ? string.Join(", ", resolvedLetters) : "";

                            list.Add(new StorageDriveInfo
                            {
                                DiskNumber = index,
                                Model = model,
                                DriveLetter = displayLetter,
                                BusType = "NVMe Gen 4x4",
                                TotalCapacityGB = totalGB,
                                UsedCapacityGB = usedGB,
                                FreeCapacityGB = freeGB,
                                UsedPercent = usedPercent,
                                HealthStatus = 0,
                                HealthDesc = "Sağlıklı (Nominal)",
                                EstHealthPercent = 100,
                                Temperature = null
                            });
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // Final fallback if WMI fails completely: query local fixed drives
        if (list.Count == 0)
        {
            int index = 0;
            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType == DriveType.Fixed && di.IsReady)
                {
                    double totalGB = Math.Round(di.TotalSize / (1024.0 * 1024.0 * 1024.0), 1);
                    double freeGB = Math.Round(di.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);
                    double usedGB = Math.Round(Math.Max(0, di.TotalSize - di.AvailableFreeSpace) / (1024.0 * 1024.0 * 1024.0), 1);
                    string driveLetter = di.Name.Replace(":\\", "").Replace(":", "").Trim();
                    list.Add(new StorageDriveInfo
                    {
                        DiskNumber = index++,
                        Model = $"Sabit Sürücü ({driveLetter}:)",
                        DriveLetter = driveLetter,
                        BusType = "SSD",
                        TotalCapacityGB = totalGB,
                        UsedCapacityGB = usedGB,
                        FreeCapacityGB = freeGB,
                        UsedPercent = totalGB > 0 ? Math.Round((usedGB / totalGB) * 100.0, 1) : 0,
                        HealthDesc = "Sağlıklı (Kondisyon İyi)",
                        EstHealthPercent = 100,
                        Temperature = null
                    });
                }
            }
        }

        // Ensure list is strictly ordered by DiskNumber
        return list.OrderBy(d => d.DiskNumber).ToList();
    }
}
