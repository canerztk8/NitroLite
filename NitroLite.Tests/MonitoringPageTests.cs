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
