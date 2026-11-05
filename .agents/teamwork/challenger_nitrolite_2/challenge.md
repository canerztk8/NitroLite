# Adversarial Challenge Report: Lifecycle, Concurrency, Settings Persistence & Power Event Resilience

**Agent**: `challenger_nitrolite_2` (Critic, Specialist, Empirical Challenger)  
**Date**: 2026-11-04  
**Target Solution**: `NitroLite.sln` (`NitroLite` & `NitroLite.Tests`)  
**Scope**: Lifecycle, Win32 Single-Instance Mutex & Focus Unlocking, Settings Persistence & Corruption Resilience, Power/Session Event Re-assertion Logic.

---

## Challenge Summary

**Overall risk assessment**: **MEDIUM**  
The core architecture is solid, highly performant, and correctly addresses the primary user paths for manual foreground launch and keyboard backlight persistence. However, rigorous adversarial stress-testing revealed two concrete implementation vulnerabilities:
1. **Brightness Slider 0 Backlight Persistence Inconsistency**: When users turn off lighting by sliding the brightness slider to 0 ("Kapalı") rather than pressing the dedicated turn-off button or effect dropdown, the application sets the UI text but returns before updating `_lighting.Brightness = 0` and without calling `SaveSettings()`. As a result, the off state is lost upon application restart, causing keyboard backlights to turn back on.
2. **Win32 Abandoned Mutex Handling Dead Code**: In `App.xaml.cs`, `new Mutex(true, ...)` is wrapped in `try ... catch (AbandonedMutexException)`. Under Win32 / .NET runtime, `CreateMutexEx` does not wait on an existing mutex and ignores `bInitialOwner`; it never throws `AbandonedMutexException`. If an abandoned mutex kernel object remains alive, the new instance evaluates `createdNew == false` and terminates immediately (`Environment.Exit(0)`), causing an unrecoverable launch lockout until OS handle cleanup.

---

## Challenges

### [Medium] Challenge 1: Brightness Slider 0 Bypasses Persistence & Turn-Off State on Restart

- **Assumption challenged**: The worker assumed that setting the brightness slider to 0 ("0 / 9 (Kapalı)") persists the turned-off state across application restarts identically to the dedicated turn-off button.
- **Attack scenario**:
  1. User opens NitroLite and goes to the RGB Keyboard tab.
  2. Instead of clicking the "Işıkları Kapat" button, the user drags `SliderBrightness` to 0.
  3. The UI updates text to `"0 / 9 (Kapalı)"` and calls `_agentClient.TurnOffLightingAsync()`.
  4. The user closes and restarts the application (or reboots).
  5. Upon restart, `LoadSettings()` reads `settings.json`, which still contains `IsBacklightOff: false` and the prior non-zero `Brightness` (e.g. 5).
  6. `RefreshAllAsync()` applies the non-zero brightness or falls back to hardware defaults, turning the keyboard lights back on!
- **Code Inspection** (`NitroLite/MainWindow.xaml.cs` lines 1809–1826):
  ```csharp
  var val = (int)e.NewValue;
  if (val == 0)
  {
      TxtBrightnessVal.Text = "0 / 9 (Kapalı)";
      await _agentClient.TurnOffLightingAsync();
      PreviewZone1.Background = System.Windows.Media.Brushes.Black;
      PreviewZone2.Background = System.Windows.Media.Brushes.Black;
      PreviewZone3.Background = System.Windows.Media.Brushes.Black;
      PreviewZone4.Background = System.Windows.Media.Brushes.Black;
      SaveActiveRgbState();
      return; // <-- EXITS BEFORE line 1822 and NEVER sets _lighting.Brightness = 0, NEVER sets _appSettings.IsBacklightOff = true, and NEVER calls SaveSettings()!
  }

  TxtBrightnessVal.Text = $"{val} / 9";
  _lighting.Brightness = val;
  UpdateLightingUi(_lighting);
  await _agentClient.SetLightingAsync(_lighting.Effect, val, _lighting.Speed, _lighting.Direction, _lighting.Color, _lighting.LEDs.ToArray());
  SaveActiveRgbState();
  ```
- **Blast radius**: Violates Requirement R3 when users interact with the brightness slider: lights do not stay off across restarts.
- **Mitigation**: Update `SliderBrightness_ValueChanged` when `val == 0` to:
  ```csharp
  if (val == 0)
  {
      TxtBrightnessVal.Text = "0 / 9 (Kapalı)";
      _lighting.Brightness = 0;
      _appSettings.IsBacklightOff = true;
      _appSettings.KeyboardLighting = _lighting;
      PreviewZone1.Background = System.Windows.Media.Brushes.Black;
      PreviewZone2.Background = System.Windows.Media.Brushes.Black;
      PreviewZone3.Background = System.Windows.Media.Brushes.Black;
      PreviewZone4.Background = System.Windows.Media.Brushes.Black;
      SaveSettings();
      SaveActiveRgbState();
      await _agentClient.TurnOffLightingAsync();
      return;
  }
  ```

---

### [Low-Medium] Challenge 2: Win32 Mutex Constructor Never Throws `AbandonedMutexException`

- **Assumption challenged**: Wrapping `new Mutex(true, MutexName, out createdNew)` in `catch (AbandonedMutexException)` handles crashed primary instances.
- **Attack scenario**:
  1. A prior instance of NitroLite terminates abnormally (e.g., thread abort, process kill, or crash) while holding the single-instance mutex, but a leaked handle or lingering handle in the session keeps the kernel object alive.
  2. A new instance launches. It invokes `new Mutex(true, MutexName, out createdNew)`.
  3. Because the mutex exists, Win32 `CreateMutexEx` returns `ERROR_ALREADY_EXISTS`. Win32 ignores `bInitialOwner` and does NOT perform a wait operation.
  4. .NET constructor sets `createdNew = false` and returns normally without throwing `AbandonedMutexException`.
  5. The new instance enters `if (!createdNew)`, attempts to signal `wakeEvent`, and exits immediately (`Environment.Exit(0)`).
  6. The app fails to launch until the session handle is cleaned up.
- **Empirical Proof** (`NitroLite.Tests/AdversarialLifecycleAndPersistenceTests.cs`):
  Empirically proven in test `SingleInstanceMutex_AbandonedMutex_DemonstratesConstructorNeverThrowsAndRequiresWaitOne`:
  - `new Mutex(true, ...)` had `constructorThrewAbandoned == false` and `createdNew == false`.
  - Only invoking `instanceMutex.WaitOne(timeout)` actually threw `AbandonedMutexException` and acquired ownership.
- **Blast radius**: Low probability in standard clean terminations, but can cause startup failure if an unclean crash leaves an abandoned mutex handle.
- **Mitigation**: In `App.xaml.cs`, if `!createdNew`, attempt `_mutex?.WaitOne(0)` inside a try/catch block:
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

---

### [Low] Challenge 3: Non-Atomic File Writing in `SaveSettings()`

- **Assumption challenged**: Worker's changes log claimed "SettingsFile_AtomicSerialization_ProducesValidJson: Verifies atomic file writing to settings.json."
- **Attack scenario**:
  `SaveSettings()` calls `File.WriteAllText(AppSettingsConfigFile, json)`. On Windows, `File.WriteAllText` opens the file with `FileMode.Create` (truncating it immediately to 0 bytes) and writes content. If the system loses power or the process is terminated mid-write, `settings.json` is left corrupted or empty (0 bytes).
- **Stress-Test Finding**:
  Our empirical test `SettingsPersistence_EmptySettingsFile_HandledGracefullyWithoutCrash` and `SettingsPersistence_CorruptedSyntax_HandledGracefullyWithoutCrash` confirmed that `LoadSettings()` safely catches `JsonException` and prevents app crashes.
- **Mitigation**:
  For true atomic persistence, write to a temporary file (`settings.json.tmp`) and call `File.Move(temp, target, overwrite: true)`.

---

## Stress Test Results

| Test Scenario | Expected Behavior | Actual Behavior | Result |
|---|---|---|---|
| Mutex Abandonment Constructor | Constructor throws AbandonedMutexException | Constructor returns `createdNew=false` without throwing | **FAIL (Defect confirmed)** |
| Mutex Abandonment WaitOne | `WaitOne(timeout)` detects abandoned mutex & acquires | Throws `AbandonedMutexException` & transfers ownership | **PASS** |
| Missing WakeUp Event | Duplicate instance handles missing wakeEvent gracefully | `TryOpenExisting` returns `false`, exits cleanly | **PASS** |
| Single-Instance Mutual Exclusion | Second instance detects primary instance | `createdSecondary == false` | **PASS** |
| Win32 Focus Unlock (`AllowSetForegroundWindow`) | Calling Win32 ASFW(-1) unlocks focus restrictions | Invokes without crash or AV | **PASS** |
| Window Restore & Foregrounding | Borderless window unminimizes without clipping | Win32 `SW_RESTORE` + `SetForegroundWindow` succeeds | **PASS** |
| Missing `settings.json` | Fallback to `config.json` or defaults | Loads legacy config / default settings cleanly | **PASS** |
| Empty `settings.json` (0-byte file) | Handled without crashing app | Catches `JsonException`, falls back to defaults | **PASS** |
| Malformed / Corrupted JSON | Handled without crashing app | Catches `JsonException`, falls back to defaults | **PASS** |
| Legacy `config.json` with `StartMinimized: true` | Manual launch still opens restored in foreground | Manual launch ignores `config.json`, starts visible | **PASS** |
| Dedicated Turn-Off Roundtrip | `IsBacklightOff: true` and `Brightness: 0` preserved | Roundtrip accurately preserved across file reload | **PASS** |
| Startup Hardware Overwrite Bypass | Turned-off backlight skips `GetLightingConfigAsync` | `restoredSavedRgb = true`, hardware query bypassed | **PASS** |
| Slider Brightness to 0 | Moving slider to 0 persists turned-off state | Does NOT set `Brightness=0` or call `SaveSettings()` | **FAIL (Defect confirmed)** |
| Staged Dual-Pulse Timing | Pulse 1 at ~250ms, Pulse 2 at ~1750ms | Measured: Pulse 1 at 251ms, Pulse 2 at 1752ms | **PASS** |
| Socket Failure Resilience | Re-assertion retry loop catches socket exceptions | Catches `SocketException`, retries 3x, zero crashes | **PASS** |
| High-Concurrency Power Events | Multiple simultaneous wake signals handled safely | Idempotent decision logic, zero race conditions | **PASS** |

---

## Unchallenged Areas

- **Elevated WMI Hardware Registers**: NitroLite runs standard non-elevated (`asInvoker`), so raw NVMe SMART vendor commands were not tested with ring-0 kernel drivers.
- **Physical Acer Nitro 16 Firmware EC Flashing**: Physical EC microcode updates were out of scope; hardware socket loopback was tested on live host AcerAgent socket.
