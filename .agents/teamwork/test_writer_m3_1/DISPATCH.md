# Task Assignment: Test Writer M3 — Automated Regression Unit Test Suite

## Role & Identity
You are `test_writer_m3_1` (teamwork_test_writer).
Your working directory: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\test_writer_m3_1`

## Mandatory Rules & Integrity Warning
- MANDATORY: Read `ORIGINAL_REQUEST.md` at `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md` first.
- MANDATORY: Read `PROJECT.md` at `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md`.
- Read test architecture designs in `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_3\survey_report.md` and `explorer_survey_r4_1\survey_report.md`.
- **MANDATORY INTEGRITY WARNING**:
  DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

## Write Ownership
You exclusively own and may edit files in:
- `NitroLite.Tests/`
Do NOT modify any production code in `NitroLite/` or root files.

## Implementation Tasks
Add automated regression unit tests in `NitroLite.Tests` (for example, in a new test file `NitroLite.Tests/VerifiedDefectsRegressionTests.cs` or modular test files) verifying all 7 defects without requiring physical hardware:

1. **DebloatService Startup Type Whitelist Validation**:
   - `ValidateStartupType` throws `ArgumentException` on invalid, empty, or malicious strings (e.g. `"; Invoke-Expression ..."`, `"Bogus"`).
   - Valid inputs (`Automatic`, `Manual`, `Disabled`, case-insensitive) return canonical casing.
   - `GenerateRestoreScript` rejects or skips invalid/unverified startup types.

2. **DebloatService Resilient Backup, State Recording & Restoration**:
   - Verify that unrecorded services are NOT forced to `"Automatic"`.
   - Verify that pre-debloat service running state (`Running` vs `Stopped`) and scheduled task state (`NitroSenseLauncher`) are recorded and respected in restore script.
   - Verify that if backup saving fails, debloat is aborted.

3. **AcerLightingService Debloat Exclusion & Consistency**:
   - Verify `DebloatService.Tier1Services` contains exactly 6 services and does NOT contain `AcerLightingService`.
   - Verify that keyboard RGB lighting controls are preserved.

4. **SettingsManager Backup Preservation During Corrupted Recovery**:
   - When `settings.json` is corrupted, saving settings does NOT overwrite or corrupt an existing valid `settings.json.bak` file.

5. **AtkMouseService Asynchronous HID Overlapped Memory Safety**:
   - Verify that `AtkMouseService` handles cancellation and overlapped completion safely, and that resources (events, buffers) are not released while I/O is pending. Verify safe handling of invalid handles or disconnects.

6. **AcerAgentClient Response Validation & MUX Supported Modes**:
   - Verify `AcerAgentClient.IsSuccessResult` returns `false` for `{"result":"99"}`, `{"result":"1"}`, empty strings, null, or malformed JSON, and `true` only for `"0"` or `"3"`.
   - Verify `MainWindow.IsGpuModeSupported` decodes capability bitmask accurately (Bit 0: Optimus, Bit 1: DiscreteGpu, Bit 2: Automatic/DDS).

7. **GameDetectionService Auto Game Mode Toggling & State Restoration**:
   - Verify that disabling `AutoGameModeEnabled` during active gameplay immediately tears down the active game session and dispatches `GameEnded`.
   - Verify that process exit detection runs even if `AutoGameModeEnabled` is false, ensuring prior power and mouse settings are restored when the game exits.

## Verification Requirements
- Run `dotnet build NitroLite.sln -c Release` — must compile with 0 errors and 0 warnings.
- Run `dotnet test NitroLite.sln` — all baseline tests + all new regression tests must pass 100%.
- Document all test method names, test counts, execution output, and verification commands in `handoff.md`.


## 2026-11-04T23:31:28Z
You are test_writer_m3_1. Your working directory is c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\test_writer_m3_1.
MANDATORY: Read ORIGINAL_REQUEST.md at c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md first.
Read PROJECT.md at c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md.
Read your full instructions in c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\test_writer_m3_1\DISPATCH.md.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

You exclusively own:
- NitroLite.Tests/ (add automated regression tests verifying all 7 defects)

Implement the unit tests, run `dotnet build NitroLite.sln -c Release` and `dotnet test NitroLite.sln`, verify 0 errors/0 warnings and all tests pass (386 + new tests).
Write your test report and results to handoff.md in your working directory and notify me with send_message.
