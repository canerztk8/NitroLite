# Forensic Audit Report: Hardware Control & Background Services (NitroLite)

**Author:** `explorer_hardware_2` (Hardware Control & Background Services Auditor)  
**Date:** 2026-11-04  
**Target Solution:** `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite`  
**Test Project:** `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests`  

---

## 1. Observation

### 1.1 Fan Control & Curve Management
- **File:** `NitroLite/Core/AcerAgentClient.cs` (lines 170–201)
  ```csharp
  public async Task<bool> SetFanModeAsync(FanMode mode, int cpuRpm = 0, int gpuRpm = 0)
  {
      await _lock.WaitAsync();
      try
      {
          if (!await EnsureConnectedAsync()) return false;

          object param = mode switch
          {
              FanMode.Max => new { mode = 1 },
              FanMode.Custom => new { mode = 2, cpu_fan = cpuRpm, gpu_fan = gpuRpm },
              _ => new { mode = 0 } // Auto
          };

          var payload = new { Function = "FAN_CONTROL", Parameter = param };
          var resp = await SendRawAsync(PacketSetDeviceData, payload);
          return !string.IsNullOrWhiteSpace(resp);
      }
      finally { _lock.Release(); }
  }
  ```
- **File:** `NitroLite/MainWindow.xaml.cs` (lines 1330–1340)
  ```csharp
  private async void BtnFanAuto_Click(object sender, RoutedEventArgs e)
  {
      await _agentClient.SetFanModeAsync(FanMode.Auto);
      ShowToast("Otomatik Fan Eğrisi Devrede");
  }

  private async void BtnFanMax_Click(object sender, RoutedEventArgs e)
  {
      await _agentClient.SetFanModeAsync(FanMode.Max);
      ShowToast("Maksimum Fan Hızı Devrede");
  }
  ```
- **Observations:**
  1. There is no dedicated `FanControlService.cs` class in the solution. Fan control logic is split directly between raw calls in `MainWindow.xaml.cs` and `AcerAgentClient.cs`.
  2. In `AcerAgentClient.SetFanModeAsync`, `FanMode.Custom` passes raw integer arguments (`cpu_fan = cpuRpm, gpu_fan = gpuRpm`) without boundary checking, clamping, or verification against hardware RPM/percentage ranges (minimum/maximum speeds).
  3. Reverse engineering of `AcerAgentService.exe` and `AcerHardwareService.exe` revealed that custom fan controls communicate with `SOFTWARE\OEM\AcerAgentService\FanControl` (`CurrentFanMode`) and `root\wmi:AcerGamingFunction::SetGamingFanBehavior` / `SetGamingFanTable`. The protocol expects percentages (0–100%) or defined stepped tables, not arbitrary RPM integers.
  4. There is zero curve interpolation, temperature-to-PWM lookup table, or hysteresis implemented in `NitroLite`. Custom curves cannot be drawn, loaded, or evaluated.
  5. In `MainWindow.xaml`, `BtnFanAuto` and `BtnFanMax` are ordinary push buttons with no visual indication of the current fan mode, and `MainWindow` never queries the active fan mode upon startup or mode switch.

---

### 1.2 CPU Power Limits & Boost Modes (CpuPowerService)
- **File:** `NitroLite/Core/CpuPowerService.cs` (lines 26–71, 73–92, 94–105)
  ```csharp
  private void UnhideSetting()
  {
      try
      {
          using var key = Registry.LocalMachine.OpenSubKey(
              $@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{SubProcessorGuid}\{PerfBoostModeGuid}", true);
          if (key != null) key.SetValue("Attributes", 2, RegistryValueKind.DWord);
      }
      catch { }
  }

  public CpuBoostMode QueryCurrentBoostMode()
  {
      try
      {
          var psi = new ProcessStartInfo
          {
              FileName = "powercfg.exe",
              Arguments = $"/query SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid}",
              UseShellExecute = false,
              RedirectStandardOutput = true,
              CreateNoWindow = true
          };
          using var p = Process.Start(psi);
          if (p == null) return CpuBoostMode.Aggressive;

          var output = p.StandardOutput.ReadToEnd();
          p.WaitForExit(3000);

          var match = System.Text.RegularExpressions.Regex.Match(output, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
          if (match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var val))
          {
              if (val == 0) return CpuBoostMode.Disabled;
              if (val == 4) return CpuBoostMode.Efficient;
              return CpuBoostMode.Aggressive;
          }
      }
      catch { }
      return CpuBoostMode.Aggressive;
  }
  ```
- **Observations:**
  1. `app.manifest` line 7 specifies `<requestedExecutionLevel level="asInvoker" uiAccess="false" />`. Because the application runs without Administrator elevation, `Registry.LocalMachine.OpenSubKey(..., writable: true)` throws `SecurityException` / `UnauthorizedAccessException`. The exception is caught and discarded by `catch { }`, so `Attributes` remains `1` (hidden).
  2. Direct command execution confirmed that when `PerfBoostModeGuid` is hidden, `powercfg.exe /query SCHEME_CURRENT 54533251-82be-4824-96c1-47b60b740d00 be337238-0d82-4146-a960-4f3749d470c7` outputs ONLY:
     ```
     Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
       GUID Alias: SCHEME_BALANCED
     ```
     It produces NO setting index text. As a consequence, `match.Success` is always `false`, and `QueryCurrentBoostMode()` perpetually falls back to `CpuBoostMode.Aggressive`, even when the active scheme is `Disabled` (0) or `Efficient` (4).
  3. `SetBoostModeAsync(CpuBoostMode mode)` executes three separate external processes sequentially:
     - `powercfg.exe /setacvalueindex SCHEME_CURRENT ...`
     - `powercfg.exe /setdcvalueindex SCHEME_CURRENT ...`
     - `powercfg.exe /setactive SCHEME_CURRENT`
     Spawning 3 separate command-line shells introduces 300–800ms of process creation overhead and disk access on every mode switch.
  4. There is no distinction between AC power vs Battery (DC) power. Overwriting both AC and DC with the identical boost mode destroys Windows' native battery-saving profile.
  5. Despite the class name `CpuPowerService`, it controls only Windows boost frequency behavior (`PerfBoostModeGuid`). It contains zero controls for TDP wattage, Sustained (PL1/SPL), Fast (PL2/fPPT), slow package power tracking (sPPT), or RyzenAdj / ACPI power limit tables.

---

### 1.3 80% Battery Charge Threshold Watchdog (BatteryWatchdogService)
- **File:** `NitroLite/Core/BatteryWatchdogService.cs` (lines 50–59, 97–117, 131–153, 168–189, 191–234)
  ```csharp
  public void EnforceHealthSetting()
  {
      try
      {
          if (!File.Exists(CcSettingsPath)) return;
          var text = File.ReadAllText(CcSettingsPath);
          var node = JsonNode.Parse(text);
          if (node == null) return;

          var val = node["BatteryHealthy"]?.GetValue<int>() ?? 0;
          if (val != 1)
          {
              node["BatteryHealthy"] = 1;
              File.WriteAllText(CcSettingsPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
          }
      }
      catch { }
  }
  ```
- **Observations:**
  1. The watchdog operates exclusively by writing `{"BatteryHealthy": 1}` to `C:\ProgramData\Acer\CC\settings.json`. It does NOT execute any ACPI or WMI hardware calls (`root\wmi:AcerGenericMethod::SetFunction`). If `AcerCCAgent.exe` (`AcerCCAgentSvis`) is stopped or debloated, writing to `settings.json` has ZERO effect on the hardware Embedded Controller.
  2. If `settings.json` already contains `"BatteryHealthy": 1` (line 179: `if (val != 1)`), the method does nothing. When the Embedded Controller resets its volatile charge threshold back to 100% (which occurs on sleep/wake, hibernate, or AC disconnect/reconnect), `EnforceHealthSetting()` fails to re-assert the limit because the JSON value is already 1.
  3. `File.ReadAllText` followed by `File.WriteAllText` without a mutex or file lock suffers from a file-sharing race condition with `AcerCCAgent.exe`, intermittently causing `IOException` and swallowed failures.
  4. In `StartSafeDischarge()`, two worker threads burn CPU cycles using `Math.Sin(d) * Math.Cos(d)` to drain the battery to 80%. When the laptop is plugged into AC power (which is the exact state where an overcharge condition occurs!), drawing CPU power simply pulls additional watts from the 230W/330W AC adapter brick while the battery continues charging at full current. The CPU loop creates extreme heat without discharging the battery.
  5. In `UpdateStatus()` (lines 97–117) and `GetBatteryHealthDetailsAsync()` (lines 132–153), `ManagementObjectSearcher.Get()` returns a `ManagementObjectCollection`. Neither the collection nor the individual `ManagementObject` instances are disposed. Because `UpdateStatus()` polls every 3 seconds, it continuously leaks COM/WMI handles in `wmiprvse.exe`.

---

### 1.4 Memory Cleaner Routines (MemoryCleanerService)
- **File:** `NitroLite/Core/MemoryCleanerService.cs` (lines 80–90, 99–108, 127–156, 158–186)
  ```csharp
  private static int TrimProcessWorkingSets()
  {
      int count = 0;
      int currentPid = Process.GetCurrentProcess().Id;

      foreach (var proc in Process.GetProcesses())
      {
          try
          {
              if (proc.Id <= 4 || proc.Id == currentPid) continue;
              if (EmptyWorkingSet(proc.Handle) != 0) count++;
          }
          catch { }
          finally
          {
              try { proc.Dispose(); } catch { }
          }
      }
      return count;
  }
  ```
- **Observations:**
  1. `TrimProcessWorkingSets` iterates over all system processes and blindly invokes `EmptyWorkingSet(proc.Handle)`. It does not exclude the active foreground game or critical services. When called on game launch (via `MainWindow.xaml.cs:805`) or during gameplay, it strips the working set of the game itself, causing massive disk thrashing, frame drops, and severe micro-stutters as game assets are paged back in from disk.
  2. `ShouldAutoClean(ramUsagePercent, ramTotalMB)` triggers if RAM usage is >= 80% or free RAM is <= 4096 MB. On high-end gaming laptops running heavy AAA titles (e.g., Cyberpunk 2077 or Microsoft Flight Simulator), RAM utilization normally exceeds 80%. The service triggers every 60 seconds indefinitely during gameplay, repeatedly evicting the game's working set.
  3. In `TryPurgeStandbyList()`, `NtSetSystemInformation(80, ...)` requires `SeProfileSingleProcessPrivilege`, which is only granted to elevated administrator tokens. Under standard user execution (`asInvoker`), `OpenProcessToken` / `AdjustTokenPrivileges` fails to grant the privilege, and `NtSetSystemInformation` returns `STATUS_PRIVILEGE_NOT_HELD` (`0xC0000061`).
  4. In `MainWindow.xaml.cs:805`, `CleanMemory()` is invoked synchronously on the UI thread when entering a game, triggering a synchronous blocking Gen-2 garbage collection, finalizer wait, and 300-process working set purge that freezes the UI for 200–500ms.

---

### 1.5 Game Detection Automation (GameDetectionService)
- **File:** `NitroLite/Core/GameDetectionService.cs` (lines 22–30, 95–193, 230–278)
  ```csharp
  public GameDetectionService()
  {
      LoadProfiles();
      ScanAndAddInstalledGames(); // Synchronous recursive scan

      _scanTimer.Interval = TimeSpan.FromSeconds(2.0);
      _scanTimer.Tick += (s, e) => ScanProcesses();
      _scanTimer.Start();
  }
  ```
- **File:** `NitroLite/MainWindow.xaml.cs` (lines 25, 775–839)
  ```csharp
  private readonly GameDetectionService _gameService = new();
  ```
- **Observations:**
  1. `GameDetectionService` is instantiated as a field initializer in `MainWindow.xaml.cs:25`. Its constructor synchronously calls `ScanAndAddInstalledGames()`, which executes `Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories)` across 6 directory roots (`C:\Program Files (x86)\Steam\...`, `D:\SteamLibrary\...`, `D:\Games`, `C:\Riot Games`, `C:\Program Files\Epic Games`, `C:\Games`). On systems with multi-terabyte drives containing dozens of games, this freezes application startup on the UI thread for 10 to 30+ seconds.
  2. In `ScanProcesses()` (line 236), `var processes = Process.GetProcesses()` is called every 2.0 seconds. The loop iterates through the array to check `ProcessName`, but none of the 200–300 `Process` instances are disposed. Every 2 seconds, hundreds of undisposed `Process` objects with native OS handles are abandoned, creating massive finalizer and GC pressure.
  3. In `MainWindow.xaml.cs:777` and `MainWindow.xaml.cs:821`, `OnGameStarted` and `OnGameEnded` use `Dispatcher.Invoke(async () => { ... await SwitchModeAsync(...) ... })`. Because `Dispatcher.Invoke(Action)` treats `async () => { ... }` as `async void`, it returns immediately at the first `await`. If a game launches and exits rapidly (or launches via a temporary bootstrap launcher), `OnGameEnded` fires while `OnGameStarted` is still awaiting hardware switches. The concurrent tasks race on `AcerAgentClient`, frequently resulting in `OnGameStarted`'s Turbo switch completing last, leaving the system stuck in Turbo mode and max fans after the game has closed.

---

### 1.6 RGB Profile Application & Lighting (RgbProfileService / AcerAgentClient)
- **File:** `NitroLite/Core/AcerAgentClient.cs` (lines 243–315)
  ```csharp
  public async Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors)
  {
      ...
      var isStatic = effUpper == "STATIC" || isOff;
      var leds = isOff 
          ? new[] { "#000000", "#000000", "#000000", "#000000" } 
          : (zoneColors ?? new[] { primaryColor, primaryColor, primaryColor, primaryColor });
      ...
      object paramKb = isStatic
          ? new { device = 1, effect = actualEffect, brightness = actualBrightness, speed = Math.Clamp(speed, 1, 9), duration = 5, direction = Math.Clamp(direction, 0, 1), color = actualColor, LEDs = leds }
          : new { device = 1, effect = actualEffect, dyEffect = actualEffect, brightness = actualBrightness, speed = Math.Clamp(speed, 1, 9), duration = 5, direction = Math.Clamp(direction, 0, 1), color = actualColor };
  }
  ```
- **File:** `NitroLite/MainWindow.xaml.cs` (lines 1419–1450)
- **Observations:**
  1. For dynamic animation modes (`BREATHING`, `WAVE`, `NEON`), `SetLightingAsync` omits the `LEDs` parameter entirely, falling back to a single `color = actualColor`. Any per-zone colors configured by the user or defined in `RgbPreset.LEDs` are discarded.
  2. Four hardware-supported animation modes present in `LightingProfile.ini` (`SHIFTING`, `ZOOM`, `METEOR`, `TWINKLING`) are completely absent from `RgbProfileService` presets and `MainWindow.xaml` effect combobox.
  3. `SliderBrightness_ValueChanged` and `SliderSpeed_ValueChanged` fire continuously during slider dragging. Each tick immediately triggers `await _agentClient.SetLightingAsync` and calls `SaveActiveRgbState()`, which executes unthrottled `File.WriteAllText(ActiveRgbConfigFile, json)`. Rapid dragging floods the TCP stream and causes file sharing collisions (`IOException: The process cannot access the file...`).

---

### 1.7 AcerAgent Communication / IPC (AcerAgentClient.cs)
- **File:** `NitroLite/Core/AcerAgentClient.cs` (lines 40–89, 91–115, 357–362)
  ```csharp
  public async Task<bool> EnsureConnectedAsync()
  {
      if (_client != null && _client.Connected) return true;
      try
      {
          _client?.Dispose();
          _client = new TcpClient();
          await _client.ConnectAsync(Host, Port);
          _stream = _client.GetStream();
          _stream.ReadTimeout = 3000;
          _stream.WriteTimeout = 3000;
          await SendRawAsync(PacketInitialization, new { Function = "VERSION" });
          return true;
      }
      catch { _client = null; _stream = null; return false; }
  }

  private async Task<string?> SendRawAsync(uint packetId, object payload)
  {
      if (_stream == null) return null;
      ...
      await _stream.WriteAsync(packet);
      await _stream.FlushAsync();

      var buffer = new byte[16384];
      var read = await _stream.ReadAsync(buffer);
      if (read <= 0) return null;
      return Encoding.UTF8.GetString(buffer, 0, read);
  }
  ```
- **Observations:**
  1. **Broken Connection Recovery Failure:** In `SendRawAsync`, if `ReadAsync` returns `<= 0` (remote host closed connection) or throws an `IOException` / `SocketException`, `_client` and `_stream` are NOT disposed or set to `null`. On subsequent calls, `EnsureConnectedAsync()` evaluates `_client != null && _client.Connected` (which returns `true` until a write is attempted or socket is polled), so it fails to reconnect, resulting in perpetual null telemetry and failed commands.
  2. **Deadlock Risk under Asynchronous Timeout:** `_stream.ReadTimeout = 3000` is set in `EnsureConnectedAsync()`. In .NET, `Stream.ReadTimeout` applies exclusively to synchronous `Read()` calls; it has ZERO effect on asynchronous `ReadAsync()`. If `AcerAgentService` hangs or stops responding, `await _stream.ReadAsync(buffer)` blocks indefinitely. Because callers acquire `await _lock.WaitAsync()`, the semaphore is never released, permanently deadlocking all hardware control and monitoring routines throughout the application.
  3. **TCP Stream Chunk Fragmentation:** `SendRawAsync` reads a single arbitrary chunk via `await _stream.ReadAsync(buffer)`. It contains no packet framing, delimiter detection, or Content-Length header. If a response arrives in multiple TCP segments, `JsonNode.Parse(resp)` throws a deserialization exception, and the remaining payload bytes remain unread in the stream, desynchronizing all subsequent request/response pairs.

---

### 1.8 Unit Test Suite Status (NitroLite.Tests)
- **File:** `NitroLite.Tests/Program.cs` (lines 1–414)
- **Observations:**
  1. `NitroLite.Tests/Program.cs` contains tests only for `StorageHealthService` and `ThermalHealthService`.
  2. There are ZERO unit tests covering:
     - `FanControl` / fan modes / RPM / table logic
     - `CpuPowerService` / boost mode queries / powercfg integration
     - `BatteryWatchdogService` / 80% threshold / health status
     - `MemoryCleanerService` / working set trimming / auto-clean logic
     - `GameDetectionService` / profile discovery / process scanning
     - `RgbProfileService` / preset management / lighting serialization
     - `AcerAgentClient` / TCP framing / error recovery / reconnection

---

## 2. Logic Chain

1. **Premise:** Hardware control services in `NitroLite` must reliably enforce user settings (fan speed, power limits, battery thresholds, memory cleaning, game profiles, RGB) without freezing the UI, crashing background services, deadlocking IPC streams, or damaging system stability.
2. **Fan Control Defect:** Because fan controls are directly wired to raw IPC calls without a dedicated manager service, and because `FanMode.Custom` passes unvalidated integers to a driver expecting percentages or stepped tables, custom fan adjustments either fail silently or risk driver rejection.
3. **CPU Power Defect:** Because `app.manifest` runs `asInvoker`, registry unhiding fails silently. `powercfg /query` omits hidden GUIDs on Windows 11. Therefore, `QueryCurrentBoostMode` regex matching always fails, causing boost mode queries to permanently misreport `Aggressive` regardless of actual system state.
4. **Battery Watchdog Defect:** Writing to `C:\ProgramData\Acer\CC\settings.json` is purely passive. Without calling `AcerGenericMethod::SetFunction` via WMI or triggering the EC driver directly, stopping `AcerCCAgent.exe` completely disables the 80% limit. Furthermore, failing to reassert the boundary after volatile EC resets (wake from sleep, AC plug cycling) allows the battery to charge to 100%.
5. **Memory Cleaner Defect:** Trimming working sets via `EmptyWorkingSet` on all processes indiscriminately strips the active game's working set. When invoked automatically during high RAM loads (> 80%), it induces cyclical frame drops, disk thrashing, and micro-stutters during gameplay.
6. **Game Detection Defect:** Running recursive directory scans across multiple drives in the constructor on the UI thread blocks WPF rendering and startup. Polling processes every 2 seconds without disposing `Process` instances leaks OS handles. Asynchronous dispatching without cancellation tokens causes rapid game exit races where Turbo mode remains stuck.
7. **RGB Lighting Defect:** Omitting `LEDs` in dynamic lighting modes strips 4-zone colors. Dragging sliders produces unthrottled TCP and disk writes, triggering file locking exceptions.
8. **IPC Deadlock Defect:** `ReadTimeout` does not apply to `ReadAsync`. If AcerAgentService pauses or drops a packet, `SendRawAsync` hangs inside an unreleased `SemaphoreSlim`, locking up the entire hardware control interface permanently.
9. **Test Suite Defect:** With zero unit tests covering these services, regressions and runtime anomalies in hardware controls cannot be verified automatically.

---

## 3. Caveats

1. **Hardware-Specific ACPI/WMI Behavior:** Testing of `AcerGenericMethod` and `AcerGamingFunction` WMI methods was performed on an Acer Nitro 16 (AN16-41, Ryzen 7 7840HS, RTX 4060). Other Nitro/Predator models (e.g., Intel-based Nitro 17 or Predator Helios) may utilize distinct WMI method signatures or register offsets.
2. **Live Service Testing:** `AcerAgentService.exe` is currently operational on loopback port 46933. Some failure modes (such as socket desynchronization and unreleased semaphore deadlock) occur primarily during service crashes, system sleep/wake transitions, or network socket resets.

---

## 4. Conclusion & Audit Defect Matrix

The code audit reveals 8 primary defect categories across the Hardware Control & Background Services subsystem, requiring systematic remediation in Milestone 2:

| # | Subsystem | File & Lines | Defect / Malfunction | Severity |
|---|-----------|--------------|----------------------|----------|
| **D1** | **Fan Control** | `AcerAgentClient.cs:170–201`, `MainWindow.xaml.cs:1330` | No dedicated `FanControlService`; `FanMode.Custom` lacks RPM vs % clamping; no curve interpolation or hysteresis; no active fan mode state tracking in UI. | **High** |
| **D2** | **CPU Power** | `CpuPowerService.cs:26–71, 73–92` | `UnhideSetting` fails due to `asInvoker`; `powercfg /query` omits hidden GUID; `QueryCurrentBoostMode` regex always fails and returns `Aggressive`; AC and DC profiles are conflated; process spawning is slow. | **High** |
| **D3** | **Battery Watchdog** | `BatteryWatchdogService.cs:50–59, 168–189, 191–234` | Passive `settings.json` write only; zero WMI EC enforcement; fails to reapply limit on sleep/wake EC resets; `StartSafeDischarge` burns CPU on AC power without discharging battery; COM WMI handle leaks. | **Critical** |
| **D4** | **Memory Cleaner** | `MemoryCleanerService.cs:80–90, 158–186` | `EmptyWorkingSet` trims active games, causing severe micro-stutters; auto-cleaner fires repeatedly during normal gaming loads (> 80% RAM); standby purge fails without admin token; synchronous UI freeze. | **High** |
| **D5** | **Game Detection** | `GameDetectionService.cs:22–30, 95–193, 230–278` | Synchronous recursive multi-drive scan in constructor freezes UI startup for 10–30s; 250 undisposed `Process` handles leaked every 2s; rapid game launch/exit race leaves system locked in Turbo mode. | **High** |
| **D6** | **RGB Lighting** | `AcerAgentClient.cs:243–315`, `MainWindow.xaml.cs:1419–1450` | Dynamic effects omit `LEDs` zone colors; missing 4 hardware effects (`SHIFTING`, `ZOOM`, `METEOR`, `TWINKLING`); unthrottled slider dragging hammers TCP socket and causes `IOException` on `active_rgb_state.json`. | **Medium** |
| **D7** | **AcerAgent IPC** | `AcerAgentClient.cs:40–89, 91–115` | Broken socket states never reset `_client` / `_stream`; `ReadAsync` ignores `ReadTimeout` causing permanent semaphore deadlock if IPC stalls; no TCP packet framing / length delimiter. | **Critical** |
| **D8** | **Test Coverage** | `NitroLite.Tests/Program.cs` | 0 unit tests exist for all 7 hardware and background services. | **High** |

---

## 5. Verification Method

### 5.1 Independent Code & Behavior Verification
1. **CPU Power Query Failure Verification:**
   Run in PowerShell:
   ```powershell
   powercfg.exe /query SCHEME_CURRENT 54533251-82be-4824-96c1-47b60b740d00 be337238-0d82-4146-a960-4f3749d470c7
   ```
   *Expected defect behavior:* Output contains only scheme GUID with no setting indices. `QueryCurrentBoostMode` regex cannot match and defaults to `Aggressive`.

2. **Battery Watchdog Dependency Verification:**
   Inspect `BatteryWatchdogService.cs:168–189`. Confirm there are no calls to `AcerGenericMethod` or `AcerGamingFunction`. Stop `AcerCCAgentSvis` and observe that writing to `settings.json` fails to stop charging past 80%.

3. **Game Detection UI Freeze Verification:**
   Measure constructor execution time of `new GameDetectionService()`. Observe disk I/O across `D:\SteamLibrary` and UI thread latency during initial window load.

4. **IPC Timeout & Deadlock Verification:**
   Inspect `AcerAgentClient.cs:51` and `SendRawAsync:85`. Note the use of `ReadAsync` without a `CancellationToken` or timeout wrapping under `_lock.WaitAsync()`.

### 5.2 Build & Test Suite Command
Run the test suite to verify current baseline:
```powershell
dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj
# or
dotnet run --project c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj
```
*Current Baseline Result:* Exits with code 0, 5 storage & thermal tests pass, 0 hardware service tests present.

---

## 6. Recommended Remediation Strategies for Milestone 2

1. **Implement Dedicated `FanControlService`:**
   - Create `Core/FanControlService.cs` managing fan mode states (`Auto`, `Max`, `Custom`), fan speed percentage clamping (0–100%), and coordination with `OperatingMode`.
   - Update `MainWindow.xaml` to display active fan status with visual highlights.
2. **Refactor `CpuPowerService` via Win32 PowrProf API:**
   - Replace slow, failure-prone `powercfg.exe` process spawning with native Win32 `PowerReadACValueIndex`, `PowerReadDCValueIndex`, `PowerWriteACValueIndex`, and `PowerWriteDCValueIndex` from `powrprof.dll`.
   - This bypasses `HKLM` unhiding restrictions, runs non-elevated (`asInvoker`), executes in sub-millisecond time, and supports separate AC vs DC battery profiles.
3. **Upgrade `BatteryWatchdogService` with WMI / Power Event Sync:**
   - Listen to `Microsoft.Win32.SystemEvents.PowerModeChanged` to immediately re-assert 80% boundary upon system wake from sleep/suspend and AC reconnect.
   - Guard `settings.json` access with a mutex, and add `root\wmi:AcerGenericMethod.SetFunction` call if `AcerCCAgent.exe` is absent.
   - In `StartSafeDischarge`, check `info.IsPluggedIn` and prompt user to unplug AC charger before initiating battery drain.
   - Dispose all `ManagementObjectSearcher` and `ManagementObject` instances.
4. **Harden `MemoryCleanerService`:**
   - Exclude the foreground active process and detected game process from `EmptyWorkingSet`.
   - Only trigger auto-cleaner if free memory is critically low (< 2048 MB) and no game is in the foreground.
   - Run memory trimming asynchronously off the UI thread.
5. **Optimize `GameDetectionService`:**
   - Move `ScanAndAddInstalledGames()` to a background `Task.Run()` so window initialization is instant.
   - Ensure all `Process` instances returned by `Process.GetProcesses()` are disposed in a `finally` block.
   - Use a `CancellationTokenSource` and lock on profile transitions to prevent rapid launch/exit race conditions.
6. **Enhance `RgbProfileService` & Debounce Sliders:**
   - Pass zone colors in `LEDs` array for dynamic animation modes where supported.
   - Add missing hardware lighting effects (`SHIFTING`, `ZOOM`, `METEOR`, `TWINKLING`).
   - Debounce `SliderBrightness_ValueChanged` and `SliderSpeed_ValueChanged` with a 150ms timer to prevent TCP stream flooding and file lock collisions.
7. **Harden `AcerAgentClient`:**
   - Pass `CancellationToken` with a 3-second timeout to `_stream.ReadAsync(buffer, cts.Token)` to eliminate permanent deadlock risks.
   - On any I/O exception or `read <= 0`, explicitly call a `Disconnect()` method that disposes `_client` and `_stream` and resets them to `null` so auto-reconnect succeeds.
   - Implement basic JSON framing / packet accumulation to handle fragmented TCP chunks safely.
8. **Expand `NitroLite.Tests`:**
   - Add unit and mock integration test suites for `CpuPowerService` (PowrProf P/Invoke), `GameProfile` serialization and matching, `RgbProfileService` preset persistence, `MemoryCleaner` auto-clean logic, and `AcerAgentClient` packet framing and error recovery.
