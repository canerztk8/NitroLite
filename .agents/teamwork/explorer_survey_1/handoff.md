# Handoff Report: Requirement R1 Survey & Concurrency Investigation

**Agent**: Survey Explorer 1 (`teamwork_explorer`)  
**Folder**: `C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_1\`  
**Handoff Type**: Hard (Task Complete)  
**Target Milestone**: Requirement R1 & R4 (Game Profile Transitions, Desktop Restore, Concurrency & Test Seams)  

---

## 1. Observation

Direct observations from source code inspections in `NitroLite` and `NitroLite.Tests`:

### Obs 1: Missing Fan Mode Baseline & Hardcoded Auto Restore
- In `NitroLite/MainWindow.xaml.cs` (lines 45-58), state variables define:
  ```csharp
  private OperatingMode _savedModeBeforeGame = OperatingMode.Default;
  private CpuBoostMode _savedCpuBoostBeforeGame = CpuBoostMode.Aggressive;
  private int _savedMouseProfileBeforeGame = 1;
  ```
  `_savedFanModeBeforeGame` and `_currentFanMode` do not exist.
- In `MainWindow.xaml.cs` (line 1858 in `OnGameEnded` and line 1999 in `RetryPendingDesktopRestoreAsync`):
  ```csharp
  bool fanOk = await _agentClient.SetFanModeAsync(FanMode.Auto);
  ```
  and
  ```csharp
  bool ok = await _agentClient.SetFanModeAsync(FanMode.Auto);
  ```
  Fan mode restoration unconditionally forces `FanMode.Auto`, wiping out pre-game user settings such as `FanMode.Max`.
- In `MainWindow.xaml.cs` (line 199):
  ```csharp
  _powerAutomationService.GetCurrentFanMode = () => FanMode.Auto;
  ```
  Hardcodes Auto for power automation queries as well.
- In `MainWindow.xaml.cs` (lines 2772 and 2785):
  `BtnFanAuto_Click` and `BtnFanMax_Click` invoke `await _agentClient.SetFanModeAsync(...)` without checking the returned boolean value, and do not track the active fan state.

### Obs 2: Loss of Unattempted Restore Steps on Interrupted Exit
- In `MainWindow.xaml.cs` (lines 1826-1868):
  `_pendingModeRestore`, `_pendingCpuBoostRestore`, `_pendingFanRestore`, and `_pendingMouseRestore` are initialized to `false`.
  In `OnGameEnded`:
  ```csharp
  // 1. Operating mode
  if (endGen != _gameSessionGeneration) return;
  bool modeOk = await SwitchModeAsync(_savedModeBeforeGame);
  if (modeOk) _pendingModeRestore = false;
  else { _pendingModeRestore = true; failedSteps.Add("Çalışma Modu"); }

  // 2. CPU Boost mode
  if (endGen != _gameSessionGeneration) return;
  bool boostOk = await ApplyCpuBoostModeAsync(_savedCpuBoostBeforeGame);
  ```
  If `endGen != _gameSessionGeneration` triggers at line 1844 (e.g. rapid game switch to Game B), execution exits immediately.
  `_pendingCpuBoostRestore`, `_pendingFanRestore`, and `_pendingMouseRestore` remain `false`.
- In `RetryPendingDesktopRestoreAsync` (lines 1968-2035):
  ```csharp
  if (_pendingCpuBoostRestore) { ... }
  if (_pendingFanRestore) { ... }
  ```
  Because the unattempted flags are `false`, `RetryPendingDesktopRestoreAsync` skips them, clears `_hasPendingDesktopRestore = false;`, and returns `true`, producing a false success toast without ever restoring CPU boost or Fan.

### Obs 3: Optimistic and Premature UI Updates
- In `MainWindow.xaml.cs` (lines 1767-1779):
  ```csharp
  TxtGameStatus.Text = $"Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} / {boostName}{mouseInfo})";
  TxtGameStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");

  await SwitchModeAsync(profile.TargetMode);
  if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

  await ApplyCpuBoostModeAsync(profile.TargetCpuBoost);
  if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

  await _agentClient.SetFanModeAsync(profile.TargetFanMode);
  ```
  `TxtGameStatus` is rendered as active green *prior* to dispatching `SwitchModeAsync`, `ApplyCpuBoostModeAsync`, and `SetFanModeAsync`.
- In line 1782:
  `_notifyIcon?.ShowBalloonTip(3000, "NitroLite", ...)` is called even if hardware commands return `false`.
- In `BtnSaveGame_Click` (lines 1584-1610): `ApplyCpuBoostModeAsync` and `SetFanModeAsync` return values are ignored, yet success toast is displayed.
- In `PowerAutomationService.cs` (lines 140-156 and 177-195): Exceptions are caught and swallowed (`catch { }`), boolean return values are ignored, and `PowerModeSwitched` is raised unconditionally, leading to `MainWindow` lines 205-217 updating mode cards optimistically.

### Obs 4: Missing Tray Menu Item & Generation Check in Retry
- In `MainWindow.xaml.cs` (lines 141-157 in `SetupTrayIcon()`):
  The context menu contains `Göster / Aç`, `Sessiz Mod`, `Dengeli Mod`, `Performans Modu`, `Turbo Mod`, `CPU Boost: Devre Dışı`, `CPU Boost: Agresif`, and `Çıkış`.
  "Masaüstü ayarlarını yeniden dene" (Retry pending desktop restore) is completely missing.
- In `RetryPendingDesktopRestoreAsync` (lines 1966-2045):
  `_gameSessionGeneration` is never read or checked. If a new game starts while retry is executing on Dispatcher, retry continues writing desktop settings right over the new game's profile.

### Obs 5: Destruction of Pending Mouse Restore Record
- In `MainWindow.xaml.cs` (lines 1898-1902 in `OnGameEnded`):
  ```csharp
  else
  {
      _isMouseProfileSwitchedForGame = false;
      _pendingMouseRestore = false;
  }
  ```
  If Game A had a failed mouse restore (`_pendingMouseRestore = true`), and Game B has no mouse profile (`TargetAtkMouseProfile == 0`), Game B leaves `_isMouseProfileSwitchedForGame = false`. When Game B exits, line 1901 forcibly executes `_pendingMouseRestore = false;`, destroying the pending restore record.
- In `MainWindow.xaml.cs` (lines 1737-1739):
  `if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled)` contains inner guard `if (mouseOk && _appSettings.AtkMouseIntegrationEnabled && sessionGen == _gameSessionGeneration)`. Because the outer condition requires `sessionGen != _gameSessionGeneration`, the inner check is logically impossible when session generation changes.

### Obs 6: Unsynchronized Live Profile Save & Power Automation
- In `MainWindow.xaml.cs` (lines 1582-1615):
  `BtnSaveGame_Click` hot-applies settings to a running game without acquiring `_gameProfileTransitionLock` and without checking session generation.
- In `PowerAutomationService.cs` (lines 118-208):
  `ProcessPowerTransitionAsync` does not acquire `_gameProfileTransitionLock`, allowing concurrent hardware writes to EC and powercfg while game transitions are active.

### Obs 7: Test Coverage and Architecture
- `NitroLite.Tests` passes 581 tests (`dotnet test NitroLite.Tests/NitroLite.Tests.csproj` in 17s).
- Grep search for `_gameProfileTransitionLock`, `OnGameStarted`, `OnGameEnded`, and `RetryPendingDesktopRestoreAsync` across `NitroLite.Tests` yielded 0 results.
- `MainWindow` has no dependency injection constructor or test seams; its dependencies are private `new()` fields.

---

## 2. Logic Chain

1. **Premise 1**: Requirement R1 requires that desktop fan settings be preserved and not unconditionally set to `Auto` upon return to desktop.
   - *Supporting Evidence*: Obs 1 proves `MainWindow` has no field to record the previous fan mode and lines 1858, 1999, and 199 explicitly hardcode `FanMode.Auto`.
   - *Deduction*: When the user selects `FanMode.Max` prior to a game, game exit overwrites it with `Auto`. A new `_savedFanModeBeforeGame` and `_currentFanMode` must be introduced and tracked.

2. **Premise 2**: Requirement R1 mandates that when an operation is interrupted before reaching the next step, unattempted steps must not be lost.
   - *Supporting Evidence*: Obs 2 proves that `_pendingCpuBoostRestore`, `_pendingFanRestore`, etc. are only set if an attempt fails. If an abort occurs before the step is reached, the flags remain `false`.
   - *Deduction*: Pre-marking all changed properties as pending *before* the sequential restore attempts guarantees that any interruption leaves remaining steps flagged as `pending`, allowing retry to complete them.

3. **Premise 3**: Requirement R1 requires donanım (hardware) changes to be reflected in UI only upon confirmed success.
   - *Supporting Evidence*: Obs 3 proves `TxtGameStatus`, toast alerts, and balloon tips are shown before writes finish or when boolean results are ignored.
   - *Deduction*: Hardware calls must be verified before mutating UI text, status cards, or displaying success notifications.

4. **Premise 4**: Requirement R1 requires `RetryPendingDesktopRestoreAsync` to be bound to a tray menu item, enabled only when pending work exists, and guarded against race conditions with new games.
   - *Supporting Evidence*: Obs 4 proves the tray menu item is absent and `RetryPendingDesktopRestoreAsync` lacks `_gameSessionGeneration` checks.
   - *Deduction*: Adding the menu item with dynamic `Enabled` binding and injecting generation checks before each step in `RetryPendingDesktopRestoreAsync` eliminates overwrite vulnerabilities.

5. **Premise 5**: Requirement R1 mandates preserving pending mouse restore across games without mouse profiles.
   - *Supporting Evidence*: Obs 5 proves line 1901 resets `_pendingMouseRestore = false;` in the `else` branch.
   - *Deduction*: Removing this destructive assignment and guarding mouse restore on `_pendingMouseRestore || _isMouseProfileSwitchedForGame` ensures the desktop mouse target remains protected.

6. **Premise 6**: Requirement R4 requires verifying these flows directly against production code with minimal test seams and safe mocks.
   - *Supporting Evidence*: Obs 7 proves `MainWindow` is currently untestable without seams and has 0 tests for transitions.
   - *Deduction*: Introducing an internal constructor and exposing transition tasks (`HandleGameStartedAsync`, `HandleGameEndedAsync`) allows `NitroLite.Tests` to execute complete end-to-end transition tests safely.

---

## 3. Caveats

1. **No Source Modification in Explorer Phase**: In accordance with the Explorer archetype and dispatch constraints, no production or test files were modified during this investigation.
2. **WPF Dispatcher in Test Runner**: Because `MainWindow` uses `Dispatcher.InvokeAsync`, unit tests running on background threads must either run within an STA thread dispatcher frame or utilize an internal test seam that completes the inner tasks synchronously or via a mocked dispatcher seam.
3. **Hardware Isolation**: All tests in R4 must strictly avoid real `powercfg`, Windows power scheme, and ATK USB endpoints by providing safe mocks (`MockAcerAgentClient`, `MockCpuPowerService`, `MockAtkMouseService`).

---

## 4. Conclusion

The entire architecture and state machine of Requirement R1 has been surveyed, mapped, and diagnosed. Eleven distinct bugs and race conditions have been pinpointed to exact lines in `MainWindow.xaml.cs` and `PowerAutomationService.cs`. 

A comprehensive remediation plan, code changes, and test matrix have been documented in:
`C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_1\analysis.md`

All findings are actionable, scoped, and ready for immediate implementation by the subsequent implementation specialist.

---

## 5. Verification Method

To verify the findings and assess future fixes:

1. **Code Inspection**:
   - Inspect `MainWindow.xaml.cs` lines 45-58, 141-157, 1681-1799, 1801-1964, 1966-2045, 2768-2792.
   - Inspect `PowerAutomationService.cs` lines 118-208.
2. **Current Test Run**:
   - Command: `dotnet test NitroLite.Tests/NitroLite.Tests.csproj`
   - Expected Result: 581 tests pass (verifies current baseline is clean).
3. **Post-Implementation Verification**:
   - Run new R1 test suite in `NitroLite.Tests` covering all 9 test scenarios specified in `analysis.md` Section 5.
   - Build Release configuration with zero warnings and zero errors:
     `dotnet build NitroLite/NitroLite.csproj -c Release`
     `dotnet build NitroLite.Tests/NitroLite.Tests.csproj -c Release`
