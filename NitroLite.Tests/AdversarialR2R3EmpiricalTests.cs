using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Adversarial Empirical Stress Test Suite for Requirements R2 and R3.
/// Authored by Challenger 2 (teamwork_challenger).
/// Tests powercfg single ownership, process kill/timeout, hanging process recovery,
/// debloat restore Stopped state, task Disabled verification, 20s timeout concurrency locks,
/// and backup retention on partial failures.
/// </summary>
public class AdversarialR2R3EmpiricalTests
{
    // =========================================================================
    // R2: PowerCfg Process Ownership, Timeouts, and AC/DC Target Verification
    // =========================================================================

    [Fact]
    public async Task R2_HangingProcess_ConcurrentSetBoostCalls_AllSafelyBlockedAndRecovered()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);

        // Spawn a background sleeping process to simulate a hanging powercfg process
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

            // Verify single ownership and tracking
            Assert.False(service.EnsureNoHangingProcess());
            Assert.NotNull(service._hangingProcess);
            Assert.False(service._hangingProcess.HasExited);

            // Execute 10 concurrent SetBoostModeAsync calls
            var tasks = new List<Task<bool>>();
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(service.SetBoostModeAsync(CpuBoostMode.Disabled));
            }

            var results = await Task.WhenAll(tasks);

            // All concurrent writes must be safely rejected without crashing or deadlocking
            Assert.All(results, r => Assert.False(r));
            Assert.False(service.IsModeVerified);
            Assert.Null(service.VerifiedCurrentMode);

            // Terminate hanging process
            hangingProc.Kill(entireProcessTree: true);
            hangingProc.WaitForExit(3000);

            // Verify recovery: EnsureNoHangingProcess must now clean up handle and return true
            Assert.True(service.EnsureNoHangingProcess());
            Assert.Null(service._hangingProcess);

            // Subsequent write must now succeed
            bool recoveredWrite = await service.SetBoostModeAsync(CpuBoostMode.Aggressive);
            Assert.True(recoveredWrite);
            Assert.Equal(CpuBoostMode.Aggressive, service.CurrentMode);
            Assert.True(service.IsModeVerified);
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
    public void R2_RunPowerCfg_TimeoutAndKill_TransfersOwnershipToHangingProcessWithoutDisposing()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);

        // Factory creates a process that runs for 10 seconds (exceeding the 3s timeout)
        service.ProcessInfoFactory = args => new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 10",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            // RunPowerCfg should timeout after 3s, attempt kill, and transfer ownership if still running
            bool result = service.RunPowerCfg("/test_timeout");
            Assert.False(result);

            // If the process could not exit immediately, _hangingProcess must hold it
            if (service._hangingProcess != null)
            {
                Assert.False(service._hangingProcess.SafeHandle.IsClosed);
                Assert.False(service._hangingProcess.SafeHandle.IsInvalid);
            }
        }
        finally
        {
            try
            {
                if (service._hangingProcess != null && !service._hangingProcess.HasExited)
                {
                    service._hangingProcess.Kill(entireProcessTree: true);
                    service._hangingProcess.WaitForExit(2000);
                    service._hangingProcess.Dispose();
                    service._hangingProcess = null;
                }
            }
            catch { }
        }
    }

    [Fact]
    public void R2_QueryBoostModePowerCfg_ParsesAcAndDcHexIndicesCorrectly()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);

        service.MockPowerCfgQueryOutput =
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\n" +
            "  Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)\n" +
            "    Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7  (Processor performance boost mode)\n" +
            "      Current AC Power Setting Index: 0x00000000\n" +
            "      Current DC Power Setting Index: 0x00000004\n";

        var acMode = service.QueryCurrentBoostModePowerCfg(isAc: true);
        var dcMode = service.QueryCurrentBoostModePowerCfg(isAc: false);

        Assert.Equal(CpuBoostMode.Disabled, acMode);
        Assert.Equal(CpuBoostMode.Efficient, dcMode);
    }

    [Fact]
    public void R2_SetBoostModePowerCfg_ChainFailureStopsSubsequentCommands()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        var executedArgs = new List<string>();

        // First command fails with exit code 1
        service.ProcessInfoFactory = args =>
        {
            executedArgs.Add(args);
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c exit 1",
                UseShellExecute = false,
                CreateNoWindow = true
            };
        };

        bool ok = service.SetBoostModePowerCfg(CpuBoostMode.Disabled, targetAc: true, targetDc: true);
        Assert.False(ok);

        // Verification of R2:
        // 1. Remaining writes (/setdcvalueindex and /setactive) MUST be halted!
        Assert.DoesNotContain(executedArgs, a => a.Contains("/setdcvalueindex"));
        Assert.DoesNotContain(executedArgs, a => a.Contains("/setactive"));

        // 2. The first failed write was /setacvalueindex
        Assert.Contains(executedArgs, a => a.Contains("/setacvalueindex"));

        // 3. R2 requires evaluating partial changes by querying: UpdateCurrentModeAfterFailure executes /query
        Assert.Contains(executedArgs, a => a.Contains("/query"));
    }

    // =========================================================================
    // R3: Debloat Service Concurrency, Restore Verification, and Backup Protection
    // =========================================================================

    [Fact]
    public async Task R3_DebloatService_SimultaneousApplyAndRestoreDuringTimeout_BothBlockedCleanly()
    {
        var service = new DebloatService();
        var tempBackup = Path.Combine(Path.GetTempPath(), $"debloat_concurrent_test_{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(tempBackup, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["AcerServiceSvc"] = "Manual",
                ["AcerServiceSvc:Status"] = "Running"
            }));
            service.BackupStateFilePath = tempBackup;

            // Simulate active un-exited PowerShell process
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

                // Run 10 Apply and 10 Restore calls concurrently
                var tasks = new List<Task<bool>>();
                for (int i = 0; i < 10; i++)
                {
                    tasks.Add(service.ApplyTier1DebloatAsync());
                    tasks.Add(service.RestoreTier1ServicesAsync());
                }

                var results = await Task.WhenAll(tasks);

                // Every single concurrent call must be rejected cleanly
                Assert.All(results, r => Assert.False(r));
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

            Assert.False(service.IsOperationInProgress);
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
        }
    }

    [Fact]
    public void R3_DebloatService_GenerateRestoreScript_VerifiesAllStoppedServicesAndDisabledTasks()
    {
        var service = new DebloatService();
        var backup = new Dictionary<string, string>
        {
            ["AcerServiceSvc"] = "Manual",
            ["AcerServiceSvc:Status"] = "Stopped",
            ["ASMSvc"] = "Automatic",
            ["ASMSvc:Status"] = "Running",
            ["AcerEZSvc"] = "Disabled",
            ["AcerEZSvc:Status"] = "Stopped",
            ["Task:NitroSenseLauncher"] = "Disabled"
        };

        string script = service.GenerateRestoreScript(backup);

        // Verification for AcerServiceSvc (Stopped)
        Assert.Contains("Set-Service -Name 'AcerServiceSvc' -StartupType Manual", script);
        Assert.Contains("Stop-Service -Name 'AcerServiceSvc' -Force", script);
        Assert.Contains("$vSvc.Status -ne 'Stopped'", script);

        // Verification for ASMSvc (Running)
        Assert.Contains("Set-Service -Name 'ASMSvc' -StartupType Automatic", script);
        Assert.Contains("Start-Service -Name 'ASMSvc'", script);
        Assert.Contains("$vSvc.Status -ne 'Running'", script);

        // Verification for AcerEZSvc (Disabled, Stopped)
        Assert.Contains("Set-Service -Name 'AcerEZSvc' -StartupType Disabled", script);
        Assert.Contains("Stop-Service -Name 'AcerEZSvc' -Force", script);
        Assert.Contains("$vSvc.Status -ne 'Stopped'", script);

        // Verification for Task (Disabled)
        Assert.Contains("Disable-ScheduledTask -TaskName 'NitroSenseLauncher'", script);
        Assert.Contains("$vTask.State -ne 'Disabled'", script);

        // Unrecorded services must NOT appear
        Assert.DoesNotContain("AcerDeviceInfoAgentService", script);
        Assert.DoesNotContain("AcerDeviceEnablingServiceV2", script);
    }

    [Fact]
    public async Task R3_DebloatService_PartialRestoreFailures_RetainsBackupAndAggregatesErrors()
    {
        var service = new DebloatService();
        var tempBackup = Path.Combine(Path.GetTempPath(), $"debloat_partial_fail_{Guid.NewGuid():N}.json");
        string? errLog = null;

        try
        {
            var backupData = new Dictionary<string, string>
            {
                ["AcerServiceSvc"] = "Manual",
                ["AcerServiceSvc:Status"] = "Stopped",
                ["ASMSvc"] = "Automatic",
                ["ASMSvc:Status"] = "Running",
                ["Task:NitroSenseLauncher"] = "Disabled"
            };
            File.WriteAllText(tempBackup, JsonSerializer.Serialize(backupData));
            service.BackupStateFilePath = tempBackup;

            // Simulate multi-service failure report written to error log
            service.PowerShellRunner = async (script, elevated) =>
            {
                // The log path is randomised per run; take it from the generated script.
                errLog = System.Text.RegularExpressions.Regex.Match(script, @"\$errLog = '([^']+)'").Groups[1].Value.Replace("''", "'");
                string errors =
                    "AcerServiceSvc: Servis durdurulamadı: Access is denied.\n" +
                    "Task:NitroSenseLauncher: NitroSenseLauncher görevi devre dışı bırakılamadı.";
                await File.WriteAllTextAsync(errLog, errors);
                return 1;
            };

            bool ok = await service.RestoreTier1ServicesAsync();

            // Must fail, report both errors, and retain backup file
            Assert.False(ok);
            Assert.NotNull(service.LastError);
            Assert.Contains("AcerServiceSvc: Servis durdurulamadı", service.LastError);
            Assert.Contains("Task:NitroSenseLauncher: NitroSenseLauncher görevi devre dışı bırakılamadı", service.LastError);
            Assert.True(File.Exists(tempBackup), "Backup file MUST NOT be deleted on partial restore failure!");
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
            if (errLog != null && File.Exists(errLog)) File.Delete(errLog);
        }
    }

    [Fact]
    public async Task R3_DebloatService_CorruptedOrInvalidBackup_ProtectsAgainstOverwritingAndSilentDeletion()
    {
        var service = new DebloatService();
        var tempBackup = Path.Combine(Path.GetTempPath(), $"debloat_corrupt_{Guid.NewGuid():N}.json");

        try
        {
            // Corrupted JSON content
            File.WriteAllText(tempBackup, "{ this is not valid JSON content }");
            service.BackupStateFilePath = tempBackup;

            // 1. Restore must fail and retain file
            bool restoreOk = await service.RestoreTier1ServicesAsync();
            Assert.False(restoreOk);
            Assert.Contains("corrupted", service.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(tempBackup));

            // 2. Apply must abort rather than overwriting the corrupted backup
            bool applyOk = await service.ApplyTier1DebloatAsync();
            Assert.False(applyOk);
            Assert.Contains("corrupted", service.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(tempBackup));
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
        }
    }

    [Fact]
    public async Task R3_DebloatService_ExistingValidBackup_PreservesOriginalBaselineAgainstResampling()
    {
        var service = new DebloatService();
        var tempBackup = Path.Combine(Path.GetTempPath(), $"debloat_baseline_{Guid.NewGuid():N}.json");

        try
        {
            var originalBaseline = new Dictionary<string, string>
            {
                ["AcerServiceSvc"] = "Automatic",
                ["AcerServiceSvc:Status"] = "Running",
                ["ASMSvc"] = "Manual",
                ["ASMSvc:Status"] = "Stopped",
                ["Task:NitroSenseLauncher"] = "Ready"
            };
            File.WriteAllText(tempBackup, JsonSerializer.Serialize(originalBaseline));
            service.BackupStateFilePath = tempBackup;

            // Simulate that the current system is currently disabled
            service.ServiceStartupTypeQuerySeam = s => "Disabled";
            service.ServiceStatusQuerySeam = s => "Stopped";
            service.ScheduledTaskStateQuerySeam = t => "Disabled";
            service.PowerShellRunner = (script, elevated) => Task.FromResult(0);

            // Re-apply debloat
            bool ok = await service.ApplyTier1DebloatAsync();
            Assert.True(ok);

            // Verify file content on disk: original values must remain unchanged!
            var currentJson = File.ReadAllText(tempBackup);
            var reloaded = JsonSerializer.Deserialize<Dictionary<string, string>>(currentJson)!;

            Assert.Equal("Automatic", reloaded["AcerServiceSvc"]);
            Assert.Equal("Running", reloaded["AcerServiceSvc:Status"]);
            Assert.Equal("Manual", reloaded["ASMSvc"]);
            Assert.Equal("Stopped", reloaded["ASMSvc:Status"]);
            Assert.Equal("Ready", reloaded["Task:NitroSenseLauncher"]);
        }
        finally
        {
            if (File.Exists(tempBackup)) File.Delete(tempBackup);
        }
    }
}
