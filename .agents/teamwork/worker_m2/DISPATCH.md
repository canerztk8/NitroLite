# Dispatch for Worker 2: Milestone M2 (Debloat Verified Restore & Process Lifecycle)

Working Directory: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m2\
Target File: C:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\Core\DebloatService.cs
Exclusive Write Ownership: NitroLite\Core\DebloatService.cs (DO NOT touch any other files!)

Inputs to read:
- ORIGINAL_REQUEST.md: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
- PROJECT.md: C:\Users\caner\Documents\antigravity\eager-shannon\PROJECT.md
- Explorer 3 Analysis: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_3\analysis.md
- Explorer 3 Handoff: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_3\handoff.md

Tasks:
1. Fix Restore Script Generator (`GenerateRestoreScript`):
   - When target status is `Stopped`, emit `Stop-Service -Name '{s}' -Force -ErrorAction Stop;` and verify `$vSvc.Status -eq 'Stopped'` (throw if status is not Stopped).
   - Enforce correct order of operations: Set startup type, then Start or Stop service, then verify startup type, then verify running/stopped status.
   - For scheduled task `NitroSenseLauncher`: If recorded state is `Disabled` (or not active), emit `Disable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop;` and verify `$vTask.State -eq 'Disabled'`.
   - Ensure all service/task failures (missing, cannot query, cannot start/stop, state mismatch) are appended to `$errs` and exit with non-zero code.
2. Fix Backup Retention and Baseline Integrity:
   - Retain `debloat_backup_state.json` unless ALL steps are verified successfully without error.
   - In `ApplyTier1DebloatAsync`: Do not overwrite an existing backup if a partial or previous debloat backup exists; protect against sampling a partially debloated baseline.
   - In `RestoreTier1ServicesAsync`: Validate backup integrity; do not silently swallow invalid backups or assume defaults that lead to deleting the backup.
3. Fix PowerShell Process Lifecycle & Concurrency:
   - In `RunElevatedPowerShellAsync`: 20-second timeout. Do NOT access `p.ExitCode` if timed out (avoids InvalidOperationException).
   - Track active process in `_activePowerShellProcess`. If timed out, retain the process in `_activePowerShellProcess` and do NOT allow new Apply/Restore operations while `_activePowerShellProcess` is still running (`!HasExited`). Return false with a clear status message.
   - When the process finally exits, release the handle and permit subsequent operations.
4. Add Test Seam:
   - Add `internal Func<string, bool, Task<int>>? PowerShellRunner { get; set; }` (or equivalent runner delegate) to allow headless unit tests to simulate elevated PowerShell runs, test timeouts, error propagation, and backup retention safely.
5. Verify:
   - Run `dotnet build -c Release NitroLite/NitroLite.csproj` (must be 0 errors, 0 warnings).
   - Run `dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~Debloat"` to verify existing tests pass.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Write your handoff report to:
C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m2\handoff.md


## 2026-11-05T03:24:44Z
You are Worker 2 (teamwork_worker).
Your working directory is: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m2\
The path to the original user request is: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
Your dispatch file is at: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m2\DISPATCH.md

You MUST read ORIGINAL_REQUEST.md before starting work.
Your task is Milestone M2: Debloat Verified Restore & Process Lifecycle (Requirement R3).
Exclusive write file: NitroLite\Core\DebloatService.cs. DO NOT edit any other file.

Implement the fixes for:
1. `GenerateRestoreScript`:
   - When target status is `Stopped`, emit `Stop-Service -Name '{s}' -Force -ErrorAction Stop;` and verify `$vSvc.Status -eq 'Stopped'`.
   - Correct sequence: set startup type, then Start/Stop service, then verify startup type, then verify running/stopped status.
   - For scheduled task `NitroSenseLauncher`: if recorded state is `Disabled`, emit `Disable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop;` and verify `$vTask.State -eq 'Disabled'`.
   - Ensure all service/task failures append to `$errs` and exit with code 1.
2. Backup Retention & Integrity:
   - Retain `debloat_backup_state.json` on any failure.
   - In `ApplyTier1DebloatAsync`, do not overwrite an existing backup if a partial or previous debloat backup exists; protect against sampling a partially debloated baseline.
   - Validate backup integrity; do not silently swallow invalid backups or assume defaults that lead to deleting the backup.
3. PowerShell Process Lifecycle & Concurrency:
   - In `RunElevatedPowerShellAsync`: 20-second timeout. Do NOT access `p.ExitCode` if timed out.
   - Track active process in `_activePowerShellProcess`. If timed out, retain in `_activePowerShellProcess` and do NOT allow new Apply/Restore operations while `_activePowerShellProcess` is still running (`!HasExited`). Return false with clear status message.
   - When process exits, release handle and permit subsequent operations.
4. Add Test Seam:
   - Add `internal Func<string, bool, Task<int>>? PowerShellRunner { get; set; }` (or equivalent runner delegate) to allow headless unit tests to simulate elevated PowerShell runs safely.

Verify:
Run `dotnet build -c Release NitroLite/NitroLite.csproj` (must compile with 0 errors, 0 warnings).
Run `dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~Debloat"` to ensure tests pass.
