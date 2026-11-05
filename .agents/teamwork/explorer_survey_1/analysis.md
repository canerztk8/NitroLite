# Requirement R1 In-Depth Survey & Architectural Analysis Report

**Date**: 2026-11-05  
**Investigator**: Survey Explorer 1 (`teamwork_explorer`)  
**Scope**: Requirement R1 (Game Profile transitions, desktop restore, concurrency, fan mode, tray retry, UI synchronization, power automation) and related R4 test validation.  
**Target Repository**: `NitroLite` (`NitroLite/MainWindow.xaml.cs`, `NitroLite/Core/PowerAutomationService.cs`, `NitroLite/Core/AtkMouseService.cs`, `NitroLite.Tests/`)  

---

## 1. Executive Summary

A comprehensive architectural and line-by-line survey of the `NitroLite` codebase was conducted with a focus on **Requirement R1** (Game Profile Application and Desktop Return Consistency) and its testability under **Requirement R4**.

The investigation revealed that while a preliminary structure using `_gameProfileTransitionLock`, `_gameSessionGeneration`, and `operation.Task.Unwrap()` was introduced in `MainWindow.xaml.cs`, it suffers from **11 severe defects and concurrency hazards**:

1. **Unconditional Fan Mode Auto Overwrite**: The desktop baseline completely lacks tracking of `_savedFanModeBeforeGame` and `_currentFanMode`. On game exit and tray retry, `FanMode.Auto` is hardcoded, destroying the user's pre-game custom/maximum fan configuration.
2. **Loss of Unattempted Restore Steps on Interruption**: `_pendingModeRestore`, `_pendingCpuBoostRestore`, `_pendingFanRestore`, and `_pendingMouseRestore` are only set to `true` inside `OnGameEnded` after a step is attempted and fails. If execution is interrupted prior to reaching a step (e.g., generation increment on rapid game switch), unattempted steps remain `false`, causing the restore state to be permanently lost and tray retry to report false completion.
3. **Optimistic/Premature UI Updates**: `TxtGameStatus`, toast messages, and mode buttons are updated *before* hardware writes complete or even when hardware writes return `false`. `BtnFanAuto_Click`, `BtnFanMax_Click`, and `BtnSaveGame_Click` completely discard the boolean return value of `SetFanModeAsync` and `ApplyCpuBoostModeAsync`.
4. **Missing Tray Menu Item**: The "Masaüstü ayarlarını yeniden dene" (Retry desktop settings) action does not exist in `_notifyIcon.ContextMenuStrip`.
5. **Generation Check Missing in Tray Retry**: `RetryPendingDesktopRestoreAsync()` fails to capture or verify `_gameSessionGeneration` across its execution steps. If a new game launches while a retry is executing, the retry continues writing obsolete desktop settings right over the new game profile.
6. **False Success Notification on Partial Failure**: If unattempted steps were left at `false`, `RetryPendingDesktopRestoreAsync()` clears `_hasPendingDesktopRestore` and displays a success toast despite no hardware writes having succeeded.
7. **Destruction of Pending Mouse Restore Record**: In `OnGameEnded`, an `else` branch unconditionally resets `_pendingMouseRestore = false;` if `_isMouseProfileSwitchedForGame` is false. When a subsequent game launches without a mouse profile (`TargetAtkMouseProfile == 0`), exiting that game destroys the previous pending mouse restore record.
8. **Broken Stale-Session Mouse Rollback Guard**: In `OnGameStarted`, line 1739 checks `sessionGen == _gameSessionGeneration` inside an enclosing `if (sessionGen != _gameSessionGeneration || ...)`, making rollback unreachable when a session is superseded by a new game.
9. **Unsynchronized Live Profile Save**: `BtnSaveGame_Click` executes real-time hardware writes without acquiring `_gameProfileTransitionLock` and without verifying session generation, racing with `OnGameEnded` and `OnGameStarted`.
10. **Unsynchronized Power Automation**: `PowerAutomationService` issues hardware writes without locking `_gameProfileTransitionLock`. If an AC/DC transition occurs during game profile transition or desktop restore, hardware commands collide on the serial/TCP pipeline. Furthermore, AC/DC transitions mutate `_currentMode` without verifying hardware write success.
11. **Zero Test Coverage & Missing Test Seams**: `MainWindow` cannot be tested directly because all services are instantiated with private `new()` expressions, and `OnGameStarted`/`OnGameEnded` are `async void` with no returned `Task`. `NitroLite.Tests` contains zero tests for `_gameProfileTransitionLock`, `OnGameStarted`, `OnGameEnded`, or `RetryPendingDesktopRestoreAsync`.

---

## 2. Codebase Map for Requirement R1

### 2.1 Affected Files and Roles

| File Path | Role in R1 / R4 |
|---|---|
| `NitroLite/MainWindow.xaml.cs` | Hosts `_gameProfileTransitionLock`, session generation, `OnGameStarted`, `OnGameEnded`, `RetryPendingDesktopRestoreAsync`, tray menu setup, manual mode handlers, and live profile hot-apply. |
| `NitroLite/Core/PowerAutomationService.cs` | Handles automated AC/DC transitions (`ProcessPowerTransitionAsync`). Must serialize with `_gameProfileTransitionLock` and respect active game sessions. |
| `NitroLite/Core/AtkMouseService.cs` | Handles ATK HID mouse profile switching (`SwitchProfile`). Must support test seams (virtual method or mock interface) to satisfy R4 without real hardware. |
| `NitroLite/Core/AcerAgentClient.cs` | Hardware client for Operating Mode, Fan Mode, and Lighting. Virtual methods already exist (`SetOperatingModeAsync`, `SetFanModeAsync`). |
| `NitroLite/Core/CpuPowerService.cs` | Hardware client for CPU boost modes via `powercfg`. Virtual methods already exist (`SetBoostModeAsync`). |
| `NitroLite/Core/GameDetectionService.cs` | Detects game launch/exit and raises `GameStarted` and `GameEnded` events. |
| `NitroLite.Tests/MockHardwareServices.cs` | Mock implementations for hardware tests (`MockAcerAgentClient`, `MockCpuPowerService`). |
| `NitroLite.Tests/GameProfileTransitionTests.cs` | **New Test Suite Required**: Integration and unit tests verifying all R1 requirements using safe mocks. |

### 2.2 Key State Variables in `MainWindow.xaml.cs`

```csharp
// --- Concurrency & Session Generation ---
private readonly SemaphoreSlim _gameProfileTransitionLock = new(1, 1);
private int _gameSessionGeneration = 0;

// --- Session & State Flags (Requirement: Separate Meanings) ---
private bool _isGameSessionActive = false;           // Indicates a game process is currently running
private bool _hasPendingDesktopRestore = false;       // Indicates a desktop baseline is saved & restore is pending

// --- Original Desktop Baseline (Saved before first game alteration) ---
private OperatingMode _savedModeBeforeGame = OperatingMode.Default;
private CpuBoostMode _savedCpuBoostBeforeGame = CpuBoostMode.Aggressive;
private int _savedMouseProfileBeforeGame = 1;
// [DEFECT]: Missing _savedFanModeBeforeGame!
// [DEFECT]: Missing _currentFanMode!

// --- Step-level Pending Restore Flags ---
private bool _pendingModeRestore = false;
private bool _pendingCpuBoostRestore = false;
private bool _pendingFanRestore = false;
private bool _pendingMouseRestore = false;
private bool _isMouseProfileSwitchedForGame = false;

// --- Active Current Hardware State (UI-synced on success only) ---
private OperatingMode _currentMode = OperatingMode.Default;
```

---

## 3. Detailed Analysis of Requirement R1 Sub-components & Identified Bugs

### 3.1 Transition Lock, Session Generation & Task.Unwrap() Lifecycle

#### Current Implementation (`MainWindow.xaml.cs` lines 1681-1799 and 1801-1964):
```csharp
private async void OnGameStarted(GameProfile profile)
{
    if (Dispatcher.HasShutdownStarted) return;
    int sessionGen = Interlocked.Increment(ref _gameSessionGeneration);
    try
    {
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

            var operation = Dispatcher.InvokeAsync(async () =>
            {
                if (Dispatcher.HasShutdownStarted || sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;
                try
                {
                    ...
                }
                catch (Exception ex) { ... }
            });
            await operation.Task.Unwrap();
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }
    catch { }
}
```

#### Observations & Evaluation:
- **Preserved Pattern**: The outer method awaits `_gameProfileTransitionLock.WaitAsync()`. Inside the lock, `Dispatcher.InvokeAsync(async () => { ... })` schedules work onto the WPF UI thread, returning a `DispatcherOperation<Task>`. `await operation.Task.Unwrap()` keeps the outer method paused while the inner asynchronous task executes on the UI thread. The `finally` block releases `_gameProfileTransitionLock` only after all inner async operations finish.
- **Problem**: Because `OnGameStarted` and `OnGameEnded` are `async void`, callers cannot await their full completion. When Game A triggers `GameEnded` and Game B triggers `GameStarted`, tests or coordinators cannot observe when the entire transition has settled.
- **Fix Recommendation**: Extract internal async methods returning `Task`:
  - `internal async Task HandleGameStartedAsync(GameProfile profile)`
  - `internal async Task HandleGameEndedAsync(GameProfile profile)`
  - Keep `OnGameStarted` and `OnGameEnded` as thin `async void` adapters invoking and awaiting these tasks.

---

### 3.2 Desktop Baseline Reference Preservation & Overwrite Prevention

#### Requirement:
> "Masaüstü referansını ilk oyun değişikliğinden önce kaydet; önceki geri yükleme bekliyorsa veya yarıda kesildiyse yeni oyun bu referansı ezmemelidir."

#### Current Implementation (`MainWindow.xaml.cs` lines 1700-1707):
```csharp
if (!_hasPendingDesktopRestore)
{
    _savedModeBeforeGame = _currentMode;
    _savedCpuBoostBeforeGame = _cpuPowerService.CurrentMode;
    _savedMouseProfileBeforeGame = _atkMouseService.CurrentActiveProfile > 0 ? _atkMouseService.CurrentActiveProfile : 1;
    _hasPendingDesktopRestore = true;
}
```

#### Defects Found:
1. **Missing Fan Mode Baseline**: `_savedFanModeBeforeGame` is not saved.
2. **Premature Clearing Risk**: If `_hasPendingDesktopRestore` was cleared to `false` during an incomplete restore (see Section 3.6), the launch of Game B treats the current modified system state as the desktop baseline, permanently corrupting the baseline!
3. **Definition of "Pending Work"**: A desktop restore is pending if `_hasPendingDesktopRestore || _pendingModeRestore || _pendingCpuBoostRestore || _pendingFanRestore || _pendingMouseRestore`. The check must be:
   ```csharp
   if (!HasPendingDesktopRestore)
   {
       _savedModeBeforeGame = _currentMode;
       _savedCpuBoostBeforeGame = _cpuPowerService.CurrentMode;
       _savedFanModeBeforeGame = _currentFanMode;
       _savedMouseProfileBeforeGame = _atkMouseService.CurrentActiveProfile > 0 ? _atkMouseService.CurrentActiveProfile : 1;
       _hasPendingDesktopRestore = true;
   }
   ```

---

### 3.3 Fan Mode Handling: Auto vs Previous Mode

#### Requirement:
> "Fan dahil gerçekten değiştirilen ayarların önceki durumunu koru; kullanıcı oyun öncesinde maksimum fan seçmişse geri dönüşte koşulsuz Auto uygulama."
> "Fan ayarı dahil yalnızca değiştirilen donanım durumları geri yüklenir; kullanıcı oyun öncesi özel/maksimum fan seçtiyse geri dönüşte Auto zorlanmaz."

#### Current Implementation:
- Line 1858 in `OnGameEnded`:
  ```csharp
  bool fanOk = await _agentClient.SetFanModeAsync(FanMode.Auto);
  ```
- Line 1999 in `RetryPendingDesktopRestoreAsync`:
  ```csharp
  bool ok = await _agentClient.SetFanModeAsync(FanMode.Auto);
  ```
- Line 199 in `MainWindow.xaml.cs`:
  ```csharp
  _powerAutomationService.GetCurrentFanMode = () => FanMode.Auto;
  ```
- Lines 2768-2792: `BtnFanAuto_Click` and `BtnFanMax_Click` ignore return values and do not track `_currentFanMode`.

#### Defects Found:
1. `MainWindow` has no `_currentFanMode` or `_savedFanModeBeforeGame` field.
2. If the user clicked `BtnFanMax` on desktop (e.g., preparing for heavy compilation or gaming), when a game starts and ends, NitroLite unconditionally forces `FanMode.Auto`, overriding the user's explicit preference.
3. If the game profile specifies `TargetFanMode = FanMode.Auto` and the pre-game desktop was already `FanMode.Auto`, NitroLite still issues redundant fan control writes on start and exit.

#### Fix Recommendation:
1. Introduce `private FanMode _currentFanMode = FanMode.Auto;` and `private FanMode _savedFanModeBeforeGame = FanMode.Auto;`.
2. Update `_currentFanMode` in `BtnFanAuto_Click` and `BtnFanMax_Click` upon verified success (`if (ok) _currentFanMode = mode;`).
3. Set `_powerAutomationService.GetCurrentFanMode = () => _currentFanMode;`.
4. In `OnGameStarted`: `_savedFanModeBeforeGame = _currentFanMode;`.
5. Only write fan changes if `profile.TargetFanMode != _currentFanMode`.
6. In `OnGameEnded` and `RetryPendingDesktopRestoreAsync`:
   ```csharp
   bool fanOk = await _agentClient.SetFanModeAsync(_savedFanModeBeforeGame);
   if (fanOk)
   {
       _currentFanMode = _savedFanModeBeforeGame;
       _pendingFanRestore = false;
   }
   ```

---

### 3.4 Hardware-to-UI Synchronization: Success-Only Updates

#### Requirement:
> "Donanım değişikliğini yalnızca başarıdan sonra arayüze yansıt; başarısız veya sonucu belirsiz yazmaları başarılı kabul etme."

#### Defects Found in Current Code:
1. **Premature UI Update in `OnGameStarted`** (lines 1767-1779):
   ```csharp
   TxtGameStatus.Text = $"Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} / {boostName}{mouseInfo})";
   TxtGameStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");

   await SwitchModeAsync(profile.TargetMode);
   await ApplyCpuBoostModeAsync(profile.TargetCpuBoost);
   await _agentClient.SetFanModeAsync(profile.TargetFanMode);
   ```
   `TxtGameStatus` declares full activation *before* `SwitchModeAsync`, `ApplyCpuBoostModeAsync`, and `SetFanModeAsync` are even dispatched!
2. **Ignored Return Values**:
   - `OnGameStarted` ignores return values of `SwitchModeAsync`, `ApplyCpuBoostModeAsync`, and `SetFanModeAsync`. Even if all fail, line 1782 shows a success balloon tip:
     ```csharp
     _notifyIcon?.ShowBalloonTip(3000, "NitroLite", $"{profile.DisplayName} algılandı. Profil: {profile.TargetMode} / {boostName}{mouseInfo}", ToolTipIcon.Info);
     ```
   - In `BtnFanAuto_Click` (line 2772) and `BtnFanMax_Click` (line 2785):
     ```csharp
     await _agentClient.SetFanModeAsync(FanMode.Auto);
     ShowToast("Otomatik fan eğrisi etkinleştirildi."); // Shown even if SetFanModeAsync returns false!
     ```
   - In `BtnSaveGame_Click` (lines 1584-1610): Return values of `ApplyCpuBoostModeAsync` and `SetFanModeAsync` are completely ignored, yet success toast is displayed.
   - In `PowerAutomationService` (lines 140-164 and 177-207): Exceptions and boolean return values are swallowed (`catch { }`), and `PowerModeSwitched` is raised unconditionally. In `MainWindow.xaml.cs` (lines 207-215), `UpdateModeUi` and `UpdateBoostUi` are invoked unconditionally.

#### Fix Recommendation:
- Track boolean results of each hardware write (`modeOk`, `boostOk`, `fanOk`, `mouseOk`).
- In `OnGameStarted`: Only display `Aktif: ...` and balloon tip if the core profile settings succeeded. If any failed, display a warning toast (`ShowToast("Bazı oyun donanım ayarları uygulanamadı.", false)`).
- In fan click handlers, verify `bool ok = await _agentClient.SetFanModeAsync(...)`. Update `_currentFanMode` and display success toast only if `ok == true`.
- In `PowerAutomationService`, evaluate return values and propagate verified status to `PowerModeSwitched`.

---

### 3.5 Active Game & Battery Monitoring Flags vs Pending Restore Steps

#### Requirement:
> "Oyun kapandığında aktif oyun ve pil izleme bayraklarını kapat; başarısız geri yükleme adımlarını ve özgün hedeflerini koru."
> "Aktif oyun bilgisi, özgün masaüstü ayarları ve bekleyen geri yükleme adımları birbirinden ayrı anlam taşımalıdır."

#### Analysis:
There are three independent operational concepts that must never be conflated:
1. **Active Game Status**:
   - `_isGameSessionActive = false;`
   - `_batteryService.IsGameSessionActive = false;`
   - Must be reset **immediately** upon game exit under `_gameProfileTransitionLock`.
2. **Original Desktop Targets**:
   - `_savedModeBeforeGame`
   - `_savedCpuBoostBeforeGame`
   - `_savedFanModeBeforeGame`
   - `_savedMouseProfileBeforeGame`
   - Must be retained until **all** corresponding restore steps have succeeded.
3. **Pending Restore Steps**:
   - `_pendingModeRestore`
   - `_pendingCpuBoostRestore`
   - `_pendingFanRestore`
   - `_pendingMouseRestore`
   - `_hasPendingDesktopRestore = _pendingModeRestore || _pendingCpuBoostRestore || _pendingFanRestore || _pendingMouseRestore;`

When Game A ends:
- Immediately clear `_isGameSessionActive` and `_batteryService.IsGameSessionActive`.
- Keep `_hasPendingDesktopRestore = true` and `_saved*BeforeGame` intact as long as any step remains unfulfilled.

---

### 3.6 Preservation of Unattempted Steps on Interruption

#### Requirement:
> "İşlem bir sonraki adıma ulaşmadan kesildiğinde henüz denenmemiş adımların kaybolmasını önle."

#### The Flawed Flow in Current `OnGameEnded`:
```csharp
// 1. Operating mode
if (endGen != _gameSessionGeneration) return;
bool modeOk = await SwitchModeAsync(_savedModeBeforeGame);
if (modeOk) _pendingModeRestore = false;
else { _pendingModeRestore = true; failedSteps.Add("Çalışma Modu"); }

// 2. CPU Boost mode
if (endGen != _gameSessionGeneration) return;
bool boostOk = await ApplyCpuBoostModeAsync(_savedCpuBoostBeforeGame);
...
```

#### What Happens When Interrupted:
1. Game A exits. `OnGameEnded` starts.
2. Step 1 (`SwitchModeAsync`) completes.
3. Meanwhile, Game B starts or generation increments.
4. At line 1844, `if (endGen != _gameSessionGeneration) return;` triggers and aborts!
5. **Critique**:
   - `_pendingCpuBoostRestore` is still `false` (it was never set to `true`).
   - `_pendingFanRestore` is still `false`.
   - `_pendingMouseRestore` is still `false`.
   - Lines 1906-1917 never execute.
   - When the user subsequently clicks "Masaüstü ayarlarını yeniden dene", `RetryPendingDesktopRestoreAsync()` evaluates:
     `_pendingModeRestore == false` (succeeded), `_pendingCpuBoostRestore == false` (never attempted), etc.
   - It skips all steps, clears `_hasPendingDesktopRestore = false`, and claims "Bekleyen masaüstü ayarları başarıyla geri yüklendi"!
   - The CPU boost and Fan settings remain stuck in Game A's profile forever!

#### Fix Recommendation:
**Pre-mark all modified/unrestored steps as pending BEFORE entering the sequential execution chain**:
```csharp
// If this is the start of restore, mark all changed hardware properties as pending:
if (modeChanged) _pendingModeRestore = true;
if (boostChanged) _pendingCpuBoostRestore = true;
if (fanChanged) _pendingFanRestore = true;
if (mouseChanged) _pendingMouseRestore = true;
_hasPendingDesktopRestore = true;

// Step 1: Mode
if (endGen != _gameSessionGeneration) return;
if (_pendingModeRestore)
{
    bool modeOk = await SwitchModeAsync(_savedModeBeforeGame);
    if (modeOk) _pendingModeRestore = false;
    else failedSteps.Add("Çalışma Modu");
}

// Step 2: CPU Boost
if (endGen != _gameSessionGeneration) return;
if (_pendingCpuBoostRestore)
{
    bool boostOk = await ApplyCpuBoostModeAsync(_savedCpuBoostBeforeGame);
    if (boostOk) _pendingCpuBoostRestore = false;
    else failedSteps.Add("CPU Boost");
}

// Step 3: Fan
if (endGen != _gameSessionGeneration) return;
if (_pendingFanRestore)
{
    bool fanOk = await _agentClient.SetFanModeAsync(_savedFanModeBeforeGame);
    if (fanOk)
    {
        _currentFanMode = _savedFanModeBeforeGame;
        _pendingFanRestore = false;
    }
    else failedSteps.Add("Fan Modu");
}
```
If execution returns at ANY generation check:
- Completed steps have `_pending* = false`.
- Unattempted steps remain `_pending* = true`!
- Nothing is lost. Tray retry will pick up exactly the unattempted/failed steps!

---

### 3.7 In-Flight Hardware Command Completion & Lock Handover

#### Requirement:
> "Yeni oyun başladığında eski akışın o anda başlamış donanım işlemini güvenli şekilde tamamlayıp sonraki komutu göndermeden kilidi devretmesini sağla."

#### Analysis:
When Game A is awaiting a TCP response from `AcerAgentClient` or a process exit from `powercfg`:
1. The low-level asynchronous task must run to completion so TCP streams or process handles are not left in an orphaned/corrupted state.
2. Immediately upon resuming from `await`, the code checks `if (sessionGen != _gameSessionGeneration) return;`.
3. If true, it returns immediately without issuing any further hardware commands.
4. The enclosing `finally { _gameProfileTransitionLock.Release(); }` block executes, cleanly handing the lock to the queued new game operation.

This pattern is verified to be sound in structure, **provided** that every async command in `OnGameStarted`, `OnGameEnded`, and `RetryPendingDesktopRestoreAsync` has a generation check immediately preceding the next write.

---

### 3.8 Mouse Profile Pending Restore Record Protection

#### Requirement:
> "Fareye ait önceki bekleyen dönüş kaydını, yeni oyunda fare profili bulunmaması veya yeni fare komutunun başarısız olması durumunda silme."
> "Fare dönüş kaydı yeni oyunda fare profili olmasa veya yazma başarısız olsa dahi silinmez."

#### Defects Found:
1. **Destructive `else` branch in `OnGameEnded`** (lines 1898-1902):
   ```csharp
   if (_appSettings.AtkMouseIntegrationEnabled && _isMouseProfileSwitchedForGame)
   {
       ...
   }
   else
   {
       _isMouseProfileSwitchedForGame = false;
       _pendingMouseRestore = false; // BUG: Destroys pending mouse restore!
   }
   ```
   If Game A had a failed mouse restore (`_pendingMouseRestore = true`), and Game B has no mouse profile (`TargetAtkMouseProfile == 0`), Game B never sets `_isMouseProfileSwitchedForGame = true`. When Game B exits, the `else` branch wipes out `_pendingMouseRestore = false;`!
2. **Broken Rollback Guard in `OnGameStarted`** (lines 1737-1739):
   ```csharp
   if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled)
   {
       if (mouseOk && _appSettings.AtkMouseIntegrationEnabled && sessionGen == _gameSessionGeneration)
   ```
   If `sessionGen != _gameSessionGeneration`, `sessionGen == _gameSessionGeneration` can never evaluate to true!

#### Fix Recommendation:
- In `OnGameStarted`: If Game B has `TargetAtkMouseProfile <= 0` or if mouse write fails, **do not touch** `_pendingMouseRestore` or `_savedMouseProfileBeforeGame` if they are already active!
- In `OnGameEnded`:
  ```csharp
  bool shouldRestoreMouse = _appSettings.AtkMouseIntegrationEnabled && (_pendingMouseRestore || _isMouseProfileSwitchedForGame);
  if (shouldRestoreMouse)
  {
      int targetMouseProfile = _appSettings.DefaultAtkMouseProfile > 0 
          ? _appSettings.DefaultAtkMouseProfile 
          : _savedMouseProfileBeforeGame;
      if (targetMouseProfile > 0)
      {
          if (endGen != _gameSessionGeneration) return;
          bool restoredOk = await Task.Run(() => _atkMouseService.SwitchProfile(targetMouseProfile));
          if (endGen != _gameSessionGeneration) return;
          if (restoredOk)
          {
              UpdateSideMouseProfileStatus(targetMouseProfile);
              _isMouseProfileSwitchedForGame = false;
              _pendingMouseRestore = false;
          }
          else
          {
              _isMouseProfileSwitchedForGame = true;
              _pendingMouseRestore = true;
              failedSteps.Add("Fare Profili");
          }
      }
  }
  // DO NOT add an else branch that clears _pendingMouseRestore!
  ```

---

### 3.9 Partial Restore Reporting vs False General Success

#### Requirement:
> "Başarısız geri yüklemeyi genel başarı bildirimiyle sonuçlandırma; bağımsız adımların ayrı ayrı denenebilmesini ve başarısız kalanların açıkça bildirilmesini sağla."

#### Defects Found:
- In `OnGameEnded`, if any step fails or is interrupted, `failedSteps` lists only the attempted steps that returned false. Unattempted steps are not included in `failedSteps`.
- In `RetryPendingDesktopRestoreAsync`, if `_pending*Restore` flags were prematurely false, it clears `_hasPendingDesktopRestore = false` and shows:
  ```csharp
  ShowToast("Bekleyen masaüstü ayarları başarıyla geri yüklendi.");
  return true;
  ```
  even though the settings were never restored.

#### Fix Recommendation:
- Calculate overall success strictly as:
  ```csharp
  bool allSucceeded = !_pendingModeRestore && !_pendingCpuBoostRestore && !_pendingFanRestore && !_pendingMouseRestore;
  if (allSucceeded)
  {
      _hasPendingDesktopRestore = false;
      ShowToast("Masaüstü ayarları başarıyla geri yüklendi.");
  }
  else
  {
      _hasPendingDesktopRestore = true;
      ShowToast($"Geri yüklenemeyen ayarlar: {string.Join(", ", failedSteps)}", false);
  }
  ```

---

### 3.10 Tray Menu "Masaüstü ayarlarını yeniden dene" & `RetryPendingDesktopRestoreAsync()`

#### Requirement:
> "`RetryPendingDesktopRestoreAsync()` metodunu tepsi menüsündeki "Masaüstü ayarlarını yeniden dene" eylemine bağla. Eylemi yalnızca bekleyen iş varsa kullanılabilir yap. Yeniden deneme sırasında aynı geçiş kilidini kullan, aktif oyun ve oturum neslini uygun noktalarda yeniden kontrol et; yinelenen donanım yazmalarını engelle."
> "Tepsi menüsünde "Masaüstü ayarlarını yeniden dene" eylemi yalnızca bekleyen geri yükleme varsa etkinleştirilir ve yinelenen tıklamalarda çakışan yazma başlatmaz."

#### Defects Found:
1. **Missing Menu Item**: `SetupTrayIcon()` does not create or add the "Masaüstü ayarlarını yeniden dene" menu item.
2. **Missing Dynamic Enablement**: The menu item must be enabled if and only if `HasPendingDesktopRestore && !_isGameSessionActive`.
3. **No Session Generation Checking**: `RetryPendingDesktopRestoreAsync()` (lines 1966-2045) does not check `_gameSessionGeneration`. If Game B starts during retry, retry keeps writing desktop settings over Game B!
4. **Hardcoded Fan Auto**: Line 1999 applies `FanMode.Auto` instead of `_savedFanModeBeforeGame`.
5. **Double-Click Race Hazard**: While `_gameProfileTransitionLock` queues the second click, once the first click completes, the second click acquires the lock and must immediately re-verify `if (_isGameSessionActive || !HasPendingDesktopRestore) return true;`. Without this re-check after acquiring the lock, the second click proceeds into redundant hardware writes.

#### Fix Recommendation:
1. In `SetupTrayIcon()`:
   ```csharp
   _menuItemRetryDesktop = new System.Windows.Forms.ToolStripMenuItem("Masaüstü ayarlarını yeniden dene", null, async (s, e) =>
   {
       await RetryPendingDesktopRestoreAsync();
   });
   _menuItemRetryDesktop.Enabled = false;
   menu.Items.Insert(1, _menuItemRetryDesktop);
   menu.Opening += (s, e) =>
   {
       _menuItemRetryDesktop.Enabled = HasPendingDesktopRestore && !_isGameSessionActive;
   };
   ```
2. In `RetryPendingDesktopRestoreAsync()`:
   ```csharp
   public async Task<bool> RetryPendingDesktopRestoreAsync()
   {
       if (!HasPendingDesktopRestore) return true;
       if (_isGameSessionActive) return false;

       await _gameProfileTransitionLock.WaitAsync();
       try
       {
           // Re-check state inside the lock (prevents duplicate execution on rapid double-clicks)
           if (_isGameSessionActive || !HasPendingDesktopRestore)
           {
               return !HasPendingDesktopRestore;
           }

           int retryGen = _gameSessionGeneration;
           var operation = Dispatcher.InvokeAsync(async () =>
           {
               if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
               var failed = new List<string>();

               // 1. Mode
               if (_pendingModeRestore)
               {
                   if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                   bool ok = await SwitchModeAsync(_savedModeBeforeGame);
                   if (ok) _pendingModeRestore = false;
                   else failed.Add("Çalışma Modu");
               }

               // 2. Boost
               if (_pendingCpuBoostRestore)
               {
                   if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                   bool ok = await ApplyCpuBoostModeAsync(_savedCpuBoostBeforeGame);
                   if (ok) _pendingCpuBoostRestore = false;
                   else failed.Add("CPU Boost");
               }

               // 3. Fan
               if (_pendingFanRestore)
               {
                   if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                   bool ok = await _agentClient.SetFanModeAsync(_savedFanModeBeforeGame);
                   if (ok)
                   {
                       _currentFanMode = _savedFanModeBeforeGame;
                       _pendingFanRestore = false;
                   }
                   else failed.Add("Fan Modu");
               }

               // 4. Mouse
               if (_pendingMouseRestore)
               {
                   if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                   int targetMouse = _appSettings.DefaultAtkMouseProfile > 0 ? _appSettings.DefaultAtkMouseProfile : _savedMouseProfileBeforeGame;
                   bool ok = await Task.Run(() => _atkMouseService.SwitchProfile(targetMouse));
                   if (ok)
                   {
                       UpdateSideMouseProfileStatus(targetMouse);
                       _isMouseProfileSwitchedForGame = false;
                       _pendingMouseRestore = false;
                   }
                   else failed.Add("Fare Profili");
               }

               if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;

               bool success = !_pendingModeRestore && !_pendingCpuBoostRestore && !_pendingFanRestore && !_pendingMouseRestore;
               _hasPendingDesktopRestore = !success;

               if (success)
               {
                   ShowToast("Bekleyen masaüstü ayarları başarıyla geri yüklendi.");
               }
               else
               {
                   ShowToast($"Yeniden deneme tamamlanamadı: {string.Join(", ", failed)}", false);
               }
               UpdateTrayRetryMenuItemState();
               return success;
           });
           return await operation.Task.Unwrap();
       }
       finally
       {
           _gameProfileTransitionLock.Release();
       }
   }
   ```

---

### 3.11 Ordering & Serialization: Manual Profile, Live Profile Save, Power Automation

#### Requirement:
> "Manuel profil uygulaması, canlı profil kaydetme uygulaması ve güç kaynağı otomasyonunun geri yükleme akışıyla sıralamasını belirle ve uygula; son kullanıcı talebinin eski bir işlem tarafından ezilmesini önle."
> "Manuel profil, canlı uygulama ve güç otomasyonu yazmaları geri yükleme akışıyla doğru sıralanır; son kullanıcı talebi eski işlemler tarafından ezilmez."

#### Analysis & Priority Order:
1. **User Explicit Manual Action is King**:
   - When the user manually clicks Quiet/Default/Extreme/Turbo, CPU Boost, or Fan on desktop:
     - The user is deliberately choosing their active desktop setting.
     - If `_pendingModeRestore` was true, the user's manual action fulfills and replaces the pending restore. `_savedModeBeforeGame` is updated to the newly selected mode, and `_pendingModeRestore = false;`.
     - Stale background restore operations must not overwrite what the user explicitly picked.
     - Manual hardware writes should synchronize via `_gameProfileTransitionLock` to prevent packet collisions on the wire.
2. **Live Profile Save Hot-Apply (`BtnSaveGame_Click`)**:
   - Lines 1582-1611 must acquire `_gameProfileTransitionLock`.
   - Must check `if (!_isGameSessionActive || _gameService.ActiveGameProfile == null) return;` so it never applies game settings to the desktop if the game closed while waiting for the lock.
   - Must only update UI on verified hardware write success.
3. **Power Automation (`PowerAutomationService`)**:
   - When AC or DC triggers:
     - If a game session is active (`_isGameSessionActive == true`):
       - If the user is gaming on DC and DC Eco is configured, or if the laptop transitions between AC and DC during a game, the power transition must acquire `_gameProfileTransitionLock` before altering hardware.
       - The desktop baseline (`_savedModeBeforeGame`, etc.) must not be overwritten with active game settings!
       - If power automation triggers on desktop, it acquires `_gameProfileTransitionLock` and applies the target profile, updating `_currentMode` only on verified success.

---

## 4. Test Architecture & Seams for Requirement R4

### 4.1 Test Seams Needed in Production Code

To satisfy Requirement R4 ("Testlerin ayrık kurgular yerine doğrudan üretim kodunu çağırmasını sağla. Gerekli en küçük test bağlantılarını (test seams) kullanarak uçtan uca akışları doğrula"):

1. **Dependency Injection Seam in `MainWindow`**:
   - Provide an internal constructor in `MainWindow` allowing tests to inject mock services without touching real hardware:
     ```csharp
     internal MainWindow(
         AcerAgentClient agentClient,
         CpuPowerService cpuPowerService,
         GameDetectionService gameService,
         AtkMouseService atkMouseService,
         BatteryWatchdogService? batteryService = null,
         AppSettings? settings = null,
         bool isTestHarness = true)
     ```
   - When `isTestHarness` is true:
     - Bypasses `SetupTrayIcon()` (or uses a dummy strip), `_pollTimer.Start()`, and real hardware initialization.
     - Uses injected mock services.
2. **Expose Transition Tasks**:
   - `internal async Task HandleGameStartedAsync(GameProfile profile)`
   - `internal async Task HandleGameEndedAsync(GameProfile profile)`
   - `public async Task<bool> RetryPendingDesktopRestoreAsync()`
3. **Expose Internal State Getters**:
   - `internal bool HasPendingDesktopRestore => _hasPendingDesktopRestore || _pendingModeRestore || _pendingCpuBoostRestore || _pendingFanRestore || _pendingMouseRestore;`
   - `internal bool PendingModeRestore => _pendingModeRestore;`
   - `internal bool PendingCpuBoostRestore => _pendingCpuBoostRestore;`
   - `internal bool PendingFanRestore => _pendingFanRestore;`
   - `internal bool PendingMouseRestore => _pendingMouseRestore;`
   - `internal OperatingMode SavedModeBeforeGame => _savedModeBeforeGame;`
   - `internal CpuBoostMode SavedCpuBoostBeforeGame => _savedCpuBoostBeforeGame;`
   - `internal FanMode SavedFanModeBeforeGame => _savedFanModeBeforeGame;`
   - `internal int SavedMouseProfileBeforeGame => _savedMouseProfileBeforeGame;`
   - `internal bool IsGameSessionActive => _isGameSessionActive;`
   - `internal int GameSessionGeneration => _gameSessionGeneration;`
   - `internal SemaphoreSlim GameProfileTransitionLock => _gameProfileTransitionLock;`
4. **Make `AtkMouseService.SwitchProfile` virtual**:
   - In `NitroLite/Core/AtkMouseService.cs`:
     `public virtual bool SwitchProfile(int configNumber)`
   - This allows `MockAtkMouseService` to record calls and simulate hardware success/failure without touching USB HID endpoints.

---

## 5. R4 Unit & Integration Test Matrix for Requirement R1

The following end-to-end scenarios must be implemented in `NitroLite.Tests`:

| Test Name | Verification Target |
|---|---|
| `R1_GameAWaitingHardware_GameACloseGameBOpen_WritesInOrder_PreservesFirstDesktopBaseline` | Game A launches; its hardware write is delayed. While in flight, Game A ends and Game B starts. Verifies Game A finishes in-flight write, aborts further Game A writes, releases lock. Game B acquires lock and applies Game B profile. Original desktop baseline from before Game A is preserved intact. |
| `R1_DesktopRestore_StepFails_OtherStepsAttempted_TrayActionRetriesOnlyFailedSteps` | Game ends. Mode restore fails, but CPU boost, Fan, and Mouse succeed. Verifies `_pendingModeRestore == true`, others `false`. Invoking `RetryPendingDesktopRestoreAsync()` attempts ONLY Mode; does not re-write CPU boost or Fan. Verifies successful completion. |
| `R1_DesktopRestore_InterruptedBeforeNextStep_PreservesUnattemptedSteps` | Restore starts. Step 1 finishes. Session generation increments before step 2. Verifies unattempted steps remain marked as pending and are not lost. Subsequent retry executes all unattempted steps. |
| `R1_DesktopRestore_RetryInterruptedByNewGame_DoesNotOverwriteNewGameProfile` | Restore retry is in progress. A new game launches. Verifies retry immediately stops before the next step, relinquishes lock, and does not overwrite new game settings. |
| `R1_MouseRestore_FailedRestore_SubsequentGameWithoutMouseProfile_PreservesPendingMouseRestore` | Mouse restore fails on Game A exit (`_pendingMouseRestore = true`). Game B starts with `TargetAtkMouseProfile = 0`. Game B exits. Verifies `_pendingMouseRestore` is NOT cleared and `_savedMouseProfileBeforeGame` is preserved. |
| `R1_FanMode_RestoreRespectsOriginalFanMode_NotHardcodedAuto` | User selects `FanMode.Max` on desktop. Game starts with `TargetFanMode = FanMode.Auto`. Game ends. Verifies fan is restored to `FanMode.Max`, NOT `FanMode.Auto`. |
| `R1_HardwareWriteFails_UiNotUpdatedOptimistically` | Operating mode hardware write returns `false`. Verifies mode cards, mode text, and toast report failure, not false success. |
| `R1_TrayMenu_RetryDesktopAction_EnabledOnlyWhenPendingWork_HandlesDoubleClicks` | Tray menu item is disabled when no pending restore. Becomes enabled when restore is pending. When double-clicked rapidly, lock serializes execution; second execution exits immediately without duplicate writes. |
| `R1_ManualModeChangeOnDesktop_UpdatesBaselineAndCancelsPendingRestore` | A pending mode restore exists. User manually clicks "Turbo" on desktop. Verifies `_savedModeBeforeGame = Turbo` and `_pendingModeRestore = false`, preventing old restore from overwriting user choice. |

---

## 6. Conclusion & Transition to Implementation

All 11 architectural defects, race conditions, and missing requirements under R1 have been isolated to exact files and lines of code. The execution flow has been fully analyzed and verified.

The proposed test seams adhere strictly to the constraint of Requirement R4 (calling actual production code without replicating logic in test bodies, while avoiding real hardware interaction).

This document serves as the authoritative blueprint for implementation in Milestone R1.
