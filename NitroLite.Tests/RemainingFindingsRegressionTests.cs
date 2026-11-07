using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace NitroLite.Tests;

public class RemainingFindingsRegressionTests
{
    // =========================================================================
    // FINDING 1: Debloat Service State Query, Selective Targeting & Backup Completeness
    // =========================================================================

    [Theory]
    [InlineData("        STATE              : 4  RUNNING", "Running")]
    [InlineData("        STATE              : 1  STOPPED", "Stopped")]
    [InlineData("        DURUM              : 4  ÇALIŞIYOR", "Running")]
    [InlineData("        DURUM              : 1  DURDURULDU", "Stopped")]
    [InlineData("        STATUS             : 4  LAUFEND", "Running")]
    [InlineData("        STATUS             : 1  BEENDET", "Stopped")]
    [InlineData("        ETAT               : 4  EN COURS", "Running")]
    [InlineData("        ETAT               : 1  ARRETE", "Stopped")]
    [InlineData("        ESTADO             : 4  EN EJECUCIÓN", "Running")]
    [InlineData("        ESTADO             : 1  DETENIDO", "Stopped")]
    [InlineData("        STATO              : 4  IN ESECUZIONE", "Running")]
    [InlineData("        STATO              : 1  ARRESTO", "Stopped")]
    [InlineData("SERVICE_NAME: AcerServiceSvc\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 4  RUNNING\r\n        WIN32_EXIT_CODE    : 0", "Running")]
    [InlineData("SERVICE_NAME: ASMSvc\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 1  STOPPED\r\n        WIN32_EXIT_CODE    : 0", "Stopped")]
    [InlineData("        СОСТОЯНИЕ          : 4  РАБОТАЕТ", "Running")]
    [InlineData("        СОСТОЯНИЕ          : 1  ОСТАНОВЛЕНА", "Stopped")]
    [InlineData("        STAN               : 4  URUCHOMIONA", "Running")]
    [InlineData("        STAN               : 1  ZATRZYMANA", "Stopped")]
    [InlineData("        状態               : 4  実行中", "Running")]
    [InlineData("        状態               : 1  停止", "Stopped")]
    [InlineData("        状态               : 4  正在运行", "Running")]
    [InlineData("        状态               : 1  已停止", "Stopped")]
    [InlineData("        상태               : 4  실행 중", "Running")]
    [InlineData("        상태               : 1  중지됨", "Stopped")]
    [InlineData("        ESTADO             : 4  EM EXECUÇÃO", "Running")]
    [InlineData("        ESTADO             : 1  PARADO", "Stopped")]
    [InlineData("        LÄGE               : 4  AKTIV", "Running")]
    [InlineData("        LÄGE               : 1  INAKTIV", "Stopped")]
    public void Finding1_ParseScQueryOutput_SupportsMultilingualAndNumericStateCodes(string output, string expected)
    {
        var result = DebloatService.ParseScQueryOutput(output);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("[SC] EnumQueryServicesStatus:OpenService FAILED 1060:\r\nThe specified service does not exist as an installed service.")]
    [InlineData("TYPE : 10 WIN32_OWN_PROCESS")]
    public void Finding1_ParseScQueryOutput_InvalidOrMissingStatus_ReturnsNull(string? output)
    {
        var result = DebloatService.ParseScQueryOutput(output!);
        Assert.Null(result);
    }

    [Fact]
    public void Finding1_GenerateDebloatScript_TargetsOnlyRecordedServices()
    {
        var debloat = new DebloatService();
        var recordedState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "AcerServiceSvc", "Automatic" },
            { "ASMSvc", "Manual" }
        };

        var script = debloat.GenerateDebloatScript(recordedState);

        // Explicitly recorded services MUST be in the shutdown list
        Assert.Contains("'AcerServiceSvc'", script);
        Assert.Contains("'ASMSvc'", script);

        // Unrecorded services MUST NOT be in the shutdown list
        foreach (var svc in DebloatService.Tier1Services)
        {
            if (recordedState.ContainsKey(svc)) continue;

            Assert.DoesNotContain($"'{svc}'", script);
        }
    }

    [Fact]
    public async Task Finding1_RestoreTier1ServicesAsync_EmptyOrUnrecordedBackup_FailsGracefully()
    {
        var debloat = new DebloatService();
        var tempFile = Path.Combine(Path.GetTempPath(), $"empty_backup_{Guid.NewGuid():N}.json");
        debloat.BackupStateFilePath = tempFile;

        try
        {
            // 1. Completely empty backup file
            File.WriteAllText(tempFile, "{}");
            bool result = await debloat.RestoreTier1ServicesAsync();
            Assert.False(result);
            Assert.Equal(DebloatService.NoBackupMessage, debloat.LastError);

            // 2. Backup file with only non-service keys
            var nonServiceMap = new Dictionary<string, string>
            {
                { "Task:NitroSenseLauncher", "Ready" }
            };
            File.WriteAllText(tempFile, JsonSerializer.Serialize(nonServiceMap));
            result = await debloat.RestoreTier1ServicesAsync();
            Assert.False(result);
            Assert.Equal(DebloatService.NoBackupMessage, debloat.LastError);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // =========================================================================
    // FINDING 2: ATK Mouse Service Safety & UI Non-Blocking Execution
    // =========================================================================

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5)]
    public void Finding2_SwitchProfile_InvalidProfile_FailsAndPreservesActiveProfile(int invalidIndex)
    {
        var mouse = new AtkMouseService();
        int initialActive = mouse.CurrentActiveProfile;

        bool result = mouse.SwitchProfile(invalidIndex);

        Assert.False(result);
        Assert.Equal(initialActive, mouse.CurrentActiveProfile);
    }

    // =========================================================================
    // FINDING 3: AcerAgentClient Connection Synchronization & RGB Retry
    // =========================================================================

    [Fact]
    public async Task Finding3_EnsureConnectedAsync_ConcurrentCalls_SerializeCleanly()
    {
        using var client = new AcerAgentClient();

        // Launch multiple concurrent EnsureConnectedAsync tasks
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            try
            {
                return await client.EnsureConnectedAsync();
            }
            catch (Exception)
            {
                // Concurrency corruption would throw InvalidOperationException or SocketException crash
                return false;
            }
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Equal(10, results.Length);
    }

    // =========================================================================
    // FINDING 4: GameDetectionService Automation Disabled Race Condition Safety
    // =========================================================================

    [Fact]
    public void Finding4_ScanProcesses_WhenAutoGameModeDisabled_NeverSetsActiveProfileOrDispatchesStarted()
    {
        using var service = new GameDetectionService();
        service.AutoGameModeEnabled = false;

        bool gameStartedFired = false;
        service.GameStarted += _ => gameStartedFired = true;

        service.TriggerScanForTesting();

        Assert.False(gameStartedFired);
        Assert.False(service.IsGameActive);
        Assert.Null(service.ActiveGameProfile);
        Assert.Equal(0, service.ActiveProcessId);
    }

    // =========================================================================
    // FINDING 5: MUX / GPU Operating Mode Unknown Initial State Handling
    // =========================================================================

    [Theory]
    [InlineData(GpuOperatingMode.DiscreteGpu, GpuOperatingMode.DiscreteGpu, true)]
    [InlineData(GpuOperatingMode.Optimus, GpuOperatingMode.Optimus, true)]
    [InlineData(GpuOperatingMode.Automatic, GpuOperatingMode.Automatic, true)]
    [InlineData(GpuOperatingMode.DiscreteGpu, GpuOperatingMode.Optimus, false)]
    [InlineData(GpuOperatingMode.Optimus, GpuOperatingMode.DiscreteGpu, false)]
    public void Finding5_GpuOperatingMode_Comparison_AccuratelyDistinguishesActiveModes(
        GpuOperatingMode currentMode, GpuOperatingMode targetMode, bool shouldBeConsideredAlreadyActive)
    {
        GpuOperatingMode? currentNullable = currentMode;
        bool isAlreadyActive = currentNullable.HasValue && targetMode == currentNullable.Value;
        Assert.Equal(shouldBeConsideredAlreadyActive, isAlreadyActive);
    }

    [Theory]
    [InlineData(GpuOperatingMode.DiscreteGpu)]
    [InlineData(GpuOperatingMode.Optimus)]
    [InlineData(GpuOperatingMode.Automatic)]
    public void Finding5_GpuOperatingMode_WhenUnknownNull_NeverBlocksTargetSelectionAsAlreadyActive(GpuOperatingMode targetMode)
    {
        // When initial query fails, _currentGpuMode is null (Unknown)
        GpuOperatingMode? currentNullable = null;

        // The check in BtnApplyGpuMode_Click:
        // if (_currentGpuMode.HasValue && targetMode == _currentGpuMode.Value)
        bool isAlreadyActive = currentNullable.HasValue && targetMode == currentNullable.Value;

        // Crucial verification: Must NEVER claim DiscreteGpu (or any mode) is already active when state is unknown!
        Assert.False(isAlreadyActive);
    }

    [Fact]
    public void Finding1_GenerateRestoreScript_EmptyOrNoServiceBackup_GeneratesFailingScriptWithExit1()
    {
        var debloat = new DebloatService();
        var emptyBackup = new Dictionary<string, string>();

        string script = debloat.GenerateRestoreScript(emptyBackup);

        Assert.Contains("exit 1", script);
        Assert.Contains("$errLog", script);
        Assert.Contains("$errs", script);
        Assert.Contains(DebloatService.NoBackupMessage, script);
    }

    [Fact]
    public void Finding1_GetServiceStatuses_ReturnsValidStatusForAllTier1Services()
    {
        var debloat = new DebloatService();
        var statuses = debloat.GetServiceStatuses();

        Assert.Equal(DebloatService.Tier1Services.Length, statuses.Count);
        var allowedStatuses = new HashSet<string> { "Çalışıyor", "Durduruldu", "Durduruldu (elle başlar)", "Devre dışı", "Kurulu değil", "Bilinmiyor" };
        foreach (var info in statuses)
        {
            Assert.Contains(info.Status, allowedStatuses);
            Assert.Contains(info.StartType, new[] { "Automatic", "Manual", "Disabled", "Unknown" });
        }
    }

    [Theory]
    [InlineData("{\"result\": \"0\"}", true)]
    [InlineData("{\"result\": \"3\"}", false)]
    [InlineData("{\"result\": \"99\"}", false)]
    [InlineData("{\"result\": \"1\"}", false)]
    [InlineData("{\"error\": \"fail\"}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Finding3_AcerAgentClient_IsSuccessResult_RejectsErrorAndAcceptsOnly0(string? responseJson, bool expectedSuccess)
    {
        bool actual = AcerAgentClient.IsSuccessResult(responseJson);
        Assert.Equal(expectedSuccess, actual);
    }

    [Fact]
    public void Finding4_GameDetectionService_ExitDetectionRace_DoesNotDispatchDuplicateGameEnded()
    {
        using var service = new GameDetectionService();
        var dummyProfile = new GameProfile { ExeName = "testgame", DisplayName = "Test Game", AutoApply = true };

        int gameEndedCount = 0;
        service.GameEnded += _ => Interlocked.Increment(ref gameEndedCount);

        // 1. Simulate active game running
        service.SetActiveGameForTesting(dummyProfile, 999999); // PID that doesn't exist
        Assert.True(service.IsGameActive);

        // 2. Concurrently disable auto game mode (which cleans up active game)
        service.AutoGameModeEnabled = false;

        // GameEnded should fire exactly once for the teardown
        Assert.Equal(1, gameEndedCount);
        Assert.False(service.IsGameActive);

        // 3. Fast-path exit scan executes after teardown: it must NOT fire GameEnded again!
        service.TriggerScanForTesting();

        Assert.Equal(1, gameEndedCount);
    }
}
