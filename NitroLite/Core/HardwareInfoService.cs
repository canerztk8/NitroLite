using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace NitroLite.Core;

/// <summary>Static hardware description read once from Windows (WMI) instead of hard-coded labels.</summary>
public sealed class HardwareInfo
{
    public string CpuName { get; init; } = "";
    public int CpuCores { get; init; }
    public int CpuThreads { get; init; }
    public string DiscreteGpuName { get; init; } = "";
    public int MemoryModules { get; init; }
    public double MemoryTotalGb { get; init; }
    public int MemorySpeedMts { get; init; }
    public string MemoryType { get; init; } = "";

    /// <summary>e.g. "8 çekirdek / 16 iş parçacığı"; falls back to the thread count from AASSvc.</summary>
    public string FormatCoreText(int fallbackThreads)
    {
        if (CpuCores > 0 && CpuThreads > 0) return $"{CpuCores} çekirdek / {CpuThreads} iş parçacığı";
        return fallbackThreads > 0 ? $"{fallbackThreads} iş parçacığı" : "";
    }

    /// <summary>e.g. "DDR5 5600 MT/s · 2 modül · 32 GB" (modules, not channels: Windows does not report channels).</summary>
    public string FormatMemoryText()
    {
        var parts = new List<string>();
        var typeSpeed = string.Join(" ", new[] { MemoryType, MemorySpeedMts > 0 ? $"{MemorySpeedMts} MT/s" : "" }.Where(s => s.Length > 0));
        if (typeSpeed.Length > 0) parts.Add(typeSpeed);
        if (MemoryModules > 0) parts.Add($"{MemoryModules} modül");
        if (MemoryTotalGb > 0) parts.Add($"{MemoryTotalGb:0.#} GB");
        return parts.Count > 0 ? string.Join(" · ", parts) : "Bellek bilgisi okunamadı";
    }
}

public static class HardwareInfoService
{
    private static readonly object Gate = new();
    private static HardwareInfo? _cached;

    public static HardwareInfo Get()
    {
        lock (Gate)
        {
            return _cached ??= Read();
        }
    }

    /// <summary>SMBIOS memory type codes (Win32_PhysicalMemory.SMBIOSMemoryType).</summary>
    public static string MemoryTypeName(int smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => ""
    };

    /// <summary>Prefers the NVIDIA/AMD discrete adapter over the integrated one and Microsoft's basic display driver.</summary>
    public static string PickDiscreteGpu(IEnumerable<string> adapterNames)
    {
        var names = adapterNames.Where(n => !string.IsNullOrWhiteSpace(n))
                                .Select(n => n.Trim())
                                .Where(n => !n.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                                .ToList();
        return names.FirstOrDefault(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(n => n.Contains("RX ", StringComparison.OrdinalIgnoreCase) || n.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault()
            ?? "";
    }

    private static HardwareInfo Read()
    {
        string cpuName = ""; int cores = 0, threads = 0;
        string gpu = "";
        int modules = 0, speed = 0, smbiosType = 0; double totalBytes = 0;

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            using var r = s.Get();
            foreach (ManagementObject o in r)
            {
                using (o)
                {
                    cpuName = (o["Name"]?.ToString() ?? "").Trim();
                    cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                    threads += Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
                }
            }
        }
        catch { }

        try
        {
            var names = new List<string>();
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            using var r = s.Get();
            foreach (ManagementObject o in r)
            {
                using (o) names.Add(o["Name"]?.ToString() ?? "");
            }
            gpu = PickDiscreteGpu(names);
        }
        catch { }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            using var r = s.Get();
            foreach (ManagementObject o in r)
            {
                using (o)
                {
                    modules++;
                    totalBytes += Convert.ToDouble(o["Capacity"] ?? 0);
                    int configured = Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0);
                    int rated = Convert.ToInt32(o["Speed"] ?? 0);
                    speed = Math.Max(speed, configured > 0 ? configured : rated);
                    if (smbiosType == 0) smbiosType = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0);
                }
            }
        }
        catch { }

        return new HardwareInfo
        {
            CpuName = cpuName,
            CpuCores = cores,
            CpuThreads = threads,
            DiscreteGpuName = gpu,
            MemoryModules = modules,
            MemoryTotalGb = Math.Round(totalBytes / (1024.0 * 1024 * 1024), 1),
            MemorySpeedMts = speed,
            MemoryType = MemoryTypeName(smbiosType)
        };
    }
}
