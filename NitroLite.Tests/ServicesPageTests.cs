using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
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

/// <summary>
/// Phase 3 of the WPF parity pass: the services and tools page.
/// </summary>
/// <remarks>
/// Every control on this page is a write, so each test asserts both halves: that the view model asked the service, and
/// that the page then said what happened. The backup test goes through the real <see cref="BackupService"/> because the
/// round trip is the feature.
/// </remarks>
public class ServicesPageTests
{
    private static MainViewModel Create(
        AppSettings? settings = null,
        Phase3Fakes.FakeDebloat? debloat = null,
        Phase3Fakes.FakeSystemTools? tools = null,
        Phase3Fakes.FakeUpdates? updates = null,
        Phase3Fakes.FakeDiagnostics? diagnostics = null,
        Phase3Fakes.FakeRunAtStartup? runAtStartup = null,
        IDialogService? dialogs = null,
        string? path = null,
        Phase3Fakes.UrlCapturer? urlCapturer = null) =>
        new(settings ?? new AppSettings(),
            null,
            null,
            path ?? Phase3Fakes.TempSettingsPath(),
            dialogs: dialogs,
            debloat: debloat,
            systemTools: tools,
            updates: updates,
            diagnostics: diagnostics,
            runAtStartup: runAtStartup,
            urlOpener: urlCapturer?.Capture);

    [Fact]
    public async Task ServiceList_Loads()
    {
        var debloat = new Phase3Fakes.FakeDebloat();
        using var vm = Create(debloat: debloat);

        await vm.InitializeAsync();

        Assert.True(vm.ServicesAvailable);
        Assert.Equal(2, vm.AcerServices.Count);
        Assert.False(vm.HasNoAcerServices);

        // The label prefers the display name and falls back to the service name.
        Assert.Equal("Acer Agent Service", vm.AcerServices[0].Label);
        Assert.Equal("Acer Care Center Agent", vm.AcerServices[1].Label);
        Assert.Equal("AcerCCAgentSvis", vm.AcerServices[1].Name);

        // The state line is the status alone: the platform service folds the start type into it, so a stopped-but-manual
        // service already reads differently from a disabled one.
        Assert.Equal("Running", vm.AcerServices[0].StateText);
        Assert.Equal("Stopped", vm.AcerServices[1].StateText);

        Assert.True(vm.NitroSenseRunning);
        Assert.Equal(vm.Labels.Running, vm.NitroSenseStateText);
    }

    [Fact]
    public async Task RepairTools_Run()
    {
        var tools = new Phase3Fakes.FakeSystemTools();
        using var vm = Create(tools: tools);

        await vm.InitializeAsync();
        Assert.True(vm.RepairToolsAvailable);

        vm.FixLightingCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => tools.Calls.Contains("lighting"));

        Assert.Equal(new[] { "lighting" }, tools.Calls);
        Assert.True(vm.HasRepairToolsStatus);
        Assert.Contains("lighting", vm.RepairToolsStatus);
    }

    [Fact]
    public async Task Optimize_Reversible()
    {
        var tools = new Phase3Fakes.FakeSystemTools();
        using var vm = Create(tools: tools);

        await vm.InitializeAsync();

        vm.OptimizeCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => tools.Calls.Contains("optimize"));

        vm.RestoreOptimizeCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => tools.Calls.Contains("restore-optimize"));

        // Both halves reached the service, in the order the buttons were pressed.
        Assert.Equal(new[] { "optimize", "restore-optimize" }, tools.Calls);
    }

    [Fact]
    public async Task Backup_RoundTrips()
    {
        var path = Phase3Fakes.TempSettingsPath();
        var backupPath = Path.Combine(Path.GetTempPath(), "nitrolite-backup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings { Theme = AppTheme.WarmCream, SidebarWidth = 240 };
            var dialogs = new Phase3Fakes.FakeDialogs
            {
                SaveResult = backupPath,
                PickResult = backupPath,
                ConfirmResult = true
            };

            using var vm = Create(settings: settings, dialogs: dialogs, path: path);
            await vm.InitializeAsync();

            vm.CreateBackupCommand.Execute(null);
            await Phase3Fakes.WaitUntilAsync(() => File.Exists(backupPath));
            Assert.True(File.Exists(backupPath));

            // A different theme, then a restore. The restore writes to the canonical settings file rather than to the
            // path this test handed the view model, which is what makes it a restore rather than a reload.
            vm.SelectedTheme = vm.Themes.First(t => t.Theme != AppTheme.WarmCream);
            Assert.NotEqual(AppTheme.WarmCream, SettingsManager.LoadSettingsWithFallback(path).Theme);

            var canonical = AppPaths.GetDataFile("settings.json");
            vm.RestoreBackupCommand.Execute(null);
            await Phase3Fakes.WaitUntilAsync(
                () => SettingsManager.LoadSettingsWithFallback(canonical).Theme == AppTheme.WarmCream);

            Assert.Equal(AppTheme.WarmCream, SettingsManager.LoadSettingsWithFallback(canonical).Theme);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
        }
    }

    [Fact]
    public async Task Diagnostics_CreatesZip()
    {
        var diagnostics = new Phase3Fakes.FakeDiagnostics();
        using var vm = Create(diagnostics: diagnostics);

        await vm.InitializeAsync();
        Assert.True(vm.DiagnosticsAvailable);

        vm.CreateDiagnosticsCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => diagnostics.LastPath != null);

        Assert.NotNull(diagnostics.LastPath);
        Assert.True(File.Exists(diagnostics.LastPath));
        Assert.True(vm.HasDiagnosticsNote);
        Assert.Contains(Path.GetFileName(diagnostics.LastPath), vm.DiagnosticsNote);

        try { File.Delete(diagnostics.LastPath!); } catch { }
    }
}
