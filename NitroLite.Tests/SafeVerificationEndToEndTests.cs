using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// End-to-end unit and integration test suite directly verifying production classes:
/// MainWindow, CpuPowerService, DebloatService, and PowerAutomationService
/// against Requirements R1, R2, R3, and R4 using test seams without mutating host OS or hardware.
/// </summary>
public class SafeVerificationEndToEndTests
{
    private static async Task RunInStaAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource<object?>();
        var thread = new Thread(async () =>
        {
            try
            {
                await action();
                tcs.SetResult(null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await tcs.Task;
    }

    #region Mock Helpers

    private class TestAcerAgentClient : AcerAgentClient
    {
        public OperatingMode CurrentMode { get; set; } = OperatingMode.Default;
        public FanMode CurrentFanMode { get; set; } = FanMode.Auto;

        public List<OperatingMode> ModeHistory { get; } = new();
        public List<FanMode> FanHistory { get; } = new();

        public Func<OperatingMode, Task<bool>>? OnSetOperatingModeAsync { get; set; }
        public Func<FanMode, Task<bool>>? OnSetFanModeAsync { get; set; }

        public override Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

        public override Task<OperatingMode> GetOperatingModeAsync() => Task.FromResult(CurrentMode);

        public override async Task<bool> SetOperatingModeAsync(OperatingMode mode)
        {
            ModeHistory.Add(mode);
            if (OnSetOperatingModeAsync != null)
            {
                bool res = await OnSetOperatingModeAsync(mode);
                if (res) CurrentMode = mode;
                return res;
            }
            CurrentMode = mode;
            return true;
        }

        public override async Task<bool> SetFanModeAsync(FanMode mode, int cpuRpm = 0, int gpuRpm = 0)
        {
            FanHistory.Add(mode);
            if (OnSetFanModeAsync != null)
            {
                bool res = await OnSetFanModeAsync(mode);
                if (res) CurrentFanMode = mode;
                return res;
            }
            CurrentFanMode = mode;
            return true;
        }
    }

    private class TestCpuPowerService : CpuPowerService
    {
        public new CpuBoostMode CurrentMode { get; set; } = CpuBoostMode.Aggressive;
        public List<CpuBoostMode> BoostHistory { get; } = new();
        public Func<CpuBoostMode, bool, bool, Task<bool>>? OnSetBoostModeAsync { get; set; }

        public TestCpuPowerService() : base(autoInitialize: false, bypassNativePowerCalls: true)
        {
            BypassNativePowerCalls = true;
        }

        public override async Task<bool> SetBoostModeAsync(CpuBoostMode mode, bool targetAc = true, bool targetDc = true)
        {
            BoostHistory.Add(mode);
            if (OnSetBoostModeAsync != null)
            {
                bool res = await OnSetBoostModeAsync(mode, targetAc, targetDc);
                if (res) CurrentMode = mode;
                return res;
            }
            CurrentMode = mode;
            return true;
        }

        public override CpuBoostMode QueryCurrentBoostMode(bool isAc = true) => CurrentMode;
    }

    #endregion

    #region Test Case 1: Rapid Game Switch & Desktop Reference Preservation

    [Fact]
    public async Task TestCase1_RapidGameSwitch_GameAPending_GameBOpens_PreservesOriginalDesktopReferenceAndExecutesInOrder()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { AtkMouseIntegrationEnabled = true };

            var mouseCalls = new List<int>();
            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => { mouseCalls.Add(p); return true; },
                isTestHarness: true);

            // Establish initial desktop reference before any game transitions
            window.SetPendingRestoreForTesting(
                hasPending: false,
                savedMode: OperatingMode.Default,
                savedCpu: CpuBoostMode.Aggressive,
                savedFan: FanMode.Auto,
                savedMouse: 1);

            var gameA = new GameProfile
            {
                ExeName = "gameA",
                DisplayName = "Game A",
                TargetMode = OperatingMode.Turbo,
                TargetCpuBoost = CpuBoostMode.Disabled,
                TargetFanMode = FanMode.Auto,
                TargetAtkMouseProfile = 2
            };

            var gameB = new GameProfile
            {
                ExeName = "gameB",
                DisplayName = "Game B",
                TargetMode = OperatingMode.Extreme,
                TargetCpuBoost = CpuBoostMode.Efficient,
                TargetFanMode = FanMode.Custom,
                TargetAtkMouseProfile = 3
            };

            var gameAInFlightTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gameAHoldTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            mockAgent.OnSetOperatingModeAsync = async mode =>
            {
                if (mode == OperatingMode.Turbo)
                {
                    gameAInFlightTcs.TrySetResult(true);
                    await gameAHoldTcs.Task;
                }
                return true;
            };

            // Start Game A: begins execution and pauses in SetOperatingModeAsync under transition lock
            var taskGameA = window.HandleGameStartedAsync(gameA);
            await gameAInFlightTcs.Task;

            // At this point, Game A is in flight and has established desktop baseline
            Assert.Equal(OperatingMode.Default, window.SavedModeBeforeGame);
            Assert.True(window.HasPendingDesktopRestore);

            // While Game A is in-flight, Game A closes and Game B starts
            var taskGameAEnd = window.HandleGameEndedAsync(gameA);
            var taskGameB = window.HandleGameStartedAsync(gameB);

            // Unblock Game A to finish its current in-flight mode call
            gameAHoldTcs.TrySetResult(true);

            await Task.WhenAll(taskGameA, taskGameAEnd, taskGameB);

            // Verify Game A's remaining commands were aborted when generation shifted
            // Verify Game B was cleanly applied
            Assert.True(window.IsGameSessionActive);
            Assert.Equal(OperatingMode.Extreme, mockAgent.CurrentMode);
            Assert.Equal(CpuBoostMode.Efficient, mockCpu.CurrentMode);
            Assert.Equal(FanMode.Custom, mockAgent.CurrentFanMode);
            Assert.Contains(3, mouseCalls);

            // Verify original desktop reference was NOT overwritten by Game A's turbo or Game B
            Assert.Equal(OperatingMode.Default, window.SavedModeBeforeGame);
            Assert.Equal(CpuBoostMode.Aggressive, window.SavedCpuBoostBeforeGame);
            Assert.True(window.HasPendingDesktopRestore);
        });
    }

    #endregion

    #region Test Case 2: Partial Desktop Restore Failure & Tray Retry

    [Fact]
    public async Task TestCase2_PartialDesktopRestoreFailure_IndependentStepsContinue_TrayRetryExecutesOnlyPendingSteps()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient();
            var mockCpu = new TestCpuPowerService();
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { AtkMouseIntegrationEnabled = true };

            var mouseCalls = new List<int>();
            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => { mouseCalls.Add(p); return true; },
                isTestHarness: true);

            var game = new GameProfile
            {
                ExeName = "testgame",
                DisplayName = "Test Game",
                TargetMode = OperatingMode.Turbo,
                TargetCpuBoost = CpuBoostMode.Disabled,
                TargetFanMode = FanMode.Max,
                TargetAtkMouseProfile = 2
            };

            await window.HandleGameStartedAsync(game);
            Assert.True(window.IsGameSessionActive);

            // Configure partial failure on restore: CPU Boost fails, but mode, fan, and mouse succeed
            mockCpu.OnSetBoostModeAsync = (m, ac, dc) => Task.FromResult(false);

            await window.HandleGameEndedAsync(game);

            // Verify game session is marked inactive immediately
            Assert.False(window.IsGameSessionActive);

            // Independent steps succeeded and cleared their pending flags
            Assert.False(window.PendingModeRestore);
            Assert.False(window.PendingFanRestore);
            Assert.False(window.PendingMouseRestore);

            // Failed step remains pending
            Assert.True(window.PendingCpuBoostRestore);
            Assert.True(window.HasPendingDesktopRestore);

            // Clear call histories to strictly monitor the retry operation
            mockAgent.ModeHistory.Clear();
            mockAgent.FanHistory.Clear();
            mockCpu.BoostHistory.Clear();
            mouseCalls.Clear();

            // Fix CPU boost in mock so retry can succeed
            mockCpu.OnSetBoostModeAsync = (m, ac, dc) => Task.FromResult(true);

            // Execute Tray Retry
            bool retryResult = await window.RetryPendingDesktopRestoreAsync();

            Assert.True(retryResult);
            Assert.False(window.PendingCpuBoostRestore);
            Assert.False(window.HasPendingDesktopRestore);

            // Strictly verify ONLY the remaining pending step (CPU Boost) was executed during retry!
            Assert.Single(mockCpu.BoostHistory);
            Assert.Empty(mockAgent.ModeHistory);
            Assert.Empty(mockAgent.FanHistory);
            Assert.Empty(mouseCalls);
        });
    }

    #endregion

    #region Test Case 3: Tray Retry Interrupted by New Game

    [Fact]
    public async Task TestCase3_TrayRetryInterruptedByNewGame_AbortsPromptlyAndDoesNotOverwriteNewProfile()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient();
            var mockCpu = new TestCpuPowerService();
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { AtkMouseIntegrationEnabled = true };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            // Setup pending restore state
            window.SetPendingRestoreForTesting(
                hasPending: true,
                pendingMode: true,
                pendingCpu: true,
                pendingFan: true,
                pendingMouse: true,
                savedMode: OperatingMode.Default,
                savedCpu: CpuBoostMode.Disabled,
                savedFan: FanMode.Auto,
                savedMouse: 1);

            var retryInFlightTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryHoldTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            mockAgent.OnSetOperatingModeAsync = async mode =>
            {
                if (mode == OperatingMode.Default)
                {
                    retryInFlightTcs.TrySetResult(true);
                    await retryHoldTcs.Task;
                }
                return true;
            };

            // Start tray retry: pauses during Mode restoration
            var retryTask = window.RetryPendingDesktopRestoreAsync();
            await retryInFlightTcs.Task;

            // While retry is in flight, a new game starts
            var gameB = new GameProfile
            {
                ExeName = "gameB",
                DisplayName = "Game B",
                TargetMode = OperatingMode.Turbo,
                TargetCpuBoost = CpuBoostMode.Aggressive,
                TargetFanMode = FanMode.Max,
                TargetAtkMouseProfile = 3
            };

            var gameBTask = window.HandleGameStartedAsync(gameB);

            // Release retry's mode write
            retryHoldTcs.TrySetResult(true);

            var retrySuccess = await retryTask;
            await gameBTask;

            // Retry should have aborted upon detecting session generation mismatch / active game
            Assert.False(retrySuccess);

            // Game B's profile must be active, not overwritten by desktop restore
            Assert.True(window.IsGameSessionActive);
            Assert.Equal(CpuBoostMode.Aggressive, mockCpu.CurrentMode);
            Assert.Equal(FanMode.Max, mockAgent.CurrentFanMode);
        });
    }

    #endregion

    #region Test Case 4: Mouse Restore Retention Across Games Without Mouse Profiles

    [Fact]
    public async Task TestCase4_MouseRestoreRetentionAcrossGamesWithoutMouseProfiles()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient();
            var mockCpu = new TestCpuPowerService();
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { AtkMouseIntegrationEnabled = true, DefaultAtkMouseProfile = 1 };

            bool mouseSwitcherSucceeds = true;
            int lastSwitchedProfile = 1;

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p =>
                {
                    if (mouseSwitcherSucceeds)
                    {
                        lastSwitchedProfile = p;
                        return true;
                    }
                    return false;
                },
                isTestHarness: true);

            // Initial desktop mouse profile
            window.SetPendingRestoreForTesting(
                hasPending: false,
                savedMode: OperatingMode.Default,
                savedCpu: CpuBoostMode.Aggressive,
                savedFan: FanMode.Auto,
                savedMouse: 1);

            // Game A switches mouse profile to 2
            var gameA = new GameProfile
            {
                ExeName = "gameA",
                DisplayName = "Game A",
                TargetMode = OperatingMode.Turbo,
                TargetCpuBoost = CpuBoostMode.Aggressive,
                TargetFanMode = FanMode.Auto,
                TargetAtkMouseProfile = 2
            };

            await window.HandleGameStartedAsync(gameA);
            Assert.Equal(2, lastSwitchedProfile);

            // Game A closes, but mouse restore fails (e.g. mouse disconnected temporarily)
            mouseSwitcherSucceeds = false;
            await window.HandleGameEndedAsync(gameA);

            Assert.True(window.PendingMouseRestore);
            Assert.True(window.HasPendingDesktopRestore);

            // Game B starts with NO mouse profile configured (TargetAtkMouseProfile = 0)
            var gameB = new GameProfile
            {
                ExeName = "gameB",
                DisplayName = "Game B",
                TargetMode = OperatingMode.Extreme,
                TargetCpuBoost = CpuBoostMode.Aggressive,
                TargetFanMode = FanMode.Auto,
                TargetAtkMouseProfile = 0
            };

            await window.HandleGameStartedAsync(gameB);

            // Pending mouse restore MUST NOT be wiped out by Game B!
            Assert.True(window.PendingMouseRestore);
            Assert.True(window.HasPendingDesktopRestore);
            Assert.Equal(1, window.SavedMouseProfileBeforeGame);

            // Game B closes, and mouse connection is restored
            mouseSwitcherSucceeds = true;
            await window.HandleGameEndedAsync(gameB);

            // Mouse restore should now succeed and return to desktop profile 1
            Assert.False(window.PendingMouseRestore);
            Assert.Equal(1, lastSwitchedProfile);
        });
    }

    #endregion

    #region Test Case 5: CpuPowerService Process Ownership, Timeout & Hanging Process Protection

    [Fact]
    public void TestCase5a_CpuPowerService_PowerCfgErrorExitCode_FailsGracefullyAndModeUnverified()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        service.ProcessInfoFactory = cmd => new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 42",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        bool ok = service.SetBoostModePowerCfg(CpuBoostMode.Disabled, targetAc: true, targetDc: false);
        Assert.False(ok);
        Assert.False(service.IsModeVerified);
        Assert.Null(service.VerifiedCurrentMode);
    }

    [Fact]
    public async Task TestCase5b_CpuPowerService_HangingProcess_BlocksNewWritesUntilExitAndRecovers()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);

        // Spawn a live process to simulate a hanging/monitored process
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 15",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var hangingProc = Process.Start(psi)!;
        try
        {
            service._hangingProcess = hangingProc;

            // While hanging process is active, EnsureNoHangingProcess returns false and blocks writes
            Assert.False(service.EnsureNoHangingProcess());

            bool writeResult = await service.SetBoostModeAsync(CpuBoostMode.Disabled);
            Assert.False(writeResult);

            // Terminate hanging process
            hangingProc.Kill(entireProcessTree: true);
            hangingProc.WaitForExit(3000);

            // Once exited, EnsureNoHangingProcess cleans up and unblocks writes
            Assert.True(service.EnsureNoHangingProcess());
            Assert.Null(service._hangingProcess);

            bool subsequentWrite = await service.SetBoostModeAsync(CpuBoostMode.Aggressive);
            Assert.True(subsequentWrite);
            Assert.Equal(CpuBoostMode.Aggressive, service.CurrentMode);
        }
        finally
        {
            try
            {
                if (!hangingProc.HasExited) hangingProc.Kill(entireProcessTree: true);
                hangingProc.Dispose();
            }
            catch { }
        }
    }

    [Fact]
    public void TestCase5c_CpuPowerService_EnsureNoHangingProcess_HandlesInvalidOperationWithoutThrowing()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);

        // Start and immediately kill a process so its handle becomes terminated
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        proc.WaitForExit(2000);

        service._hangingProcess = proc;
        bool ok = service.EnsureNoHangingProcess();
        Assert.True(ok);
        Assert.Null(service._hangingProcess);
    }

    [Fact]
    public void TestCase5d_CpuPowerService_QueryPath_PreservesHangingProcessWithoutPrematureDispose()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 15",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var hangingProc = Process.Start(psi)!;
        try
        {
            // Simulate that ownership of hangingProc was transferred to _hangingProcess
            service._hangingProcess = hangingProc;

            // When QueryCurrentBoostModePowerCfg is invoked, it checks EnsureNoHangingProcess
            // Since hangingProc is alive, it must return null without throwing and without prematurely disposing the handle
            var result = service.QueryCurrentBoostModePowerCfg(isAc: true);
            Assert.Null(result);

            // Handle must remain open and undisposed so state can be continuously polled
            Assert.NotNull(service._hangingProcess);
            Assert.False(service._hangingProcess.HasExited);
            Assert.False(service._hangingProcess.SafeHandle.IsClosed);
            Assert.False(service._hangingProcess.SafeHandle.IsInvalid);
            Assert.NotEqual(IntPtr.Zero, service._hangingProcess.Handle);
        }
        finally
        {
            try
            {
                if (!hangingProc.HasExited) hangingProc.Kill(entireProcessTree: true);
                hangingProc.Dispose();
            }
            catch { }
        }
    }

    #endregion

    #region Test Case 6: DebloatService Verified Restore Stopped & Error Aggregation

    [Fact]
    public void TestCase6a_DebloatService_GenerateRestoreScript_EmitsStopAndVerificationForStoppedServices()
    {
        var service = new DebloatService();
        var backup = new Dictionary<string, string>
        {
            ["AcerServiceSvc"] = "Manual",
            ["AcerServiceSvc:Status"] = "Stopped",
            ["ASMSvc"] = "Automatic",
            ["ASMSvc:Status"] = "Running",
            ["Task:NitroSenseLauncher"] = "Disabled"
        };

        string script = service.GenerateRestoreScript(backup);

        // Stopped service: must set startup type, emit Stop-Service, and verify Status -ne 'Stopped'
        Assert.Contains("Set-Service -Name 'AcerServiceSvc' -StartupType Manual", script);
        Assert.Contains("Stop-Service -Name 'AcerServiceSvc' -Force", script);
        Assert.Contains("$vSvc.Status -ne 'Stopped'", script);

        // Running service: must set startup type, emit Start-Service, and verify Status -ne 'Running'
        Assert.Contains("Set-Service -Name 'ASMSvc' -StartupType Automatic", script);
        Assert.Contains("Start-Service -Name 'ASMSvc'", script);
        Assert.Contains("$vSvc.Status -ne 'Running'", script);

        // Disabled scheduled task: must emit Disable-ScheduledTask and verify State -ne 'Disabled'
        Assert.Contains("Disable-ScheduledTask -TaskName 'NitroSenseLauncher'", script);
        Assert.Contains("$vTask.State -ne 'Disabled'", script);
    }

    [Fact]
    public async Task TestCase6b_DebloatService_StopFails_RecordsErrorAndRetainsBackupFile()
    {
        var service = new DebloatService();
        var tempBackup = Path.Combine(Path.GetTempPath(), $"debloat_test_fail_{Guid.NewGuid():N}.json");

        try
        {
            var backupData = new Dictionary<string, string>
            {
                ["AcerServiceSvc"] = "Manual",
                ["AcerServiceSvc:Status"] = "Stopped"
            };
            File.WriteAllText(tempBackup, JsonSerializer.Serialize(backupData));
            service.BackupStateFilePath = tempBackup;

            // Simulate PowerShell failure (exit code 1)
            service.PowerShellRunner = (script, elevated) => Task.FromResult(1);

            bool ok = await service.RestoreTier1ServicesAsync();
            Assert.False(ok);
            Assert.NotNull(service.LastError);
            Assert.True(File.Exists(tempBackup), "Backup file MUST be retained when restore fails!");
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
        }
    }

    [Fact]
    public async Task TestCase6c_DebloatService_StopSucceeds_DeletesBackupOnlyOnVerifiedSuccess()
    {
        var service = new DebloatService();
        var tempBackup = Path.Combine(Path.GetTempPath(), $"debloat_test_success_{Guid.NewGuid():N}.json");

        try
        {
            var backupData = new Dictionary<string, string>
            {
                ["AcerServiceSvc"] = "Manual",
                ["AcerServiceSvc:Status"] = "Stopped"
            };
            File.WriteAllText(tempBackup, JsonSerializer.Serialize(backupData));
            service.BackupStateFilePath = tempBackup;

            // Simulate PowerShell verified success (exit code 0)
            service.PowerShellRunner = (script, elevated) => Task.FromResult(0);

            bool ok = await service.RestoreTier1ServicesAsync();
            Assert.True(ok);
            Assert.False(File.Exists(tempBackup), "Backup file MUST be deleted only upon verified success!");
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
        }
    }

    [Fact]
    public async Task TestCase6d_DebloatService_CorruptedBackup_ReportsErrorAndRetainsFile()
    {
        var service = new DebloatService();
        var tempBackup = Path.Combine(Path.GetTempPath(), $"debloat_test_corrupt_{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(tempBackup, "{ not valid json syntax !! }");
            service.BackupStateFilePath = tempBackup;

            bool ok = await service.RestoreTier1ServicesAsync();
            Assert.False(ok);
            Assert.Contains("corrupted", service.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(tempBackup), "Corrupted backup file must NOT be deleted silently!");
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
        }
    }

    [Fact]
    public async Task TestCase6e_DebloatService_MissingServiceOrTask_CollectsErrorsAndRetainsBackupFile()
    {
        var service = new DebloatService();
        var tempBackup = Path.Combine(Path.GetTempPath(), $"debloat_test_missing_{Guid.NewGuid():N}.json");
        var errLog = Path.Combine(Path.GetTempPath(), "nitrolite_restore_err.log");

        try
        {
            var backupData = new Dictionary<string, string>
            {
                ["AcerServiceSvc"] = "Manual",
                ["AcerServiceSvc:Status"] = "Stopped",
                ["Task:NitroSenseLauncher"] = "Disabled"
            };
            File.WriteAllText(tempBackup, JsonSerializer.Serialize(backupData));
            service.BackupStateFilePath = tempBackup;

            // Simulate script failure reporting missing service/task in error log
            service.PowerShellRunner = async (script, elevated) =>
            {
                await File.WriteAllTextAsync(errLog, "AcerServiceSvc: Servis bulunamadı: AcerServiceSvc\nTask:NitroSenseLauncher: NitroSenseLauncher görevi bulunamadı.");
                return 1;
            };

            bool ok = await service.RestoreTier1ServicesAsync();
            Assert.False(ok);
            Assert.NotNull(service.LastError);
            Assert.Contains("Servis bulunamadı", service.LastError);
            Assert.Contains("NitroSenseLauncher görevi bulunamadı", service.LastError);
            Assert.True(File.Exists(tempBackup), "Backup file MUST be retained when missing service errors occur!");
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
            if (File.Exists(errLog)) File.Delete(errLog);
        }
    }

    #endregion

    #region Test Case 7: Debloat Timeout Concurrency Lock & Baseline Protection

    [Fact]
    public async Task TestCase7a_DebloatService_Timeout_BlocksConcurrentApplyOrRestoreUntilProcessExits()
    {
        var service = new DebloatService();
        var tempBackup = Path.Combine(Path.GetTempPath(), $"debloat_test_timeout_{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(tempBackup, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["AcerServiceSvc"] = "Manual",
                ["AcerServiceSvc:Status"] = "Running"
            }));
            service.BackupStateFilePath = tempBackup;

            // Spawn live process to simulate active un-exited PowerShell process
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command Start-Sleep -Seconds 15",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var activeProc = Process.Start(psi)!;
            try
            {
                service.ActivePowerShellProcess = activeProc;
                Assert.True(service.IsOperationInProgress);

                // Both Apply and Restore must be blocked while process is running
                bool applyOk = await service.ApplyTier1DebloatAsync();
                Assert.False(applyOk);
                Assert.Contains("devam ediyor", service.LastError);

                bool restoreOk = await service.RestoreTier1ServicesAsync();
                Assert.False(restoreOk);
                Assert.Contains("devam ediyor", service.LastError);
            }
            finally
            {
                try
                {
                    if (!activeProc.HasExited) activeProc.Kill(entireProcessTree: true);
                    activeProc.WaitForExit(2000);
                    activeProc.Dispose();
                }
                catch { }
            }

            // Once process is terminated, operations are unblocked
            Assert.False(service.IsOperationInProgress);
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
        }
    }

    [Fact]
    public async Task TestCase7b_DebloatService_ReapplyAfterPartialRestore_PreservesOriginalBaselineBackup()
    {
        var service = new DebloatService();
        var tempBackup = Path.Combine(Path.GetTempPath(), $"debloat_test_baseline_{Guid.NewGuid():N}.json");

        try
        {
            // Initial valid baseline backup from pre-debloat system
            var baseline = new Dictionary<string, string>
            {
                ["AcerServiceSvc"] = "Automatic",
                ["AcerServiceSvc:Status"] = "Running",
                ["ASMSvc"] = "Manual",
                ["ASMSvc:Status"] = "Running",
                ["Task:NitroSenseLauncher"] = "Ready"
            };
            File.WriteAllText(tempBackup, JsonSerializer.Serialize(baseline));
            service.BackupStateFilePath = tempBackup;

            // Simulate currently debloated / modified system state
            service.ServiceStartupTypeQuerySeam = s => "Disabled";
            service.ServiceStatusQuerySeam = s => "Stopped";
            service.ScheduledTaskStateQuerySeam = t => "Disabled";
            service.PowerShellRunner = (script, elevated) => Task.FromResult(0);

            // Re-apply debloat
            bool ok = await service.ApplyTier1DebloatAsync();
            Assert.True(ok);

            // Re-read backup file from disk: must preserve the original baseline, NOT overwrite with "Disabled"!
            var savedContent = File.ReadAllText(tempBackup);
            var savedBackup = JsonSerializer.Deserialize<Dictionary<string, string>>(savedContent)!;

            Assert.Equal("Automatic", savedBackup["AcerServiceSvc"]);
            Assert.Equal("Running", savedBackup["AcerServiceSvc:Status"]);
            Assert.Equal("Manual", savedBackup["ASMSvc"]);
            Assert.Equal("Running", savedBackup["ASMSvc:Status"]);
            Assert.Equal("Ready", savedBackup["Task:NitroSenseLauncher"]);
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
        }
    }

    #endregion

    #region Test Case 8: PowerAutomationService Concurrency & Game Session Awareness

    [Fact]
    public async Task TestCase8_PowerAutomationService_TransitionLockAndGameSessionActive_PreservesDesktopBaseline()
    {
        var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Extreme };
        var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
        var mockDisplay = new DisplayRefreshRateService();
        var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco, AutoRefreshRateSwitching = false };
        var transitionLock = new SemaphoreSlim(1, 1);

        bool isGameActive = true;
        var powerService = new PowerAutomationService(
            mockAgent,
            mockCpu,
            mockDisplay,
            settings,
            attachSystemEvents: false,
            transitionLock: transitionLock)
        {
            IsGameSessionActive = () => isGameActive,
            GetCurrentOperatingMode = () => OperatingMode.Default, // desktop baseline is Default
            GetCurrentFanMode = () => FanMode.Auto
        };

        // Transition from AC to DC
        await powerService.ProcessPowerTransitionAsync(isAc: false);

        Assert.True(powerService.IsDcEcoActive);
        // Saved mode before DC must be the desktop baseline (Default), not the game's Extreme mode
        Assert.Equal(OperatingMode.Default, powerService.SavedOperatingMode);
        Assert.Equal(FanMode.Auto, powerService.SavedFanMode);

        powerService.Dispose();
    }

    #endregion
}
