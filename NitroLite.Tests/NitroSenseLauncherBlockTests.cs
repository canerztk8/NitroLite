using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The permanent half of the Nitro key: renaming the launcher the Acer hardware service starts, which is the only
/// thing that keeps the key from opening NitroSense while NitroLite is not running.
/// </summary>
/// <remarks>
/// The redirect switch only works while the app runs, because the press is handled by Acer's own stack. These tests
/// pin the second mechanism down: the path, the rename, the state the page reports, and the way back.
/// </remarks>
public class NitroSenseLauncherBlockTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static MainViewModel Create(Phase3Fakes.FakeDebloat debloat, IDialogService? dialogs = null) =>
        new(new AppSettings(), null, null, Phase3Fakes.TempSettingsPath(), dialogs: dialogs, debloat: debloat);

    // ---------------------------------------------------------------- the path

    [Fact]
    public void DefaultPath_IsTheOneTheAcerHardwareServiceStarts()
    {
        // AcerHardwareService.exe — the child process the Acer agent runs — carries this literal, next to the
        // PredatorSense, QuickPanel and XSense equivalents. Renaming anything else would leave the key working.
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NitroSense", "Prerequisites", "NitroSenseLauncher.exe");

        Assert.Equal(expected, DebloatService.DefaultNitroSenseLauncherPath);
        Assert.EndsWith("NitroSenseLauncher.exe", DebloatService.DefaultNitroSenseLauncherPath);
    }

    [Fact]
    public void BlockedSuffix_CannotBeLaunchedByAStalePath()
    {
        // The Acer service asks for the exact path. A suffix that still ended in .exe would be a second executable
        // sitting next to the first, and a launcher that globbed the folder would find it.
        Assert.DoesNotContain(".exe", DebloatService.LauncherBlockedSuffix, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(".", DebloatService.LauncherBlockedSuffix, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the rename, end to end

    /// <summary>
    /// Runs the generated script for real, in a temporary folder, without elevation. The script is the feature, so a
    /// test that only asserted its text would not prove the rename works.
    /// </summary>
    private static Func<string, bool, Task<int>> ScriptRunner(string workingDirectory) => async (script, _) =>
    {
        var scriptPath = Path.Combine(workingDirectory, "run-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(true));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        await process!.WaitForExitAsync();
        return process.ExitCode;
    };

    private static (DebloatService Service, string Directory, string Launcher) TempLauncher()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nl-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var launcher = Path.Combine(dir, "NitroSenseLauncher.exe");
        File.WriteAllText(launcher, "stub");

        var service = new DebloatService
        {
            LauncherPathOverride = launcher,
            PowerShellRunner = ScriptRunner(dir)
        };

        return (service, dir, launcher);
    }

    [Fact]
    public async Task Block_RenamesTheLauncher_AndUnblockPutsItBack()
    {
        var (service, dir, launcher) = TempLauncher();
        try
        {
            Assert.False(service.IsNitroSenseLauncherBlocked);

            Assert.True(await service.BlockNitroSenseLauncherAsync());
            Assert.False(File.Exists(launcher));
            Assert.True(File.Exists(launcher + DebloatService.LauncherBlockedSuffix));
            Assert.True(service.IsNitroSenseLauncherBlocked);

            Assert.True(await service.UnblockNitroSenseLauncherAsync());
            Assert.True(File.Exists(launcher));
            Assert.False(File.Exists(launcher + DebloatService.LauncherBlockedSuffix));
            Assert.False(service.IsNitroSenseLauncherBlocked);
        }
        finally
        {
            try { TestDataIsolation.DeleteFolder(dir); } catch { }
        }
    }

    [Fact]
    public async Task Block_IsIdempotent()
    {
        var (service, dir, launcher) = TempLauncher();
        try
        {
            Assert.True(await service.BlockNitroSenseLauncherAsync());
            Assert.True(await service.BlockNitroSenseLauncherAsync());

            Assert.True(service.IsNitroSenseLauncherBlocked);
            Assert.True(File.Exists(launcher + DebloatService.LauncherBlockedSuffix));
        }
        finally
        {
            try { TestDataIsolation.DeleteFolder(dir); } catch { }
        }
    }

    [Fact]
    public async Task Block_OnAMachineWithNoNitroSense_ReportsTheReason()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nl-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var service = new DebloatService
            {
                LauncherPathOverride = Path.Combine(dir, "NitroSenseLauncher.exe"),
                PowerShellRunner = (_, _) => throw new InvalidOperationException("must not run")
            };

            Assert.False(await service.BlockNitroSenseLauncherAsync());
            Assert.Equal(Loc.T("NitroSense başlatıcısı bulunamadı."), service.LastError);
        }
        finally
        {
            try { TestDataIsolation.DeleteFolder(dir); } catch { }
        }
    }

    // ---------------------------------------------------------------- the page

    [Fact]
    public async Task LauncherRow_IsOfferedAndStartsUnblocked()
    {
        var debloat = new Phase3Fakes.FakeDebloat();
        using var vm = Create(debloat);

        await vm.InitializeAsync();

        Assert.True(vm.NitroSenseLauncherBlockAvailable);
        Assert.False(vm.NitroSenseLauncherBlocked);
        Assert.Equal(vm.Labels.NitroSenseLauncherBlock, vm.NitroSenseLauncherBlockAction);
        Assert.Equal(vm.Labels.NitroSenseLauncherActiveState, vm.NitroSenseLauncherBlockStateText);
    }

    [Fact]
    public async Task Toggle_BlocksTheLauncher_AndSaysSo()
    {
        var debloat = new Phase3Fakes.FakeDebloat();
        using var vm = Create(debloat, new Phase3Fakes.FakeDialogs());

        await vm.InitializeAsync();
        vm.ToggleNitroSenseLauncherCommand.Execute(null);

        await Phase3Fakes.WaitUntilAsync(() => debloat.Calls.Contains("block-launcher"));
        await Phase3Fakes.WaitUntilAsync(() => vm.NitroSenseLauncherBlocked);

        Assert.True(vm.NitroSenseLauncherBlocked);
        Assert.Equal(vm.Labels.NitroSenseLauncherUnblock, vm.NitroSenseLauncherBlockAction);
        Assert.Equal(vm.Labels.NitroSenseLauncherBlockedState, vm.NitroSenseLauncherBlockStateText);
        Assert.Equal(vm.Labels.NitroSenseLauncherBlockedDone, vm.ServicesStatus);
    }

    [Fact]
    public async Task Toggle_UnblocksTheLauncher_WhenAlreadyBlocked()
    {
        var debloat = new Phase3Fakes.FakeDebloat { LauncherBlocked = true };
        using var vm = Create(debloat, new Phase3Fakes.FakeDialogs());

        await vm.InitializeAsync();
        Assert.True(vm.NitroSenseLauncherBlocked);

        vm.ToggleNitroSenseLauncherCommand.Execute(null);

        await Phase3Fakes.WaitUntilAsync(() => debloat.Calls.Contains("unblock-launcher"));
        await Phase3Fakes.WaitUntilAsync(() => !vm.NitroSenseLauncherBlocked);

        Assert.False(vm.NitroSenseLauncherBlocked);
        Assert.Equal(vm.Labels.NitroSenseLauncherUnblockedDone, vm.ServicesStatus);
    }

    [Fact]
    public async Task Toggle_ReportsFailure_WhenElevationIsRefused()
    {
        var debloat = new Phase3Fakes.FakeDebloat { LauncherBlockResult = false };
        using var vm = Create(debloat, new Phase3Fakes.FakeDialogs());

        await vm.InitializeAsync();
        vm.ToggleNitroSenseLauncherCommand.Execute(null);

        await Phase3Fakes.WaitUntilAsync(() => vm.HasServicesStatus);

        // The state must not flip on a refusal: the launcher is still there, so the page has to keep saying so.
        Assert.False(vm.NitroSenseLauncherBlocked);
        Assert.Contains("denied", vm.ServicesStatus);
    }

    [Fact]
    public async Task Toggle_DoesNothing_WhenTheConfirmationIsDeclined()
    {
        var debloat = new Phase3Fakes.FakeDebloat();
        using var vm = Create(debloat, new Phase3Fakes.FakeDialogs { ConfirmResult = false });

        await vm.InitializeAsync();
        vm.ToggleNitroSenseLauncherCommand.Execute(null);
        await Task.Delay(200);

        Assert.Empty(debloat.Calls);
        Assert.False(vm.NitroSenseLauncherBlocked);
    }

    // ---------------------------------------------------------------- the markup

    [Fact]
    public void SettingsView_OffersTheLauncherRow()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "SettingsView.axaml"));

        Assert.Contains("ToggleNitroSenseLauncherCommand", markup, StringComparison.Ordinal);
        Assert.Contains("NitroSenseLauncherBlockAction", markup, StringComparison.Ordinal);
        Assert.Contains("NitroSenseLauncherBlockStateText", markup, StringComparison.Ordinal);

        // The explanation is no longer a paragraph under the row: it is one of the four lines in the group's (?)
        // badge, which the view model assembles. The markup therefore names the badge, and the badge's text is
        // asserted on the view model instead.
        Assert.Contains("NitroKeyHelp", markup, StringComparison.Ordinal);
    }
}
