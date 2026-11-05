# Task Assignment: Worker M1 — Debloat Resilience, Settings Protection & Baseline Privacy

## Role & Identity
You are `worker_m1_1` (teamwork_worker).
Your working directory: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m1_1`

## Mandatory Rules & Integrity Warning
- MANDATORY: Read `ORIGINAL_REQUEST.md` at `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md` first.
- MANDATORY: Read `PROJECT.md` at `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md`.
- Read Explorer findings in `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_1\survey_report.md` and `explorer_survey_r4_3\survey_report.md`.
- **MANDATORY INTEGRITY WARNING**:
  DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

## Write Ownership
You exclusively own and may edit ONLY these files:
- `NitroLite/Core/DebloatService.cs`
- `NitroLite/Core/SettingsManager.cs`
- `.gitignore`
Do NOT modify any other files in this milestone.

## Implementation Tasks

### 1. `DebloatService.cs` Startup Type Validation (Defect 1)
- In `NitroLite/Core/DebloatService.cs:188-208`:
- Add a strict whitelist of allowed startup types: `Automatic`, `Manual`, `Disabled`.
- Implement `ValidateStartupType(string? input)` that normalizes casing and throws `ArgumentException` on invalid or unverified values.
- In `GenerateRestoreScript(Dictionary<string, string> backupState)`, validate every startup type using `ValidateStartupType` before interpolating into the PowerShell script (`Set-Service -Name '{s}' -StartupType {targetType} -ErrorAction Stop;`). Reject or skip any invalid entries safely.

### 2. `DebloatService.cs` Exclude AcerLightingService (Defect 4)
- In `Tier1Services` array (lines 19–28), remove `"AcerLightingService"` so that it contains exactly 6 non-critical services:
  `"AcerServiceSvc"`, `"ASMSvc"`, `"AcerDIAgentSvis"`, `"AcerEZSvc"`, `"AcerDeviceInfoAgentService"`, `"AcerDeviceEnablingServiceV2"`.
- This preserves keyboard RGB lighting controls and aligns with `MainWindow.xaml.cs:2571` ("Altı adet OEM telemetri servisi ... Donanım kontrolleri etkilenmeyecektir") and 214 MB savings.

### 3. `DebloatService.cs` Resilient State Restoration (Defect 5)
- In `SaveBackupState` (lines 134–149), do not silently swallow exceptions in `catch { }`. Return `bool` indicating whether the write succeeded.
- In `ApplyTier1DebloatAsync` (line 271), check the result of `SaveBackupState`. If saving fails, ABORT service shutdown and return failure immediately (do not shut down services without saved backup).
- In `GenerateRestoreScript`, do NOT force missing/unrecorded services to `"Automatic"`. Only generate restore commands for services that were actually recorded in `backupState`.
- Ensure service running states (`Running` vs `Stopped`) and scheduled task states (`Task:NitroSenseLauncher`) are recorded prior to modification and restored accordingly.

### 4. `SettingsManager.cs` Backup Preservation (Defect 7)
- In `SaveSettingsAtomic` (lines 62–87), implement `IsValidSettingsFile(string path)`: check file exists, size > 0, and JSON deserializes to valid `AppSettings`.
- Only execute `File.Copy(filePath, backupPath, overwrite: true)` if `IsValidSettingsFile(filePath)` returns true.
- If `filePath` is corrupted, do NOT overwrite `backupPath`. This preserves valid `.bak` backup data during and after recovery.

### 5. `.gitignore` Privacy Baseline Fix
- Add `battery-report.html` to `.gitignore` at project root so privacy test `Privacy_BatteryReportHtml_NotInWorkspaceOrExcluded` passes.

## Verification Requirements
- Run `dotnet build NitroLite.sln -c Release` — must compile with 0 errors and 0 warnings.
- Run `dotnet test NitroLite.sln` — all 386 baseline tests must pass.
- Document exact commands and output in your `handoff.md`.


## 2026-11-04T23:13:59Z
[Message] timestamp=2026-11-04T23:13:59Z sender=c07d35ed-7dee-42ca-8db4-7d8fd9f82587 priority=MESSAGE_PRIORITY_HIGH content=You are worker_m1_1. Your working directory is c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m1_1.
MANDATORY: Read ORIGINAL_REQUEST.md at c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md first.
Read PROJECT.md at c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md.
Read your full instructions in c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m1_1\DISPATCH.md.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

You exclusively own:
- NitroLite/Core/DebloatService.cs
- SettingsManager.cs (NitroLite/Core/SettingsManager.cs)
- .gitignore

Implement the changes per DISPATCH.md, run `dotnet build NitroLite.sln -c Release` and `dotnet test NitroLite.sln`, verify 0 errors/0 warnings and all tests pass.
Write your changes and test results to handoff.md in your working directory and notify me with send_message.
