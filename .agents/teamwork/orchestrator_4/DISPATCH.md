## 2026-11-04T23:01:51Z

Fix all 7 verified defects and security issues in NitroLite across DebloatService, AtkMouseService, AcerAgentClient, MainWindow, GameDetectionService, and SettingsManager, ensuring resilient service recovery, safe native HID I/O, accurate hardware state tracking, and regression test coverage.

Integrity mode: development

## Requirements

### R1. High-Priority Security and Stability Fixes
- Validate startup types in debloat_backup_state.json before passing them to elevated PowerShell commands to prevent arbitrary command execution during service restoration (DebloatService.cs:188).
- Eliminate memory corruption risk in ATK mouse HID asynchronous reads: ensure pending I/O and OVERLAPPED buffers are properly completed, safely cancelled, or waited on when timeouts occur or the receiver is disconnected before releasing or exiting (AtkMouseService.cs:330).
- Validate Acer agent MUX and performance mode JSON responses so non-success responses (such as {"result":"99"}) are treated as failures, and ensure MUX options only display modes supported by the device (AcerAgentClient.cs:266, 325; MainWindow.xaml.cs:1121).
- Resolve the AcerLightingService debloat discrepancy between the service list and the confirmation UI/hardware analysis: decide whether to exclude AcerLightingService from debloat to preserve RGB lighting controls and timeout, or retain it in debloat and update the confirmation dialog and documentation to accurately state that keyboard RGB control is disabled (DebloatService.cs:19; MainWindow.xaml.cs:2571).

### R2. Secondary Defect Remediation and State Consistency
- Make service restoration in DebloatService resilient: abort service shutdown if backup state cannot be written, do not force missing backup services to Automatic, and record service running states and scheduled task states prior to modification (DebloatService.cs:271).
- Handle automatic game mode toggling during active gameplay in GameDetectionService: ensure disabling auto game mode does not leave the profile stuck and that prior power and mouse settings are restored when the game exits (GameDetectionService.cs:433).
- Fix backup corruption during settings recovery in SettingsManager: ensure that restoring settings from a malformed settings.json does not overwrite a valid .bak backup file with corrupt data upon saving (SettingsManager.cs:62).

### R3. Test Suite and Regression Coverage
- Add automated regression unit tests in NitroLite.Tests verifying each of the 7 fixes without requiring physical hardware modifications.

## Acceptance Criteria

### Build and Test Execution
- [ ] Application and test project compile in Release configuration with 0 errors and 0 warnings (`dotnet build -c Release`).
- [ ] All existing tests pass without regressions.
- [ ] Automated regression tests covering all 7 addressed issues are added and pass in NitroLite.Tests.

### Security and Validation
- [ ] Service restore script generation rejects invalid or unverified startup type values.
- [ ] SettingsManager preserves existing valid backup (.bak) content during and after corrupted settings recovery.

### Hardware Communication and Service State
- [ ] AtkMouseService cleans up or awaits pending overlapped I/O on timeouts and receiver disconnection without crashing or corrupting memory.
- [ ] AcerAgentClient checks response status codes/values and treats {"result":"99"} or other non-success responses as failed operations.
- [ ] MUX UI presents only hardware-supported modes.
- [ ] AcerLightingService debloat behavior and confirmation dialog are consistent.
- [ ] DebloatService records running state and scheduled task state, aborts on backup write failure, and avoids defaulting missing entries to Automatic.
- [ ] GameDetectionService restores prior settings cleanly when auto mode is disabled while a game is running.
