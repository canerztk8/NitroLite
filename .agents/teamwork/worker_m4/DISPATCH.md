# Dispatch for Worker 4: Milestone M4 (Safe Test Verification & End-to-End Suite)

Working Directory: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m4\
Exclusive Write Ownership:
- NitroLite.Tests/ (Any test file in NitroLite.Tests project)
DO NOT modify production code in NitroLite/!

Inputs to read:
- ORIGINAL_REQUEST.md: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
- PROJECT.md: C:\Users\caner\Documents\antigravity\eager-shannon\PROJECT.md
- Explorer 1, 2, 3 analysis and handoffs
- Worker 1, 2, 3 handoffs

Specific Tasks (Requirement R4):
1. Fix existing dangerous live tests:
   - In `VerifiedDefectsRegressionTests.cs` (around line 1005 `CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock`): set `service.BypassNativePowerCalls = true;` to prevent mutating the host Windows power plan.
   - In `AtkMouseServiceTests.cs` (around line 95 `LiveHardware_QueryAndSwitch_IfConnected`) and any other live mouse tests: ensure they do not send real packets to physical USB HID endpoints unless guarded or mocked via test seam.
2. Implement comprehensive, end-to-end unit and integration tests directly testing production classes (`MainWindow`, `CpuPowerService`, `DebloatService`, `PowerAutomationService`) using the newly added test seams:
   - **Test Case 1 (R4 Transition & Reference)**: Rapid game switch — Game A is pending/in-flight, Game A closes and Game B opens. Verify writes start in correct order and the original desktop reference is preserved.
   - **Test Case 2 (R4 Partial Failure & Tray Retry)**: Desktop restore where one step fails (e.g. fan or boost) but other independent steps continue. Verify failed steps remain pending, and tray retry (`RetryPendingDesktopRestoreAsync`) executes ONLY the remaining pending steps.
   - **Test Case 3 (R4 Retry Interrupted by New Game)**: During desktop retry, a new game starts. Verify the old retry aborts promptly and does not overwrite the new game's profile.
   - **Test Case 4 (R4 Mouse Restore Retention)**: Mouse restore fails on Game A exit. Game B starts with NO mouse profile (`TargetAtkMouseProfile == 0`). Verify the pending mouse restore record is NOT wiped out and survives.
   - **Test Case 5 (R4 powercfg Lifecycle & Timeout)**: `powercfg` process error exit code, timeout, and hanging process blocking. Verify new writes are blocked while an unverified process is active; verify that when the process exits, subsequent writes succeed; verify in query path that `_hangingProcess` handle is not prematurely disposed.
   - **Test Case 6 (R4 Debloat Restore Stopped & Error Aggregation)**: Debloat backup has `Stopped`, fake system has `Running`. If stop fails, verify error is recorded and backup is retained. If stop succeeds, verify `Stopped` state is verified. Also verify missing service/task errors are collected and backup is retained.
   - **Test Case 7 (R4 Debloat Timeout Concurrency Lock & Re-debloat)**: PowerShell process times out (>20s). Verify second Apply/Restore operation is blocked while first process is active. Verify re-debloat after partial restore uses original baseline backup.
3. Verification:
   - `dotnet build -c Release NitroLite.sln`: Must succeed with 0 errors and 0 warnings.
   - `dotnet test NitroLite.Tests/NitroLite.Tests.csproj`: All tests must pass cleanly.
4. MANDATORY INTEGRITY WARNING:
   DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Write your handoff report to:
C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m4\handoff.md


## 2026-11-05T03:54:35Z
[Message] timestamp=2026-11-05T03:54:35Z sender=1eb61985-d596-4a08-ac15-e4c49f4e70de priority=MESSAGE_PRIORITY_HIGH
Tasks assigned:
1. Fix dangerous live tests in existing files (VerifiedDefectsRegressionTests.cs, AtkMouseServiceTests.cs).
2. Implement comprehensive, end-to-end unit and integration tests directly testing production classes (MainWindow, CpuPowerService, DebloatService, PowerAutomationService) using newly added test seams (Test Cases 1 to 7).
3. Verification: dotnet build -c Release NitroLite.sln (0 errors, 0 warnings), dotnet test NitroLite.Tests/NitroLite.Tests.csproj.
4. Integrity mandate: genuine tests, real state/behavior, no cheats/dummy facades.
Write handoff to .agents\teamwork\worker_m4\handoff.md and notify orchestrator.
