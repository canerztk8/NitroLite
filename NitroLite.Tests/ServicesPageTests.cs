using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class ServiceRestoreTests
{
    [Fact]
    public void FactoryDefaults_AreAutomaticForEveryService()
    {
        foreach (var s in DebloatService.Tier1Services)
            Assert.Equal("Automatic", DebloatService.FactoryStartTypes[s]);
    }

    [Fact]
    public void FactoryRestoreScript_SetsAutomaticSkipsMissingAndLeavesTaskAlone()
    {
        var script = new DebloatService().GenerateFactoryRestoreScript(Path.Combine(Path.GetTempPath(), "x.log"));
        foreach (var s in DebloatService.Tier1Services)
        {
            Assert.Contains($"Set-Service -Name '{s}' -StartupType Automatic", script);
            Assert.Contains($"Start-Service -Name '{s}' -ErrorAction SilentlyContinue", script);
        }
        Assert.Contains("-ErrorAction SilentlyContinue; if ($svc)", script);   // not-installed services are skipped
        Assert.DoesNotContain("ScheduledTask", script);
        Assert.DoesNotContain(DebloatService.NoBackupMessage, script);
    }

    [Fact]
    public async Task Apply_WhenEverythingIsAlreadyDisabled_WritesNoBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nl_svc_" + System.Guid.NewGuid().ToString("N"));
        var debloat = new DebloatService
        {
            BackupStateFilePath = Path.Combine(dir, "backup.json"),
            ServiceStartupTypeQuerySeam = _ => "Disabled",
            ServiceStatusQuerySeam = _ => "Stopped",
            ScheduledTaskStateQuerySeam = _ => "Disabled",
            PowerShellRunner = (_, _) => throw new System.InvalidOperationException("must not run")
        };

        Assert.False(await debloat.ApplyTier1DebloatAsync());
        Assert.Equal(DebloatService.AlreadyDisabledMessage, debloat.LastError);
        Assert.False(File.Exists(debloat.BackupStateFilePath));
        Assert.False(debloat.HasRestorableBackup());
    }

    [Theory]
    [InlineData("Running", "Automatic", "Çalışıyor")]
    [InlineData("Stopped", "Automatic", "Durduruldu")]
    [InlineData("Stopped", "Manual", "Durduruldu (elle başlar)")]
    [InlineData("Stopped", "Disabled", "Devre dışı")]
    [InlineData(null, "Disabled", "Devre dışı")]
    [InlineData(null, "Automatic", "Bilinmiyor")]
    public void StatusText_IncludesStartType(string? live, string startType, string expected)
    {
        Assert.Equal(expected, DebloatService.DescribeStatus(live, startType));
    }

    [Theory]
    [InlineData("@oem72.inf,%AcerServiceComponentSVCDESC%;Acer Service Component Service", "Acer Service Component Service")]
    [InlineData("Acer Device Info", "Acer Device Info")]
    [InlineData(null, "AcerEZSvc")]
    [InlineData("@oem35.inf,%X%", "AcerEZSvc")]
    public void DisplayName_ResolvesIndirectStrings(string? raw, string expected)
    {
        Assert.Equal(expected, DebloatService.CleanDisplayName(raw, "AcerEZSvc"));
    }
}

public class AutoStartEntryTests
{
    private static (DebloatService svc, List<string?> writes) Create(string? existing, bool fileExists)
    {
        var writes = new List<string?>();
        var svc = new DebloatService
        {
            AutoStartReadSeam = () => existing,
            AutoStartWriteSeam = c => { writes.Add(c); return true; },
            FileExistsSeam = _ => fileExists
        };
        return (svc, writes);
    }

    [Theory]
    [InlineData("\"C:\\A B\\NitroLite.exe\" --autostart --minimized", @"C:\A B\NitroLite.exe")]
    [InlineData(@"C:\Apps\NitroLite.exe --autostart", @"C:\Apps\NitroLite.exe")]
    [InlineData("", null)]
    public void ExePathIsParsed(string command, string? expected)
    {
        Assert.Equal(expected, DebloatService.ParseAutoStartExePath(command));
    }

    [Fact]
    public void Repair_DoesNothingWhenAutostartIsOff()
    {
        var (svc, writes) = Create(null, true);
        Assert.False(svc.RepairNitroLiteAutoStart(true, @"C:\New\NitroLite.exe"));
        Assert.Empty(writes);
    }

    [Fact]
    public void Repair_PointsAMissingExeAtTheCurrentOne()
    {
        var (svc, writes) = Create("\"C:\\Old\\NitroLite.exe\" --autostart --minimized", fileExists: false);
        Assert.True(svc.RepairNitroLiteAutoStart(true, @"C:\New\NitroLite.exe"));
        Assert.Equal("\"C:\\New\\NitroLite.exe\" --autostart --minimized", Assert.Single(writes));
    }

    [Fact]
    public void Repair_FixesOnlyTheMinimizedFlagWhenTheExeExists()
    {
        var (svc, writes) = Create("\"C:\\Other\\NitroLite.exe\" --autostart --minimized", fileExists: true);
        Assert.True(svc.RepairNitroLiteAutoStart(false, @"C:\New\NitroLite.exe"));
        Assert.Equal("\"C:\\Other\\NitroLite.exe\" --autostart", Assert.Single(writes));
    }

    [Fact]
    public void Repair_LeavesAMatchingEntryAlone()
    {
        var (svc, writes) = Create("\"C:\\Other\\NitroLite.exe\" --autostart --minimized", fileExists: true);
        Assert.False(svc.RepairNitroLiteAutoStart(true, @"C:\New\NitroLite.exe"));
        Assert.Empty(writes);
    }

    [Fact]
    public void SetAutoStart_ReportsARefusedWrite()
    {
        var svc = new DebloatService { AutoStartWriteSeam = _ => false };
        Assert.False(svc.SetNitroLiteAutoStart(false));
    }
}
