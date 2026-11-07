using System;
using System.Diagnostics;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class MemoryCleanerServiceTests
{
    [Fact]
    public void ShouldAutoClean_WithoutMemoryLists_ReturnsFalse()
    {
        Assert.False(new MemoryCleanerService().ShouldAutoClean(null));
    }

    [Fact]
    public void ShouldAutoClean_LowFreeAndLargeStandby_ReturnsTrue()
    {
        // The case "RAM used %" cannot see: ~55% used, but only 0.8 GB is truly free and 8 GB sits in standby.
        Assert.True(new MemoryCleanerService().ShouldAutoClean(new MemoryListSizes(0.8, 8.2)));
    }

    [Fact]
    public void ShouldAutoClean_PlentyOfFreeMemory_ReturnsFalse()
    {
        Assert.False(new MemoryCleanerService().ShouldAutoClean(new MemoryListSizes(5.7, 8.2)));
    }

    [Fact]
    public void ShouldAutoClean_SmallStandbyList_ReturnsFalseBecauseAPurgeWouldFreeNothing()
    {
        // Genuinely full memory (programs, not cache): purging the standby list cannot help.
        Assert.False(new MemoryCleanerService().ShouldAutoClean(new MemoryListSizes(0.4, 0.5)));
    }

    [Theory]
    [InlineData(0.99, 1.01, true)]
    [InlineData(1.00, 4.00, false)]   // free exactly 1 GB is not "below"
    [InlineData(0.50, 1.00, false)]   // standby exactly 1 GB is not "above"
    public void ShouldAutoClean_ThresholdsAreStrict(double freeGb, double standbyGb, bool expected)
    {
        Assert.Equal(expected, new MemoryCleanerService().ShouldAutoClean(new MemoryListSizes(freeGb, standbyGb)));
    }

    [LiveSystemFact]
    public void ShouldAutoClean_CooldownDebounce_ReturnsFalseImmediatelyAfterCleaning()
    {
        var service = new MemoryCleanerService();

        // A manual clean also restarts the automatic interval.
        service.CleanMemory(excludePid: Process.GetCurrentProcess().Id);

        Assert.False(service.ShouldAutoClean(new MemoryListSizes(0.2, 10.0)),
            "AutoClean must be rate-limited by the 5-minute interval.");
    }

    [LiveSystemFact]
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

    [LiveSystemFact]
    public async Task CleanMemoryAsync_ExecutesAsynchronouslyWithoutDeadlock()
    {
        var service = new MemoryCleanerService();
        int currentPid = Process.GetCurrentProcess().Id;

        var result = await service.CleanMemoryAsync(excludePid: currentPid);

        Assert.NotNull(result);
        Assert.True(result.AvailableGigabytes > 0.0);
    }
}
