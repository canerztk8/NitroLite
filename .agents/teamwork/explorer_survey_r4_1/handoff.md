# Handoff Report: Explorer Survey 1 — DebloatService & SettingsManager

**Agent**: `explorer_survey_r4_1` (teamwork_explorer)  
**Parent / Caller**: orchestrator (`c07d35ed-7dee-42ca-8db4-7d8fd9f82587`)  
**Working Directory**: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_1`  
**Date**: 2026-11-04  
**Deliverable**: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_1\survey_report.md`  

---

## 1. Observation

1. **Unvalidated Startup Type String Concatenation (`NitroLite/Core/DebloatService.cs:188-208`)**:
   ```csharp
   188: foreach (var s in Tier1Services)
   189: {
   190:     string targetType = "Automatic";
   191:     if (backupState != null && backupState.TryGetValue(s, out var recordedType) && !string.IsNullOrWhiteSpace(recordedType))
   192:     {
   193:         targetType = recordedType;
   194:     }
   ...
   199:     sb.Append($"Set-Service -Name '{s}' -StartupType {targetType} -ErrorAction Stop; ");
   ```
   `targetType` is directly interpolated from `backupState` (loaded from `debloat_backup_state.json`) into the PowerShell command line without validation or sanitization.
   At lines 231–235, `RunElevatedPowerShellAsync` executes this script with elevated Administrator privileges via `powershell.exe -EncodedCommand` using `Verb = "runas"`.

2. **AcerLightingService Discrepancy (`NitroLite/Core/DebloatService.cs:19-28` vs `NitroLite/MainWindow.xaml.cs:2571-2573`)**:
   `DebloatService.cs` lines 19–28 defines:
   ```csharp
   public static readonly string[] Tier1Services = new[]
   {
       "AcerServiceSvc", "ASMSvc", "AcerDIAgentSvis", "AcerEZSvc",
       "AcerDeviceInfoAgentService", "AcerDeviceEnablingServiceV2", "AcerLightingService"
   };
   ```
   There are 7 services in `Tier1Services`.
   In contrast, `MainWindow.xaml.cs` lines 2571–2573 presents the confirmation prompt:
   ```csharp
   var confirm = System.Windows.MessageBox.Show(this, 
       "Altı adet OEM telemetri servisi devre dışı bırakılacak ve yaklaşık 214 MB bellek tasarrufu sağlanacaktır.\n\nDonanım kontrolleri etkilenmeyecektir. Devam edilsin mi?",
       "Servis Yapılandırması", MessageBoxButton.YesNo, MessageBoxImage.Question);
   ```
   The UI dialog promises 6 services ("Altı adet") and 214 MB memory savings with zero hardware control impact.
   `scripts/research/nitro_sense_analysis.md:131, 141-147, 485, 728` proves that Tier 1 Safe Debloat consists of exactly 6 non-critical services (213.51 MB), while `AcerLightingService` is the critical 39.11 MB keyboard RGB lighting engine listening on port 6742. Disabling it halts dynamic RGB lighting animations and idle timeout control.

3. **Silent Persistence Failure & Defaulting to Automatic (`NitroLite/Core/DebloatService.cs:134-149, 188-208, 271-291`)**:
   - `SaveBackupState` (lines 134–149) catches all exceptions (`catch { }`) and returns `void`.
   - `ApplyTier1DebloatAsync` (lines 271–291) calls `SaveBackupState(existingBackup)` without checking success, proceeding directly to stop and disable services even if the backup file was never written to disk.
   - In `GenerateRestoreScript` (lines 190–204), if a service in `Tier1Services` is not found in `backupState`, `targetType` defaults to `"Automatic"` and issues `Start-Service -Name '{s}'`, unconditionally forcing unrecorded or missing services to `Automatic` and starting them.
   - `RecordCurrentStartupTypes` only queries the registry `Start` value (`2`, `3`, `4`). It fails to record service running state (`Running` vs `Stopped`) and scheduled task state (`NitroSenseLauncher`).

4. **Corrupted Settings Overwrites Valid Backup (`NitroLite/Core/SettingsManager.cs:62-87`)**:
   ```csharp
   70: var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
   71: string tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
   72: string backupPath = filePath + ".bak";
   73: 
   74: File.WriteAllText(tempPath, json);
   75: 
   76: // Maintain backup of previous valid settings
   77: if (File.Exists(filePath))
   78: {
   79:     try
   80:     {
   81:         File.Copy(filePath, backupPath, overwrite: true);
   82:     }
   83:     catch { }
   84: }
   85: 
   86: File.Move(tempPath, filePath, overwrite: true);
   ```
   When `settings.json` is corrupted, `LoadSettingsWithFallback` gracefully recovers by loading `settings.json.bak` (or legacy config).
   However, on the subsequent call to `SaveSettingsAtomic`, line 77 evaluates `File.Exists(filePath)` as true, and line 81 copies the corrupted `filePath` directly over `backupPath`, overwriting and destroying the valid `.bak` file.

---

## 2. Logic Chain

1. **Privilege Escalation Vector (Observation 1)**:
   - Untrusted strings from JSON files on disk are concatenated directly into an elevated PowerShell script without validation or sanitization.
   - Because the process runs with Administrator privileges (`Verb = "runas"`), an attacker or non-privileged user modifying `debloat_backup_state.json` achieves arbitrary code execution with elevated privileges.
   - Therefore, startup types must be strictly validated against a whitelist of valid SCM modes (`Automatic`, `Manual`, `Disabled`), and invalid entries rejected by throwing an exception.

2. **AcerLightingService Consistency & Functionality (Observation 2)**:
   - The UI claims 6 services, ~214 MB memory reduction, and zero hardware impact.
   - `Tier1Services` contains 7 services, with `AcerLightingService` controlling keyboard RGB backlighting. Disabling it breaks keyboard lighting, violating the UI promise and the architectural specification in `nitro_sense_analysis.md`.
   - Therefore, removing `AcerLightingService` from `Tier1Services` aligns code with the UI confirmation dialog and preserves keyboard lighting functionality.

3. **Debloat State Resilience (Observation 3)**:
   - If `SaveBackupState` fails silently, the application disables services without any record of their prior state, preventing restoration.
   - Defaulting unrecorded services to `"Automatic"` alters services that were never intended to be Automatic or were not installed.
   - Omitting running states and task states causes stopped services to be started and disabled scheduled tasks to be enabled upon restoration.
   - Therefore, `SaveBackupState` must return a boolean, `ApplyTier1DebloatAsync` must abort service shutdown if saving fails, `GenerateRestoreScript` must skip unrecorded services, and running/task states must be recorded and respected.

4. **Settings Backup Preservation (Observation 4)**:
   - Copying `filePath` to `backupPath` without checking if `filePath` is valid causes corrupted primary files to destroy healthy backup files.
   - Therefore, adding `IsValidSettingsFile(filePath)` prior to `File.Copy` guarantees that corrupted data will never overwrite a valid backup.

---

## 3. Caveats

1. **Existing Baseline Tests**:
   - `AuditRemediationTests.DebloatService_PreDebloatStartupTypes_RecordedAndRestoredAccurately` verifies that recorded startup types are respected and that an Automatic service emits `Start-Service`. The proposed restoration logic maintains full backward compatibility with this test by defaulting to starting Automatic services when running status is absent.
2. **Privacy Test Status**:
   - During verification, `NitroLite.Tests.AuditRemediationTests.Privacy_BatteryReportHtml_NotInWorkspaceOrExcluded` failed because `.gitignore` in the repository does not yet include `battery-report.html`. This is assigned to another agent and was not modified per read-only rules.
3. **No Code Modification Constraint**:
   - In accordance with instructions, zero application or test code files were modified. All proposed changes are documented as diffs and specifications in `survey_report.md`.

---

## 4. Conclusion

All four defects have been thoroughly investigated, with exact code locations, root causes, security impact, and technical solutions identified:
1. `DebloatService.cs`: Enforce a strict whitelist (`Automatic`, `Manual`, `Disabled`) with canonical normalization and exception rejection.
2. `DebloatService.cs` / `MainWindow.xaml.cs`: Exclude `AcerLightingService` from `Tier1Services` (6 services total), preserving RGB lighting controls and matching the UI prompt and hardware research.
3. `DebloatService.cs`: Return `bool` from `SaveBackupState`, abort `ApplyTier1DebloatAsync` on write failure, omit missing services from `GenerateRestoreScript`, and record/restore service running states and scheduled task states.
4. `SettingsManager.cs`: Validate `filePath` using `IsValidSettingsFile` before copying to `backupPath`, preventing corrupted primary files from destroying valid `.bak` backups.

---

## 5. Verification Method

1. **Inspect Survey Report**:
   Read `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_1\survey_report.md` for comprehensive diffs and technical design.
2. **Build and Test Verification**:
   ```powershell
   dotnet build c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.sln
   dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj
   ```
3. **Unit Test Implementation Verification**:
   Implement the 6 unit test methods specified in Section 6 of `survey_report.md` once code changes are applied by the worker agent.
4. **Invalidation Conditions**:
   - Any script generation allowing arbitrary strings in `-StartupType`.
   - `AcerLightingService` retained in debloat without warning that RGB lighting is disabled.
   - Debloat proceeding when `debloat_backup_state.json` write fails.
   - `SaveSettingsAtomic` overwriting a valid `.bak` with corrupted JSON data.
