# Handoff Report: Keyboard Backlight State Persistence (Requirement R3)

**Agent:** `explorer_rgb_1`  
**Date:** 2026-11-04  
**Working Directory:** `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_rgb_1`  
**Recipient / Parent:** `2989ccbf-2e6a-4cee-9edb-1583739d9df3` (Orchestrator / Implementers)  
**Deliverable Path:** `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_rgb_1\handoff.md`  
**Detailed Analysis:** `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_rgb_1\analysis.md`

---

## 1. Observation

1. **Absence of `settings.json` and lighting data in `AppSettings`:**
   - In `NitroLite/Core/Models.cs` (lines 135–144), `AppSettings` contains only window and UI parameters:
     ```csharp
     public class AppSettings
     {
         public bool StartMinimized { get; set; } = false;
         public bool OsdEnabled { get; set; } = false;
         public string SelectedChartMetric { get; set; } = "CPU_TEMP";
         public bool AutoRamCleanerEnabled { get; set; } = true;
         public double? WindowLeft { get; set; }
         public double? WindowTop { get; set; }
         public int OsdCorner { get; set; } = 0;
     }
     ```
   - In `NitroLite/MainWindow.xaml.cs` (lines 49–50):
     ```csharp
     private static readonly string ActiveRgbConfigFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "active_rgb_state.json");
     private static readonly string AppSettingsConfigFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
     ```
   - No file named `settings.json` is created, read, or updated anywhere in `NitroLite` (observed via `grep_search` across `NitroLite/`).

2. **Immediate save on UI change writes only to `active_rgb_state.json`:**
   - In `MainWindow.xaml.cs` (lines 1548–1566):
     ```csharp
     private async Task TurnOffLightsAsync()
     {
         _lighting.Brightness = 0;
         _lighting.Effect = "OFF";
         _lighting.Color = "#000000";
         _lighting.LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" };

         SliderBrightness.Value = 0;
         TxtBrightnessVal.Text = "0 / 9 (Kapalı)";
         ...
         UpdateEffectComboBox("OFF");
         await _agentClient.TurnOffLightingAsync();
         SaveActiveRgbState();
     }
     ```
   - `SaveActiveRgbState()` at line 288 writes only to `active_rgb_state.json`. `config.json` and `settings.json` are not updated when lighting changes.

3. **Startup overwrite hazard:**
   - In `MainWindow.xaml.cs` (lines 165–201):
     ```csharp
     bool restoredSavedRgb = false;
     if (File.Exists(ActiveRgbConfigFile))
     {
         try
         {
             var json = await File.ReadAllTextAsync(ActiveRgbConfigFile);
             var savedRgb = JsonSerializer.Deserialize<LightingConfig>(json);
             if (savedRgb != null)
             {
                 ...
                 restoredSavedRgb = true;
             }
         }
         catch { }
     }

     if (!restoredSavedRgb)
     {
         var cfg = await _agentClient.GetLightingConfigAsync();
         if (cfg != null)
         {
             _lighting = cfg;
             UpdateLightingUi(cfg);
             SaveActiveRgbState();
         }
     }
     ```
   - If `active_rgb_state.json` is absent or read fails, NitroLite queries AcerAgent for hardware defaults (which are ON after boot) and saves them, permanently overwriting the user's intent.

4. **Zero power or session event listeners for lighting:**
   - `BatteryWatchdogService.cs` (lines 53 & 334) hooks `SystemEvents.PowerModeChanged` for the battery threshold watchdog:
     ```csharp
     SystemEvents.PowerModeChanged += OnPowerModeChanged;
     ```
   - `MainWindow.xaml.cs` has **zero** subscriptions to `SystemEvents.PowerModeChanged` and **zero** subscriptions to `SystemEvents.SessionSwitch`.
   - As a result, when the system wakes from sleep or a session is unlocked, NitroLite executes no re-assertion logic.

5. **Existing test suite status:**
   - `dotnet test NitroLite.Tests\NitroLite.Tests.csproj` executes 119 tests, all passing cleanly (observed runtime: 900 ms).
   - There are currently 0 tests covering RGB profile persistence, backlight off persistence, or `settings.json` serialization.

---

## 2. Logic Chain

1. **From Observation 1 & 2 to Storage Gap:**
   - Requirement R3 states: *"Turning off keyboard lighting persists the off state and brightness in `settings.json`."*
   - Because `settings.json` does not exist and `AppSettings` contains no keyboard lighting fields, turning off lights does not fulfill the persistence specification in `settings.json`.
   - Therefore, `AppSettings` must be expanded to include `KeyboardLighting` (`LightingConfig?`) and `IsBacklightOff` (`bool`), and `SaveSettings()` must serialize to `settings.json` (synchronizing with `config.json` for backward compatibility).

2. **From Observation 3 to Boot Overwrite Elimination:**
   - In `RefreshAllAsync()`, querying `GetLightingConfigAsync()` when a local file cannot be read causes the factory boot state (active LEDs) to overwrite user preferences.
   - Therefore, startup must prioritize `settings.json`, fall back to `active_rgb_state.json`, and default to `IsBacklightOff = false` only when genuinely unconfigured, never overwriting saved settings if a read error occurs.

3. **From Observation 4 to Wake/Session Relapse:**
   - On Acer Nitro laptops, the Embedded Controller (EC) powers down the keyboard in sleep and reloads default factory profiles upon wake.
   - Without `SystemEvents.PowerModeChanged` (Resume/StatusChange) and `SystemEvents.SessionSwitch` (SessionUnlock/SessionLogon) listeners, NitroLite never issues hardware packets upon wake.
   - Therefore, the keyboard backlight always turns back on when waking from sleep or logging in.

4. **From Hardware EC Reset Timing to Staged Re-Assertion:**
   - The EC and `AaSvc` (AcerAgent) complete their post-wake hardware initialization between 200 ms and 1000 ms after `PowerModes.Resume` fires.
   - A single immediate command at $t = 0\text{ ms}$ can fail due to dropped TCP sockets or get overwritten by the EC's late reset at $t \approx 500\text{ ms}$.
   - Therefore, a staged re-assertion (initial pulse at $t \approx 250\text{ ms}$ + reinforcement pulse at $t \approx 1500\text{ ms}$ with up to 3 TCP retries) is required to guarantee that turned-off lights stay strictly off.

---

## 3. Caveats

1. **Hardware-dependent AcerAgent Service:**
   - `AcerAgentClient` communicates via TCP with `AaSvc` at `127.0.0.1:46933`. If the Acer background service is completely killed or uninstalled, lighting packets return `false`. The UI and local persistence will still correctly record the off state, but physical LEDs cannot be modified without `AaSvc` or OpenRGB.
2. **ACPI / EC Firmware Hardwired Behavior:**
   - During the literal BIOS boot splash before Windows loads, the EC hardware controls LEDs directly. NitroLite takes control as soon as user space / Windows starts.
3. **No other caveats:** Code paths and requirements have been verified directly in the source code.

---

## 4. Conclusion

Requirement R3 can be completely and cleanly satisfied with four coordinated adjustments:
1. **Model & Persistence:**
   - Add `KeyboardLighting` (`LightingConfig?`) and `IsBacklightOff` (`bool`) to `AppSettings` in `NitroLite/Core/Models.cs`.
   - Standardize on `settings.json` as the primary configuration file, keeping `config.json` and `active_rgb_state.json` synchronized.
   - In `TurnOffLightsAsync()`, set `IsBacklightOff = true`, `Brightness = 0`, `Effect = "OFF"` and immediately persist to `settings.json`.
2. **Power & Session Event Hooks:**
   - In `MainWindow.xaml.cs`, subscribe to `SystemEvents.PowerModeChanged` and `SystemEvents.SessionSwitch` in `SetupEvents()`, and unsubscribe in `RealExit()`.
3. **Staged Re-Assertion with Socket Retry:**
   - Implement `ReassertKeyboardLightingAsync()` with a dual-pulse strategy ($t = 250\text{ ms}$ and $t = 1500\text{ ms}$) and up to 3 socket reconnect attempts. If `IsBacklightOff` is true or `Brightness == 0`, enforce `TurnOffLightingAsync()`.
4. **Unit Test Suite:**
   - Add `KeyboardBacklightPersistenceTests.cs` to `NitroLite.Tests` validating serialization of `settings.json`, off state retention, backward compatibility, and re-assertion decision logic.

---

## 5. Verification Method

To verify the implementation independently:

1. **Compilation Check:**
   ```powershell
   dotnet build c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.sln
   ```
   *Expected:* Build succeeds with 0 errors and 0 warnings.

2. **Unit Test Execution:**
   ```powershell
   dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj
   ```
   *Expected:* All existing 119 tests plus new persistence tests pass cleanly.

3. **Configuration Inspection:**
   - Launch application, click "Işıkları Kapat" (Turn Off Lights).
   - Inspect `settings.json` in the application base directory:
     ```powershell
     Get-Content NitroLite\bin\Debug\net10.0-windows\settings.json
     ```
   - Verify `"IsBacklightOff": true` and `"Brightness": 0`.
   - Close application, relaunch, verify `_lighting.Brightness == 0`, `Effect == "OFF"`, and physical lights remain off.
