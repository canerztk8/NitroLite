using System;
using System.Diagnostics;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class MemoryCleanerServiceTests
{
    [Fact]
    public void ShouldAutoClean_ZeroOrNegativeRamTotal_ReturnsFalse()
    {
        var service = new MemoryCleanerService();

        Assert.False(service.ShouldAutoClean(95.0, 0.0, false));
        Assert.False(service.ShouldAutoClean(95.0, -1024.0, false));
    }

    [Fact]
    public void ShouldAutoClean_WhenGameIsActive_AlwaysReturnsFalseToPreventStutter()
    {
        var service = new MemoryCleanerService();

        // High RAM pressure (92% used, 1GB free), but game is actively playing
        bool result = service.ShouldAutoClean(ramUsagePercent: 92.0, ramTotalMB: 32768.0, isGameActive: true);
        Assert.False(result, "AutoClean must never trigger when a game is actively running to prevent stutter.");
    }

    [Fact]
    public void ShouldAutoClean_NormalRamConditions_ReturnsFalse()
    {
        var service = new MemoryCleanerService();

        // 60% usage on 32GB RAM (approx 13GB free)
        bool result = service.ShouldAutoClean(ramUsagePercent: 60.0, ramTotalMB: 32768.0, isGameActive: false);
        Assert.False(result);
    }

    [Theory]
    [InlineData(88.0, 32768.0)]
    [InlineData(92.5, 32768.0)]
    [InlineData(98.0, 16384.0)]
    public void ShouldAutoClean_HighUsageThreshold_ReturnsTrue(double usagePct, double totalMB)
    {
        var service = new MemoryCleanerService();

        bool result = service.ShouldAutoClean(usagePct, totalMB, false);
        Assert.True(result, $"Expected auto clean to trigger at {usagePct}% usage.");
    }

    [Fact]
    public void ShouldAutoClean_LowFreeMemoryThreshold_ReturnsTrue()
    {
        var service = new MemoryCleanerService();

        // 80% usage on 8192 MB -> Free = 1638.4 MB (<= 2048 MB threshold)
        bool result = service.ShouldAutoClean(ramUsagePercent: 80.0, ramTotalMB: 8192.0, isGameActive: false);
        Assert.True(result, "Expected auto clean to trigger when free RAM is <= 2048 MB.");
    }

    [Fact]
    public void ShouldAutoClean_CooldownDebounce_ReturnsFalseImmediatelyAfterCleaning()
    {
        var service = new MemoryCleanerService();

        // Trigger manual clean
        service.CleanMemory(excludePid: Process.GetCurrentProcess().Id);

        // Immediately check auto clean condition (even under 95% pressure)
        bool shouldClean = service.ShouldAutoClean(95.0, 32768.0, false);
        Assert.False(shouldClean, "AutoClean must be rate-limited by the 120-second cooldown period.");
    }

    [Fact]
    public void CleanMemory_ExecutesSafelyAndReturnsValidMetrics()
    {
        var service = new MemoryCleanerService();
        int currentPid = Process.GetCurrentProcess().Id;

        var result = service.CleanMemory(excludePid: currentPid);

        Assert.NotNull(result);
        Assert.True(result.AvailableGigabytes > 0.0, "Available physical RAM must be greater than 0 GB.");
        Assert.True(result.ProcessesOptimized >= 0, "Processes optimized count must be non-negative.");
        Assert.True(result.FreedMegabytes >= 0.0, "Freed megabytes must be non-negative.");
    }

    [Fact]
    public async Task CleanMemoryAsync_ExecutesAsynchronouslyWithoutDeadlock()
    {
        var service = new MemoryCleanerService();
        int currentPid = Process.GetCurrentProcess().Id;

        var result = await service.CleanMemoryAsync(excludePid: currentPid);

        Assert.NotNull(result);
        Assert.True(result.AvailableGigabytes > 0.0);
    }
}
