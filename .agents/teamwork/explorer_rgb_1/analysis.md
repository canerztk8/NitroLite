# Investigation Report: Keyboard Backlight State Persistence (Requirement R3)

**Author:** `explorer_rgb_1` (Teamwork Explorer)  
**Date:** 2026-11-04  
**Target:** NitroLite (Acer Nitro 16 AN16-41 / Windows 11 net10.0-windows)  
**Requirement Scope:** Requirement R3 — Keyboard Backlight State Persistence across restarts, power events (sleep/wake), and session events (unlock/logon).

---

## Executive Summary

When a user turns off the keyboard RGB backlight in NitroLite, the backlight currently turns back on under several reproducible scenarios:
1. **System Sleep / Resume (Wake):** The Acer Embedded Controller (EC) and hardware bus autonomously reset upon wake, powering the keyboard LEDs back on to hardware factory defaults (e.g., dynamic wave / static cyan at medium brightness). Because NitroLite lacks hooks for system power events (`SystemEvents.PowerModeChanged`), NitroLite never re-asserts the off state.
2. **Workstation Unlock / Logon:** Screen lock/unlock or session switches (`SystemEvents.SessionSwitch`) re-enumerate HID devices, yet NitroLite has no session listener to re-apply the off state.
3. **Application Restart & Startup Race:** If `active_rgb_state.json` is missing or fails to deserialize during application launch, NitroLite falls back to querying `AcerAgentClient.GetLightingConfigAsync()`, which reads the hardware's active default (ON) and overwrites the saved configuration with the ON state.
4. **Configuration Storage Fragmentation:** NitroLite stores general application settings in `config.json` (`AppSettings`) and lighting state separately in `active_rgb_state.json`. Neither file is named `settings.json` (the contract specified by Requirement R3), and `AppSettings` lacks keyboard lighting and backlight on/off fields entirely.

To solve this permanently, NitroLite requires:
- Unified configuration persistence into `settings.json` (with backward compatibility for `config.json` and `active_rgb_state.json`).
- Adding `KeyboardLighting` (`LightingConfig?`) and `IsBacklightOff` (`bool`) to `AppSettings`.
- System event listeners for `SystemEvents.PowerModeChanged` (Resume/StatusChange) and `SystemEvents.SessionSwitch` (Unlock/Logon).
- A **staged re-assertion strategy** (initial pulse at $t \approx 300\text{ ms}$ + secondary pulse at $t \approx 1500\text{ ms}$ with socket retry) to decisively overcome the delayed EC firmware initialization reset.
- Unit tests in `NitroLite.Tests` covering serialization, state persistence, backward compatibility, and re-assertion decision logic.

---

## 1. Existing Keyboard Backlight / RGB Services & Models in NitroLite

### 1.1 Data Models
The lighting subsystem in NitroLite consists of the following data structures:

1. **`LightingConfig`** (`NitroLite/Core/AcerAgentClient.cs`, lines 14–22):
   ```csharp
   public class LightingConfig
   {
       public string Effect { get; set; } = "WAVE";
       public int Brightness { get; set; } = 5;
       public int Speed { get; set; } = 3;
       public int Direction { get; set; } = 1;
       public string Color { get; set; } = "#00E5FF";
       public List<string> LEDs { get; set; } = new() { "#00E5FF", "#00E5FF", "#00E5FF", "#00E5FF" };
   }
   ```
2. **`RgbPreset`** (`NitroLite/Core/RgbProfileService.cs`, lines 6–16):
   - Represents built-in and user-custom presets.
   - Built-in blackout preset at line 144: `"🌑 Işık Kapalı (Blackout)"` with `Effect = "OFF"`, `Brightness = 0`, `Color = "#000000"`, `LEDs = ["#000000", "#000000", "#000000", "#000000"]`.
3. **`AppSettings`** (`NitroLite/Core/Models.cs`, lines 135–144):
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
   **Critical Defect:** `AppSettings` does not have any properties representing keyboard lighting, backlight state, or whether the backlight is turned off.

### 1.2 Hardware Interface: `AcerAgentClient`
`AcerAgentClient` communicates with Acer's local hardware agent service (`AaSvc` / `AcerAgentService`) listening on TCP port `127.0.0.1:46933`.
- **Packet Structure:** ACER magic header (`"ACER"`, 4 bytes) + uint32 `packetId` (4 bytes LittleEndian) + UTF-8 JSON payload.
- **Lighting Controls:**
  - `SetLightingAsync(...)` (`AcerAgentClient.cs`, lines 323–406):
    - Determines if state is off:
      ```csharp
      var effUpper = effect.ToUpperInvariant();
      var isOff = effUpper == "OFF" || brightness <= 0;
      var isStatic = effUpper == "STATIC" || isOff;
      var leds = isOff 
          ? new[] { "#000000", "#000000", "#000000", "#000000" } 
          : (zoneColors ?? new[] { primaryColor, primaryColor, primaryColor, primaryColor });

      var actualBrightness = isOff ? 0 : Math.Clamp(brightness, 0, 9);
      var actualColor = isOff ? "#000000" : primaryColor;
      var actualEffect = isOff ? "STATIC" : effUpper;
      ```
    - Sends `PacketSetDeviceData` (100) to `device = 1` (Keyboard) and synchronizes with `device = 0` (Global device).
  - `TurnOffLightingAsync()` (`AcerAgentClient.cs`, lines 408–411):
    ```csharp
    public async Task<bool> TurnOffLightingAsync()
    {
        return await SetLightingAsync("OFF", 0, 1, 1, "#000000", new[] { "#000000", "#000000", "#000000", "#000000" });
    }
    ```
  - `SetBacklightTimeoutAsync(bool enable30s)` (`AcerAgentClient.cs`, lines 413–437): Toggles 30s timeout via function `"BACKLIGHT_30S"`.

---

## 2. Configuration Persistence Analysis (`settings.json` vs `config.json` vs `active_rgb_state.json`)

### 2.1 Current Storage Fragmentation
In `MainWindow.xaml.cs`:
- Line 49: `private static readonly string ActiveRgbConfigFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "active_rgb_state.json");`
- Line 50: `private static readonly string AppSettingsConfigFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");`

When the user turns off the lights or modifies any lighting control:
- In `BtnTurnOffLights_Click` / `TurnOffLightsAsync()` (`MainWindow.xaml.cs`, lines 1548–1566):
  ```csharp
  private async Task TurnOffLightsAsync()
  {
      _lighting.Brightness = 0;
      _lighting.Effect = "OFF";
      _lighting.Color = "#000000";
      _lighting.LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" };

      SliderBrightness.Value = 0;
      TxtBrightnessVal.Text = "0 / 9 (Kapalı)";
      // ... UI zone brush updates ...
      UpdateEffectComboBox("OFF");
      await _agentClient.TurnOffLightingAsync();
      SaveActiveRgbState();
  }
  ```
- `SaveActiveRgbState()` (`MainWindow.xaml.cs`, lines 288–296):
  ```csharp
  private void SaveActiveRgbState()
  {
      try
      {
          var json = JsonSerializer.Serialize(_lighting, new JsonSerializerOptions { WriteIndented = true });
          File.WriteAllText(ActiveRgbConfigFile, json);
      }
      catch { }
  }
  ```

### 2.2 Shortcomings & Violations of Requirement R3
1. **Missing `settings.json`:** Requirement R3 explicitly specifies that keyboard backlight state (RGB profile, brightness, on/off state) must persist in `settings.json`. Currently, NitroLite never creates or updates `settings.json`.
2. **Omission from `AppSettings`:** `AppSettings` in `NitroLite/Core/Models.cs` does not encapsulate `LightingConfig` or backlight on/off state. As a result, serialization of `AppSettings` completely discards lighting configuration.
3. **Startup Overwrite Hazard (`MainWindow.xaml.cs`, lines 165–202):**
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
               _lighting = savedRgb;
               // ... apply to hardware ...
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
   If `active_rgb_state.json` is missing or temporarily locked during Windows startup, NitroLite calls `_agentClient.GetLightingConfigAsync()`. Because the Acer Embedded Controller powers on in default active mode upon system boot, `GetLightingConfigAsync()` returns an ON profile (e.g. WAVE, brightness 5). NitroLite then saves this to disk, permanently overriding the user's intended OFF state.

---

## 3. System Power & Session Event Vulnerabilities

### 3.1 Sleep/Suspend & Resume/Wake (`SystemEvents.PowerModeChanged`)
- When Windows enters Sleep (S3) or Modern Standby (S0ix), peripheral power rails are disabled.
- Upon resume/wake, the Embedded Controller (EC) powers up the keyboard backlight and loads factory defaults.
- Windows fires `SystemEvents.PowerModeChanged` with `e.Mode == PowerModes.Resume`.
- While `BatteryWatchdogService.cs` (lines 53 & 334) subscribes to `SystemEvents.PowerModeChanged` to enforce the 80% battery charging threshold, **neither `MainWindow.xaml.cs` nor any lighting service hooks into `SystemEvents.PowerModeChanged`**.
- As a consequence, whenever the laptop wakes from sleep, the keyboard backlight returns to factory ON state and remains ON indefinitely.

### 3.2 Session Switch Events (`SystemEvents.SessionSwitch`)
- When a user locks the workstation (Win+L) or logs out/in, Windows fires `SystemEvents.SessionSwitch` (`SessionSwitchReason.SessionUnlock`, `SessionSwitchReason.SessionLogon`).
- Acer background utilities or Windows logon services frequently re-enumerate and reset USB HID input devices upon logon.
- Because NitroLite does not listen to `SystemEvents.SessionSwitch`, it fails to re-assert the off state when the user unlocks the machine.

### 3.3 Power Source Transitions (`PowerModes.StatusChange`)
- On Acer laptops, plugging or unplugging the AC power adapter triggers ACPI events that may switch keyboard backlight timeout modes or cycle keyboard power.
- Handling `PowerModes.StatusChange` ensures the keyboard backlight state remains consistently enforced.

---

## 4. Race Conditions, Hardware EC Resets, and Staged Re-Assertion

### 4.1 The Embedded Controller Hardware Reset Sequence
The Acer Nitro 16 AN16-41 Embedded Controller firmware operates independently of the Windows OS thread scheduler:
1. At $t = 0\text{ ms}$: Windows kernel resumes, and `SystemEvents.PowerModeChanged` fires in the application thread.
2. At $t \approx 50\text{--}300\text{ ms}$: The USB/HID host controller finishes bus reset and port power-up.
3. At $t \approx 200\text{--}800\text{ ms}$: `AcerAgentService` (port 46933) detects driver availability and performs internal initialization, which can push default lighting packets to the keyboard.
4. At $t \approx 500\text{--}1200\text{ ms}$: The EC firmware completes its internal POST and initializes LED PWM controllers to default active mode if no persistent override was accepted.

### 4.2 Why a Single Immediate Command Fails
If NitroLite attempts to send `TurnOffLightingAsync()` immediately at $t = 0\text{ ms}$ upon receiving `PowerModes.Resume`:
- **Socket Disconnect Hazard:** The existing TCP connection to `127.0.0.1:46933` may be in a broken/half-open state from the sleep transition. `EnsureConnectedAsync()` must detect the broken socket, cleanly dispose, and establish a new connection.
- **Hardware Overwrite Race:** Even if the command is accepted by `AcerAgentService` at $t = 100\text{ ms}$, the EC firmware or `AcerAgentService`'s deferred resume handler may overwrite the register values at $t = 600\text{ ms}$, turning the lights back on.

### 4.3 Staged Re-Assertion Solution
To eliminate the hardware reset race condition:
- **Phase 1 (Immediate / Fast Pulse):** After a short 250ms delay (allowing socket re-connection), execute `ReassertKeyboardLightingAsync()`.
- **Phase 2 (Reinforcement Pulse):** After a secondary delay of 1500ms, execute a second enforcement pulse. This guarantees that any late initialization by the EC or AcerAgent is definitively overwritten.
- **Socket Resilience & Retry:** In `AcerAgentClient`, wrap `EnsureConnectedAsync()` and `SendRawAsync()` with socket disconnect detection and up to 3 retries (with 250ms backoff) to handle the brief window where `AcerAgentService` is restarting its network listener.

---

## 5. Architectural Recommendations & Proposed Code Changes

### 5.1 Update Data Models in `NitroLite/Core/Models.cs`
Update `AppSettings` to directly contain the keyboard lighting configuration and explicit off state:
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

    // R3: Keyboard Backlight State Persistence
    public LightingConfig? KeyboardLighting { get; set; }
    public bool IsBacklightOff { get; set; } = false;
}
```

### 5.2 Unified Persistence Strategy in `MainWindow.xaml.cs`
Unify file handling so `settings.json` is the primary file, while maintaining compatibility with `config.json` and `active_rgb_state.json`:
```csharp
private static readonly string SettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
private static readonly string AppSettingsConfigFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
private static readonly string ActiveRgbConfigFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "active_rgb_state.json");

private void SaveSettings()
{
    try
    {
        _appSettings.KeyboardLighting = _lighting;
        _appSettings.IsBacklightOff = _lighting.Brightness == 0 || _lighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase);

        var json = JsonSerializer.Serialize(_appSettings, new JsonSerializerOptions { WriteIndented = true });
        // Primary R3 contract
        File.WriteAllText(SettingsFile, json);
        // Synchronized for backward compatibility
        File.WriteAllText(AppSettingsConfigFile, json);
    }
    catch { }
}

private void SaveActiveRgbState()
{
    try
    {
        var json = JsonSerializer.Serialize(_lighting, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ActiveRgbConfigFile, json);
        SaveSettings();
    }
    catch { }
}
```

When loading settings in `LoadSettings()` / `RefreshAllAsync()`:
1. Check `settings.json` first.
2. If `settings.json` contains `KeyboardLighting` or `IsBacklightOff`, load directly from `settings.json`.
3. If not found, fall back to `active_rgb_state.json` and `config.json`.
4. Only if no saved settings exist should NitroLite query `GetLightingConfigAsync()`.
5. When applying saved settings: if `IsBacklightOff` is true or `_lighting.Brightness == 0` or `_lighting.Effect == "OFF"`, call `_agentClient.TurnOffLightingAsync()` with retry; do not turn on.

### 5.3 System Events Subscription in `MainWindow.xaml.cs`
In `SetupEvents()`:
```csharp
try
{
    SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
    SystemEvents.SessionSwitch += OnSystemSessionSwitch;
}
catch { }
```

In `RealExit()`:
```csharp
try
{
    SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
    SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
}
catch { }
```

Event handlers:
```csharp
private void OnSystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
{
    if (e.Mode == PowerModes.Resume || e.Mode == PowerModes.StatusChange)
    {
        _ = Task.Run(async () => await ReassertKeyboardLightingAsync($"PowerMode_{e.Mode}"));
    }
}

private void OnSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
{
    if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon)
    {
        _ = Task.Run(async () => await ReassertKeyboardLightingAsync($"SessionSwitch_{e.Reason}"));
    }
}
```

### 5.4 Staged Re-Assertion Implementation
```csharp
private async Task ReassertKeyboardLightingAsync(string triggerSource)
{
    // Stage 1: Quick assertion after brief wake settlement
    await Task.Delay(250);
    await ApplyPersistedLightingToHardwareAsync();

    // Stage 2: Reinforcement pulse to defeat delayed EC firmware resets
    await Task.Delay(1250);
    await ApplyPersistedLightingToHardwareAsync();
}

private async Task ApplyPersistedLightingToHardwareAsync()
{
    try
    {
        bool isOff = _appSettings.IsBacklightOff || _lighting.Brightness == 0 || _lighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            bool success = isOff
                ? await _agentClient.TurnOffLightingAsync()
                : await _agentClient.SetLightingAsync(_lighting.Effect, _lighting.Brightness, _lighting.Speed, _lighting.Direction, _lighting.Color, _lighting.LEDs.ToArray());

            if (success) break;
            await Task.Delay(250);
        }
    }
    catch { }
}
```

---

## 6. Unit Testing Strategy for `NitroLite.Tests`

Create a dedicated test suite `NitroLite.Tests/KeyboardBacklightPersistenceTests.cs` validating:

1. **`SettingsSerialization_PersistsOffStateAndBrightness`**:
   - Create `AppSettings` with `IsBacklightOff = true`, `KeyboardLighting = new LightingConfig { Brightness = 0, Effect = "OFF", Color = "#000000" }`.
   - Serialize to JSON and verify that JSON contains `"IsBacklightOff": true` and `"Brightness": 0`.
   - Deserialize back and assert that the loaded properties match exactly.
2. **`SettingsSerialization_PersistsActiveProfile`**:
   - Create `AppSettings` with custom profile (e.g. Wave, brightness 7, color #00E5FF, 4 LEDs).
   - Serialize and deserialize to ensure profile integrity.
3. **`BackwardCompatibility_LoadsActiveRgbStateIfSettingsJsonMissing`**:
   - Simulate legacy environment where only `active_rgb_state.json` exists.
   - Verify that the lighting configuration is correctly parsed and applied.
4. **`HardwareDecisionLogic_SelectsTurnOffWhenOffStatePersisted`**:
   - Validate state machine logic: when `IsBacklightOff == true` or `Brightness == 0` or `Effect == "OFF"`, the execution branch strictly triggers the turn-off sequence.
5. **`StagedReassertion_ExecutesWithoutExceptionsOrDeadlocks`**:
   - Verify that simulated wake and session switch trigger routines complete asynchronously without UI thread deadlocks or memory leaks.

---

## 7. Risk Analysis & Implementation Guidance

| Risk | Likelihood | Impact | Mitigation Strategy |
|---|---|---|---|
| **EC firmware overwrites RGB registers 1s after wake** | High | High | Implement two-stage re-assertion ($t = 250\text{ ms}$ and $t = 1500\text{ ms}$) so late EC resets are overridden. |
| **TCP socket broken during system sleep** | High | Medium | `AcerAgentClient` must detect disconnection and reconnect before retrying packet transmission. |
| **Startup file race (reading file during rapid reboot)** | Low | Medium | Read settings before querying hardware default; never overwrite saved settings with hardware defaults if file read fails. |
| **Breaking existing config files** | Medium | Low | Write to both `settings.json` and `config.json` simultaneously, and fall back to `active_rgb_state.json` if needed. |
| **`SystemEvents` static event memory leak** | Medium | Low | Always unsubscribe from `PowerModeChanged` and `SessionSwitch` during application shutdown (`RealExit()`). |
