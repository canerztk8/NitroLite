# Changes Summary: Adversarial Defect Remediation

**Worker**: `worker_core_2`  
**Date**: 2026-11-04  
**Solution**: `NitroLite.sln`  

---

## 1. NitroLite/MainWindow.xaml.cs

### Fix 1: Brightness Slider 0 Persistence & Backlight Off Synchronization
- **Location**: `SliderBrightness_ValueChanged` (lines 1803–1835)
- **Problem**: When `val == 0`, the handler updated the UI text to `"0 / 9 (Kapalı)"` and called `_agentClient.TurnOffLightingAsync()`, but immediately returned before setting `_lighting.Brightness = 0` or updating `_appSettings.IsBacklightOff = true`, and without calling `SaveSettings()`. On application restart, `settings.json` still contained `IsBacklightOff: false` and the prior non-zero brightness, causing keyboard backlights to turn back on.
- **Solution**:
  - When `val == 0`:
    1. Set `_lighting.Brightness = 0;`
    2. Set `_appSettings.IsBacklightOff = true;`
    3. Set `_appSettings.KeyboardLighting = _lighting;`
    4. Call `SaveSettings();` to persist `IsBacklightOff: true` and `Brightness: 0` to `settings.json` and legacy `config.json`.
    5. Call `SaveActiveRgbState();` to persist to `active_rgb_state.json`.
    6. Dispatch `await _agentClient.TurnOffLightingAsync();`.
  - When `val > 0`:
    1. Set `_lighting.Brightness = val;`
    2. Set `_appSettings.IsBacklightOff = false;`
    3. Set `_appSettings.KeyboardLighting = _lighting;`
    4. Call `SaveSettings();` so that non-zero brightness and active backlight state are saved immediately.
    5. Update UI preview and dispatch `SetLightingAsync`.

---

## 2. NitroLite/App.xaml.cs

### Fix 2: Win32 Abandoned Mutex Recovery via `WaitOne(0)`
- **Location**: `OnStartup` (lines 30–56)
- **Problem**: Win32 `CreateMutexEx` (wrapped by `new Mutex(true, MutexName, out createdNew)`) ignores initial ownership requests when an existing named mutex object is encountered, returning `createdNew = false` without waiting and without throwing `AbandonedMutexException`. If a previous instance exited abnormally and an abandoned mutex kernel object remained alive, the application treated the state as a duplicate instance and terminated immediately (`Environment.Exit(0)`), causing an application lockout.
- **Solution**:
  - If `!createdNew`, attempt `_mutex?.WaitOne(0)` inside a try/catch block catching `AbandonedMutexException`:
    ```csharp
    if (!createdNew)
    {
        try
        {
            if (_mutex != null && _mutex.WaitOne(0))
            {
                createdNew = true;
            }
        }
        catch (AbandonedMutexException)
        {
            createdNew = true;
        }
    }
    ```
  - If `WaitOne(0)` returns `true` or catches `AbandonedMutexException`, `createdNew` is set to `true`, allowing the new instance to acquire primary ownership and proceed with normal application launch.
  - If `createdNew` remains `false`, the secondary instance executes the focus unlock and wake signaling logic (`AllowSetForegroundWindow(ASFW_ANY); wakeEvent.Set(); Environment.Exit(0);`).

---

## 3. NitroLite.Tests/AdversarialLifecycleAndPersistenceTests.cs & Program.cs

### Test Additions
- Added `SingleInstanceMutex_AbandonedMutex_RecoveryLogic_AcquiresOwnershipAndSetsCreatedNew`:
  - Verifies that when an abandoned mutex exists in the OS session, the exact recovery pattern in `App.xaml.cs` successfully acquires the mutex, catches `AbandonedMutexException`, and sets `createdNew = true`.
- Added `SliderBrightnessZero_RemediatedPersistence_PreservesZeroBrightnessAndIsBacklightOff`:
  - Verifies that when brightness is set to 0, `Brightness` becomes 0, `IsBacklightOff` becomes true, and serialization / deserialization roundtrip preserves the turned-off state.
- Registered both tests into `NitroLite.Tests/Program.cs` under `[TEST 16]` for console test harness runs.

---

## 4. Verification Results

- `dotnet build NitroLite.sln`: Succeeded with **0 Warning(s), 0 Error(s)**.
- `dotnet test NitroLite.Tests\NitroLite.Tests.csproj`: **199 passed, 0 failed, 0 skipped** (net10.0).
- `dotnet run --project NitroLite.Tests\NitroLite.Tests.csproj`: All 16 test suites passed with **0 failures**.
