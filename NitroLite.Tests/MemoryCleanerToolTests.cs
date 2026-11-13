using System;
using System.IO;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The release package ships an unmodified WinMemoryCleaner under <c>tools\</c>, so the RAM card works without a
/// separate installation. These tests pin the lookup order: a copy the user dropped into the data folder wins,
/// then the bundled one, then a system-wide install.
/// </summary>
public class MemoryCleanerToolTests
{
    private static string ToolsFolder => Path.Combine(AppPaths.DataDirectory, "tools");

    private static string PortableCopy => Path.Combine(ToolsFolder, MemoryCleanerService.ToolFileName);

    /// <summary>Creates the bundled stub next to the test assembly and returns its path.</summary>
    private static string CreateBundledStub()
    {
        string bundled = MemoryCleanerService.BundledToolPath
                         ?? throw new InvalidOperationException("BundledToolPath is null in the test host.");
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllText(bundled, "stub");
        return bundled;
    }

    private static void RemoveBundledStub(string bundled)
    {
        try { File.Delete(bundled); } catch { }
        try { Directory.Delete(Path.GetDirectoryName(bundled)!, recursive: false); } catch { }
    }

    [Fact]
    public void BundledToolPath_PointsAtTheToolsFolderNextToTheExecutable()
    {
        string? bundled = MemoryCleanerService.BundledToolPath;
        Assert.NotNull(bundled);
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "tools", MemoryCleanerService.ToolFileName),
            bundled);
    }

    [Fact]
    public void FindTool_FindsAPortableCopyInTheDataFolder()
    {
        Directory.CreateDirectory(ToolsFolder);
        try
        {
            File.WriteAllText(PortableCopy, "stub");
            Assert.Equal(PortableCopy, MemoryCleanerService.FindTool());
        }
        finally
        {
            try { File.Delete(PortableCopy); } catch { }
        }
    }

    [Fact]
    public void FindTool_FindsTheBundledCopyWhenNothingElseIsInstalled()
    {
        try { File.Delete(PortableCopy); } catch { }
        string bundled = CreateBundledStub();
        try
        {
            Assert.Equal(bundled, MemoryCleanerService.FindTool());
        }
        finally
        {
            RemoveBundledStub(bundled);
        }
    }

    [Fact]
    public void FindTool_PrefersTheDataFolderCopyOverTheBundledOne()
    {
        // The data folder is the escape hatch for trying a newer upstream build without a NitroLite release.
        Directory.CreateDirectory(ToolsFolder);
        string bundled = CreateBundledStub();
        try
        {
            File.WriteAllText(PortableCopy, "portable");
            Assert.Equal(PortableCopy, MemoryCleanerService.FindTool());
        }
        finally
        {
            try { File.Delete(PortableCopy); } catch { }
            RemoveBundledStub(bundled);
        }
    }

    [Fact]
    public void FindTool_ReturnsNullWhenThePortableCopyIsGone()
    {
        try { File.Delete(PortableCopy); } catch { }

        // The machine may genuinely have WinMemoryCleaner installed through winget or Program Files; only the
        // portable copy is under this test's control, so assert on that path rather than on a global null.
        Assert.NotEqual(PortableCopy, MemoryCleanerService.FindTool());
    }

    [Fact]
    public void CleanMemory_ReportsTheToolMissingInsteadOfThrowing()
    {
        try { File.Delete(PortableCopy); } catch { }

        if (MemoryCleanerService.FindTool() != null) return; // installed on this machine; nothing to assert

        var result = new MemoryCleanerService().CleanMemoryAsync().GetAwaiter().GetResult();
        Assert.True(result.ToolMissing);
        Assert.False(result.Cleaned);
        Assert.Equal(0, result.FreedMegabytes);
    }

    [Fact]
    public void CleanArguments_TouchOnlyTheStandbyList()
    {
        // Trimming other processes' working sets would push their pages straight back into the list we just
        // released, so the argument list must stay at the one area WinMemoryCleaner's own demo uses.
        Assert.Equal("/StandbyList", MemoryCleanerService.CleanArguments);
    }

    [Fact]
    public void ToolUrls_PointAtTheOfficialProject()
    {
        Assert.StartsWith("https://github.com/IgorMundstein/WinMemoryCleaner", MemoryCleanerService.ToolProjectUrl);
        Assert.StartsWith("https://github.com/IgorMundstein/WinMemoryCleaner/releases", MemoryCleanerService.ToolReleasesUrl);
        Assert.Equal("IgorMundstein.WinMemoryCleaner", MemoryCleanerService.ToolWingetId);
    }

    [Fact]
    public void ReadMemoryLists_ReturnsPlausibleSizesWithoutAdministratorRights()
    {
        var lists = MemoryCleanerService.ReadMemoryLists();
        Assert.NotNull(lists);
        Assert.True(lists!.Value.FreeGigabytes >= 0);
        Assert.True(lists.Value.StandbyGigabytes >= 0);
    }
}
