# Technical Survey Report: GameDetectionService & NitroLite.Tests Regression Architecture

**Author**: `explorer_survey_r4_3` (teamwork_explorer)  
**Date**: 2026-11-04T23:15:00Z  
**Target Solution**: `NitroLite.sln` (.NET 10 WPF, net10.0-windows)  
**Scope**: GameDetectionService state machine & automatic game mode toggling (`GameDetectionService.cs:433`); `NitroLite.Tests` infrastructure and complete regression test suite design for all 7 verified defects without physical hardware requirements.

---

## 1. Executive Summary

GameDetectionService currently disables all process monitoring at the entry of `ScanProcesses()` when `AutoGameModeEnabled` is false. Because this check precedes the active game fast-path, toggling off automatic game mode during active gameplay strands the active process ID and profile in memory, suppresses `GameEnded` event dispatch upon game process exit, and permanently freezes user power plans, aggressive CPU boost, max fan speeds, and custom mouse DPI profiles. 

All 7 verified defects across `DebloatService`, `AtkMouseService`, `AcerAgentClient`, `MainWindow`, `GameDetectionService`, and `SettingsManager` can be rigorously verified via automated unit and integration tests in `NitroLite.Tests` using pure software abstractions, process lifecycle hooks, JSON fuzzing, and Win32 mock constructs without requiring any physical Acer laptop hardware or peripheral modifications.

---

## 2. GameDetectionService State Machine & Automatic Game Mode Toggling

### 2.1 Code Inspection & Root Cause Analysis

**File**: `NitroLite/Core/GameDetectionService.cs`  
**Lines**: 25, 433–485  
**Related UI Handler**: `NitroLite/MainWindow.xaml.cs:1477–1596`

In `GameDetectionService.cs`:
```csharp
25:  public bool AutoGameModeEnabled { get; set; } = true;
...
433: private void ScanProcesses()
434: {
435:     if (!AutoGameModeEnabled) return;
436: 
437:     // FAST-PATH: If a game is ALREADY ACTIVE, monitor ONLY that specific process PID.
438:     // Takes ~0.05 ms and eliminates 100% of full-system process scanning while gaming!
439:     int activePid;
440:     GameProfile? currentProfile;
441:     lock (_stateLock)
442:     {
443:         activePid = _activeProcessId;
444:         currentProfile = _activeGameProfile;
445:     }
446: 
447:     if (currentProfile != null && activePid > 0)
448:     {
449:         bool isStillAlive = false;
450:         try
451:         {
452:             using var activeProc = Process.GetProcessById(activePid);
453:             if (!activeProc.HasExited && activeProc.ProcessName.Equals(currentProfile.ExeName, StringComparison.OrdinalIgnoreCase))
454:             {
455:                 isStillAlive = true;
456:             }
457:         }
...
476:         // Active game process has terminated
477:         lock (_stateLock)
478:         {
479:             _activeGameProfile = null;
480:             _activeProcessId = 0;
481:         }
482:         GameEnded?.Invoke(currentProfile);
483:         return;
484:     }
485: 
486:     // SLOW-PATH (Only when NO game is active): Scan process list to detect newly launched games
```

### 2.2 Flawed State Machine Breakdown

1. **Active Game Initiation**:
   - A recognized game launches (e.g. `cs2.exe`).
   - `ScanProcesses()` identifies the process in the slow-path, sets `_activeGameProfile = foundProfile; _activeProcessId = foundPid;`, and fires `GameStarted?.Invoke(foundProfile)`.
   - In `MainWindow.xaml.cs:OnGameStarted` (lines 1490–1522):
     - Prior settings are saved: `_savedModeBeforeGame = _currentMode; _savedCpuBoostBeforeGame = _cpuPowerService.CurrentMode; _savedMouseProfileBeforeGame = ...;`
     - Flags set: `_isGameSessionActive = true; _batteryService.IsGameSessionActive = true;`
     - Target settings applied: Mode set to `Turbo`, CPU boost set to `Aggressive (5.0 GHz)`, fan mode set to `FanMode.Auto` / Turbo, mouse profile switched to profile `TargetAtkMouseProfile`.
2. **User Disables Auto Game Mode During Gameplay**:
   - The user opens NitroLite and unchecks `ChkAutoGameMode`.
   - In `MainWindow.xaml.cs:ChkAutoGameMode_Changed` (line 1592):
     `_gameService.AutoGameModeEnabled = false;`
     `TxtGameStatus.Text = "Durum: Devre Dışı (Manuel Kontrol)";`
   - Notice: `ChkAutoGameMode_Changed` does not invoke `GameEnded` and does not restore previous settings.
   - In `GameDetectionService.cs`: `AutoGameModeEnabled` is a plain auto-property (`public bool AutoGameModeEnabled { get; set; } = true;`), so setting it to `false` performs no state cleanup or event invocation.
3. **Subsequent Scan Ticks**:
   - The background timer `_scanTimer` fires every 2 seconds, calling `OnScanTimerTick` -> `ScanProcesses()`.
   - `ScanProcesses()` enters line 435: `if (!AutoGameModeEnabled) return;`.
   - Because `AutoGameModeEnabled` is false, it returns immediately.
   - The entire FAST-PATH (lines 437–484) is skipped!
4. **Game Process Exits**:
   - The user exits `cs2.exe`.
   - On every timer tick, `ScanProcesses()` hits `if (!AutoGameModeEnabled) return;` and aborts.
   - Lines 477–482 are never executed:
     - `_activeGameProfile` remains set to `cs2Profile`.
     - `_activeProcessId` remains set to the old PID.
     - `GameEnded?.Invoke(currentProfile)` is NEVER triggered.
     - `MainWindow.OnGameEnded` is NEVER called.
   - Result:
     - Operating mode remains permanently locked in `Turbo` (high power, high voltage).
     - Fans remain locked in max/game RPM.
     - CPU boost remains locked in `Aggressive`.
     - Mouse profile remains switched to gaming config.
     - `IsGameActive` property continues returning `true` indefinitely.

### 2.3 Corrected State Machine & Implementation Strategy

To cleanly handle automatic game mode toggling during active gameplay and guarantee restoration on game exit or when toggled off, two coordinated changes are required:

#### A. Active Session Teardown in `AutoGameModeEnabled` Setter
When `AutoGameModeEnabled` transitions from `true` to `false` while `_activeGameProfile != null`:
1. Safely snapshot and clear `_activeGameProfile` and `_activeProcessId` under `_stateLock`.
2. Fire `GameEnded?.Invoke(activeProfile)` immediately outside the lock.
3. This allows `MainWindow.OnGameEnded` to restore `_savedModeBeforeGame`, `_savedCpuBoostBeforeGame`, fan mode `Auto`, and mouse profile immediately upon the user requesting manual control.

#### B. Safe FAST-PATH Ordering in `ScanProcesses()`
In `ScanProcesses()`, the check for `!AutoGameModeEnabled` must be repositioned AFTER the active game PID monitor:
1. If `_activeGameProfile != null && _activeProcessId > 0`, the FAST-PATH MUST run regardless of `AutoGameModeEnabled`.
2. If the active process is still alive, return.
3. If the active process has terminated, clear `_activeGameProfile`, reset `_activeProcessId = 0`, and invoke `GameEnded?.Invoke(currentProfile)`.
4. Only then, before entering the SLOW-PATH process enumeration, evaluate `if (!AutoGameModeEnabled) return;`.

#### Proposed Code for `GameDetectionService.cs`:
```csharp
private bool _autoGameModeEnabled = true;

public bool AutoGameModeEnabled
{
    get
    {
        lock (_stateLock)
        {
            return _autoGameModeEnabled;
        }
    }
    set
    {
        GameProfile? endedProfile = null;
        lock (_stateLock)
        {
            if (_autoGameModeEnabled == value) return;
            _autoGameModeEnabled = value;

            // When disabled during active gameplay, cleanly terminate active session
            if (!value && _activeGameProfile != null)
            {
                endedProfile = _activeGameProfile;
                _activeGameProfile = null;
                _activeProcessId = 0;
            }
        }

        if (endedProfile != null)
        {
            GameEnded?.Invoke(endedProfile);
        }
    }
}

private void ScanProcesses()
{
    // FAST-PATH: If a game is ALREADY ACTIVE, monitor ONLY that specific process PID.
    // Must execute even if AutoGameModeEnabled is toggled off to ensure clean exit detection!
    int activePid;
    GameProfile? currentProfile;
    lock (_stateLock)
    {
        activePid = _activeProcessId;
        currentProfile = _activeGameProfile;
    }

    if (currentProfile != null && activePid > 0)
    {
        bool isStillAlive = false;
        try
        {
            using var activeProc = Process.GetProcessById(activePid);
            if (!activeProc.HasExited && activeProc.ProcessName.Equals(currentProfile.ExeName, StringComparison.OrdinalIgnoreCase))
            {
                isStillAlive = true;
            }
        }
        catch (ArgumentException)
        {
            isStillAlive = false;
        }
        catch
        {
            isStillAlive = true;
        }

        if (isStillAlive)
        {
            return;
        }

        // Active game process has terminated
        lock (_stateLock)
        {
            _activeGameProfile = null;
            _activeProcessId = 0;
        }
        GameEnded?.Invoke(currentProfile);
        return;
    }

    // SLOW-PATH: Only search for newly launched games if Auto Game Mode is enabled
    if (!AutoGameModeEnabled) return;

    // ... slow-path process scan ...
}
```

---

## 3. Test Infrastructure Verification (`NitroLite.Tests`)

### 3.1 Project & Solution Build Verification
- **Solution File**: `NitroLite.sln`
- **Projects**:
  - `NitroLite/NitroLite.csproj` (`net10.0-windows`, WPF, Output: `WinExe`)
  - `NitroLite.Tests/NitroLite.Tests.csproj` (`net10.0-windows`, WPF, Output: `Exe`)
- **Test Frameworks**:
  - `Microsoft.NET.Test.Sdk` (18.11.0)
  - `xunit` (2.9.3)
  - `xunit.runner.visualstudio` (4.0.1)
- **Verified Build Command**:
  `dotnet build NitroLite.sln -c Release`
  - Output: `Build succeeded. 0 Warning(s), 0 Error(s). Time Elapsed 00:00:04.45`
  - *Note*: Running raw `dotnet build -c Release` without specifying `NitroLite.sln` yields MSBuild error `MSB1011` because both `NitroLite.sln` and multiple project folders exist. Always target `NitroLite.sln` or `NitroLite.Tests/NitroLite.Tests.csproj`.
- **Verified Test Execution Commands**:
  - `dotnet test NitroLite.sln`
  - `dotnet test NitroLite.Tests`
- **Current Test Status**:
  - Total Tests: 386
  - Passed: 385
  - Failed: 1 (`NitroLite.Tests.AuditRemediationTests.Privacy_BatteryReportHtml_NotInWorkspaceOrExcluded`)
  - Root Cause of single failure: `.gitignore` does not currently contain the string `"battery-report.html"`, which line 304 of `AuditRemediationTests.cs` checks (`Assert.Contains("battery-report.html", gitignoreContent)`). Adding `battery-report.html` to `.gitignore` brings the suite to 386/386 passed.

---

## 4. Comprehensive Regression Test Design for All 7 Verified Defects

Every defect can be verified 100% deterministically in `NitroLite.Tests` without physical hardware access, without peripheral dongles, and without administrator privilege prompts.

```
+-------------------------------------------------------------------------------------------------------------+
|                                        7-DEFECT REGRESSION MATRIX                                           |
+---+------------------------------------+--------------------------+-----------------------+-----------------+
| # | Defect / Vulnerability             | Affected Target          | Pure Software Vector  | Test Class      |
+---+------------------------------------+--------------------------+-----------------------+-----------------+
| 1 | Startup Type Command Injection     | DebloatService.cs:188    | Injected JSON String  | DebloatTests    |
| 2 | Asynchronous HID OVERLAPPED Leak   | AtkMouseService.cs:330   | Win32 Cancel & Bounds | AtkMouseTests   |
| 3 | MUX Non-Success Code & UI Modes    | AcerAgentClient.cs:285   | Result "99" & Bitmask | AcerAgentTests  |
| 4 | AcerLightingService Debloat Incon. | DebloatService.cs:19     | Service Invariants    | DebloatTests    |
| 5 | Resilient Service Restoration      | DebloatService.cs:271    | Path Fault & States   | DebloatTests    |
| 6 | Auto Game Mode Stuck on Exit/Off   | GameDetectionService:433 | PID & Setter Triggers | GameDetectTests |
| 7 | Settings Recovery .bak Overwrite   | SettingsManager.cs:62    | Corrupt File Parsing  | SettingsTests   |
+---+------------------------------------+--------------------------+-----------------------+-----------------+
```

---

### Defect 1: DebloatService Startup Type Validation (`DebloatService.cs:188`)

#### Observation & Vulnerability
In `GenerateRestoreScript(Dictionary<string, string>? backupState, ...)`:
```csharp
191: if (backupState != null && backupState.TryGetValue(s, out var recordedType) && !string.IsNullOrWhiteSpace(recordedType))
192: {
193:     targetType = recordedType;
194: }
...
199: sb.Append($"Set-Service -Name '{s}' -StartupType {targetType} -ErrorAction Stop; ");
```
`targetType` is read from `debloat_backup_state.json` without validation. If the backup file is tampered with or contains `"Manual; Start-Process cmd.exe"`, the elevated PowerShell instance will execute the command.

#### Concrete Test Cases (`NitroLite.Tests/DebloatStartupTypeValidationTests.cs`):
1. **`GenerateRestoreScript_CommandInjectionPayload_SanitizesOrRejects`**:
   - Setup: `var backup = new Dictionary<string, string> { ["AcerServiceSvc"] = "Manual; Start-Process calc.exe" };`
   - Execution: `var script = service.GenerateRestoreScript(backup);`
   - Verification: `Assert.DoesNotContain("calc.exe", script); Assert.DoesNotContain("Start-Process", script); Assert.DoesNotContain(";", script.Substring(script.IndexOf("StartupType")));`
2. **`GenerateRestoreScript_InvalidStartupTypes_RejectsUnrecognizedValues`**:
   - Inputs: `"Hacked"`, `"' | Out-File bad.txt"`, `"AutomaticDelayedStart; Evil"`, `""`, `"   "`.
   - Verification: The script generation rejects the input or safely falls back without injecting illegal PowerShell arguments.
3. **`GenerateRestoreScript_ValidStartupTypes_AcceptedCaseInsensitively`**:
   - Inputs: `"Automatic"`, `"manual"`, `"DISABLED"`.
   - Verification: Generated script contains `-StartupType Automatic`, `-StartupType Manual`, `-StartupType Disabled`.

---

### Defect 2: AtkMouseService Native HID Asynchronous Reads & Memory Safety (`AtkMouseService.cs:330`)

#### Observation & Vulnerability
In `WriteAndReadPacket`:
```csharp
339: ReadFile(handle, rxBuf, (uint)packet.Length, out readBytes, ref ov);
341: bool txOk = HidD_SetOutputReport(handle, packet, packet.Length);
342: if (!txOk) return null;
344: uint waitRes = WaitForSingleObject(hEvent, 300);
```
If `txOk == false` or `waitRes == WAIT_TIMEOUT` (0x102), the method returned `null` and closed `hEvent` without canceling the pending asynchronous `ReadFile`. The kernel driver retains the pointer to `ref ov` and managed `rxBuf`. When the driver later attempts to write to the completed buffer, memory corruption / access violation occurs.

#### Concrete Test Cases (`NitroLite.Tests/AtkMouseServiceTests.cs`):
1. **`AtkMouseService_SwitchProfile_GracefulFailureOnMissingDevice`**:
   - Verify `SwitchProfile(1)` and `QueryCurrentProfile()` complete safely on machines without physical ATK dongles without crashing, throwing unhandled Win32 exceptions, or leaking handles.
2. **`AtkMouseService_PacketFraming_ChecksumAlgorithmStrictCheck`**:
   - Verify packet checksum invariant `checksum == 0x55 - sum(bytes[0..15])` for all configs 1..4.
3. **`AtkMouseService_OverlappedCancellationSimulation_NoMemoryFault`**:
   - Simulation harness: Using Win32 pipe handle opened with `FILE_FLAG_OVERLAPPED`, initiate `ReadFile`. Simulate timeout. Issue `CancelIoEx`. Await completion via `GetOverlappedResult(..., bWait: true)`. Verify `ERROR_OPERATION_ABORTED` (995) is received cleanly before handle closure.

---

### Defect 3: AcerAgentClient Non-Success Response Handling & MUX Supported Modes UI (`AcerAgentClient.cs:266, 325`, `MainWindow.xaml.cs:1121`)

#### Observation & Vulnerability
In `AcerAgentClient.cs`:
```csharp
285: return resStr == "0" || resStr == "3" || !string.IsNullOrWhiteSpace(resp);
...
343: return resStr == "0" || resStr == "3" || !string.IsNullOrWhiteSpace(resp);
```
The expression `|| !string.IsNullOrWhiteSpace(resp)` unconditionally evaluates to `true` whenever any string is returned, causing error responses such as `{"result": "99"}` to be treated as successful operations.

In `MainWindow.xaml:459` and `MainWindow.xaml.cs:1070`:
`CmbGpuMode` hardcodes all three modes (`Automatic`, `Optimus`, `DiscreteGpu`) regardless of whether the hardware actually supports DDS (Advanced Optimus) or MUX switching.

#### Concrete Test Cases (`NitroLite.Tests/AcerAgentClientTests.cs`):
1. **`EvaluateResponse_Result99_ReturnsFalse`**:
   - Input: `{"result": "99", "error": "Hardware busy or unsupported"}`
   - Verification: `Assert.False(client.EvaluateSuccess(resp))` — treated as failure.
2. **`EvaluateResponse_Result1_ReturnsFalse`**:
   - Input: `{"result": "1"}` -> treated as failure.
3. **`EvaluateResponse_Result0And3_ReturnsTrue`**:
   - Input: `{"result": "0"}` -> `true`; `{"result": "3"}` -> `true`.
4. **`GpuModeCapability_BitmaskFiltering_OnlyPresentsSupportedModes`**:
   - Bitmask 3 (`0b011`): Optimus (1) + dGPU (2). DDS (4) unsupported.
   - Verification: Capability filter method excludes `Automatic` from available UI options when bit 2 is 0.
   - Bitmask 7 (`0b111`): All 3 modes permitted.

---

### Defect 4: AcerLightingService Debloat Behavior & Confirmation Consistency (`DebloatService.cs:19`, `MainWindow.xaml.cs:2571`)

#### Observation & Discrepancy
`DebloatService.Tier1Services` defines 7 services, including `AcerLightingService`. However:
- Confirmation dialog in `MainWindow.xaml.cs:2571` states: `"Altı adet OEM telemetri servisi devre dışı bırakılacak ... Donanım kontrolleri etkilenmeyecektir."`
- Disabling `AcerLightingService` kills RGB lighting control, backlight timeout synchronization, and zone animations.

#### Resolution Decision:
Exclude `AcerLightingService` from `Tier1Services`.
- Rationale: `AcerLightingService` is a hardware controller service for keyboard RGB lighting, not an OEM data telemetry service. Preserving it maintains the guarantee that "Donanım kontrolleri etkilenmeyecektir" and leaves exactly 6 pure telemetry services in Tier 1 (`AcerServiceSvc`, `ASMSvc`, `AcerDIAgentSvis`, `AcerEZSvc`, `AcerDeviceInfoAgentService`, `AcerDeviceEnablingServiceV2`).

#### Concrete Test Cases (`NitroLite.Tests/DebloatServiceTests.cs`):
1. **`Tier1Services_ExcludesHardwareLightingController`**:
   - `Assert.DoesNotContain("AcerLightingService", DebloatService.Tier1Services);`
   - `Assert.Equal(6, DebloatService.Tier1Services.Length);`
2. **`ConfirmationDialog_MatchesConfiguredServiceCount`**:
   - Verify consistency between configured Tier 1 array length (6) and confirmation dialog text.

---

### Defect 5: DebloatService Resilient Service Restoration (`DebloatService.cs:271`)

#### Observation & Vulnerabilities
1. In `ApplyTier1DebloatAsync()`: `SaveBackupState` swallows write errors in `catch { }`. If writing `debloat_backup_state.json` fails, services are still killed and disabled without any backup on disk.
2. In `GenerateRestoreScript`: Unrecorded services default to `"Automatic"`, forcefully enabling services that may have been `Disabled` or `Manual` before debloat.
3. Service running states (`Running` vs `Stopped`) and scheduled task states are not recorded prior to modification.

#### Concrete Test Cases (`NitroLite.Tests/DebloatServiceResilienceTests.cs`):
1. **`ApplyTier1Debloat_BackupWriteFailure_AbortsDebloatOperation`**:
   - Setup: Point `BackupStateFilePath` to an illegal/unwritable path (e.g. `Z:\invalid_path\backup.json`).
   - Execution: Call `ApplyTier1DebloatAsync()`.
   - Verification: Returns `false`. `LastError` contains backup write failure description. PowerShell process execution is aborted.
2. **`GenerateRestoreScript_MissingServices_AreNotForcedToAutomatic`**:
   - Setup: Backup dictionary containing only 2 services.
   - Execution: `GenerateRestoreScript(partialBackup)`.
   - Verification: Missing services are omitted or skipped; they are never emitted with `Set-Service -StartupType Automatic` or `Start-Service`.
3. **`DebloatService_RecordsRunningAndTaskStates`**:
   - Model verification: State snapshot serializes both `StartupType` and `WasRunning` / `TaskEnabled` flags.

---

### Defect 6: GameDetectionService Automatic Game Mode Toggling & Restoration (`GameDetectionService.cs:433`)

#### Observation & State Freeze
When `AutoGameModeEnabled` is toggled off while playing, `ScanProcesses()` early-returns at line 435, skipping PID monitoring. When the game terminates, `GameEnded` is never called, leaving power mode, boost, fans, and mouse settings stuck.

#### Concrete Test Cases (`NitroLite.Tests/GameDetectionServiceTests.cs`):
1. **`AutoGameModeEnabled_ToggledFalseDuringGame_CleansUpAndInvokesGameEnded`**:
   - Setup: Simulate active game session (`service.SetActiveGameForTesting(profile, dummyPid)` or trigger scan).
   - Action: `service.AutoGameModeEnabled = false;`
   - Verification:
     - `GameEnded` is invoked with the active profile.
     - `service.IsGameActive` is `false`.
     - `service.ActiveGameProfile` is `null`.
     - `service.ActiveProcessId` is `0`.
2. **`FastPath_GameExitsWhileAutoGameModeDisabled_InvokesGameEnded`**:
   - Setup: Active game profile present. Auto game mode disabled.
   - Action: Active PID terminates.
   - Verification: FAST-PATH executes exit detection, resets state, and fires `GameEnded`.
3. **`AutoGameModeEnabled_Reenabled_ResumesScanning`**:
   - Action: Set `service.AutoGameModeEnabled = true;`
   - Verification: Process scanning resumes cleanly without double-activation or exceptions.

---

### Defect 7: SettingsManager Corruption Recovery Preserving .bak (`SettingsManager.cs:62`)

#### Observation & Corruption Cascading
In `SettingsManager.SaveSettingsAtomic(string filePath, AppSettings settings)`:
```csharp
77: if (File.Exists(filePath))
78: {
79:     try
80:     {
81:         File.Copy(filePath, backupPath, overwrite: true);
82:     }
83:     catch { }
84: }
```
When `settings.json` is corrupted on disk, `LoadSettingsWithFallback` correctly falls back to `settings.json.bak`. However, when `SaveSettingsAtomic` subsequently runs, it executes `File.Copy(filePath, backupPath, overwrite: true)`. Because `filePath` contains the corrupt data, it copies the corrupt file over the valid `.bak` file, destroying the only working backup!

#### Concrete Test Cases (`NitroLite.Tests/SettingsManagerTests.cs`):
1. **`SaveSettingsAtomic_WhenPrimaryCorrupted_DoesNotOverwriteValidBackupWithCorruptData`**:
   - Setup: In a temp directory, write `{ BAD_CORRUPT_JSON @@` to `settings.json`. Write `{"CpuMode": 2, "DefaultAtkMouseProfile": 3}` to `settings.json.bak`.
   - Action:
     - `var loaded = SettingsManager.LoadSettingsWithFallback(settingsPath, backupPath);`
     - Assert `loaded.DefaultAtkMouseProfile == 3` (recovered from `.bak`).
     - `SettingsManager.SaveSettingsAtomic(settingsPath, loaded);`
   - Verification:
     - Read `File.ReadAllText(backupPath)`.
     - `Assert.DoesNotContain("BAD_CORRUPT_JSON", backupContent);`
     - Verify `backupPath` remains valid JSON.
     - Verify `settings.json` is replaced with valid atomic JSON.
2. **`SaveSettingsAtomic_NormalValidFile_MaintainsRollingBackup`**:
   - Setup: Write valid config A to `settings.json`.
   - Action: Save config B with `SaveSettingsAtomic`.
   - Verification: `settings.json.bak` contains config A; `settings.json` contains config B.

---

## 5. Implementation Roadmap & Recommendation for Subsequent Agents

1. **DebloatService**:
   - Whitelist valid startup types in `DebloatService`: `Automatic`, `Manual`, `Disabled`, `AutomaticDelayedStart`.
   - Check write success in `SaveBackupState` and return `bool`. If false, abort `ApplyTier1DebloatAsync()`.
   - Remove `AcerLightingService` from `Tier1Services` (preserving RGB hardware control and aligning with 6-service dialog).
   - Only restore services recorded in backup; omit missing entries from `Start-Service`.
2. **AtkMouseService**:
   - In `WriteAndReadPacket`: Wrap `ReadFile` in `try/finally` with `CancelIoEx(handle, ref ov)`.
   - On timeout or set output failure, cancel I/O and wait for completion via `GetOverlappedResult(..., true)` before handle disposal.
3. **AcerAgentClient & MainWindow**:
   - In `SetOperatingModeAsync` and `SetGpuModeAsync`: Check `resStr == "0" || resStr == "3"` and remove `|| !string.IsNullOrWhiteSpace(resp)`.
   - In `MainWindow.xaml.cs`: Query `GetGpuModeCapabilityAsync()` on startup to populate only supported MUX items in `CmbGpuMode`.
4. **GameDetectionService**:
   - Implement `AutoGameModeEnabled` full property with lock: on disabling during game, clear active profile and fire `GameEnded`.
   - In `ScanProcesses()`: Move `if (!AutoGameModeEnabled) return;` below the FAST-PATH block.
5. **SettingsManager**:
   - Before copying `filePath` to `backupPath`, verify `filePath` is valid JSON (e.g. `IsValidJson(File.ReadAllText(filePath))`).
6. **Git / Privacy**:
   - Add `battery-report.html` to `.gitignore` to resolve the 1 existing failing test in `AuditRemediationTests.cs`.
