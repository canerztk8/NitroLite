# Project: NitroLite Restoration, Process Management & Concurrency Hardening

## Architecture
- NitroLite is a lightweight .NET 10 WPF system utility for Acer Nitro laptops.
- Core services:
  - `CpuPowerService`: Manages CPU boost mode via `powrprof.dll` and `powercfg.exe`.
  - `DebloatService`: Safely debloats OEM bloatware services/tasks and verifies full restoration from backup state.
  - `GameProfileService` & `MainWindow`: Handles active game profile transitions, desktop settings restore, fan modes, mouse profiles, and power automation.
  - `PowerAutomationService`: Automatically adjusts performance profiles upon AC/DC power state changes.
  - `NitroLite.Tests`: xUnit regression test suite.

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | powercfg Single Ownership | Replace `using var p` with explicit single ownership in `QueryCurrentBoostModePowerCfg` | M1 (DONE) | Survey E2 |
| 2 | Process Kill & Timeout Verification | Verify exit on kill, retain untracked process in `_hangingProcess`, prevent new writes | M1 (DONE) | Survey E2 |
| 3 | Inverted Query Logic Fix | Fix line 344 in `UpdateCurrentModeAfterFailure` to query active power source directly | M1 (DONE) | Survey E2 |
| 4 | Power Source Mode Tracking | Update `CurrentMode` only when active power source was targeted; expose `IsModeVerified` | M1 (DONE) | Survey E2 |
| 5 | Output Stream Timeouts | Add bounded timeouts and process termination for stdout stream reading in powercfg queries | M1 (DONE) | Survey E2 |
| 6 | Startup Battery Power Check | Query correct active power source on startup instead of hardcoded AC default | M1 (DONE) | Survey E2 |
| 7 | Debloat Restore Stopped Status | Emit `Stop-Service` and verify `Status -eq 'Stopped'` when restoring to Stopped state | M2 (DONE) | Survey E3 |
| 8 | Debloat Task Disabled Verification | Verify scheduled task `Disabled` state and handle missing tasks/services as errors | M2 (DONE) | Survey E3 |
| 9 | PowerShell Process Lifecycle | Track `_activePowerShellProcess`, block subsequent runs across 20s timeout until exit | M2 (DONE) | Survey E3 |
| 10 | Debloat Backup Retention | Retain backup file on any partial failure; reject corrupted/legacy backups | M2 (DONE) | Survey E3 |
| 11 | Desktop Fan Mode Preservation | Save `_savedFanModeBeforeGame` and track `_currentFanMode`; avoid unconditional Auto | M3 (DONE) | Survey E1 |
| 12 | Unattempted Restore Step Retention | Pre-mark restore steps so interruptions retain pending status for unattempted steps | M3 (DONE) | Survey E1 |
| 13 | Success-Only UI Updates | Reflect hardware changes in UI only upon confirmed success; propagate errors | M3 (DONE) | Survey E1 |
| 14 | Tray Retry Menu Action | Bind "Masaüstü ayarlarını yeniden dene" with dynamic enabled state and session gen checks | M3 (DONE) | Survey E1 |
| 15 | Mouse Restore Preservation | Guard mouse restore so subsequent games without mouse profiles do not erase pending target | M3 (DONE) | Survey E1 |
| 16 | Transition Concurrency Lock | Synchronize live profile save and power automation with `_gameProfileTransitionLock` | M3 (DONE) | Survey E1 |
| 17 | Test Seams & Hardware Isolation | Add seams in `CpuPowerService`, `DebloatService`, `AtkMouseService` to avoid live hardware | M4 (DONE) | Survey E2, E3 |
| 18 | Transition & Restore End-to-End Tests | Verify rapid game switches, partial restore failures, retry actions, and session generation | M4 (DONE) | Survey E1, E4 |
| 19 | Process Timeout & Error Tests | Verify powercfg timeouts, hanging process blocking/recovery, and debloat timeout locking | M4 (DONE) | Survey E2, E3 |
| 20 | Clean Release Compilation | Verify `dotnet build -c Release` with 0 errors and 0 warnings, and `dotnet test` all green | M4 (DONE) | Survey E1, E2, E3 |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | CPU Power Process Ownership & Timeout (R2) | `NitroLite/Core/CpuPowerService.cs` | none | DONE |
| M2 | Debloat Verified Restore & Process Lifecycle (R3) | `NitroLite/Core/DebloatService.cs` | none | DONE |
| M3 | Game Profile & Desktop Restore Concurrency (R1) | `NitroLite/MainWindow.xaml.cs`, `NitroLite/Core/PowerAutomationService.cs` | M1, M2 | DONE |
| M4 | Safe Test Verification & CI Green (R4) | `NitroLite.Tests/` | M1, M2, M3 | DONE |

## Interface Contracts
### CpuPowerService ↔ MainWindow / Tests
- `CpuPowerService.SetBoostModeAsync(mode, targetAc, targetDc)`: Thread-safe, non-blocking on hanging processes.
- `CpuPowerService.IsModeVerified`: Boolean property indicating if CurrentMode was verified from active power scheme.
- `CpuPowerService.BypassNativePowerCalls`: Test seam property/delegate for isolated headless testing without spawning powercfg.exe or calling powrprof.dll.

### DebloatService ↔ MainWindow / Tests
- `DebloatService.PowerShellRunner`: Test seam delegate allowing tests to mock elevated PowerShell execution.
- `DebloatService.IsOperationInProgress`: Property indicating if a PowerShell process is currently active or hanging.

### MainWindow ↔ Tray Menu / GameWatcher / PowerAutomation
- `MainWindow.RetryPendingDesktopRestoreAsync()`: Thread-safe, acquires `_gameProfileTransitionLock`, checks session generation.
- `_gameProfileTransitionLock`: Protects all mode transitions, game profile apply/exit, and power automation transitions.

## Code Layout
- `NitroLite/Core/CpuPowerService.cs`: CPU power management & powercfg process execution. Exclusive write owner: Worker M1 / Remediation Worker.
- `NitroLite/Core/DebloatService.cs`: Service/Task debloater, backup generation, verified restoration, PowerShell runner. Exclusive write owner: Worker M2.
- `NitroLite/Core/PowerAutomationService.cs`: Power source transitions. Exclusive write owner: Worker M3.
- `NitroLite/MainWindow.xaml.cs`: Application lifecycle, game profile transitions, desktop restore, tray menu. Exclusive write owner: Worker M3.
- `NitroLite.Tests/`: Unit and integration test suite. Exclusive write owner: Worker M4.
