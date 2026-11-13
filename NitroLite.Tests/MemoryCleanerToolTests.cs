using System;
using System.IO;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// WinMemoryCleaner is GPL-3.0 and NitroLite is MIT, so the tool is never bundled: NitroLite only launches a copy
/// the user installed. These tests pin the lookup order and the "not installed" answer the RAM card depends on.
/// </summary>
public class MemoryCleanerToolTests
{
    private static string ToolsFolder => Path.Combine(AppPaths.DataDirectory, "tools");

    [Fact]
    public void FindTool_FindsAPortableCopyInTheDataFolder()
    {
        string folder = ToolsFolder;
        string exe = Path.Combine(folder, MemoryCleanerService.ToolFileName);
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(exe, "stub");
            Assert.Equal(exe, MemoryCleanerService.FindTool());
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void FindTool_ReturnsNullWhenThePortableCopyIsGone()
    {
        string exe = Path.Combine(ToolsFolder, MemoryCleanerService.ToolFileName);
        try { File.Delete(exe); } catch { }

        // The machine may genuinely have WinMemoryCleaner installed through winget or Program Files; only the
        // portable copy is under this test's control, so assert on that path rather than on a global null.
        Assert.NotEqual(exe, MemoryCleanerService.FindTool());
    }

    [Fact]
    public void CleanMemory_ReportsTheToolMissingInsteadOfThrowing()
    {
        string exe = Path.Combine(ToolsFolder, MemoryCleanerService.ToolFileName);
        try { File.Delete(exe); } catch { }

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
