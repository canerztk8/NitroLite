# Task Assignment: Explorer Survey 3 — GameDetectionService & Test Infrastructure

## Role & Identity
You are `explorer_survey_r4_3` (teamwork_explorer).
Your working directory: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_3`

## Mandatory Rules
- Read `ORIGINAL_REQUEST.md` at `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md` first.
- Read-only exploration agent. DO NOT write or modify application code or tests.
- Investigate the authoritative source of truth in the codebase.
- Write your comprehensive findings to `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_3\survey_report.md` and your handoff to `handoff.md`.

## Investigation Scope
Investigate the following defects and testing requirements in NitroLite:
1. **GameDetectionService Automatic Game Mode Toggling** (`GameDetectionService.cs:433`):
   - Trace how auto game mode toggling behaves when changed during active gameplay.
   - Investigate why disabling auto game mode leaves profile stuck and why prior power and mouse settings are not restored when the game exits.
   - Determine state tracking needed so prior power plan / fan / mouse settings are cleanly restored on game exit or when toggled off.
2. **NitroLite.Tests Infrastructure and Regression Test Design**:
   - Inspect existing test structure in `NitroLite.Tests`, existing test helpers, mocks, framework (`xUnit`, `Moq` or custom fakes, .NET version).
   - Verify how tests can be executed cleanly (`dotnet test NitroLite.Tests` and `dotnet build -c Release`).
   - Design concrete test cases for all 7 issues:
     1. DebloatService startup type validation in restore script.
     2. AtkMouseService safe I/O cancellation / timeout / disconnect handling.
     3. AcerAgentClient non-success response handling (e.g. `{"result":"99"}`) and MUX supported modes UI.
     4. AcerLightingService debloat behavior & confirmation consistency.
     5. DebloatService resilient restoration (abort on write failure, don't force missing to Automatic, record running & task states).
     6. GameDetectionService auto game mode toggling during gameplay and exit restoration.
     7. SettingsManager corruption recovery preserving `.bak` file.

## Required Output
In `survey_report.md`:
- Detailed code analysis with exact file paths and line numbers.
- State machine analysis for GameDetectionService.
- Test suite architecture and concrete test case specifications for all 7 issues.


## 2026-11-04T23:04:09Z
You are explorer_survey_r4_3. Your working directory is c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_3.
MANDATORY: Read ORIGINAL_REQUEST.md at c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md before starting work.
Also read your assignment in c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_3\DISPATCH.md.

Investigate GameDetectionService & Test Infrastructure:
1. GameDetectionService.cs (line ~433): handle automatic game mode toggling during active gameplay; ensure disabling auto game mode does not leave profile stuck and prior power and mouse settings are restored when the game exits.
2. NitroLite.Tests infrastructure and regression test design for all 7 verified defects without requiring physical hardware modifications. Verify build configuration and test runner commands.

Do NOT modify code or tests. Write your findings to c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_3\survey_report.md and write your handoff to handoff.md in your working directory. Then send a completion message with the path to your report.
