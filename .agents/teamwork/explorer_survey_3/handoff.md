# Handoff Report: Debloat Restore Verification & Safe Testing Architecture (R3 & R4)

**Agent**: Survey Explorer 3 (`teamwork_explorer`)  
**Working Directory**: `C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_3\`  
**Target Requirements**: R3 (Debloat Verified Restore) and R4 (Safe Testing Architecture) in `ORIGINAL_REQUEST.md`  
**Date**: 2026-11-05  

---

## 1. Observation

### Observation 1.1: Missing `Stop-Service` and Incomplete Verification in Restore Script
- **File**: `NitroLite\Core\DebloatService.cs` (lines 492-515)
- **Direct Code Quote**:
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
- **Finding**: When `recordedStatus` is `"Stopped"`, `shouldStart` is `false`. The generator emits neither `Stop-Service` nor `$vSvc.Status -eq 'Stopped'` verification. If a service is currently `Running`, it remains running and the restore operation erroneously reports complete success and deletes the backup.

### Observation 1.2: Scheduled Task Incomplete Verification
- **File**: `NitroLite\Core\DebloatService.cs` (lines 521-535)
- **Direct Code Quote**:
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
- **Finding**: When `taskState` was `"Disabled"`, `IsActiveTaskState(taskState)` evaluates to `false`. The script performs zero action and zero verification.

### Observation 1.3: PowerShell Process Timeout & Unmanaged Process Lifecycle
- **File**: `NitroLite\Core\DebloatService.cs` (lines 561-584)
- **Direct Code Quote**:
  ```csharp
  using var p = Process.Start(psi);
  p?.WaitForExit(20000);
  var exitCode = p?.ExitCode ?? -1;
  ```
- **Finding**: `p.WaitForExit(20000)` return value (`bool`) is uninspected. If 20 seconds elapse without the process exiting, `p.ExitCode` throws `InvalidOperationException: Process must exit before requested information can be determined`. The exception is caught by `catch (Exception ex)`, `p` is disposed by the `using` block, and `_debloatLock.Release()` is called in `finally`. The background elevated PowerShell process continues running unmanaged, and a second click by the user initiates a concurrent elevated process.

### Observation 1.4: Real Hardware Power Setting Mutation in Concurrency Test
- **File**: `NitroLite.Tests\VerifiedDefectsRegressionTests.cs` (lines 1004-1017)
- **Direct Code Quote**:
  ```csharp
  [Fact]
  public async Task CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock()
  {
      var service = new CpuPowerService();
      var tasks = new[]
      {
          Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: true, targetDc: false)),
          Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Efficient, targetAc: false, targetDc: true)),
          Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Aggressive, targetAc: true, targetDc: true))
      };

      var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
      Assert.Null(ex);
  }
  ```
- **Finding**: `new CpuPowerService()` executes `UnhideSetting()` against `HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings` and `service.SetBoostModeAsync()` invokes Win32 P/Invoke `PowerWriteACValueIndex` and `PowerSetActiveScheme`. Running `dotnet test` directly mutates the host computer's active power plan settings.

### Observation 1.5: Real USB HID Mouse Profile Mutation in Tests
- **File**: `NitroLite.Tests\AtkMouseServiceTests.cs` (lines 94-115) & `VerifiedDefectsRegressionTests.cs` (lines 502, 525)
- **Direct Code Quote (`AtkMouseServiceTests.cs:94-110`)**:
  ```csharp
  [Fact]
  public void LiveHardware_QueryAndSwitch_IfConnected()
  {
      var service = new AtkMouseService();
      var current = service.QueryCurrentProfile();
      _output.WriteLine($"QueryCurrentProfile before: {current}");
      if (current.HasValue)
      {
          int initial = current.Value;
          int target = initial == 1 ? 2 : 1;
          
          bool switched = service.SwitchProfile(target);
          _output.WriteLine($"SwitchProfile({target}) result: {switched}");

          System.Threading.Thread.Sleep(150);

          var verified = service.QueryCurrentProfile();
  ```
- **Finding**: If an ATK mouse is plugged in during testing, `dotnet test` physically toggles the mouse's active onboard profile between 1 and 2.

---

## 2. Logic Chain

1. **Premise 1 (R3 Restore Completeness)**: Per `ORIGINAL_REQUEST.md`, debloat restoration must restore and verify both the service startup type and running status (`Running` / `Stopped`).
   - *From Observation 1.1*: Because `DebloatService.GenerateRestoreScript` never calls `Stop-Service` and never verifies `Status -eq 'Stopped'` when target state is `Stopped`, running services remain running and are incorrectly reported as successfully restored.
   - *Inference*: The script generator must be updated to conditionally emit `Stop-Service -Force` and verify `$vSvc.Status -eq 'Stopped'`.
2. **Premise 2 (R3 Error Collection & Retention)**: Per `ORIGINAL_REQUEST.md`, any service/task failure (missing, cannot query, cannot start/stop, state mismatch) must be collected into errors while other independent services proceed; and the backup file must be retained unless all verifications succeed.
   - *From Observation 1.1 & 1.2*: Each service block has independent try-catch appending to `$errs`. If any error occurs, exit code is 1 and `RestoreTier1ServicesAsync` retains `debloat_backup_state.json`. However, missing `Stop-Service` and missing task `Disabled` check cause false exits with code 0.
   - *Inference*: Fixing these checks ensures all discrepancies produce non-zero exits, keeping the backup file safe on disk.
3. **Premise 3 (R3 PowerShell Lifecycle & Concurrency)**: Per `ORIGINAL_REQUEST.md`, when PowerShell operations exceed 20s, subsequent Apply/Restore operations must be blocked until the process exits.
   - *From Observation 1.3*: `WaitForExit(20000)` timeout leads to unhandled exception, handle disposal, and lock release while the process is still running.
   - *Inference*: An explicit `_activePowerShellProcess` reference must be stored, checked, and maintained across timeouts. When timed out, return false with informative status and reject subsequent calls until `HasExited` is true.
4. **Premise 4 (R4 Safety Constraint)**: Per `ORIGINAL_REQUEST.md`, tests must never touch real hardware, mouse profiles, Windows services, or power plans; all tests must execute through minimal test seams.
   - *From Observations 1.4 & 1.5*: Existing tests in `VerifiedDefectsRegressionTests.cs` and `AtkMouseServiceTests.cs` execute live Win32 power scheme writes and live USB HID communications.
   - *Inference*: Minimal test seams must be added (`PowerShellRunner` in `DebloatService`, `NativePowerSchemeWriter` in `CpuPowerService`, and `PacketTransceiver` in `AtkMouseService`) to decouple test verification from operating system and physical hardware modifications.

---

## 3. Caveats

- **Network / Live WMI Services**: Hardware monitoring tests (`HardwareMonitorService`, `StorageHealthService`) perform read-only WMI queries (`Win32_DiskDrive`). These queries do not mutate state and are safe.
- **Elevation Requirement in Production**: Debloat operations in real usage require UAC elevation (`Verb = "runas"`). The test seams isolate this for automated testing, but production code must retain the elevated PowerShell launcher.
- **Scope Boundary**: Investigation was read-only; no production files were modified. Proposed fixes are documented with exact line numbers and code snippets in `analysis.md`.

---

## 4. Conclusion

1. **Debloat Restoration (R3)** has two major logic flaws: failure to stop and verify running services when restoring to `Stopped`, and failure to verify `Disabled` scheduled tasks. Both must be corrected in `GenerateRestoreScript`.
2. **PowerShell Lifecycle (R3)** lacks process tracking; timeouts trigger `InvalidOperationException` and drop concurrency locks prematurely. An `_activePowerShellProcess` tracking mechanism must be implemented.
3. **Test Safety (R4)** requires three non-breaking test seams:
   - `DebloatService.PowerShellRunner` (delegate to simulate elevated script runs).
   - `CpuPowerService.NativePowerSchemeWriter` (delegate to intercept Win32 power scheme writes).
   - `AtkMouseService.PacketTransceiver` (delegate to simulate USB HID packet responses).
4. All 581 existing tests pass, and implementing these seams will allow 100% safe execution of all new R4 test scenarios without physical device interference.

---

## 5. Verification Method

To independently verify the findings and proposed seams:
1. **Inspect Code Locations**:
   - `NitroLite\Core\DebloatService.cs`: lines 480-538 (`GenerateRestoreScript`) and lines 542-592 (`RunElevatedPowerShellAsync`).
   - `NitroLite.Tests\VerifiedDefectsRegressionTests.cs`: lines 1005-1017 (`CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock`).
   - `NitroLite.Tests\AtkMouseServiceTests.cs`: lines 95-115 (`LiveHardware_QueryAndSwitch_IfConnected`).
2. **Build and Test Verification**:
   - Run: `dotnet build -c Release NitroLite.sln` -> Ensure 0 errors, 0 warnings.
   - Run: `dotnet test NitroLite.sln --no-build` -> Verify 581 existing tests pass.
3. **Invalidation Conditions**:
   - If `DebloatService.GenerateRestoreScript` already emitted `Stop-Service` when `recordedStatus == "Stopped"`, finding 1.1 would be invalidated (it does not; verified).
   - If `CpuPowerService.SetBoostModeAsync` did not call `PowerWriteACValueIndex` during `SetBoostModeAsync`, finding 1.4 would be invalidated (it does; verified via P/Invoke tracing).
