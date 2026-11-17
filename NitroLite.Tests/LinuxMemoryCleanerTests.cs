using NitroLite.Core;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The Linux half of the RAM card: dropping the page cache, which is what "empty the standby list" means here.
/// </summary>
/// <remarks>
/// WinMemoryCleaner's <c>/StandbyList</c> is one call to <c>NtSetSystemInformation</c>; the Linux equivalent is one
/// write to <c>/proc/sys/vm/drop_caches</c>. These tests pin the three things that can go wrong: the write needs root,
/// the user can dismiss the password dialog, and the "freed" figure has to come from the reclaimable half of
/// <c>/proc/meminfo</c> rather than from free memory, which swings on its own.
/// </remarks>
public class LinuxMemoryCleanerTests
{
    private const string MemInfo = LinuxMemoryCleaner.MemInfoPath;

    /// <summary>A <c>/proc/meminfo</c> with the six fields the reader uses, in kB.</summary>
    private static string MemInfoText(
        double totalKb = 32_000_000,
        double freeKb = 4_000_000,
        double availableKb = 12_000_000,
        double buffersKb = 500_000,
        double cachedKb = 8_000_000,
        double sReclaimableKb = 1_500_000) =>
        "MemTotal:       " + totalKb + " kB\n" +
        "MemFree:        " + freeKb + " kB\n" +
        "MemAvailable:   " + availableKb + " kB\n" +
        "Buffers:        " + buffersKb + " kB\n" +
        "Cached:         " + cachedKb + " kB\n" +
        "SReclaimable:   " + sReclaimableKb + " kB\n" +
        "SwapCached:     0 kB\n";

    private static InMemorySysfsFileSystem Proc(string memInfo) =>
        new InMemorySysfsFileSystem().WithFile(MemInfo, memInfo);

    [Fact]
    public void IsSupported_IsTrueAndNeedsNoHelper()
    {
        // The card hides itself on IsSupported false, and offers a download page on NeedsExternalTool. Linux is the
        // case where the first is true and the second is not: the interface is the kernel's.
        var cleaner = new LinuxMemoryCleaner(Proc(MemInfoText()), (_, _) => Task.FromResult(LinuxElevationResult.Ok));

        Assert.True(cleaner.IsSupported);
        Assert.False(cleaner.NeedsExternalTool);
    }

    [Fact]
    public void ReadSnapshot_ReportsAvailableAndTheReclaimableHalf()
    {
        var cleaner = new LinuxMemoryCleaner(Proc(MemInfoText()), (_, _) => Task.FromResult(LinuxElevationResult.Ok));

        var snapshot = cleaner.ReadSnapshot();

        Assert.NotNull(snapshot);
        // 12 000 000 kB of MemAvailable is 11.44 GiB.
        Assert.Equal(11.44, snapshot!.Value.AvailableGigabytes, 2);
        // Cached + SReclaimable + Buffers = 10 000 000 kB is 9.54 GiB: the Linux standby list.
        Assert.Equal(9.54, snapshot.Value.CachedGigabytes, 2);
    }

    [Fact]
    public void ReadSnapshot_FallsBackToFreePlusCachedOnAKernelWithoutMemAvailable()
    {
        // MemAvailable arrived in 3.14. The fallback is the expression it replaced, so an old kernel still reports a
        // figure rather than zero.
        var text = "MemTotal:       32000000 kB\n" +
                   "MemFree:        4000000 kB\n" +
                   "Buffers:         500000 kB\n" +
                   "Cached:         8000000 kB\n" +
                   "SReclaimable:   1500000 kB\n";
        var cleaner = new LinuxMemoryCleaner(Proc(text), (_, _) => Task.FromResult(LinuxElevationResult.Ok));

        var snapshot = cleaner.ReadSnapshot();

        Assert.NotNull(snapshot);
        // 4 000 000 + 500 000 + 8 000 000 = 12 500 000 kB is 11.92 GiB.
        Assert.Equal(11.92, snapshot!.Value.AvailableGigabytes, 2);
    }

    [Fact]
    public void ReadSnapshot_ReturnsNullWhenMemInfoIsUnreadable()
    {
        var cleaner = new LinuxMemoryCleaner(new InMemorySysfsFileSystem(), (_, _) => Task.FromResult(LinuxElevationResult.Ok));

        Assert.Null(cleaner.ReadSnapshot());
    }

    [Fact]
    public async Task Clean_WritesDropCachesAndReportsTheDrop()
    {
        var fs = Proc(MemInfoText());
        string? command = null;

        var cleaner = new LinuxMemoryCleaner(fs, (cmd, _) =>
        {
            command = cmd;
            // The kernel has dropped the cache by the time the command returns, so the second read sees less.
            fs.WithFile(MemInfo, MemInfoText(availableKb: 20_000_000, cachedKb: 1_000_000, sReclaimableKb: 200_000));
            return Task.FromResult(LinuxElevationResult.Ok);
        });

        var result = await cleaner.CleanStandbyMemoryAsync();

        Assert.True(result.Cleaned);
        Assert.Null(result.Error);
        Assert.False(result.ToolMissing);
        Assert.False(result.ElevationRefused);

        // The command is the whole operation: drop_caches=3 drops the page cache and the reclaimable slab.
        Assert.NotNull(command);
        Assert.Contains($"echo {LinuxMemoryCleaner.DropCachesValue} > {LinuxMemoryCleaner.DropCachesPath}", command);

        // 9.54 GiB of reclaimable memory became 1.62 GiB (the buffers are not dropped), so 7.92 GiB was released.
        Assert.Equal(8110.0, result.FreedMegabytes, 0);
        Assert.Equal(11.44, result.AvailableBeforeGigabytes, 2);
        Assert.Equal(19.07, result.AvailableGigabytes, 2);
    }

    [Fact]
    public async Task Clean_WhenThePasswordDialogIsDismissed_ReportsARefusal()
    {
        var cleaner = new LinuxMemoryCleaner(
            Proc(MemInfoText()),
            (_, _) => Task.FromResult(LinuxElevationResult.Refused("pkexec exited with 126.")));

        var result = await cleaner.CleanStandbyMemoryAsync();

        Assert.False(result.Cleaned);
        Assert.True(result.ElevationRefused);
    }

    [Fact]
    public async Task Clean_WhenNeitherPkexecNorSudoExists_ReportsAnError()
    {
        var cleaner = new LinuxMemoryCleaner(
            Proc(MemInfoText()),
            (_, _) => Task.FromResult(LinuxElevationResult.Unavailable("Neither pkexec nor sudo is installed.")));

        var result = await cleaner.CleanStandbyMemoryAsync();

        Assert.False(result.Cleaned);
        Assert.False(result.ElevationRefused);
        Assert.Contains("pkexec", result.Error);
    }

    [Fact]
    public async Task Clean_WhenTheCommandFails_ReportsTheToolsOwnMessage()
    {
        var cleaner = new LinuxMemoryCleaner(
            Proc(MemInfoText()),
            (_, _) => Task.FromResult(LinuxElevationResult.Failed("sh exited with 1: Read-only file system")));

        var result = await cleaner.CleanStandbyMemoryAsync();

        Assert.False(result.Cleaned);
        Assert.Contains("Read-only file system", result.Error);
    }

    [Fact]
    public async Task Clean_WhenTheCacheGrowsInsteadOfShrinking_ReportsZeroRatherThanANegative()
    {
        // Another process can refill the cache between the two reads. A negative saving would be reported as
        // "-512 MB freed", which reads as a bug in the app rather than as a busy machine.
        var fs = Proc(MemInfoText());
        var cleaner = new LinuxMemoryCleaner(fs, (_, _) =>
        {
            fs.WithFile(MemInfo, MemInfoText(cachedKb: 9_000_000));
            return Task.FromResult(LinuxElevationResult.Ok);
        });

        var result = await cleaner.CleanStandbyMemoryAsync();

        Assert.True(result.Cleaned);
        Assert.Equal(0, result.FreedMegabytes);
    }

    [Fact]
    public async Task Clean_WhenMemInfoCannotBeReadBeforeTheClean_DoesNotRunAnything()
    {
        bool ran = false;
        var cleaner = new LinuxMemoryCleaner(new InMemorySysfsFileSystem(), (_, _) =>
        {
            ran = true;
            return Task.FromResult(LinuxElevationResult.Ok);
        });

        var result = await cleaner.CleanStandbyMemoryAsync();

        Assert.False(result.Cleaned);
        Assert.False(ran);
        Assert.Contains(LinuxMemoryCleaner.MemInfoPath, result.Error);
    }
}
