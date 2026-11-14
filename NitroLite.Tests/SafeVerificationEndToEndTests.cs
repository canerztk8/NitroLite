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

        public override Task<OperatingMode?> TryGetOperatingModeAsync() => Task.FromResult<OperatingMode?>(CurrentMode);

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

        public List<(FanMode Mode, int Cpu, int Gpu)> FanSpeedHistory { get; } = new();

        public override async Task<bool> SetFanModeAsync(FanMode mode, int cpuRpm = 0, int gpuRpm = 0)
        {
            FanHistory.Add(mode);
            FanSpeedHistory.Add((mode, cpuRpm, gpuRpm));
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
        public List<CpuBoostMode> BoostHistory { get; } = new();
        public Func<CpuBoostMode, bool, bool, Task<bool>>? OnSetBoostModeAsync { get; set; }

        public TestCpuPowerService() : base(autoInitialize: false, bypassNativePowerCalls: true)
        {
            BypassNativePowerCalls = true;
            CurrentMode = CpuBoostMode.Aggressive;
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

    #region Settings are never overwritten before they were loaded

    [Fact]
    public async Task ClosingAWindowWhoseSettingsWereNeverLoaded_DoesNotOverwriteTheSettingsFile()
    {
        // Regression: a window that failed during startup (InitializeComponent threw) is still closed by WPF on
        // shutdown; OnClosing → SaveWindowPosition wrote default settings over the user's real ones.
        var settingsPath = AppPaths.GetDataFile("settings.json");
        const string userSettings = "{ \"StartMinimized\": true, \"atk_mouse_integration_enabled\": true }";

        await RunInStaAsync(() =>
        {
            File.WriteAllText(settingsPath, userSettings);

            var window = new MainWindow(
                new TestAcerAgentClient(),
                new TestCpuPowerService(),
                new GameDetectionService(),
                new AtkMouseService(),
                settings: null,            // nothing loaded yet, like a window that never reached its Loaded event
                isTestHarness: true);
            window.Close();                // hides instead of exiting, after saving the window position

            Assert.Equal(userSettings, File.ReadAllText(settingsPath));
            return Task.CompletedTask;
        });
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

    [Fact]
    public Task ManualChangeAfterFailedRestore_DropsTheStaleCrashRecord() => RunInStaAsync(async () =>
    {
        // A game ends with the CPU boost restore failing, so active_game_session.json stays behind. The user then
        // picks a boost value by hand. That satisfies the last pending step, and the crash record has to go with
        // it: otherwise the next start would restore the pre-game value over the choice the user just made.
        var mockAgent = new TestAcerAgentClient();
        var mockCpu = new TestCpuPowerService();
        using var gameService = new GameDetectionService();
        var window = new MainWindow(mockAgent, mockCpu, gameService, new AtkMouseService(),
            settings: new AppSettings(), isTestHarness: true);

        var game = new GameProfile
        {
            ExeName = "testgame",
            DisplayName = "Test Game",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Disabled,
            TargetFanMode = FanMode.Max
        };

        await window.HandleGameStartedAsync(game);
        Assert.True(window.IsGameSessionActive);

        mockCpu.OnSetBoostModeAsync = (m, ac, dc) => Task.FromResult(false);
        await window.HandleGameEndedAsync(game);

        Assert.True(window.PendingCpuBoostRestore);
        Assert.True(window.HasPendingDesktopRestore);
        Assert.NotNull(window.LoadGameSessionRecoveryState());

        mockCpu.OnSetBoostModeAsync = (m, ac, dc) => Task.FromResult(true);
        Assert.True(await window.SetManualCpuBoostAsync(CpuBoostMode.Efficient));

        Assert.False(window.PendingCpuBoostRestore);
        Assert.False(window.HasPendingDesktopRestore);
        Assert.Null(window.LoadGameSessionRecoveryState());
    });

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

    private static async Task<int> ExecuteScriptWithMockCmdletsAsync(
        string script,
        Dictionary<string, (string Status, string StartType)> mockServices,
        Dictionary<string, string>? mockTasks = null,
        bool simulateStopError = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("$global:mockServices = @{};");
        foreach (var kvp in mockServices)
        {
            sb.AppendLine($"$global:mockServices['{kvp.Key}'] = [PSCustomObject]@{{ Status = '{kvp.Value.Status}'; StartType = '{kvp.Value.StartType}' }};");
        }

        sb.AppendLine("$global:mockTasks = @{};");
        if (mockTasks != null)
        {
            foreach (var kvp in mockTasks)
            {
                sb.AppendLine($"$global:mockTasks['{kvp.Key}'] = [PSCustomObject]@{{ State = '{kvp.Value}' }};");
            }
        }

        var exportFile = Path.Combine(Path.GetTempPath(), $"nitrolite_mock_state_{Guid.NewGuid():N}.json");
        var exportFileEscaped = exportFile.Replace("'", "''");

        sb.AppendLine(@"
function global:Export-MockState {
    $export = @{}
    foreach ($k in $global:mockServices.Keys) {
        $export[$k] = @{ Status = $global:mockServices[$k].Status; StartType = $global:mockServices[$k].StartType }
    }
    $export | ConvertTo-Json | Out-File -FilePath '" + exportFileEscaped + @"' -Encoding utf8
}
function global:Get-Service {
    param([string]$Name, [string]$ErrorAction)
    if ($global:mockServices.ContainsKey($Name)) { return $global:mockServices[$Name] }
    if ($ErrorAction -eq 'Stop') { throw ""Service not found: $Name"" }
    return $null
}
function global:Set-Service {
    param([string]$Name, [string]$StartupType, [string]$ErrorAction)
    if ($global:mockServices.ContainsKey($Name)) {
        $global:mockServices[$Name].StartType = $StartupType
        Export-MockState
        return
    }
    if ($ErrorAction -eq 'Stop') { throw ""Service not found: $Name"" }
}
function global:Start-Service {
    param([string]$Name, [string]$ErrorAction)
    if ($global:mockServices.ContainsKey($Name)) {
        if ($global:mockServices[$Name].StartType -eq 'Disabled') {
            if ($ErrorAction -eq 'Stop') { throw ""Cannot start disabled service: $Name"" }
            return
        }
        $global:mockServices[$Name].Status = 'Running'
        Export-MockState
        return
    }
    if ($ErrorAction -eq 'Stop') { throw ""Service not found: $Name"" }
}
function global:Stop-Service {
    param([string]$Name, [switch]$Force, [string]$ErrorAction)
    if (" + (simulateStopError ? "$true" : "$false") + @") {
        if ($ErrorAction -eq 'Stop') { throw ""Simulated stop service failure: $Name"" }
        return
    }
    if ($global:mockServices.ContainsKey($Name)) {
        $global:mockServices[$Name].Status = 'Stopped'
        Export-MockState
        return
    }
    if ($ErrorAction -eq 'Stop') { throw ""Service not found: $Name"" }
}
function global:Get-ScheduledTask {
    param([string]$TaskName, [string]$ErrorAction)
    if ($global:mockTasks.ContainsKey($TaskName)) { return $global:mockTasks[$TaskName] }
    if ($ErrorAction -eq 'Stop') { throw ""Task not found: $TaskName"" }
    return $null
}
function global:Enable-ScheduledTask {
    param([string]$TaskName, [string]$ErrorAction)
    if ($global:mockTasks.ContainsKey($TaskName)) { $global:mockTasks[$TaskName].State = 'Ready'; return }
    if ($ErrorAction -eq 'Stop') { throw ""Task not found: $TaskName"" }
}
function global:Disable-ScheduledTask {
    param([string]$TaskName, [string]$ErrorAction)
    if ($global:mockTasks.ContainsKey($TaskName)) { $global:mockTasks[$TaskName].State = 'Disabled'; return }
    if ($ErrorAction -eq 'Stop') { throw ""Task not found: $TaskName"" }
}
");

        sb.AppendLine(script);

        var tempPs1 = Path.Combine(Path.GetTempPath(), $"nitrolite_test_cmd_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(tempPs1, sb.ToString());

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempPs1}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
            {
                Console.WriteLine($"[ExecuteScriptWithMockCmdlets] ExitCode={proc.ExitCode}, StdOut={stdout}, StdErr={stderr}");
            }

            if (File.Exists(exportFile))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(exportFile);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var status = prop.Value.GetProperty("Status").GetString() ?? "";
                        var startType = prop.Value.GetProperty("StartType").GetString() ?? "";
                        mockServices[prop.Name] = (status, startType);
                    }
                }
                catch { }
            }

            return proc.ExitCode;
        }
        finally
        {
            if (File.Exists(tempPs1)) File.Delete(tempPs1);
            if (File.Exists(exportFile)) File.Delete(exportFile);
        }
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

            var mockServices = new Dictionary<string, (string Status, string StartType)>
            {
                ["AcerServiceSvc"] = ("Running", "Manual")
            };

            // Execute actual generated script against mock service cmdlets with simulated stop failure
            service.PowerShellRunner = (script, elevated) =>
                ExecuteScriptWithMockCmdletsAsync(script, mockServices, simulateStopError: true);

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

            var mockServices = new Dictionary<string, (string Status, string StartType)>
            {
                ["AcerServiceSvc"] = ("Running", "Automatic")
            };

            // Execute actual generated script against mock service cmdlets with real command flow
            service.PowerShellRunner = (script, elevated) =>
                ExecuteScriptWithMockCmdletsAsync(script, mockServices, simulateStopError: false);

            bool ok = await service.RestoreTier1ServicesAsync();
            Assert.True(ok, service.LastError);
            Assert.False(File.Exists(tempBackup), "Backup file MUST be deleted only upon verified success!");
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
        }
    }

    [Fact]
    public async Task TestCase6e_DebloatService_DisabledAndRunningRestore_AppliesManualBeforeStartAndExitsZero()
    {
        var service = new DebloatService();
        var tempBackup = Path.Combine(Path.GetTempPath(), $"debloat_test_disrun_{Guid.NewGuid():N}.json");

        try
        {
            // Service was recorded as Disabled + Running (fails in Windows if Set-Service Disabled is run before Start-Service)
            var backupData = new Dictionary<string, string>
            {
                ["AcerServiceSvc"] = "Disabled",
                ["AcerServiceSvc:Status"] = "Running"
            };
            File.WriteAllText(tempBackup, JsonSerializer.Serialize(backupData));
            service.BackupStateFilePath = tempBackup;

            var mockServices = new Dictionary<string, (string Status, string StartType)>
            {
                ["AcerServiceSvc"] = ("Stopped", "Disabled")
            };

            // Verify that the restore script successfully executes Manual -> Start -> Disabled sequence and exits with 0
            service.PowerShellRunner = (script, elevated) =>
                ExecuteScriptWithMockCmdletsAsync(script, mockServices);

            bool ok = await service.RestoreTier1ServicesAsync();
            Assert.True(ok, service.LastError);
            Assert.False(File.Exists(tempBackup), "Backup must be removed on verified restore of Disabled+Running service!");
            Assert.Equal("Disabled", mockServices["AcerServiceSvc"].StartType);
            Assert.Equal("Running", mockServices["AcerServiceSvc"].Status);
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
        }
    }

    [Fact]
    public async Task TestCase6f_DebloatService_DebloatScript_ExitsWithOneIfServiceFailsToStop()
    {
        var service = new DebloatService();
        var mockServices = new Dictionary<string, (string Status, string StartType)>
        {
            ["AcerServiceSvc"] = ("Running", "Automatic")
        };

        // When stop fails, the generated script must throw and exit 1
        service.PowerShellRunner = (script, elevated) =>
            ExecuteScriptWithMockCmdletsAsync(script, mockServices, simulateStopError: true);

        bool ok = await service.ApplyTier1DebloatAsync();
        Assert.False(ok);
        Assert.NotNull(service.LastError);
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
            Assert.Contains("bozuk", service.LastError, StringComparison.OrdinalIgnoreCase);
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
        string? errLog = null;

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

            // Simulate script failure reporting missing service/task in error log.
            // The log path is randomised per run, so take it from the generated script ("$errLog = '...';").
            service.PowerShellRunner = async (script, elevated) =>
            {
                errLog = System.Text.RegularExpressions.Regex.Match(script, @"\$errLog = '([^']+)'").Groups[1].Value.Replace("''", "'");
                await File.WriteAllTextAsync(errLog,"AcerServiceSvc: Servis bulunamadı: AcerServiceSvc\nTask:NitroSenseLauncher: NitroSenseLauncher görevi bulunamadı.");
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
            if (errLog != null && File.Exists(errLog)) File.Delete(errLog);
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
            var mockServices = new Dictionary<string, (string Status, string StartType)>
            {
                ["AcerServiceSvc"] = ("Running", "Automatic"),
                ["ASMSvc"] = ("Running", "Manual")
            };
            var mockTasks = new Dictionary<string, string>
            {
                ["NitroSenseLauncher"] = "Ready"
            };
            service.PowerShellRunner = (script, elevated) =>
                ExecuteScriptWithMockCmdletsAsync(script, mockServices, mockTasks);

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
        var mockDisplay = new TestDisplayRefreshRateServiceStub();
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

    [Fact]
    public async Task TestCase8b_PowerAutomationService_CommandFailure_DoesNotClearEcoActive_AndReportsPartialFailure()
    {
        var mockAgent = new TestAcerAgentClient();
        var mockCpu = new TestCpuPowerService();
        var mockDisplay = new TestDisplayRefreshRateServiceStub();
        var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco, AutoRefreshRateSwitching = false };

        var powerService = new PowerAutomationService(
            mockAgent,
            mockCpu,
            mockDisplay,
            settings,
            attachSystemEvents: false);

        string lastMsg = "";
        powerService.PowerModeSwitched += e => lastMsg = e.Message;

        // Transition to DC succeeds
        await powerService.ProcessPowerTransitionAsync(isAc: false);
        Assert.True(powerService.IsDcEcoActive);

        // Transition to AC where SetOperatingModeAsync fails
        mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(false);
        await powerService.ProcessPowerTransitionAsync(isAc: true);

        // Power automation must NOT clear _isDcEcoActive if commands failed!
        Assert.True(powerService.IsDcEcoActive);
        Assert.True(powerService.HasPendingAcRestore);
        Assert.True(powerService.PendingAcModeRestore);
        Assert.Contains("Kısmi", lastMsg);
        Assert.Contains("Çalışma Modu", lastMsg);

        powerService.Dispose();
    }

    [Fact]
    public async Task TestCase8c_GameEndsOnBatteryEco_RestoresQuietEcoAndPreservesDesktopBaselineForAc()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco, AtkMouseIntegrationEnabled = true };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);
            // Pinned to AC: this test is about the Eco baseline, not the battery clamp, and reading the host's
            // real power state made it fail whenever the suite ran on a laptop that was unplugged.
            window.PowerStateProvider = () => (OnAc: true, BatteryPercent: 100);

            // Simulate laptop is on battery (DC)
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);
            Assert.True(window.PowerAutomation.IsDcEcoActive);

            var game = new GameProfile
            {
                ExeName = "testgame",
                DisplayName = "Test Game",
                TargetMode = OperatingMode.Turbo,
                TargetCpuBoost = CpuBoostMode.Aggressive,
                TargetFanMode = FanMode.Max,
                TargetAtkMouseProfile = 2
            };

            await window.HandleGameStartedAsync(game);
            Assert.True(window.IsGameSessionActive);
            Assert.Equal(OperatingMode.Turbo, mockAgent.CurrentMode);

            // Game ends while STILL on battery!
            await window.HandleGameEndedAsync(game);

            // Must NOT restore desktop turbo/default over DC Eco mode!
            // Must transition to Quiet, Boost Disabled, Fan Auto!
            Assert.Equal(OperatingMode.Quiet, mockAgent.CurrentMode);
            Assert.Equal(CpuBoostMode.Disabled, mockCpu.CurrentMode);
            Assert.Equal(FanMode.Auto, mockAgent.CurrentFanMode);

            // And the saved AC baseline must still be the desktop baseline (Default / Aggressive)
            Assert.Equal(OperatingMode.Default, window.PowerAutomation.SavedOperatingMode);
            Assert.Equal(CpuBoostMode.Aggressive, window.PowerAutomation.SavedCpuBoost);

            // Now plug AC back in: desktop baseline is restored!
            mockAgent.OnSetOperatingModeAsync = null;
            await window.PowerAutomation.ProcessPowerTransitionAsync(isAc: true);
            Assert.False(window.PowerAutomation.IsDcEcoActive);
            Assert.Equal(OperatingMode.Default, mockAgent.CurrentMode);
            Assert.Equal(CpuBoostMode.Aggressive, mockCpu.CurrentMode);
        });
    }

    [Fact]
    public async Task TestCase8d_AcConnectedDuringActiveGame_RestoresActiveGameProfileNotDesktopBaseline()
    {
        var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default };
        var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Disabled };
        var mockDisplay = new TestDisplayRefreshRateServiceStub();
        var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco, AutoRefreshRateSwitching = false };

        var activeGame = new GameProfile
        {
            ExeName = "game.exe",
            DisplayName = "Active Game",
            TargetMode = OperatingMode.Extreme,
            TargetCpuBoost = CpuBoostMode.Aggressive,
            TargetFanMode = FanMode.Custom
        };

        bool isGameActive = true;
        var powerService = new PowerAutomationService(
            mockAgent,
            mockCpu,
            mockDisplay,
            settings,
            attachSystemEvents: false)
        {
            IsGameSessionActive = () => isGameActive,
            GetActiveGameProfile = () => activeGame,
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentCpuBoost = () => CpuBoostMode.Disabled,
            GetCurrentFanMode = () => FanMode.Auto
        };

        // On battery with Eco
        await powerService.ProcessPowerTransitionAsync(isAc: false);
        Assert.True(powerService.IsDcEcoActive);

        // Plug AC back in WHILE game is still active
        await powerService.ProcessPowerTransitionAsync(isAc: true);

        // Must restore GAME PROFILE (Extreme / Aggressive / Custom), NOT desktop Default!
        Assert.Equal(OperatingMode.Extreme, mockAgent.CurrentMode);
        Assert.Equal(CpuBoostMode.Aggressive, mockCpu.CurrentMode);
        Assert.Equal(FanMode.Custom, mockAgent.CurrentFanMode);

        powerService.Dispose();
    }

    [Fact]
    public async Task TestCase4c_LiveProfileSave_MouseDisabledRestoresToDesktopAndClearsFlag()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient();
            var mockCpu = new TestCpuPowerService();
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { AtkMouseIntegrationEnabled = true, DefaultAtkMouseProfile = 1 };

            var mouseSwitchedHistory = new List<int>();
            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => { mouseSwitchedHistory.Add(p); return true; },
                isTestHarness: true);

            var gameProfile = new GameProfile
            {
                ExeName = "game1.exe",
                DisplayName = "Game 1",
                TargetMode = OperatingMode.Turbo,
                TargetCpuBoost = CpuBoostMode.Aggressive,
                TargetFanMode = FanMode.Auto,
                TargetAtkMouseProfile = 2
            };

            gameService.SetActiveGameForTesting(gameProfile, 1234);
            await window.HandleGameStartedAsync(gameProfile);
            Assert.True(window.IsGameSessionActive);
            Assert.Equal(2, mouseSwitchedHistory.Last());

            // Active game profile is edited while game is running: mouse changed to 0 (Don't use)
            var editedProfile = new GameProfile
            {
                ExeName = "game1.exe",
                DisplayName = "Game 1",
                TargetMode = OperatingMode.Turbo,
                TargetCpuBoost = CpuBoostMode.Aggressive,
                TargetFanMode = FanMode.Auto,
                TargetAtkMouseProfile = 0
            };

            gameService.SetActiveGameForTesting(editedProfile, 1234);
            // Apply live profile update directly: mouse must be restored to desktop profile 1!
            bool ok = await window.ApplyLiveGameProfileAsync(editedProfile);
            Assert.True(ok);
            Assert.Equal(1, mouseSwitchedHistory.Last());
        });
    }

    [Theory]
    [InlineData("{\"result\":\"0\"}", true)]
    [InlineData("{\"result\":\"3\"}", false)]
    [InlineData("{\"result\":\"99\"}", false)]
    [InlineData("{\"result\":\"1\"}", false)]
    [InlineData("{\"result\":\"error\"}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("invalid json", false)]
    public void TestCase9_AcerAgentClient_IsSuccessResult_ValidatesOnlyZero(string? json, bool expected)
    {
        bool actual = AcerAgentClient.IsSuccessResult(json);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task TestCase8e_GameStartedOnBatteryEco_PreservesOriginalAcBaseline()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco, AtkMouseIntegrationEnabled = true };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            // 1. Unplug AC: machine transitions to DC Eco
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);
            Assert.True(window.PowerAutomation.IsDcEcoActive);
            Assert.Equal(OperatingMode.Quiet, mockAgent.CurrentMode);
            Assert.Equal(CpuBoostMode.Disabled, mockCpu.CurrentMode);
            Assert.Equal(OperatingMode.Default, window.PowerAutomation.SavedOperatingMode);
            Assert.Equal(CpuBoostMode.Aggressive, window.PowerAutomation.SavedCpuBoost);

            // 2. Start game while on DC Eco
            var game = new GameProfile
            {
                ExeName = "testgame",
                DisplayName = "Test Game",
                TargetMode = OperatingMode.Turbo,
                TargetCpuBoost = CpuBoostMode.Aggressive,
                TargetFanMode = FanMode.Max,
                TargetAtkMouseProfile = 2
            };

            await window.HandleGameStartedAsync(game);
            Assert.True(window.IsGameSessionActive);
            Assert.Equal(OperatingMode.Turbo, mockAgent.CurrentMode);
            // Saved baseline for game restore must be the true AC baseline, NOT Quiet/Disabled!
            Assert.Equal(OperatingMode.Default, window.SavedModeBeforeGame);
            Assert.Equal(CpuBoostMode.Aggressive, window.SavedCpuBoostBeforeGame);

            // 3. Game ends while STILL on battery: returns to Eco (Quiet / Disabled / Auto)
            await window.HandleGameEndedAsync(game);
            Assert.Equal(OperatingMode.Quiet, mockAgent.CurrentMode);
            Assert.Equal(CpuBoostMode.Disabled, mockCpu.CurrentMode);
            Assert.Equal(FanMode.Auto, mockAgent.CurrentFanMode);

            // AC baseline in PowerAutomation must NOT have been corrupted to Quiet / Disabled!
            Assert.Equal(OperatingMode.Default, window.PowerAutomation.SavedOperatingMode);
            Assert.Equal(CpuBoostMode.Aggressive, window.PowerAutomation.SavedCpuBoost);

            // 4. Plug AC back in: AC baseline restored!
            await window.PowerAutomation.ProcessPowerTransitionAsync(isAc: true);
            Assert.False(window.PowerAutomation.IsDcEcoActive);
            Assert.Equal(OperatingMode.Default, mockAgent.CurrentMode);
            Assert.Equal(CpuBoostMode.Aggressive, mockCpu.CurrentMode);
        });
    }

    [Fact]
    public async Task TestCase8f_RetryPendingDesktopRestore_OnBattery_AppliesEcoQuietNotDesktop()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco, AtkMouseIntegrationEnabled = true };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            // On battery
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            var game = new GameProfile
            {
                ExeName = "game.exe",
                DisplayName = "Game",
                TargetMode = OperatingMode.Extreme,
                TargetCpuBoost = CpuBoostMode.Aggressive,
                TargetFanMode = FanMode.Custom
            };

            await window.HandleGameStartedAsync(game);

            // Fail Quiet mode switch during game exit
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(false);
            await window.HandleGameEndedAsync(game);

            Assert.True(window.HasPendingDesktopRestore);
            Assert.True(window.PendingModeRestore);

            // Now retry on battery: must apply Quiet (Eco), NOT Default!
            mockAgent.OnSetOperatingModeAsync = null;
            bool retryOk = await window.RetryPendingDesktopRestoreAsync();
            Assert.True(retryOk);
            Assert.False(window.HasPendingDesktopRestore);
            Assert.Equal(OperatingMode.Quiet, mockAgent.CurrentMode);
            Assert.Equal(CpuBoostMode.Disabled, mockCpu.CurrentMode);
        });
    }

    [Fact]
    public async Task TestCase4d_DisablingMouseIntegrationCheckbox_RestoresMouseAndClearsDebt()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient();
            var mockCpu = new TestCpuPowerService();
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { AtkMouseIntegrationEnabled = true, DefaultAtkMouseProfile = 1 };

            var mouseSwitchedHistory = new List<int>();
            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => { mouseSwitchedHistory.Add(p); return true; },
                isTestHarness: true);

            var game = new GameProfile
            {
                ExeName = "game.exe",
                DisplayName = "Game",
                TargetMode = OperatingMode.Turbo,
                TargetCpuBoost = CpuBoostMode.Aggressive,
                TargetFanMode = FanMode.Auto,
                TargetAtkMouseProfile = 2
            };

            await window.HandleGameStartedAsync(game);
            Assert.Equal(2, mouseSwitchedHistory.Last());

            // User unchecks mouse integration checkbox while game is running
            await window.SetMouseIntegrationEnabledAsync(false);

            // Mouse must be restored immediately to desktop profile 1!
            Assert.Equal(1, mouseSwitchedHistory.Last());
            Assert.False(window.PendingMouseRestore);

            // Game ends: restore must not fail or leave mouse in wrong profile
            await window.HandleGameEndedAsync(game);
            Assert.Equal(1, mouseSwitchedHistory.Last());
            Assert.False(window.HasPendingDesktopRestore);
        });
    }

    [Fact]
    public async Task TestCase8g_PendingAcRestore_RetriedAndExposedToTray()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            // Switch to DC Eco
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            // Connect AC, but mode restore fails
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(false);
            await window.PowerAutomation.ProcessPowerTransitionAsync(isAc: true);

            Assert.True(window.PowerAutomation.HasPendingAcRestore);
            // MainWindow.HasPendingDesktopRestore must reflect AC failure so tray menu offers retry!
            Assert.True(window.HasPendingDesktopRestore);

            // Clear failure and call retry
            mockAgent.OnSetOperatingModeAsync = null;
            bool retryOk = await window.RetryPendingDesktopRestoreAsync();
            Assert.True(retryOk);
            Assert.False(window.PowerAutomation.HasPendingAcRestore);
            Assert.False(window.HasPendingDesktopRestore);
            Assert.Equal(OperatingMode.Default, mockAgent.CurrentMode);
        });
    }

    [Fact]
    public async Task TestCase8h_PowerModeSwitched_PartialFailure_DoesNotCorruptUiFields()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Extreme };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            window.SetCurrentModeForTesting(OperatingMode.Extreme);
            // Initial mode is Extreme
            Assert.Equal(OperatingMode.Extreme, window.CurrentMode);

            // Fail Quiet mode write when unplugging AC
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(false);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            // _currentMode must NOT be corrupted to Quiet since the hardware command failed!
            Assert.Equal(OperatingMode.Extreme, window.CurrentMode);
        });
    }

    #endregion

    #region Bug Fix Tests — 4 Reproduced Bugs

    // Bug 1: Desktop baseline captured when pending AC restore exists.
    // HasPendingDesktopRestore includes AC failures, so game start saw "pending=true"
    // and skipped saving the real desktop baseline. Fix: use _hasGameBaselineCaptured flag.
    [Fact]
    public async Task TestCase_AdapterPending_GameStart_CapturesFreshBaseline()
    {
        await RunInStaAsync(async () =>
        {
        var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Turbo };
        var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Efficient };
        var gameService = new GameDetectionService();
        var mouseService = new AtkMouseService();
        var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

        var window = new MainWindow(
            mockAgent,
            mockCpu,
            gameService,
            mouseService,
            settings: settings,
            mouseProfileSwitcher: p => true,
            isTestHarness: true);

        window.SetCurrentModeForTesting(OperatingMode.Turbo);
        mockCpu.CurrentMode = CpuBoostMode.Efficient;

        // Step 1: Unplug AC (DC transition) - saves Turbo/Efficient as desktop reference in PowerAutomation
        mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
        mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
        await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

        // Step 2: Reconnect AC with fan failure → HasPendingAcRestore = true
        mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
        mockAgent.OnSetFanModeAsync = m => Task.FromResult(false); // fan fails
        await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: true);
        Assert.True(window.PowerAutomation!.HasPendingAcRestore);

        // HasPendingDesktopRestore includes AC restore, but HasGameBaselineCaptured must be false
        Assert.True(window.HasPendingDesktopRestore);
        Assert.False(window.HasGameBaselineCaptured);

        // Start a game — baseline should be captured even though HasPendingDesktopRestore = true
        var profile = new GameProfile
        {
            DisplayName = "TestGame",
            ExeName = "test.exe",
            TargetMode = OperatingMode.Extreme,
            TargetCpuBoost = CpuBoostMode.Aggressive,
            TargetFanMode = FanMode.Max
        };

        mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
        mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
        await window.HandleGameStartedAsync(profile);

        // Baseline must reflect Turbo / Efficient (the real desktop values), not stale AC state
        Assert.True(window.HasGameBaselineCaptured);
        Assert.Equal(OperatingMode.Turbo, window.SavedModeBeforeGame);
        Assert.Equal(CpuBoostMode.Efficient, window.SavedCpuBoostBeforeGame);
        });
    }

    // Bug 2: Mouse integration disabled while mouse command in-flight causes race.
    // Flag is read before lock is acquired → flag is false (command still running) → no restore attempted.
    // Fix: acquire lock BEFORE reading flag.
    [Fact]
    public async Task TestCase_MouseIntegration_DisabledWhileCommandInFlight_RestoresMouse()
    {
        await RunInStaAsync(async () =>
        {
        var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default };
        var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
        var gameService = new GameDetectionService();
        var mouseService = new AtkMouseService();
        var settings = new AppSettings
        {
            AtkMouseIntegrationEnabled = true,
            DefaultAtkMouseProfile = 1
        };

        // TCS blocks inside the mouse profile switcher to simulate an in-flight command
        var mouseCommandStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mouseCommandGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int lastSwitchedProfile = 0;

        var window = new MainWindow(
            mockAgent,
            mockCpu,
            gameService,
            mouseService,
            settings: settings,
            mouseProfileSwitcher: p =>
            {
                lastSwitchedProfile = p;
                mouseCommandStarted.TrySetResult(true);
                mouseCommandGate.Task.GetAwaiter().GetResult();
                return true;
            },
            isTestHarness: true);

        // Start game profile switch (mouse → Config 2) in background
        var profile = new GameProfile
        {
            DisplayName = "RaceGame",
            ExeName = "race.exe",
            TargetMode = OperatingMode.Extreme,
            TargetCpuBoost = CpuBoostMode.Aggressive,
            TargetFanMode = FanMode.Auto,
            TargetAtkMouseProfile = 2
        };

        mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
        var gameStartTask = Task.Run(async () => await window.HandleGameStartedAsync(profile));

        // Wait until mouse command is in-flight (blocked at gate)
        await mouseCommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Disable integration while command is still in-flight.
        // With the fix, SetMouseIntegrationEnabledAsync acquires the lock first → waits for
        // the in-flight command to finish → reads flag (now true) → restores mouse.
        var disableTask = Task.Run(async () => await window.SetMouseIntegrationEnabledAsync(false));

        // Let the mouse command complete
        await Task.Delay(100); // give disableTask a moment to try to acquire the lock
        mouseCommandGate.TrySetResult(true);

        await gameStartTask.WaitAsync(TimeSpan.FromSeconds(5));
        await disableTask.WaitAsync(TimeSpan.FromSeconds(5));

        // The last profile switched should be 1 (restore to desktop profile)
        Assert.Equal(1, lastSwitchedProfile);
        Assert.False(window.PendingMouseRestore);
        });
    }

    [Fact]
    public Task InterruptedGameRecovery_KeepsFailedFanAndRestoresBoostPerPowerSource() => RunInStaAsync(async () =>
    {
        var writes = new List<(CpuBoostMode Mode, bool Ac, bool Dc)>();
        int modeCalls = 0, fanCalls = 0;
        var agent = new TestAcerAgentClient
        {
            OnSetOperatingModeAsync = _ => { modeCalls++; return Task.FromResult(true); },
            OnSetFanModeAsync = _ => Task.FromResult(++fanCalls > 1)
        };
        var cpu = new TestCpuPowerService
        {
            OnSetBoostModeAsync = (mode, ac, dc) => { writes.Add((mode, ac, dc)); return Task.FromResult(true); }
        };
        using var game = new GameDetectionService();
        var window = new MainWindow(agent, cpu, game, new AtkMouseService(),
            settings: new AppSettings(), isTestHarness: true);
        var state = new GameSessionRecoveryState
        {
            IsActive = true,
            SavedAcBoost = CpuBoostMode.Aggressive,
            SavedDcBoost = CpuBoostMode.Disabled
        };

        Assert.False(await window.RestoreInterruptedGameAsync(state));
        var pending = window.LoadGameSessionRecoveryState();
        Assert.NotNull(pending);
        Assert.True(pending.RestoreFan);
        Assert.False(pending.RestoreMode);
        Assert.Null(pending.SavedAcBoost);
        Assert.Null(pending.SavedDcBoost);

        Assert.True(await window.RestoreInterruptedGameAsync(pending));
        Assert.Null(window.LoadGameSessionRecoveryState());
        Assert.Equal(1, modeCalls);
        Assert.Equal(2, fanCalls);
        Assert.Equal(new[] { (CpuBoostMode.Aggressive, true, false), (CpuBoostMode.Disabled, false, true) }, writes);
    });

    [Fact]
    public Task DesktopMouseProfile_RetriesWhenReceiverStartsResponding() => RunInStaAsync(async () =>
    {
        var profiles = new List<int>();
        var window = new MainWindow(new TestAcerAgentClient(), new TestCpuPowerService(),
            new GameDetectionService(), new AtkMouseService(),
            settings: new AppSettings { AtkMouseIntegrationEnabled = true },
            mouseProfileSwitcher: profile =>
            {
                profiles.Add(profile);
                return profiles.Count > 1;
            }, isTestHarness: true);

        Assert.Equal(AtkMouseResult.NoResponse, await window.ApplyDesktopMouseProfileAsync(1));
        await window.RetryPendingDesktopMouseProfileIfDueAsync(long.MaxValue);
        await window.RetryPendingDesktopMouseProfileIfDueAsync(long.MaxValue);

        Assert.Equal(new[] { 1, 1 }, profiles);
    });

    [Fact]
    public Task DesktopMouseProfile_QueuedRetryYieldsToNewerSelection() => RunInStaAsync(async () =>
    {
        var profiles = new List<int>();
        var window = new MainWindow(new TestAcerAgentClient(), new TestCpuPowerService(),
            new GameDetectionService(), new AtkMouseService(),
            settings: new AppSettings { AtkMouseIntegrationEnabled = true },
            mouseProfileSwitcher: profile =>
            {
                profiles.Add(profile);
                return profile == 2;
            }, isTestHarness: true);

        Assert.Equal(AtkMouseResult.NoResponse, await window.ApplyDesktopMouseProfileAsync(1));

        await window.GameProfileTransitionLock.WaitAsync();
        Task oldRetry;
        Task<AtkMouseResult?> newRequest;
        try
        {
            oldRetry = window.RetryPendingDesktopMouseProfileIfDueAsync(long.MaxValue);
            newRequest = window.ApplyDesktopMouseProfileAsync(2);
        }
        finally
        {
            window.GameProfileTransitionLock.Release();
        }

        await oldRetry;
        Assert.Equal(AtkMouseResult.Confirmed, await newRequest);
        Assert.Equal(new[] { 1, 2 }, profiles);
    });

    // The sidebar row used to fall back to "Bilinmiyor" whenever a caller passed no profile number, so a
    // successful switch could be overwritten by the startup placeholder.
    [Fact]
    public void SideMouseProfile_KeepsTheConfirmedConfigWhenAPlaceholderCallFollows()
    {
        Assert.Equal(("Config 2", true),
            MainWindow.DescribeSideMouseProfile(0, AtkMouseResult.Confirmed, retryPending: false, lastShownProfile: 2));
    }

    [Fact]
    public void SideMouseProfile_SaysItIsCheckingBeforeTheFirstQueryAnswers()
    {
        Assert.Equal(("Sorgulanıyor…", false),
            MainWindow.DescribeSideMouseProfile(0, AtkMouseResult.Confirmed, retryPending: false, lastShownProfile: 0));
    }

    [Fact]
    public void SideMouseProfile_ShowsTheFailureInsteadOfUnknownWhenTheSwitchFails()
    {
        Assert.Equal(("Uyanıyor…", false),
            MainWindow.DescribeSideMouseProfile(0, AtkMouseResult.NoResponse, retryPending: true, lastShownProfile: 0));
        Assert.Equal(("Yanıt yok", false),
            MainWindow.DescribeSideMouseProfile(0, AtkMouseResult.NoResponse, retryPending: false, lastShownProfile: 0));
        Assert.Equal(("Alıcı yok", false),
            MainWindow.DescribeSideMouseProfile(0, AtkMouseResult.ReceiverNotFound, retryPending: false, lastShownProfile: 0));
    }

    // Bug 3: Balanced DC mode AC reconnect fires event with Applied=true when no commands were sent.
    // Fix: when !_isDcEcoActive && !HasPendingAcRestore, all Applied flags must be false.
    [Fact]
    public async Task TestCase_BalancedDcMode_AcReconnect_DoesNotCorruptCurrentMode()
    {
        await RunInStaAsync(async () =>
        {
        var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default };
        var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
        var gameService = new GameDetectionService();
        var mouseService = new AtkMouseService();
        // Balanced mode — DC eco is NOT active
        var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Balanced };

        var window = new MainWindow(
            mockAgent,
            mockCpu,
            gameService,
            mouseService,
            settings: settings,
            mouseProfileSwitcher: p => true,
            isTestHarness: true);

        window.SetCurrentModeForTesting(OperatingMode.Extreme);
        Assert.Equal(OperatingMode.Extreme, window.CurrentMode);

        // Track whether any hardware command was actually sent
        bool commandSent = false;
        mockAgent.OnSetOperatingModeAsync = m => { commandSent = true; return Task.FromResult(true); };

        // Simulate AC reconnect in Balanced mode (no pending restore, no eco)
        await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: true);

        // No command should have been sent
        Assert.False(commandSent);
        // CurrentMode must remain Extreme — the event must NOT overwrite it with a stale saved value
        Assert.Equal(OperatingMode.Extreme, window.CurrentMode);
        });
    }

    // Bug 4: RetryPendingAcRestoreAsync succeeds on hardware but never fires PowerModeSwitched.
    // UI stays stale (shows Quiet instead of Default after retry).
    // Fix: fire PowerModeSwitched with verified values after all pending flags clear.
    [Fact]
    public async Task TestCase_RetryPendingAcRestore_UpdatesUiAfterSuccess()
    {
        await RunInStaAsync(async () =>
        {
        var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default };
        var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
        var gameService = new GameDetectionService();
        var mouseService = new AtkMouseService();
        var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

        var window = new MainWindow(
            mockAgent,
            mockCpu,
            gameService,
            mouseService,
            settings: settings,
            mouseProfileSwitcher: p => true,
            isTestHarness: true);

        // Start in Default mode on adapter
        window.SetCurrentModeForTesting(OperatingMode.Default);

        // Unplug AC: saves Default, switches to Eco/Quiet
        mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
        mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
        await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

        // Reconnect AC: fail mode command → pending restore
        mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(false);
        await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: true);
        Assert.True(window.PowerAutomation!.HasPendingAcRestore);

        // Allow all commands to succeed on retry
        mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
        mockCpu.OnSetBoostModeAsync = (m, ac, dc) => Task.FromResult(true);
        mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);

        bool retryOk = await window.PowerAutomation!.RetryPendingAcRestoreAsync();
        Assert.True(retryOk);
        Assert.False(window.PowerAutomation!.HasPendingAcRestore);

        // UI must reflect the successfully restored Default mode — not remain stale
        Assert.Equal(OperatingMode.Default, window.CurrentMode);
        });
    }

    // Scenario 1: Game A exit has failed mode restore -> Retry succeeds -> Game B starts and ends.
    // Verifies that Game B exit does NOT skip desktop restore and restores the true desktop baseline.
    [Fact]
    public async Task TestCase_RetrySuccess_SubsequentGame_RestoresDesktopCorrectly()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);
            // Pinned to AC: this test is about the Eco baseline, not the battery clamp, and reading the host's
            // real power state made it fail whenever the suite ran on a laptop that was unplugged.
            window.PowerStateProvider = () => (OnAc: true, BatteryPercent: 100);
            // The automation service read the host's adapter state in its constructor, before the pin above
            // could apply, so it has to be told once more. Without this the restore targets come back as Eco's
            // Quiet instead of the desktop Default.
            window.PowerAutomation!.AcStatusProvider = () => true;
            await window.PowerAutomation.EvaluatePowerStateAsync();

            window.SetCurrentModeForTesting(OperatingMode.Default);
            mockCpu.CurrentMode = CpuBoostMode.Aggressive;

            var gameA = new GameProfile
            {
                DisplayName = "GameA",
                ExeName = "gameA.exe",
                TargetMode = OperatingMode.Extreme,
                TargetCpuBoost = CpuBoostMode.Disabled,
                TargetFanMode = FanMode.Max
            };

            // Start Game A
            await window.HandleGameStartedAsync(gameA);
            Assert.Equal(OperatingMode.Extreme, window.CurrentMode);

            // End Game A with failure on mode command
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(false);
            await window.HandleGameEndedAsync(gameA);
            Assert.True(window.HasPendingDesktopRestore);
            Assert.True(window.PendingModeRestore);

            // Now retry desktop restore and let it succeed
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            bool retryOk = await window.RetryPendingDesktopRestoreAsync();
            Assert.True(retryOk);
            Assert.False(window.HasPendingDesktopRestore);
            Assert.False(window.HasGameBaselineCaptured);
            Assert.Equal(OperatingMode.Default, window.CurrentMode);

            // Start Game B
            var gameB = new GameProfile
            {
                DisplayName = "GameB",
                ExeName = "gameB.exe",
                TargetMode = OperatingMode.Turbo,
                TargetCpuBoost = CpuBoostMode.Disabled,
                TargetFanMode = FanMode.Max
            };

            await window.HandleGameStartedAsync(gameB);
            Assert.Equal(OperatingMode.Turbo, window.CurrentMode);
            Assert.True(window.HasGameBaselineCaptured);
            Assert.Equal(OperatingMode.Default, window.SavedModeBeforeGame);

            // End Game B
            await window.HandleGameEndedAsync(gameB);
            Assert.False(window.HasPendingDesktopRestore);
            Assert.Equal(OperatingMode.Default, window.CurrentMode);
            Assert.Equal(CpuBoostMode.Aggressive, mockCpu.CurrentMode);
            Assert.Equal(FanMode.Auto, window.CurrentFanMode);
        });
    }

    // Scenario 2: AC retry with partial success: mode succeeds, fan fails.
    // UI must update the mode to Default and not remain stuck at Quiet.
    [Fact]
    public async Task TestCase_AcRetry_PartialSuccess_UpdatesOnlySuccessfulStepsInUi()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            window.SetCurrentModeForTesting(OperatingMode.Default);

            // Unplug AC -> Eco (Quiet)
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            // Reconnect AC: fail both mode and fan
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(false);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(false);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: true);
            Assert.True(window.PowerAutomation!.HasPendingAcRestore);

            // Retry: mode succeeds, fan still fails
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(false);

            bool retryOk = await window.PowerAutomation!.RetryPendingAcRestoreAsync();
            Assert.False(retryOk); // Fan still pending
            Assert.True(window.PowerAutomation!.HasPendingAcRestore);

            // UI must have updated to Default for the succeeded mode step!
            Assert.Equal(OperatingMode.Default, window.CurrentMode);
        });
    }

    // Scenario 3: Only fan pending during AC restore. User manually switches mode to Turbo.
    // When fan retry succeeds, mode was not in retry scope, so UI and hardware must stay at Turbo (not overwritten with Default).
    [Fact]
    public async Task TestCase_AcRetry_OnlyFanPending_DoesNotOverwriteUserSelectedMode()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            window.SetCurrentModeForTesting(OperatingMode.Default);

            // Unplug AC -> Eco (Quiet)
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            // Reconnect AC: mode succeeds, fan fails
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(false);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: true);
            Assert.True(window.PowerAutomation!.HasPendingAcRestore);
            Assert.Equal(OperatingMode.Default, window.CurrentMode);

            // User manually changes mode to Turbo while fan is still pending
            window.SetCurrentModeForTesting(OperatingMode.Turbo);
            mockAgent.CurrentMode = OperatingMode.Turbo;

            // Now fan retry runs and succeeds
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            bool retryOk = await window.PowerAutomation!.RetryPendingAcRestoreAsync();
            Assert.True(retryOk);
            Assert.False(window.PowerAutomation!.HasPendingAcRestore);

            // Mode command was never sent during retry -> CurrentMode must still be Turbo!
            Assert.Equal(OperatingMode.Turbo, window.CurrentMode);
        });
    }

    // Manual Selection Overwrites Adapter Retry Targets: Mode
    [Fact]
    public async Task TestCase_ManualModeSelection_UpdatesAcPendingTarget_NotOverwrittenByRetry()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            window.SetCurrentModeForTesting(OperatingMode.Default);

            // Unplug AC -> Eco
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            // Reconnect AC: mode fails -> PendingAcModeRestore = true, saved target is Default
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(false);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: true);
            Assert.True(window.PowerAutomation!.PendingAcModeRestore);

            // User makes a manual choice using real SetManualModeAsync: Turbo
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            bool modeOk = await window.SetManualModeAsync(OperatingMode.Turbo);
            Assert.True(modeOk);
            Assert.Equal(OperatingMode.Turbo, window.CurrentMode);
            Assert.False(window.PowerAutomation!.PendingAcModeRestore);
            Assert.Equal(OperatingMode.Turbo, window.PowerAutomation!.SavedOperatingMode);

            // Now retry runs
            bool retryOk = await window.PowerAutomation!.RetryPendingAcRestoreAsync();
            Assert.True(retryOk);

            // Must NOT have reset to Default — must remain Turbo!
            Assert.Equal(OperatingMode.Turbo, window.CurrentMode);
        });
    }

    // Manual Selection Overwrites Adapter Retry Targets: CPU Boost
    [Fact]
    public async Task TestCase_ManualCpuBoostSelection_UpdatesAcPendingTarget_NotOverwrittenByRetry()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            // Unplug AC -> Eco (Disabled boost)
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            // Reconnect AC: CPU boost restore fails -> PendingAcCpuRestore = true, saved target is Aggressive
            mockCpu.OnSetBoostModeAsync = (m, ac, dc) => Task.FromResult(false);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: true);
            Assert.True(window.PowerAutomation!.PendingAcCpuRestore);

            // User makes a manual choice using real SetManualCpuBoostAsync: Efficient
            mockCpu.OnSetBoostModeAsync = (m, ac, dc) => Task.FromResult(true);
            bool boostOk = await window.SetManualCpuBoostAsync(CpuBoostMode.Efficient);
            Assert.True(boostOk);
            Assert.Equal(CpuBoostMode.Efficient, mockCpu.CurrentMode);
            Assert.False(window.PowerAutomation!.PendingAcCpuRestore);
            Assert.Equal(CpuBoostMode.Efficient, window.PowerAutomation!.SavedCpuBoost);

            // Now retry runs
            bool retryOk = await window.PowerAutomation!.RetryPendingAcRestoreAsync();
            Assert.True(retryOk);

            // Must NOT have reset to Aggressive — must remain Efficient!
            Assert.Equal(CpuBoostMode.Efficient, mockCpu.CurrentMode);
        });
    }

    // Manual Selection Overwrites Adapter Retry Targets: Fan
    [Fact]
    public async Task TestCase_ManualFanSelection_UpdatesAcPendingTarget_NotOverwrittenByRetry()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            // Unplug AC -> Eco
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            // Reconnect AC: fan fails -> PendingAcFanRestore = true, saved target is Auto
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(false);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: true);
            Assert.True(window.PowerAutomation!.PendingAcFanRestore);

            // User makes a manual choice using real SetManualFanModeAsync: Max
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            bool fanOk = await window.SetManualFanModeAsync(FanMode.Max);
            Assert.True(fanOk);
            Assert.Equal(FanMode.Max, window.CurrentFanMode);
            Assert.False(window.PowerAutomation!.PendingAcFanRestore);
            Assert.Equal(FanMode.Max, window.PowerAutomation!.SavedFanMode);

            // Now retry runs
            bool retryOk = await window.PowerAutomation!.RetryPendingAcRestoreAsync();
            Assert.True(retryOk);

            // Must NOT have reset to Auto — must remain Max!
            Assert.Equal(FanMode.Max, window.CurrentFanMode);
        });
    }

    // Scenario: Fan pending -> User manually selects Turbo -> Game starts and ends.
    // Verifies that Game baseline captures the manual Turbo choice (not stale Default)
    // and returns to Turbo upon game exit.
    [Fact]
    public async Task TestCase_FanPending_ManualTurbo_GameStartAndEnd_RestoresTurbo()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            window.SetCurrentModeForTesting(OperatingMode.Default);

            // Unplug AC -> Eco
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            // Reconnect AC: mode succeeds, fan fails -> PendingAcFanRestore = true
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(false);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: true);
            Assert.True(window.PowerAutomation!.PendingAcFanRestore);
            Assert.False(window.PowerAutomation!.PendingAcModeRestore);

            // User manually chooses Turbo
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            bool manualOk = await window.SetManualModeAsync(OperatingMode.Turbo);
            Assert.True(manualOk);
            Assert.Equal(OperatingMode.Turbo, window.CurrentMode);

            // Start a game (which targets Extreme)
            var game = new GameProfile
            {
                DisplayName = "HeavyGame",
                ExeName = "game.exe",
                TargetMode = OperatingMode.Extreme,
                TargetCpuBoost = CpuBoostMode.Disabled,
                TargetFanMode = FanMode.Max
            };

            await window.HandleGameStartedAsync(game);
            Assert.Equal(OperatingMode.Extreme, window.CurrentMode);
            // Baseline must be Turbo, NOT the stale Default!
            Assert.Equal(OperatingMode.Turbo, window.SavedModeBeforeGame);

            // End game
            await window.HandleGameEndedAsync(game);

            // Upon game exit, mode must restore to Turbo (the manual desktop baseline)!
            Assert.Equal(OperatingMode.Turbo, window.CurrentMode);
        });
    }

    // Battery Scenario 1: On battery, change fan to Max -> reconnect AC
    // Expected: Default / Aggressive / Max (untouched mode and boost restored to AC targets)
    [Fact]
    public async Task TestCase_BatteryManualFanChange_ReconnectAc_RestoresModeAndBoost_KeepsFanMax()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            window.SetCurrentModeForTesting(OperatingMode.Default);
            mockCpu.CurrentMode = CpuBoostMode.Aggressive;

            // 1. Unplug AC: machine transitions to DC Eco (Quiet / Disabled / Auto)
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            mockCpu.OnSetBoostModeAsync = (m, ac, dc) => Task.FromResult(true);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            Assert.True(window.PowerAutomation.IsDcEcoActive);
            Assert.Equal(OperatingMode.Quiet, mockAgent.CurrentMode);
            Assert.Equal(CpuBoostMode.Disabled, mockCpu.CurrentMode);

            // 2. While on battery, manually set fan to Max
            bool fanOk = await window.SetManualFanModeAsync(FanMode.Max);
            Assert.True(fanOk);
            Assert.Equal(FanMode.Max, window.CurrentFanMode);
            // System must STILL be recognized as DC Eco active since we are still on battery!
            Assert.True(window.PowerAutomation.IsDcEcoActive);

            // 3. Reconnect AC
            await window.PowerAutomation.ProcessPowerTransitionAsync(isAc: true);

            // Expected: Default mode and Aggressive boost restored; Fan remains Max
            Assert.Equal(OperatingMode.Default, window.CurrentMode);
            Assert.Equal(CpuBoostMode.Aggressive, mockCpu.CurrentMode);
            Assert.Equal(FanMode.Max, window.CurrentFanMode);
            Assert.False(window.PowerAutomation.IsDcEcoActive);
        });
    }

    // Battery Scenario 2: On battery, change mode to Turbo -> reconnect AC
    // Expected: Turbo / Aggressive / Auto (manual Turbo preserved, untouched boost and fan restored to AC targets)
    [Fact]
    public async Task TestCase_BatteryManualModeChange_ReconnectAc_RestoresBoostAndFan_KeepsModeTurbo()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);
            // Pinned to AC: this test is about the Eco baseline, not the battery clamp, and reading the host's
            // real power state made it fail whenever the suite ran on a laptop that was unplugged.
            window.PowerStateProvider = () => (OnAc: true, BatteryPercent: 100);

            window.SetCurrentModeForTesting(OperatingMode.Default);
            mockCpu.CurrentMode = CpuBoostMode.Aggressive;

            // 1. Unplug AC: machine transitions to DC Eco (Quiet / Disabled / Auto)
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            mockCpu.OnSetBoostModeAsync = (m, ac, dc) => Task.FromResult(true);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            Assert.True(window.PowerAutomation.IsDcEcoActive);

            // 2. While on battery, manually set mode to Turbo
            bool modeOk = await window.SetManualModeAsync(OperatingMode.Turbo);
            Assert.True(modeOk);
            Assert.Equal(OperatingMode.Turbo, window.CurrentMode);
            Assert.True(window.PowerAutomation.IsDcEcoActive);

            // 3. Reconnect AC
            await window.PowerAutomation.ProcessPowerTransitionAsync(isAc: true);

            // Expected: Turbo mode preserved; Aggressive boost and Auto fan restored
            Assert.Equal(OperatingMode.Turbo, window.CurrentMode);
            Assert.Equal(CpuBoostMode.Aggressive, mockCpu.CurrentMode);
            Assert.Equal(FanMode.Auto, window.CurrentFanMode);
            Assert.False(window.PowerAutomation.IsDcEcoActive);
        });
    }

    // Battery Scenario 3: On battery, change CPU boost to Efficient -> reconnect AC
    // Expected: Default / Efficient / Auto (manual Efficient preserved, untouched mode and fan restored to AC targets)
    [Fact]
    public async Task TestCase_BatteryManualCpuBoostChange_ReconnectAc_RestoresModeAndFan_KeepsBoostEfficient()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            window.SetCurrentModeForTesting(OperatingMode.Default);
            mockCpu.CurrentMode = CpuBoostMode.Aggressive;

            // 1. Unplug AC: machine transitions to DC Eco (Quiet / Disabled / Auto)
            mockAgent.OnSetOperatingModeAsync = m => Task.FromResult(true);
            mockAgent.OnSetFanModeAsync = m => Task.FromResult(true);
            mockCpu.OnSetBoostModeAsync = (m, ac, dc) => Task.FromResult(true);
            await window.PowerAutomation!.ProcessPowerTransitionAsync(isAc: false);

            Assert.True(window.PowerAutomation.IsDcEcoActive);

            // 2. While on battery, manually set boost to Efficient
            bool boostOk = await window.SetManualCpuBoostAsync(CpuBoostMode.Efficient);
            Assert.True(boostOk);
            Assert.Equal(CpuBoostMode.Efficient, mockCpu.CurrentMode);
            Assert.True(window.PowerAutomation.IsDcEcoActive);

            // 3. Reconnect AC
            await window.PowerAutomation.ProcessPowerTransitionAsync(isAc: true);

            // Expected: Default mode and Auto fan restored; Boost remains Efficient
            Assert.Equal(OperatingMode.Default, window.CurrentMode);
            Assert.Equal(CpuBoostMode.Efficient, mockCpu.CurrentMode);
            Assert.Equal(FanMode.Auto, window.CurrentFanMode);
            Assert.False(window.PowerAutomation.IsDcEcoActive);
        });
    }

    [Fact]
    public void TestCase_OperatingMode_PersistsAndLoadsThroughSettingsManager()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"settings_mode_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string primaryPath = Path.Combine(tempDir, "settings.json");
        try
        {
            var initial = new AppSettings
            {
                LastOperatingMode = OperatingMode.Extreme
            };
            SettingsManager.SaveSettingsAtomic(primaryPath, initial);
            Assert.True(File.Exists(primaryPath));

            var loaded = SettingsManager.LoadSettingsWithFallback(primaryPath);
            Assert.Equal(OperatingMode.Extreme, loaded.LastOperatingMode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TestCase_StartupOperatingModeRestoration_OverridesEcBalancedDefault()
    {
        await RunInStaAsync(async () =>
        {
            // Device initially boots into Default (Balanced) as Acer EC does on cold boot/reboot
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings
            {
                LastOperatingMode = OperatingMode.Extreme
            };

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            window.PowerStateProvider = () => (OnAc: true, BatteryPercent: 90);

            // Run startup mode restoration
            await window.RestoreStartupOperatingModeAsync();

            // Hardware must have been switched to Extreme, UI updated, baseline synced
            Assert.Equal(OperatingMode.Extreme, mockAgent.CurrentMode);
            Assert.Equal(OperatingMode.Extreme, window.CurrentMode);
            Assert.Equal(OperatingMode.Extreme, window.SavedModeBeforeGame);
        });
    }

    [Fact]
    public async Task TestCase_SetManualModeAsync_PersistsToAppSettings()
    {
        await RunInStaAsync(async () =>
        {
            var mockAgent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default };
            var mockCpu = new TestCpuPowerService { CurrentMode = CpuBoostMode.Aggressive };
            var gameService = new GameDetectionService();
            var mouseService = new AtkMouseService();
            var settings = new AppSettings();

            var window = new MainWindow(
                mockAgent,
                mockCpu,
                gameService,
                mouseService,
                settings: settings,
                mouseProfileSwitcher: p => true,
                isTestHarness: true);

            window.PowerStateProvider = () => (OnAc: true, BatteryPercent: 100);

            bool ok = await window.SetManualModeAsync(OperatingMode.Extreme);
            Assert.True(ok);
            Assert.Equal(OperatingMode.Extreme, window.CurrentMode);
            Assert.Equal(OperatingMode.Extreme, window.CurrentAppSettings.LastOperatingMode);
        });
    }

    #endregion

    #region Software fan curve

    private static MainWindow FanCurveWindow(TestAcerAgentClient agent, AppSettings settings) =>
        new(agent, new TestCpuPowerService(), new GameDetectionService(), new AtkMouseService(),
            settings: settings, isTestHarness: true);

    [Fact]
    public async Task EnablingTheFanCurve_TakesTheFansInCustomModeAndRemembersIt()
    {
        await RunInStaAsync(async () =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var settings = new AppSettings
            {
                CustomFanCpuPercent = 55,
                FanCurvePoints = new List<FanCurvePoint> { new(50, 40), new(80, 100) }
            };
            var window = FanCurveWindow(agent, settings);

            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));

            Assert.Equal(FanMode.Custom, agent.CurrentFanMode);
            Assert.Equal(FanMode.Custom, window.CurrentFanMode);
            Assert.True(settings.FanCurveEnabled);
            // No temperature has been read yet, so the curve falls back to the saved manual speed instead of
            // guessing a fan speed from a sensor it does not have.
            Assert.Equal(55, agent.CustomFanCpuPercent);
            Assert.Equal(55, agent.CustomFanGpuPercent);
        });
    }

    [Fact]
    public async Task DisablingTheFanCurve_HandsTheFansBackToTheFirmware()
    {
        await RunInStaAsync(async () =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var settings = new AppSettings { CustomFanCpuPercent = 70, CustomFanGpuPercent = 65 };
            var window = FanCurveWindow(agent, settings);

            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));
            Assert.True(await window.SetFanCurveEnabledAsync(false, quiet: true));

            Assert.Equal(FanMode.Auto, agent.CurrentFanMode);
            Assert.Equal(FanMode.Auto, window.CurrentFanMode);
            Assert.False(settings.FanCurveEnabled);
            // The manual speeds the user had before the curve are back in the client, not the curve's last value.
            Assert.Equal(70, agent.CustomFanCpuPercent);
            Assert.Equal(65, agent.CustomFanGpuPercent);
        });
    }

    [Fact]
    public async Task SessionEnding_HandsTheFansBackToTheFirmwareWhenTheCurveOwnsThem()
    {
        await RunInStaAsync(async () =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var settings = new AppSettings { CustomFanCpuPercent = 55 };
            var window = FanCurveWindow(agent, settings);

            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));
            Assert.Equal(FanMode.Custom, agent.CurrentFanMode);

            // Windows is taking the process down and the normal exit path never runs. The curve cannot run any
            // more, so the fans must not stay pinned at the last speed it asked for.
            window.FastExitForSessionEnding();

            Assert.Equal(FanMode.Auto, agent.CurrentFanMode);
        });
    }

    [Fact]
    public async Task SessionEnding_LeavesAManualFanModeAlone()
    {
        await RunInStaAsync(() =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Max };
            var window = FanCurveWindow(agent, new AppSettings());

            window.FastExitForSessionEnding();

            // A speed the user picked by hand is their choice, not a leftover of the curve.
            Assert.Equal(FanMode.Max, agent.CurrentFanMode);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ChoosingAFanModeByHand_StopsTheCurve()
    {
        await RunInStaAsync(async () =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var settings = new AppSettings();
            var window = FanCurveWindow(agent, settings);

            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));
            Assert.True(await window.SetManualFanModeAsync(FanMode.Max));

            Assert.False(settings.FanCurveEnabled);
            Assert.Equal(FanMode.Max, agent.CurrentFanMode);
            Assert.Equal(FanMode.Max, window.CurrentFanMode);
        });
    }

    [Fact]
    public async Task AFailedCurveStart_LeavesTheSettingOffAndTheFansAlone()
    {
        await RunInStaAsync(async () =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            agent.OnSetFanModeAsync = _ => Task.FromResult(false);
            var settings = new AppSettings();
            var window = FanCurveWindow(agent, settings);

            Assert.False(await window.SetFanCurveEnabledAsync(true, quiet: true));

            Assert.False(settings.FanCurveEnabled);
            Assert.Equal(FanMode.Auto, agent.CurrentFanMode);
        });
    }

    #endregion

    #region Per-game fan curve

    private static GameProfile CurveGame(List<FanCurvePoint>? points, FanMode fixedMode = FanMode.Max) => new()
    {
        ExeName = "testgame",
        DisplayName = "Test Game",
        TargetMode = OperatingMode.Default,
        TargetCpuBoost = CpuBoostMode.Disabled,
        TargetFanMode = fixedMode,
        TargetFanCurveEnabled = points != null,
        TargetFanCurvePoints = points
    };

    [Fact]
    public async Task AGameProfileWithItsOwnCurve_TakesTheFansInCustomModeInsteadOfItsFixedSpeed()
    {
        await RunInStaAsync(async () =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var settings = new AppSettings { CustomFanCpuPercent = 50 };
            var window = FanCurveWindow(agent, settings);

            await window.HandleGameStartedAsync(CurveGame(new List<FanCurvePoint> { new(40, 45), new(90, 100) }));

            Assert.True(window.IsGameSessionActive);
            Assert.True(window.IsGameFanCurveArmed);
            Assert.Equal(FanMode.Custom, agent.CurrentFanMode);
            // No temperature has been read yet, so the curve falls back to the saved manual speed rather than
            // guessing from a sensor it does not have.
            Assert.Equal(50, agent.CustomFanCpuPercent);
        });
    }

    [Fact]
    public async Task AGameProfileWithoutItsOwnAnchors_FollowsTheDesktopCurve()
    {
        await RunInStaAsync(async () =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var settings = new AppSettings
            {
                CustomFanCpuPercent = 44,
                FanCurvePoints = new List<FanCurvePoint> { new(40, 33), new(90, 100) }
            };
            var window = FanCurveWindow(agent, settings);

            var game = CurveGame(new List<FanCurvePoint> { new(40, 33), new(90, 100) });
            game.TargetFanCurvePoints = null;

            await window.HandleGameStartedAsync(game);

            Assert.True(window.IsGameFanCurveArmed);
            Assert.Equal(FanMode.Custom, agent.CurrentFanMode);
            Assert.Equal(44, agent.CustomFanCpuPercent);
        });
    }

    [Fact]
    public async Task EndingAGameWithItsOwnCurve_HandsTheFansBackToTheDesktopCurve()
    {
        await RunInStaAsync(async () =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var settings = new AppSettings
            {
                CustomFanCpuPercent = 50,
                FanCurveEnabled = true,
                FanCurvePoints = new List<FanCurvePoint> { new(40, 30), new(90, 100) }
            };
            var window = FanCurveWindow(agent, settings);
            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));

            var game = CurveGame(new List<FanCurvePoint> { new(40, 45), new(90, 100) });
            await window.HandleGameStartedAsync(game);
            Assert.True(window.IsGameFanCurveArmed);

            await window.HandleGameEndedAsync(game);

            Assert.False(window.IsGameSessionActive);
            Assert.False(window.IsGameFanCurveArmed);
            // The desktop curve is back, and the user's preference was never cleared by the handover.
            Assert.True(window.IsFanCurveArmed);
            Assert.True(settings.FanCurveEnabled);
            Assert.Equal(FanMode.Custom, agent.CurrentFanMode);
        });
    }

    [Fact]
    public async Task EndingAGameWithItsOwnCurve_LeavesTheDesktopCurveOffWhenTheUserHadItOff()
    {
        await RunInStaAsync(async () =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var settings = new AppSettings { CustomFanCpuPercent = 50, FanCurveEnabled = false };
            var window = FanCurveWindow(agent, settings);

            var game = CurveGame(new List<FanCurvePoint> { new(40, 45), new(90, 100) });
            await window.HandleGameStartedAsync(game);
            await window.HandleGameEndedAsync(game);

            Assert.False(window.IsFanCurveArmed);
            Assert.False(settings.FanCurveEnabled);
        });
    }

    [Fact]
    public async Task AGameWithoutACurve_SuspendsTheDesktopCurveAndResumesItAfter()
    {
        await RunInStaAsync(async () =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            var settings = new AppSettings
            {
                CustomFanCpuPercent = 50,
                FanCurveEnabled = true,
                FanCurvePoints = new List<FanCurvePoint> { new(40, 30), new(90, 100) }
            };
            var window = FanCurveWindow(agent, settings);
            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));

            var game = CurveGame(null, FanMode.Max);
            await window.HandleGameStartedAsync(game);

            // The profile's fixed speed wins for the session; the desktop curve stays armed but suspended.
            Assert.Equal(FanMode.Max, agent.CurrentFanMode);
            Assert.False(window.IsGameFanCurveArmed);
            Assert.True(window.IsFanCurveArmed);

            await window.HandleGameEndedAsync(game);

            Assert.Equal(FanMode.Custom, agent.CurrentFanMode);
            Assert.True(settings.FanCurveEnabled);
        });
    }

    [Fact]
    public async Task AFailedGameCurveStart_LeavesNoCurveArmed()
    {
        await RunInStaAsync(async () =>
        {
            var agent = new TestAcerAgentClient { CurrentMode = OperatingMode.Default, CurrentFanMode = FanMode.Auto };
            agent.OnSetFanModeAsync = _ => Task.FromResult(false);
            var settings = new AppSettings();
            var window = FanCurveWindow(agent, settings);

            Assert.False(await window.ArmGameFanCurveAsync(CurveGame(new List<FanCurvePoint> { new(40, 45), new(90, 100) })));

            Assert.False(window.IsFanCurveArmed);
            Assert.False(window.IsGameFanCurveArmed);
            Assert.Equal(FanMode.Auto, agent.CurrentFanMode);
        });
    }

    #endregion
}
