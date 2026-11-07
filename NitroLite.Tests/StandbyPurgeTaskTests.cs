using System;
using System.IO;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class StandbyPurgeTaskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nl_purge_" + Guid.NewGuid().ToString("N"));
    private string Bundled => Path.Combine(_root, "bundled");
    private string Installed => Path.Combine(_root, "installed");

    public StandbyPurgeTaskTests()
    {
        Directory.CreateDirectory(Bundled);
        foreach (var f in StandbyPurgeTask.CandidateFiles) File.WriteAllText(Path.Combine(Bundled, f), "v1 " + f);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void CopyToInstalled(string? changeFile = null)
    {
        Directory.CreateDirectory(Installed);
        foreach (var f in StandbyPurgeTask.CandidateFiles)
            File.WriteAllText(Path.Combine(Installed, f), f == changeFile ? "old " + f : "v1 " + f);
    }

    private StandbyPurgeTask Create(Func<StandbyPurgeTask.RegisteredTaskInfo?> reader) => new()
    {
        BundledDirectory = Bundled,
        InstallDirectory = Installed,
        TaskReader = reader
    };

    private StandbyPurgeTask.RegisteredTaskInfo GoodTask() =>
        new(Path.Combine(Installed, StandbyPurgeTask.HelperExeName), null, RunsHighest: true);

    [Fact]
    public void State_IsUnavailableWhenTheHelperIsMissingNextToNitroLite()
    {
        File.Delete(Path.Combine(Bundled, StandbyPurgeTask.HelperExeName));
        Assert.Equal(StandbyPurgeTaskState.Unavailable, Create(() => null).GetState());
    }

    [Fact]
    public void ReleasePackage_SingleFileHelperIsRecognisedAndVerified()
    {
        // Native AOT release: the exe alone.
        File.Delete(Path.Combine(Bundled, "NitroLite.StandbyPurge.dll"));
        File.Delete(Path.Combine(Bundled, "NitroLite.StandbyPurge.runtimeconfig.json"));
        var task = Create(GoodTask);
        Assert.Equal(new[] { StandbyPurgeTask.HelperExeName }, task.BundledHelperFiles());

        Directory.CreateDirectory(Installed);
        File.WriteAllText(Path.Combine(Installed, StandbyPurgeTask.HelperExeName), "v1 " + StandbyPurgeTask.HelperExeName);
        Assert.Equal(StandbyPurgeTaskState.Ready, task.GetState());

        File.WriteAllText(Path.Combine(Installed, StandbyPurgeTask.HelperExeName), "dev build exe");
        Assert.Equal(StandbyPurgeTaskState.NeedsUpdate, task.GetState());   // switched from a normal build

        var script = task.GenerateInstallScript("caner", task.BundledHelperFiles(),
            StandbyPurgeTask.ComputeHashes(Bundled, task.BundledHelperFiles())!, @"C:\Temp\e.log");
        Assert.DoesNotContain(".dll", script);
    }

    [Fact]
    public void IncompleteFrameworkDependentHelper_IsUnavailable()
    {
        File.Delete(Path.Combine(Bundled, "NitroLite.StandbyPurge.runtimeconfig.json"));
        var task = Create(() => null);
        Assert.Empty(task.BundledHelperFiles());
        Assert.Equal(StandbyPurgeTaskState.Unavailable, task.GetState());
    }

    [Fact]
    public void State_IsNotInstalledWithoutTheTask()
    {
        Assert.Equal(StandbyPurgeTaskState.NotInstalled, Create(() => null).GetState());
    }

    [Fact]
    public void State_IsReadyWhenTaskAndInstalledFilesMatch()
    {
        CopyToInstalled();
        Assert.Equal(StandbyPurgeTaskState.Ready, Create(GoodTask).GetState());
    }

    [Fact]
    public void State_NeedsUpdateWhenTheInstalledHelperDiffers()
    {
        CopyToInstalled(changeFile: "NitroLite.StandbyPurge.dll");
        Assert.Equal(StandbyPurgeTaskState.NeedsUpdate, Create(GoodTask).GetState());
    }

    [Fact]
    public void State_NeedsUpdateWhenTheTaskPointsElsewhereOrIsNotElevatedOrHasArguments()
    {
        CopyToInstalled();
        var exe = Path.Combine(Installed, StandbyPurgeTask.HelperExeName);
        Assert.Equal(StandbyPurgeTaskState.NeedsUpdate,
            Create(() => new(@"C:\Users\x\Documents\NitroLite.StandbyPurge.exe", null, true)).GetState());
        Assert.Equal(StandbyPurgeTaskState.NeedsUpdate, Create(() => new(exe, null, RunsHighest: false)).GetState());
        Assert.Equal(StandbyPurgeTaskState.NeedsUpdate, Create(() => new(exe, "--evil", true)).GetState());
        Assert.Equal(StandbyPurgeTaskState.Ready, Create(() => new("\"" + exe + "\"", "", true)).GetState());
    }

    [Fact]
    public void InstallScript_CopiesVerifiesAndRegistersAnElevatedTaskForTheUser()
    {
        var task = Create(() => null);
        var files = task.BundledHelperFiles();
        var hashes = StandbyPurgeTask.ComputeHashes(Bundled, files)!;
        var script = task.GenerateInstallScript(@"CZTRK\caner", files, hashes, @"C:\Temp\err.log");

        foreach (var h in hashes) Assert.Contains(h, script);
        Assert.Contains("Get-FileHash", script);
        Assert.Contains("-RunLevel Highest", script);
        Assert.Contains("-UserId 'CZTRK\\caner'", script);
        Assert.Contains("-TaskPath '\\NitroLite\\' -TaskName 'StandbyPurge'", script);
        Assert.Contains(Installed, script);
        Assert.DoesNotContain("New-ScheduledTaskTrigger", script);   // only NitroLite starts it
        Assert.DoesNotContain("-Argument", script);                    // the helper takes no arguments
    }

    [Theory]
    [InlineData("CZTRK\\caner'; Remove-Item C:\\ -Recurse; '")]
    [InlineData("user\"name")]
    [InlineData("")]
    public void InstallScript_RejectsUnsafeUserNames(string user)
    {
        var task = Create(() => null);
        var files = task.BundledHelperFiles();
        var hashes = StandbyPurgeTask.ComputeHashes(Bundled, files)!;
        Assert.Throws<ArgumentException>(() => task.GenerateInstallScript(user, files, hashes, @"C:\Temp\err.log"));
    }

    [Fact]
    public void InstallScript_RejectsBadHashes()
    {
        var task = Create(() => null);
        Assert.Throws<ArgumentException>(() => task.GenerateInstallScript("caner", task.BundledHelperFiles(), new[] { "x", "y", "z" }, @"C:\Temp\e.log"));
    }

    [Fact]
    public void UninstallScript_RemovesTaskFolderAndFiles()
    {
        var script = Create(() => null).GenerateUninstallScript(@"C:\Temp\err.log");
        Assert.Contains("Unregister-ScheduledTask -TaskPath '\\NitroLite\\' -TaskName 'StandbyPurge'", script);
        Assert.Contains("DeleteFolder('NitroLite'", script);
        Assert.Contains(Installed, script);
    }

    [Fact]
    public async Task Install_ReportsADeclinedUacPrompt()
    {
        var task = Create(() => null);
        task.ElevatedRunner = _ => Task.FromResult(-3);
        Assert.False(await task.InstallAsync());
        Assert.Equal("Yönetici izni verilmedi.", task.LastError);
    }

    [Fact]
    public void Cleaner_WithoutSetup_PurgesNothingAndSaysSo()
    {
        if (MemoryCleanerService.IsRunningAsAdministrator()) return;   // elevated runs purge directly
        var cleaner = new MemoryCleanerService { PurgeTask = Create(() => null) };
        var res = cleaner.CleanMemory();
        Assert.False(res.StandbyPurged);
        Assert.True(res.RequiresAdministrator);
        Assert.Equal(StandbyPurgeTaskState.NotInstalled, res.TaskState);
        Assert.Null(res.TaskExitCode);
    }

    [Fact]
    public void MemoryLists_AreReadableWithoutAdministratorRights()
    {
        var lists = MemoryCleanerService.ReadMemoryLists();
        Assert.NotNull(lists);
        Assert.True(lists!.Value.FreeGigabytes >= 0);
        Assert.True(lists.Value.StandbyGigabytes >= 0);
        Assert.True(lists.Value.FreeGigabytes + lists.Value.StandbyGigabytes < 1024);
    }
}
