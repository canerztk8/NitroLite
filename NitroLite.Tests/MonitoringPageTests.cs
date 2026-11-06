using NitroLite;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class HardwareInfoTests
{
    [Fact]
    public void CoreText_ShowsCoresAndThreads_NotThreadsAsCores()
    {
        var info = new HardwareInfo { CpuCores = 8, CpuThreads = 16 };
        Assert.Equal("8 çekirdek / 16 iş parçacığı", info.FormatCoreText(16));
        Assert.Equal("16 iş parçacığı", new HardwareInfo().FormatCoreText(16));
    }

    [Fact]
    public void MemoryText_UsesDetectedValues_AndModulesNotChannels()
    {
        var info = new HardwareInfo { MemoryType = "DDR5", MemorySpeedMts = 5600, MemoryModules = 2, MemoryTotalGb = 32 };
        Assert.Equal("DDR5 5600 MT/s · 2 modül · 32 GB", info.FormatMemoryText());
        Assert.Equal("Bellek bilgisi okunamadı", new HardwareInfo().FormatMemoryText());
    }

    [Theory]
    [InlineData(34, "DDR5")]
    [InlineData(26, "DDR4")]
    [InlineData(35, "LPDDR5")]
    [InlineData(0, "")]
    public void MemoryTypeCodes_MapToNames(int code, string expected)
    {
        Assert.Equal(expected, HardwareInfoService.MemoryTypeName(code));
    }

    [Fact]
    public void PickDiscreteGpu_PrefersNvidiaOverIntegratedAndBasicDisplay()
    {
        Assert.Equal("NVIDIA GeForce RTX 4070 Laptop GPU",
            HardwareInfoService.PickDiscreteGpu(new[] { "AMD Radeon(TM) 780M", "NVIDIA GeForce RTX 4070 Laptop GPU" }));
        Assert.Equal("AMD Radeon(TM) 780M",
            HardwareInfoService.PickDiscreteGpu(new[] { "Microsoft Basic Display Adapter", "AMD Radeon(TM) 780M" }));
        Assert.Equal("", HardwareInfoService.PickDiscreteGpu(System.Array.Empty<string>()));
    }

    [Fact]
    public void LiveRead_ReturnsSomethingForThisMachine()
    {
        // Read-only WMI query.
        var info = HardwareInfoService.Get();
        Assert.False(string.IsNullOrWhiteSpace(info.CpuName));
        Assert.True(info.CpuThreads >= info.CpuCores);
    }
}

public class ChartValueTests
{
    [Fact]
    public void GpuMetrics_AreSkippedWhileTheGpuSleeps()
    {
        var asleep = new MonitorData { GpuTemperature = 0, GpuPowerWatts = 0, GpuUsage = 0, VramTotalMB = 8192 };
        Assert.Null(MainWindow.GetChartValue("GPU_TEMP", asleep));
        Assert.Null(MainWindow.GetChartValue("GPU_WATT", asleep));
        Assert.Null(MainWindow.GetChartValue("GPU_USAGE", asleep));
        Assert.Null(MainWindow.GetChartValue("VRAM_GB", asleep));

        var awake = new MonitorData { GpuTemperature = 55, GpuPowerWatts = 20, GpuUsage = 12, VramUsedMB = 2048, VramTotalMB = 8192 };
        Assert.Equal(55, MainWindow.GetChartValue("GPU_TEMP", awake));
        Assert.Equal(2.0, MainWindow.GetChartValue("VRAM_GB", awake));
    }

    [Fact]
    public void MissingCpuReadings_AreSkipped_ButZeroUsageIsReal()
    {
        var d = new MonitorData { CpuTemperature = 0, CpuFrequencyMHz = 0, CpuUsage = 0 };
        Assert.Null(MainWindow.GetChartValue("CPU_TEMP", d));
        Assert.Null(MainWindow.GetChartValue("CPU_GHZ", d));
        Assert.Equal(0, MainWindow.GetChartValue("CPU_USAGE", d));
    }
}

public class MemoryCleanerSafetyTests
{
    [Fact]
    public void CleanMemory_NeverTrimsOtherProcesses()
    {
        // Safe to run: the cleaner only purges the standby list (when elevated) and trims NitroLite's own heap.
        var result = new MemoryCleanerService().CleanMemory();
        Assert.Equal(0, result.ProcessesOptimized);
        if (!MemoryCleanerService.IsRunningAsAdministrator())
        {
            Assert.True(result.RequiresAdministrator);
            Assert.False(result.StandbyPurged);
        }
    }
}
