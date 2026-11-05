## 2026-11-04T03:02:32Z
You are worker_core_1, a teamwork_worker subagent.

## Working Directory
`c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_core_1`

## Authoritative Request & Specifications
Read and follow:
1. `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md` (MUST read first)
2. `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md`
3. Explorer handoff reports:
   - `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_thermal_1\handoff.md`
   - `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_window_1\handoff.md`
   - `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_rgb_1\handoff.md`

## MANDATORY INTEGRITY WARNING
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

## Objective
Implement Milestone 1 (all three requirements R1, R2, R3) in `NitroLite` and add comprehensive unit tests to `NitroLite.Tests`:

### 1. Requirement R1: Thermal Degradation Trend & Sub-Component Telemetry Matrix
- In `NitroLite/Core/ThermalHealthService.cs`:
  - Implement `GetStabilityTrend(int days = 30)` calculating the 14/30-day thermal stability & aging trend from `DailyHistory` in `thermal_health.json`. Use linear regression slope of thermal resistance $R_{th}$ and coefficient of variation ($CV$). Return a `ThermalStabilityTrend` model with days count, stability score percent (e.g. 98.7%), degradation slope, and status text/level (e.g. "Kararlı, Macun kondisyonu mükemmel"). Handle $N=0$ and $N=1$ edge cases safely without divide-by-zero.
- In `NitroLite/Core/StorageHealthService.cs`:
  - Expose `Temperature` on `StorageDriveInfo` for Disk 0 (990 Pro) and Disk 1 (PM9A1) using `MSFT_StorageReliabilityCounter` with non-elevated physical telemetry baselines (~42°C and ~39°C).
- In `NitroLite/Core/BatteryWatchdogService.cs`:
  - Expose `TemperatureCelsius` on `BatteryStatusInfo` via ACPI thermal zone / thermodynamic wattage model (~29.5°C).
- In `NitroLite/Core/TelemetryFormatter.cs`:
  - Add formatting methods for the stability trend and sub-component temperature metrics.
- In `NitroLite/MainWindow.xaml` and `MainWindow.xaml.cs`:
  - Under `TabBattery` (Card 3), integrate an ergonomic 14/30-day thermal stability indicator box with a radio/toggle selector, progress/status bar, and description label.
  - Add a compact 4-card `UniformGrid` Sub-Component Thermal Matrix displaying live physical temperatures: Motherboard/VRM (`SYS1_TEMPERATURE` from AcerAgent ~51-58°C), NVMe SSD 1 (990 Pro), NVMe SSD 2 (PM9A1), and Battery pack.
  - Wire live updates into `PollTelemetryAsync()`, `RefreshThermalHealthUi()`, `OnBatteryStatusUpdated()`, and `RefreshStorageHealthUi()`.

### 2. Requirement R2: Window Launch Behavior Fix (Manual vs Autostart)
- In `NitroLite/Core/StartupOptions.cs` (create this class):
  - Parse command-line arguments: set `StartMinimized = true` ONLY if args contains `--autostart` or `--minimized` (case-insensitive).
  - Manual launches (empty args or clicking desktop shortcut) must have `StartMinimized = false`.
- In `NitroLite/App.xaml.cs`:
  - Use `StartupOptions.Parse(e.Args)`.
  - DO NOT override manual launch with `cfg.StartMinimized` from `config.json`!
  - When second instance detects mutex, call Win32 `AllowSetForegroundWindow(ASFW_ANY = -1)` before signaling `wakeEvent.Set()`.
  - On manual launch, show and focus `MainWindow`.
- In `NitroLite/Core/DebloatService.cs`:
  - Update autostart registry command to include `--autostart`.
- In `NitroLite/MainWindow.xaml.cs`:
  - In `ShowWindow()`, call Win32 `ShowWindow(hwnd, SW_RESTORE)` and `SetForegroundWindow(hwnd)` alongside WPF `Show()`, `WindowState = WindowState.Normal`, `Activate()`, and `Focus()`.

### 3. Requirement R3: Keyboard Backlight State Persistence across Restarts & Power Events
- In `NitroLite/Core/Models.cs`:
  - Add `public LightingConfig? KeyboardLighting { get; set; }` and `public bool IsBacklightOff { get; set; } = false;` to `AppSettings`.
- In `NitroLite/MainWindow.xaml.cs`:
  - Standardize persistence: save to `settings.json` (synchronizing with `config.json` and `active_rgb_state.json`).
  - In `TurnOffLightsAsync()`, set `IsBacklightOff = true`, `Brightness = 0`, `Effect = "OFF"`, and immediately save to `settings.json`.
  - On startup, load `settings.json` and apply. If `IsBacklightOff` or `Brightness == 0`, ensure lights remain strictly off.
  - Subscribe to `SystemEvents.PowerModeChanged` and `SystemEvents.SessionSwitch` in `SetupEvents()` (and unsubscribe on exit).
  - On `PowerModes.Resume` and `SessionSwitchReasons.SessionUnlock`, execute `ReassertKeyboardLightingAsync()` with staged dual-pulse re-assertion (~250ms and ~1500ms) with socket reconnect retry to override Acer EC firmware default resets.

### 4. Unit Tests in `NitroLite.Tests`
- Add unit tests covering:
  - `ThermalHealthServiceTests.cs`: test `GetStabilityTrend` calculation, 14-day vs 30-day, empty/single history, degradation slope, and status classification.
  - `StartupOptionsTests.cs`: test empty args (manual launch), `--autostart`, `--minimized`, registry command line generation, and case-insensitivity.
  - `KeyboardBacklightPersistenceTests.cs`: test settings serialization to `settings.json`, `IsBacklightOff` flag persistence, and power event re-assertion logic.
- Ensure all existing tests (119 passed) AND new tests compile and pass with 0 errors.

## Verification
You MUST execute the builds and tests yourself using run_command:
1. `dotnet build c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.sln`
2. `dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj`
Document the exact command outputs in your handoff report.

## Output
Write your detailed changes and test results to:
`c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_core_1\changes.md`
and write your structured handoff to:
`c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_core_1\handoff.md`.
Send a completion message when finished.


## 2026-11-04T03:12:51Z
**Context**: Milestone 1 Implementation (R1, R2, R3, Unit Tests)
**Content**: Checking in on implementation progress. We see `StartupOptions.cs` has been created.
**Action**: Continue implementation, run build and tests, and provide handoff when complete.
