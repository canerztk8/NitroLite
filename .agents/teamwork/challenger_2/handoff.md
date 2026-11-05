# Empirical Verification and Stress Test Report: Requirements R2 & R3

**Author**: Challenger 2 (`teamwork_challenger`)  
**Target Milestone**: R2 & R3 Process Ownership, Timeouts, Debloat Restore Verification  
**Verdict**: **APPROVE**  
**Overall Risk Assessment**: LOW  

---

## 1. Observation

Direct code and test observations from the repository:

### R2: `powercfg` Process Single Ownership & Timeout Handling
- **File**: `NitroLite/Core/CpuPowerService.cs`
  - **Lines 219–260 (`QueryCurrentBoostModePowerCfg`)**:
    ```csharp
    p = Process.Start(psi);
    if (p == null) return null;
    var readTask = p.StandardOutput.ReadToEndAsync();
    if (!p.WaitForExit(3000))
    {
        bool exited = false;
        try {
            p.Kill(entireProcessTree: true);
            exited = p.WaitForExit(1000) && p.HasExited;
        } catch { try { exited = p.HasExited; } catch { exited = false; } }
        if (!exited) {
            _hangingProcess = p;
            p = null; // Ownership transferred: local finally will NOT dispose _hangingProcess
        }
        return null;
    }
    if (!p.HasExited || p.ExitCode != 0) return null;
    if (!readTask.Wait(1000)) {
        try { p.Kill(entireProcessTree: true); } catch { }
        return null;
    }
    output = readTask.Result;
    finally { p?.Dispose(); }
    ```
    *Observation*: When `WaitForExit(3000)` fails to terminate within 3 seconds, `p.Kill(entireProcessTree: true)` is attempted. If the process remains alive, `_hangingProcess = p` takes exclusive ownership, and `p = null` ensures the local `finally { p?.Dispose(); }` does NOT prematurely close the process handle. Furthermore, `readTask.Wait(1000)` applies a strict 1-second timeout to asynchronous stdout stream consumption, eliminating unmonitored stream deadlocks.
  - **Lines 284–312 (`EnsureNoHangingProcess`)**:
    ```csharp
    internal bool EnsureNoHangingProcess()
    {
        if (_hangingProcess != null)
        {
            try
            {
                if (!_hangingProcess.HasExited)
                {
                    if (!_hangingProcess.WaitForExit(500) || !_hangingProcess.HasExited)
                    {
                        return false;
                    }
                }
                _hangingProcess.Dispose();
                _hangingProcess = null;
            }
            catch (InvalidOperationException)
            {
                try { _hangingProcess?.Dispose(); } catch { }
                _hangingProcess = null;
            }
            catch { return false; }
        }
        return true;
    }
    ```
    *Observation*: If a hanging process is tracked, subsequent writes (`SetBoostModeAsync`) and queries (`QueryCurrentBoostModePowerCfg`) test `EnsureNoHangingProcess()`. While alive, it polls for 500ms and returns `false`, preventing unmonitored write leaks and blocking new operations. Once the process terminates, it safely calls `_hangingProcess.Dispose()`, resets `_hangingProcess = null`, and cleanly returns `true`.
  - **Lines 417–461 (`SetBoostModePowerCfg`)**:
    *Observation*: If `/setacvalueindex` fails (returns `false`), the method immediately halts the chain, skips `/setdcvalueindex` and `/setactive`, calls `UpdateCurrentModeAfterFailure(targetAc, targetDc)`, and returns `false`.
  - **Lines 378–415 (`UpdateCurrentModeAfterSuccess` & `UpdateCurrentModeAfterFailure`)**:
    *Observation*: On failure, `QueryBoostModeExact(isAcActive)` queries the actual active hardware state. If verified, `IsModeVerified = true`; otherwise, `IsModeVerified = false`. `VerifiedCurrentMode` returns `null` when unverified, never presenting an unverified assumption as truth. When targeting AC on battery (or DC on wall power), `CurrentMode` is not assumed; the active power supply is evaluated.

### R3: Debloat Service Verified Restore & Process Protection
- **File**: `NitroLite/Core/DebloatService.cs`
  - **Lines 608–643 (`GenerateRestoreScript`)**:
    *Observation*: For services recorded as `Stopped`:
    ```powershell
    Set-Service -Name '{s}' -StartupType {canonicalType} -ErrorAction Stop;
    Stop-Service -Name '{s}' -Force -ErrorAction Stop;
    $vSvc = Get-Service -Name '{s}' -ErrorAction Stop;
    if ($vSvc.StartType -ne '{canonicalType}') { throw 'Servis başlangıç türü doğrulanamadı: {s}' };
    if ($vSvc.Status -ne 'Stopped') { throw 'Servis çalışma durumu doğrulanamadı: {s}' };
    ```
    Any failure throws an exception caught in a per-service catch block (`$errs += ('{s}: ' + $_.Exception.Message);`), allowing remaining independent services to continue restoring.
  - **Lines 645–671 (`GenerateRestoreScript` - Scheduled Tasks)**:
    *Observation*: For `Task:NitroSenseLauncher` recorded as `Disabled`:
    ```powershell
    Disable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop;
    $vTask = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop;
    if ($vTask.State -ne 'Disabled') { throw 'NitroSenseLauncher görevi devre dışı bırakılamadı veya doğrulanamadı.' };
    ```
    If the task's prior state was unknown or omitted from the backup, no scheduled task modification command is emitted.
  - **Lines 739–751 & 820–835 (`RunElevatedPowerShellAsync` & Concurrency Lock)**:
    *Observation*: `p.WaitForExit(PowerShellTimeoutMs)` (20,000ms timeout). If timed out, `_activePowerShellProcess = p` is retained, `p.ExitCode` is NOT called (preventing `InvalidOperationException`), and `LastError` is set. In `IsPreviousProcessRunning()`, any subsequent call to `ApplyTier1DebloatAsync()` or `RestoreTier1ServicesAsync()` while `_activePowerShellProcess` is alive immediately fails with `"Önceki PowerShell işlemi hâlâ devam ediyor (durum belirsiz). Lütfen işlemin tamamlanmasını bekleyin."`
  - **Lines 836–893 (`ApplyTier1DebloatAsync` - Baseline Protection)**:
    *Observation*: If `hasExistingValidBackup` is true, the method bypasses re-sampling the system, preventing a debloated system (where services are already Disabled/Stopped) from overwriting the original OEM baseline. If a backup file exists on disk but is invalid or corrupted, it immediately aborts: `"Apply debloat aborted: an existing backup file exists at '{BackupStateFilePath}' but its content is invalid or corrupted. Please inspect or restore it before applying debloat."`
  - **Lines 957–974 (`RestoreTier1ServicesAsync` - Backup Retention)**:
    *Observation*: `BackupStateFilePath` is deleted **only** if `success == true` (exit code 0 and no error logs). On partial restore failure or error, the file remains intact on disk for retry.

### Build & Empirical Test Execution Commands
1. **Release Build**:
   - Command: `dotnet build NitroLite.sln -c Release`
   - Output: `Build succeeded. 0 Warning(s), 0 Error(s). Time Elapsed 00:00:04.46`
2. **CpuPowerService Filtered Tests**:
   - Command: `dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~CpuPowerService"`
   - Output: `Passed! - Failed: 0, Passed: 21, Skipped: 0, Total: 21, Duration: 6 s`
3. **Debloat Filtered Tests**:
   - Command: `dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~Debloat"`
   - Output: `Passed! - Failed: 0, Passed: 30, Skipped: 0, Total: 30, Duration: 310 ms`
4. **SafeVerificationEndToEndTests Filtered Tests**:
   - Command: `dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~SafeVerificationEndToEndTests"`
   - Output: `Passed! - Failed: 0, Passed: 16, Skipped: 0, Total: 16, Duration: 2 s`
5. **Adversarial Empirical Stress Suite (`NitroLite.Tests/AdversarialR2R3EmpiricalTests.cs`)**:
   - Command: `dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~AdversarialR2R3EmpiricalTests"`
   - Output: `Passed! - Failed: 0, Passed: 9, Skipped: 0, Total: 9, Duration: 14 s`
6. **Full Test Suite**:
   - Command: `dotnet test NitroLite.Tests/NitroLite.Tests.csproj`
   - Output: `Passed! - Failed: 0, Passed: 606, Skipped: 0, Total: 606, Duration: 31 s`

---

## 2. Logic Chain

1. **R2 Process Ownership & Timeout**:
   - *Observation*: `QueryCurrentBoostModePowerCfg` lines 241-243 transfer process handle to `_hangingProcess` and set `p = null` before local `finally` executes.
   - *Logic*: Because `p` is nullified, `finally { p?.Dispose(); }` cannot close or invalidate the handle while the process is still running. The process ownership is centralized in `_hangingProcess`.
   - *Observation*: In `R2_HangingProcess_ConcurrentSetBoostCalls_AllSafelyBlockedAndRecovered`, 10 concurrent tasks invoked `SetBoostModeAsync` while a background process was running. All 10 returned `false`, `_hangingProcess` handle remained open and valid, and `IsModeVerified` was `false`.
   - *Logic*: Concurrent writes are safely blocked under hanging conditions, avoiding resource leaks or conflicting powercfg invocations.
   - *Observation*: Upon killing the hanging process, `EnsureNoHangingProcess()` detected termination, disposed the handle, nulled `_hangingProcess`, and permitted subsequent writes.
   - *Logic*: System self-recovers immediately upon process exit without process restart or orphan handle accumulation.

2. **R2 Chain Failure Halting & Query Verification**:
   - *Observation*: In `R2_SetBoostModePowerCfg_ChainFailureStopsSubsequentCommands`, forcing `/setacvalueindex` to exit code 1 halted the write chain: `/setdcvalueindex` and `/setactive` were never issued.
   - *Logic*: Halting writes on initial failure prevents corrupting or desynchronizing power plan states.
   - *Observation*: After the write failure, `UpdateCurrentModeAfterFailure` issued a `/query` command to inspect active state.
   - *Logic*: Satisfies R2's requirement: "Zincirde bir yazma başarısızsa kalan yazmaları durdur; kısmi değişiklik ihtimalini sorgulayarak değerlendir."

3. **R3 Debloat Concurrency Locking & 20s Timeout**:
   - *Observation*: In `R3_DebloatService_SimultaneousApplyAndRestoreDuringTimeout_BothBlockedCleanly`, 10 concurrent `ApplyTier1DebloatAsync` and 10 concurrent `RestoreTier1ServicesAsync` calls were issued while an active process was running.
   - *Logic*: Every concurrent call was rejected with `"Önceki PowerShell işlemi hâlâ devam ediyor"`, proving total mutual exclusion and process lifecycle protection.

4. **R3 Verified Restore for Stopped Services & Tasks**:
   - *Observation*: In `R3_DebloatService_GenerateRestoreScript_VerifiesAllStoppedServicesAndDisabledTasks`, `GenerateRestoreScript` generated `Stop-Service -Force` followed by `$vSvc.Status -ne 'Stopped'` for stopped services, and `Disable-ScheduledTask` followed by `$vTask.State -ne 'Disabled'` for disabled tasks.
   - *Logic*: Both service running states and scheduled tasks are actively verified upon restoration rather than assumed.

5. **R3 Backup Retention on Partial Failures & Baseline Preservation**:
   - *Observation*: In `R3_DebloatService_PartialRestoreFailures_RetainsBackupAndAggregatesErrors`, when two services failed during restore, `RestoreTier1ServicesAsync` returned `false`, aggregated both failure messages into `LastError`, and left the backup file on disk.
   - *Logic*: Backup retention guarantees that partial failures can be corrected and retried without losing baseline configuration.
   - *Observation*: In `R3_DebloatService_ExistingValidBackup_PreservesOriginalBaselineAgainstResampling`, re-running `ApplyTier1DebloatAsync` against an already debloated system did not overwrite the original backup with `Disabled`.
   - *Logic*: OEM baseline configuration is strictly protected against corruption by repeated debloat passes.

---

## 3. Caveats

1. **Hardware Powercfg Registry State**: In test environments, `BypassNativePowerCalls` and mock process runners (`ProcessInfoFactory`, `PowerShellRunner`) were used to verify logic without altering host machine power plan GUIDs or Windows services. Full end-to-end execution on live Windows OS relies on the same production codepaths verified here.
2. **UAC Elevation Prompt**: `RunElevatedPowerShellAsync` requires administrative elevation (`Verb = "runas"`) when executed in production without `PowerShellRunner` injected. If the user rejects the UAC prompt, the method gracefully returns `false` with an appropriate error.

---

## 4. Conclusion

All acceptance criteria for Requirements R2 (`powercfg` Process Management) and R3 (Debloat Verified Restoration and Concurrency Protection) are **fully satisfied and empirically proven**. 

- Single process ownership is preserved across timeouts and error paths.
- Concurrency locks prevent duplicate executions during process timeouts.
- Restore scripts accurately enforce `Stopped` status, startup type, and `Disabled` scheduled tasks.
- OEM baselines are protected against re-sampling, and backups are strictly preserved upon partial failures.
- Zero warnings and zero errors in Release build; 606/606 tests pass.

**Verdict: APPROVE**

---

## 5. Verification Method

To independently verify all findings and test suites:

```powershell
# 1. Verify clean Release build (0 errors, 0 warnings)
dotnet build NitroLite.sln -c Release

# 2. Verify CpuPowerService filtered tests (21 tests)
dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~CpuPowerService"

# 3. Verify Debloat filtered tests (30 tests)
dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~Debloat"

# 4. Verify SafeVerificationEndToEndTests (16 tests)
dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~SafeVerificationEndToEndTests"

# 5. Verify Adversarial R2 & R3 Empirical Stress Tests (9 tests)
dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~AdversarialR2R3EmpiricalTests"

# 6. Verify entire project test suite (606 tests)
dotnet test NitroLite.Tests/NitroLite.Tests.csproj
```

**Invalidation Conditions**:
- If `_hangingProcess` is disposed while still active, or subsequent `SetBoostModeAsync` calls execute while `_hangingProcess` is alive.
- If `ApplyTier1DebloatAsync` overwrites an existing backup file with `Disabled` values when re-executed.
- If `RestoreTier1ServicesAsync` deletes the backup file when one or more services fail to restore.
