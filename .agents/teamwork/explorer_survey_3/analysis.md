# Deep Architectural Analysis: Debloat Restore Verification & Safe Testing Architecture (R3 & R4)

**Explorer**: Survey Explorer 3 (`teamwork_explorer`)  
**Workspace**: `C:\Users\caner\Documents\antigravity\eager-shannon`  
**Date**: 2026-11-05  
**Target Areas**: Requirement R3 (Debloat Verified Restore & Lifecycle) & Requirement R4 (Safe Testing Architecture & Test Seams)

---

## 1. Executive Summary

This investigation delivers a comprehensive, code-level analysis of **Requirement R3** (Debloat verified restoration, state management, and PowerShell lifecycle) and **Requirement R4** (safe test verification without touching real hardware, mouse profiles, power plans, or services).

### Core Findings:
1. **Critical Flaw in Debloat Restore Logic (R3)**:
   In `DebloatService.GenerateRestoreScript`, when a service's recorded state is `"Stopped"`, the generator **never emits `Stop-Service`** and **never verifies `$vSvc.Status -eq 'Stopped'`**. If a service is currently running when restore is invoked, it is left running and falsely reported as restored. Furthermore, `Set-Service -StartupType` must precede `Start-Service` to prevent Windows error 1058 (`ERROR_SERVICE_DISABLED`).
2. **Scheduled Task Verification Gap (R3)**:
   `NitroSenseLauncher` scheduled task restoration handles active states (`Ready`/`Enabled`), but if the recorded state was `"Disabled"`, it does not enforce or verify that the task remains disabled.
3. **PowerShell Process Timeout & Concurrency Vulnerability (R3)**:
   In `RunElevatedPowerShellAsync`, when `p.WaitForExit(20000)` times out after 20 seconds, reading `p.ExitCode` throws `InvalidOperationException` ("Process must exit before requested information can be determined"). The process is abandoned, `using` disposes its handle while it still runs in the background, and `_debloatLock` is released. If the user clicks Apply or Restore again, a second elevated PowerShell process is spawned concurrently, causing race conditions and state corruption.
4. **Dangerous Live Hardware Invocations in Test Suite (R4)**:
   - `VerifiedDefectsRegressionTests.cs` (line 1005: `CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock`) directly calls `service.SetBoostModeAsync()`, which invokes Win32 P/Invoke `PowerWriteACValueIndex` and `PowerSetActiveScheme` on the developer's physical Windows power schemes.
   - `AtkMouseServiceTests.cs` (line 95: `LiveHardware_QueryAndSwitch_IfConnected`) actively switches physical mouse profiles via USB HID if an ATK mouse is plugged in.
   - `VerifiedDefectsRegressionTests.cs` (lines 502, 525) issues raw HID writes to connected USB devices.
5. **Debloat Test Seam Deficit (R4)**:
   `DebloatService` currently lacks seams for PowerShell execution and service/task status queries, preventing end-to-end verification of restore error handling, partial failure backup retention, and process lifecycle locking.

---

## 2. Requirement R3: Debloat Service Architecture & Bug Analysis

### 2.1 Backup File Format, Persistence & Atomicity

- **Location**: `DebloatService.BackupStateFilePath` (default: `AppDomain.CurrentDomain.BaseDirectory\debloat_backup_state.json`).
- **Data Structure**: `Dictionary<string, string>` (OrdinalIgnoreCase).
  - Service Startup Types: `"{ServiceName}"` -> `"Automatic" | "Manual" | "Disabled"`
  - Service Runtime States: `"{ServiceName}:Status"` -> `"Running" | "Stopped"`
  - Scheduled Task State: `"Task:NitroSenseLauncher"` -> `"Ready" | "Disabled" | "Unknown"`
- **Persistence Mechanism**:
  Written atomically via temporary file:
  ```csharp
  var tmp = BackupStateFilePath + ".tmp";
  File.WriteAllText(tmp, json);
  File.Move(tmp, BackupStateFilePath, overwrite: true);
  ```
- **Vulnerabilities Identified**:
  1. *Corrupt Baseline Overwrite*: In `ApplyTier1DebloatAsync`, if debloat has already been applied or partially restored, running `ApplyTier1DebloatAsync` again reads startup types and statuses from the *currently modified* system. If any service key is missing from `existingBackup`, it captures the disabled/stopped state as the baseline.
  2. *Silent Exception Swallowing*: In `LoadBackupState()`, `JsonException` or file I/O exceptions are swallowed in `catch { }`, returning an empty dictionary. This prevents differentiating between "no backup exists" and "backup file is corrupted on disk".

### 2.2 Service Start Type vs. Running/Stopped State Restoration & Order of Operations

#### Current Implementation (`DebloatService.cs` lines 492-515):
```csharp
bool shouldStart = false;
if (backupState.TryGetValue(s + ":Status", out var recordedStatus))
{
    shouldStart = string.Equals(recordedStatus, "Running", StringComparison.OrdinalIgnoreCase);
}
else
{
    shouldStart = string.Equals(canonicalType, "Automatic", StringComparison.OrdinalIgnoreCase);
}

sb.Append("try { ");
sb.Append($"$svc = Get-Service -Name '{s}' -ErrorAction Stop; ");
sb.Append($"if (-not $svc) {{ throw 'Servis bulunamadı: {s}' }}; ");
sb.Append($"Set-Service -Name '{s}' -StartupType {canonicalType} -ErrorAction Stop; ");
if (shouldStart)
{
    sb.Append($"Start-Service -Name '{s}' -ErrorAction Stop; ");
}
sb.Append($"$vSvc = Get-Service -Name '{s}' -ErrorAction Stop; ");
sb.Append($"if ($vSvc.StartType -ne '{canonicalType}') {{ throw 'Servis başlangıç türü doğrulanamadı: {s}' }}; ");
if (shouldStart)
{
    sb.Append($"if ($vSvc.Status -ne 'Running') {{ throw 'Servis çalışma durumu doğrulanamadı: {s}' }}; ");
}
sb.Append("} catch { ");
sb.Append($"$errs += ('{s}: ' + $_.Exception.Message); ");
sb.Append("}; ");
```

#### Bugs & Flaws:
1. **Missing `Stop-Service`**: If `recordedStatus == "Stopped"`, `shouldStart` is `false`. The script does nothing to stop the service if it is currently `Running`.
2. **Missing `Stopped` Verification**: If `recordedStatus == "Stopped"`, the script only checks `$vSvc.StartType -ne '{canonicalType}'`. It never checks `$vSvc.Status -ne 'Stopped'`. If the service is running, it reports total success and deletes the backup!
3. **Strict Order of Operations**:
   - **For Target = `Running`**:
     If a service is currently `Disabled`, calling `Start-Service` fails immediately with Win32 Error 1058. Therefore, `Set-Service -StartupType {canonicalType}` **must** execute before `Start-Service`.
   - **For Target = `Stopped`**:
     If a service is currently `Running`, call `Stop-Service -Name '{s}' -Force -ErrorAction Stop`, then `Set-Service -Name '{s}' -StartupType {canonicalType} -ErrorAction Stop`.
   - **Verification Phase**:
     Query `$vSvc = Get-Service -Name '{s}' -ErrorAction Stop`:
     - Verify `$vSvc.StartType -eq '{canonicalType}'`.
     - If target was `Running`: verify `$vSvc.Status -eq 'Running'`.
     - If target was `Stopped`: verify `$vSvc.Status -eq 'Stopped'`.

### 2.3 Scheduled Tasks Status Verification

#### Current Implementation (`DebloatService.cs` lines 521-535):
```csharp
if (backupState != null && backupState.TryGetValue("Task:NitroSenseLauncher", out var taskState))
{
    if (IsActiveTaskState(taskState))
    {
        sb.Append("try { ");
        sb.Append("  $task = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
        sb.Append("  if (-not $task) { throw 'NitroSenseLauncher görevi bulunamadı.' }; ");
        sb.Append("  Enable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
        sb.Append("  $vTask = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
        sb.Append("  if ($vTask.State -eq 'Disabled') { throw 'NitroSenseLauncher görevi etkinleştirilemedi veya doğrulanamadı.' }; ");
        sb.Append("} catch { ");
        sb.Append("  $errs += ('Task:NitroSenseLauncher: ' + $_.Exception.Message); ");
        sb.Append("}; ");
    }
}
```

#### Bugs & Flaws:
- If `taskState` was `"Disabled"`, `IsActiveTaskState` is `false`. The script completely ignores the task. If another process or user enabled it, it remains enabled without verification.
- Requirement R3 explicitly states: *"Zamanlanmış görevin kaydedilmiş etkin veya devre dışı durumunu doğrula; özgün durum bilinmiyorsa görevi değiştirme."*
- **Required Behavior**:
  - If `taskState` is active (`Ready`/`Enabled`): enable it and verify `$vTask.State -ne 'Disabled'`.
  - If `taskState` is `"Disabled"`: disable it (`Disable-ScheduledTask`) and verify `$vTask.State -eq 'Disabled'`.
  - If `taskState` is `"Unknown"` or omitted: do not modify the task.
  - If task is missing: `Get-ScheduledTask` throws `'NitroSenseLauncher görevi bulunamadı.'`, caught in `$errs`.

### 2.4 Error Collection Architecture & Granular Reporting

- Each service and task operation is isolated inside `try { ... } catch { $errs += ('{id}: ' + $_.Exception.Message) }`.
- If one service fails (e.g. `ASMSvc` fails to start), the script **continues** to restore the remaining services (`AcerEZSvc`, `AcerServiceSvc`, etc.).
- At the end of the script:
  ```powershell
  if ($errs.Count -gt 0) {
      $errs -join "`n" | Out-File -FilePath $errLog -Encoding utf8;
      exit 1;
  } else {
      exit 0;
  }
  ```
- In C#: `RunElevatedPowerShellAsync` reads `$errLog`, populates `LastError`, and returns `false`.
- In `MainWindow.xaml.cs`: The error string is surfaced directly to the user in a toast notification.

### 2.5 Backup Retention on Partial Failure & Protection Against Baseline Corruption

- In `RestoreTier1ServicesAsync`:
  ```csharp
  bool success = await RunElevatedPowerShellAsync(script, errLog);
  if (success)
  {
      if (File.Exists(BackupStateFilePath)) File.Delete(BackupStateFilePath);
      var bak = BackupStateFilePath + ".bak";
      if (File.Exists(bak)) File.Delete(bak);
  }
  return success;
  ```
- **Rule**: If `success == false`, the backup file **MUST NOT** be deleted.
- **Rule**: If a restore was partially successful, running `RestoreTier1ServicesAsync` a second time **MUST** use the exact same original backup.
- **Rule**: If `ApplyTier1DebloatAsync` is called when a valid backup already exists on disk, it **MUST NOT** generate a new backup from the currently modified/disabled services. It must reject or retain the existing backup.

### 2.6 Corrupt & Legacy Backup Handling

1. **Corrupted JSON Syntax**:
   If `debloat_backup_state.json` contains invalid JSON or truncated bytes, `LoadBackupState()` currently returns `{}`. `RestoreTier1ServicesAsync` fails gracefully: `"Restore aborted: backup state is empty or contains no recorded services."` The corrupt file is preserved on disk for manual recovery or investigation.
2. **Missing Service Records (Legacy Backups)**:
   If a backup file has only a subset of Tier 1 services (e.g. created by an earlier version or manual edit):
   - Unrecorded services must **not** be forced to `Automatic`.
   - However, if the backup was intended to cover installed debloated services and records are missing or corrupt, it must be flagged as incomplete.
3. **Invalid Startup Type Values**:
   `ValidateStartupType` strictly whitelists `Automatic`, `Manual`, `Disabled`. Any other string (e.g. injection attempts, typos) throws `ArgumentException`, causing `RestoreTier1ServicesAsync` to abort with error and preserve the backup.

### 2.7 PowerShell Administrator Process Lifecycle, 20-Second Timeout, and Concurrency Locking

#### Current Implementation (`DebloatService.cs` lines 542-592):
```csharp
internal async Task<bool> RunElevatedPowerShellAsync(string script, string? errorLogPath = null)
{
    LastError = null;
    return await Task.Run(() =>
    {
        try
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(script);
            var encoded = Convert.ToBase64String(bytes);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var p = Process.Start(psi);
            p?.WaitForExit(20000);
            var exitCode = p?.ExitCode ?? -1;
...
```

#### The Timeout Catastrophe:
1. If the PowerShell process takes longer than 20,000 ms, `p.WaitForExit(20000)` returns `false`.
2. The code ignores the `bool` return value!
3. The next statement `var exitCode = p?.ExitCode ?? -1;` tries to read `p.ExitCode`.
4. Because the process is still running, Windows throws:
   `InvalidOperationException: Process must exit before requested information can be determined.`
5. The catch block catches this and sets `LastError = ex.Message`.
6. The `using var p` block exits and disposes `p`.
7. `ApplyTier1DebloatAsync` or `RestoreTier1ServicesAsync` executes its `finally` block: `_debloatLock.Release()`.
8. The elevated `powershell.exe` process is **still executing in the background**.
9. The user sees an error toast, clicks "Apply" or "Restore" again, and `_debloatLock.WaitAsync()` succeeds!
10. A second elevated PowerShell script is launched concurrently, competing for service control and writing to the same error log and backup file!

#### Required Lifecycle Locking Fix:
1. Maintain an internal `Process? _activePowerShellProcess` reference.
2. In both `ApplyTier1DebloatAsync` and `RestoreTier1ServicesAsync`, verify before acquiring or starting:
   ```csharp
   if (_activePowerShellProcess != null)
   {
       if (!_activePowerShellProcess.HasExited)
       {
           LastError = "Önceki PowerShell işlemi hâlâ devam ediyor (durum belirsiz). Lütfen işlemin tamamlanmasını bekleyin.";
           return false;
       }
       _activePowerShellProcess.Dispose();
       _activePowerShellProcess = null;
   }
   ```
3. When `Process.Start` creates `p`, store `_activePowerShellProcess = p;`.
4. If `!p.WaitForExit(20000)`:
   - Do **NOT** read `p.ExitCode`.
   - Do **NOT** dispose `p`.
   - Set `LastError = "PowerShell işlemi 20 saniye içinde tamamlanamadı ve arka planda devam ediyor. Süreç sonlanana kadar yeni işlem başlatılamaz.";`
   - Return `false`.
5. Only when `p.HasExited` is true do we read `p.ExitCode`, inspect `$errLog`, dispose `p`, and set `_activePowerShellProcess = null`.

---

## 3. Requirement R4: Safe Testing Architecture & Test Seams

### 3.1 Overview of `NitroLite.Tests` Architecture
The `NitroLite.Tests` suite currently contains 581 xUnit tests across 24 test files. It compiles as an executable (`<OutputType>Exe</OutputType>`) targeting `net10.0-windows` with WPF support. It can be run either via `dotnet test` or directly via `Program.Main()`.

### 3.2 Direct Live Hardware/Service Invocation in Existing Tests (Dangerous Tests Pinpointed)

| Test File | Line | Test Method | Problematic Operation | Impact on Host Machine |
|---|---|---|---|---|
| `VerifiedDefectsRegressionTests.cs` | 1005 | `CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock` | Calls `service.SetBoostModeAsync()` 3 times concurrently on `new CpuPowerService()`. | Invokes native P/Invoke `PowerWriteACValueIndex` and `PowerSetActiveScheme` on Windows power schemes! Writes to registry `HKLM\SYSTEM\...\PowerSettings`. |
| `AtkMouseServiceTests.cs` | 95 | `LiveHardware_QueryAndSwitch_IfConnected` | Calls `service.QueryCurrentProfile()`, then `service.SwitchProfile(target)`. | If user has an ATK mouse plugged in, actively switches the physical mouse profile between 1 and 2! |
| `VerifiedDefectsRegressionTests.cs` | 502 | `Defect5_AtkMouseService_SwitchProfile_SafeExecution` | Calls `service.SwitchProfile(profile)` for profiles 1..4. | Directly attempts raw HID packet writes to connected USB mouse endpoints. |
| `VerifiedDefectsRegressionTests.cs` | 525 | `Defect5_AtkMouseService_QueryCurrentProfile_SafeExecution` | Calls `service.QueryCurrentProfile()`. | Sends raw HID queries to physical mouse hardware. |
| `CpuPowerServiceTests.cs` | 61 | `ServiceInitialization_InstantiatesAndQueriesCurrentMode` | Calls `new CpuPowerService()`. | Calls `UnhideSetting()` (HKLM registry write) and Win32 `PowerGetActiveScheme`. |

### 3.3 Proposed Minimal Test Seams

To ensure tests execute safely with 0 risk of modifying hardware, mouse profiles, powercfg, Windows services, or scheduled tasks, we propose the following minimal, clean test seams:

#### 1. `DebloatService` Test Seams:
```csharp
// Seam for elevated PowerShell execution:
internal Func<string, string?, Task<bool>>? PowerShellRunner { get; set; }

// Seams for querying system status without touching Registry / SCM / schtasks:
internal Func<string, string?>? ServiceStatusQuerySeam { get; set; }
internal Func<string, string?>? ServiceStartupTypeQuerySeam { get; set; }
internal Func<string, string>? ScheduledTaskStateQuerySeam { get; set; }
```
- In `RunElevatedPowerShellAsync`: If `PowerShellRunner != null`, delegate to `PowerShellRunner(script, errorLogPath)`.
- In `QueryServiceStatusDirect`: If `ServiceStatusQuerySeam != null`, use it instead of `advapi32.dll`.
- In `RecordCurrentScheduledTaskState`: If `ScheduledTaskStateQuerySeam != null`, use it instead of spawning `schtasks.exe`.

#### 2. `CpuPowerService` Test Seams:
```csharp
// Constructor seam:
public CpuPowerService(bool autoInitialize = true)

// Native Win32 power scheme write seam:
internal Func<Guid, Guid, Guid, uint, bool, bool, bool>? NativePowerSchemeWriter { get; set; }
internal bool DisableNativePowerCallsForTesting { get; set; }
```
- When `NativePowerSchemeWriter != null`, `SetBoostModeAsync` calls the delegate instead of `PowerWriteACValueIndex` and `PowerSetActiveScheme`.
- When `autoInitialize` is `false`, constructor skips `UnhideSetting()` and active scheme query.

#### 3. `AtkMouseService` Test Seams:
```csharp
// USB HID packet transceiver seam:
internal Func<string, string, byte[], byte, byte[]?>? PacketTransceiver { get; set; }
internal bool DisableHardwareIoForTesting { get; set; }
```
- When `PacketTransceiver != null`, `SendAndReceivePacket` delegates to the test seam instead of opening `\\?\hid#...` device handles.
- `LiveHardware_QueryAndSwitch_IfConnected` must be guarded or excluded from normal automated test runs.

### 3.4 Safe Test Scenarios to Satisfy R4 Acceptance Criteria

#### Scenario 1: Debloat Backup `Stopped`, System `Running`
- **Setup**: Backup state has `ASMSvc:Status = "Stopped"`, `ASMSvc = "Manual"`.
- **Sub-case A (Stop Failure)**:
  `PowerShellRunner` simulates `Stop-Service` failing (e.g. error logged to `$errLog`, returns `false`).
  - *Verification*: `RestoreTier1ServicesAsync` returns `false`, `LastError` contains error, backup file is **retained**.
- **Sub-case B (Stop Success)**:
  `PowerShellRunner` simulates `Stop-Service` succeeding and verification passing (returns `true`).
  - *Verification*: `RestoreTier1ServicesAsync` returns `true`, backup file is **deleted**.

#### Scenario 2: Debloat Backup with Missing Service or Task
- **Setup**: Backup state contains a service or task not present on the system.
- **Action**: Run restore script.
- **Verification**: Script logs `'Servis bulunamadı: ...'` or `'NitroSenseLauncher görevi bulunamadı.'`, exit code 1. `RestoreTier1ServicesAsync` returns `false`, `LastError` reports missing item, backup file is **retained**.

#### Scenario 3: Debloat Process Timeout (20s) and Concurrency Lock
- **Setup**: Simulated PowerShell process takes >20s.
- **Action**:
  1. Call `ApplyTier1DebloatAsync()` or `RestoreTier1ServicesAsync()`.
  2. First call returns `false` with timeout warning after 20s.
  3. While process is still alive, call `ApplyTier1DebloatAsync()` again.
  4. Second call returns `false` immediately with: `"Önceki PowerShell işlemi hâlâ devam ediyor..."`.
  5. When first process terminates, third call proceeds normally and reuses original backup.

#### Scenario 4: Concurrent `CpuPowerService.SetBoostModeAsync` Without Hardware Writes
- **Setup**: `var service = new CpuPowerService(autoInitialize: false);`
  Configure `service.NativePowerSchemeWriter = (scheme, subGroup, setting, val, ac, dc) => true;`
- **Action**: Launch 3 concurrent tasks calling `SetBoostModeAsync`.
- **Verification**: All tasks complete with `Assert.Null(ex)`, `_powerLock` synchronizes execution without deadlock, and **zero** Win32 power changes touch the OS.

---

## 4. Pinpoint Mapping: Files, Methods, Bugs, and Fix Recommendations

### 4.1 `NitroLite/Core/DebloatService.cs`

| Method / Member | Lines | Identified Bug / Vulnerability | Recommended Fix |
|---|---|---|---|
| `GenerateRestoreScript` | 464-539 | Does not call `Stop-Service` when recorded status is `Stopped`. Does not verify `$vSvc.Status -eq 'Stopped'`. | If `recordedStatus == "Stopped"`, emit: `if ($svc.Status -ne 'Stopped') { Stop-Service -Name '{s}' -Force -ErrorAction Stop }; Set-Service ...; if ($vSvc.Status -ne 'Stopped') { throw ... }`. |
| `GenerateRestoreScript` | 505-515 | Order of operations: `Set-Service` must precede `Start-Service`. | Maintain `Set-Service` before `Start-Service` for `Running` target. |
| `GenerateRestoreScript` | 521-535 | Scheduled task verification ignores `Disabled` recorded state. | If `taskState == "Disabled"`, emit `Disable-ScheduledTask` and verify `$vTask.State -eq 'Disabled'`. |
| `RunElevatedPowerShellAsync` | 542-592 | `p.WaitForExit(20000)` timeout unhandled. `p.ExitCode` throws `InvalidOperationException`. Process abandoned, lock released. | Track `_activePowerShellProcess`. Handle `!WaitForExit(20000)` without reading `ExitCode`. Hold lifecycle lock until exit. |
| `ApplyTier1DebloatAsync` | 606-634 | Overwrites pre-debloat baseline if debloat is applied to a partially modified system. | If a valid backup already exists, preserve original backup or reject re-debloat. |
| `RestoreTier1ServicesAsync` | 673-715 | Swallows exceptions on corrupt backup; no check for running active process. | Check `_activePowerShellProcess` before running. Retain backup on all non-zero exits. |
| Class fields | 594 | Only `_debloatLock` exists; no active process tracking. | Add `private Process? _activePowerShellProcess;` and test seams (`PowerShellRunner`, query seams). |

### 4.2 `NitroLite/Core/CpuPowerService.cs`

| Method / Member | Lines | Identified Bug / Vulnerability | Recommended Fix |
|---|---|---|---|
| `CpuPowerService()` | 66-77 | Parameterless constructor forces `autoInitialize = true`, writing to HKLM registry. | Make constructor `public CpuPowerService(bool autoInitialize = true)` so tests can pass `false`. |
| `SetBoostModeAsync` | 277-339 | Direct P/Invoke to `PowerWriteACValueIndex` / `PowerSetActiveScheme` modifies OS power scheme. | Add `internal Func<Guid, Guid, Guid, uint, bool, bool, bool>? NativePowerSchemeWriter { get; set; }` to intercept in tests. |

### 4.3 `NitroLite/Core/AtkMouseService.cs`

| Method / Member | Lines | Identified Bug / Vulnerability | Recommended Fix |
|---|---|---|---|
| `SendAndReceivePacket` | 48-105 | Sends raw HID Output Reports directly to physical mouse endpoint if plugged in. | Add `internal Func<string, string, byte[], byte, byte[]?>? PacketTransceiver { get; set; }` to mock packet I/O in tests. |

### 4.4 `NitroLite.Tests/VerifiedDefectsRegressionTests.cs`

| Method / Member | Lines | Problem | Recommended Fix |
|---|---|---|---|
| `CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock` | 1005-1017 | Calls real Win32 power APIs. | Use `new CpuPowerService(autoInitialize: false)` with `NativePowerSchemeWriter` mock. |
| `Defect5_AtkMouseService_SwitchProfile_SafeExecution` | 502-515 | Writes to physical mouse. | Configure `PacketTransceiver` or disable hardware I/O seam. |

### 4.5 `NitroLite.Tests/AtkMouseServiceTests.cs`

| Method / Member | Lines | Problem | Recommended Fix |
|---|---|---|---|
| `LiveHardware_QueryAndSwitch_IfConnected` | 95-115 | Modifies physical mouse hardware profile. | Remove test or guard with `[Trait("Category", "LiveHardware")]` requiring manual opt-in. |

---

## 5. Implementation Roadmap for Subsequent Phases

1. **Phase 1: Test Seam Injection (Non-breaking)**:
   - Expose `CpuPowerService(bool autoInitialize = true)` and `NativePowerSchemeWriter`.
   - Add `DebloatService.PowerShellRunner` and query seams.
   - Add `AtkMouseService.PacketTransceiver`.
2. **Phase 2: Debloat Restoration Hardening (R3)**:
   - Update `DebloatService.GenerateRestoreScript` to implement `Stop-Service` and verify `Status -eq 'Stopped'`.
   - Update scheduled task restore to verify `Disabled` state.
   - Harden `RunElevatedPowerShellAsync` with `_activePowerShellProcess` lifecycle locking.
   - Protect `ApplyTier1DebloatAsync` against overwriting valid existing backups.
3. **Phase 3: Test Suite Safety & New R4 Verification Tests**:
   - Refactor `CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock` to use mock seam.
   - Safely isolate `AtkMouseService` tests.
   - Add automated test cases for all R4 scenarios (stopped service restore, partial failure retention, timeout lifecycle locking).
4. **Phase 4: Build & Test Run**:
   - `dotnet build -c Release` (0 errors, 0 warnings).
   - `dotnet test` with 100% green pass and zero hardware interference.
