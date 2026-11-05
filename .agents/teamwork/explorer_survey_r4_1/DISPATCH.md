# Task Assignment: Explorer Survey 1 — DebloatService & SettingsManager

## Role & Identity
You are `explorer_survey_r4_1` (teamwork_explorer).
Your working directory: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_1`

## Mandatory Rules
- Read `ORIGINAL_REQUEST.md` at `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md` first.
- Read-only exploration agent. DO NOT write or modify application code or tests.
- Investigate the authoritative source of truth in the codebase.
- Write your comprehensive findings to `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_1\survey_report.md` and your handoff to `handoff.md`.

## Investigation Scope
Investigate the following defects in NitroLite:
1. **Startup Type Validation in DebloatService** (`DebloatService.cs:188`):
   - How `debloat_backup_state.json` is read and passed to elevated PowerShell commands.
   - Attack vectors / injection risks (e.g. arbitrary command execution).
   - What startup types are valid (e.g. `Automatic`, `Manual`, `Disabled`, etc.) and how to strictly validate or whitelist them.
2. **AcerLightingService Debloat Discrepancy** (`DebloatService.cs:19`, `MainWindow.xaml.cs:2571`):
   - Check the service list and how `AcerLightingService` is handled in DebloatService vs UI confirmation dialog and hardware analysis.
   - Analyze whether it should be excluded from debloat to preserve RGB lighting controls, or retained and UI/docs updated.
   - Detail the exact lines and changes needed for either approach.
3. **Resilient Service Restoration in DebloatService** (`DebloatService.cs:271`):
   - What happens if backup state cannot be written (abort service shutdown).
   - How missing backup services are handled (avoid forcing them to Automatic).
   - How to record service running states and scheduled task states prior to modification.
4. **SettingsManager Backup Corruption** (`SettingsManager.cs:62`):
   - Trace settings loading and saving flow when `settings.json` is malformed.
   - How `.bak` backup file is handled during load and save, and how to prevent overwriting valid `.bak` with corrupted/default settings.

## Required Output
In `survey_report.md`:
- Detailed code analysis with exact file paths and line numbers.
- Root cause for each of the 4 items.
- Recommended implementation strategy with interface contracts and edge case handling.
- Suggested unit test strategy for these items in `NitroLite.Tests`.


## 2026-11-04T23:04:00Z
Received dispatch message:
Investigate DebloatService and SettingsManager:
1. Startup type validation in DebloatService.cs (line ~188) before passing to elevated PowerShell commands.
2. AcerLightingService debloat discrepancy between service list (DebloatService.cs:19) and UI confirmation (MainWindow.xaml.cs:2571).
3. Resilient service restoration in DebloatService.cs (line ~271): abort service shutdown if backup state cannot be written, do not force missing backup services to Automatic, record running states and scheduled task states.
4. SettingsManager.cs (line ~62): prevent overwriting valid .bak backup file when recovering from malformed settings.json.

Do NOT modify code or tests. Write your findings to c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_1\survey_report.md and write your handoff to handoff.md in your working directory. Then send a completion message with the path to your report.
