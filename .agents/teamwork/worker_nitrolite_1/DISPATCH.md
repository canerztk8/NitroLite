# Dispatch to worker_nitrolite_1

## Objective
Implement Milestone 1: Power Profile Automation (R1), Battery Discharge Telemetry & Dynamic Runtime (R2), Game OSD FPS/Frametime Integration (R3), and Comprehensive Unit Tests (R4) with zero regressions in NitroLite.

## Working Directory
`c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_nitrolite_1\`

## Authoritative User Request
Read verbatim from: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md`

## Project Architecture & Plans
Read `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md`
Read Survey Reports:
- `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_power_survey_1\handoff.md` and `analysis.md`
- `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_battery_survey_1\handoff.md` and `analysis.md`
- `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_osd_survey_1\handoff.md` and `analysis.md`

## Scope of Implementation (Exclusive File Ownership)
You have exclusive write ownership of:
1. `NitroLite/Core/Models.cs`
   - Extend `AppSettings` with `DcPowerProfilePreference DcPowerMode` (enum `Eco`, `Balanced`) and `bool AutoRefreshRateSwitching`.
   - Extend `BatteryStatusInfo` with `DischargeRateMilliwatts`, `RemainingCapacityMWh`, `SmoothedDischargeRateWatts`.
   - Add `FpsTelemetryInfo` (`IsAvailable`, `Fps`, `FrametimeMs`, `GameTitle`).
2. `NitroLite/Core/IDisplayRefreshRateService.cs` & `DisplayRefreshRateService.cs`
   - Interface `IDisplayRefreshRateService` (`int GetCurrentRefreshRate()`, `bool SetRefreshRate(int hz)`).
   - Win32 implementation using `EnumDisplaySettings` and `ChangeDisplaySettingsEx` (using `CDS_UPDATEREGISTRY | CDS_GLOBAL`), switching to 60Hz and 165Hz safely without touching GPU adapters or MUX hardware.
3. `NitroLite/Core/PowerAutomationService.cs`
   - Listen to `SystemEvents.PowerModeChanged` and `GetSystemPowerStatus` with a 500ms debounce.
   - When AC is disconnected (DC mode):
     - If `DcPowerMode == Eco`: saves current operating mode, CPU boost mode, and fan mode; applies `OperatingMode.Quiet` via `AcerAgentClient`, `CpuBoostMode.Disabled` via `CpuPowerService`, `FanMode.Auto`, and switches display to 60Hz if `AutoRefreshRateSwitching` is enabled.
     - If `DcPowerMode == Balanced`: retains current settings without alteration.
   - When AC is reconnected (AC mode):
     - If DC Eco was active, restores saved operating mode, CPU boost, and fan mode.
     - If `AutoRefreshRateSwitching` is enabled, restores display refresh rate to 165Hz.
4. `NitroLite/Core/BatteryWatchdogService.cs`
   - In battery polling, extract `RemainingCapacity` (mWh), `DischargeRate` (mW), and `Voltage` (mV) from `root\WMI:BatteryStatus`.
   - Implement EMA smoothing filter on discharge rate ($\alpha = 0.20$, $\tau \approx 13.5\text{s}$) with zero-lag cold-start seeding ($S[0] = R[0]$ upon unplug).
   - Calculate `EstimatedRemainingMinutes = (int)((remMWh / smoothedDischargeMw) * 60.0)` clamped to [1, 1440].
5. `NitroLite/Core/FpsMonitorService.cs`
   - Open memory-mapped file `"RTSSSharedMemoryV2"`.
   - Verify `'RTSS'` signature `0x53535452`, read `dwFrameTime` in microseconds (convert to ms), read `dwStatFramerateAvg`, and resolve active process/title.
   - Graceful fallback: If RTSS is closed (`FileNotFoundException`) or no game is running, smoothly return `IsAvailable = false`, `Fps = 0`, `FrametimeMs = 0` without crashing or spamming errors.
6. `NitroLite/Core/TelemetryFormatter.cs`
   - Pure, testable static methods:
     - `CalculateRemainingMinutes(double remMWh, double dischargeMw)`
     - `UpdateSmoothedRate(double current, double instant, ref bool initialized, double alpha = 0.20)`
     - `FormatFps(double fps, bool isAvailable)` -> `"{0:F0} FPS"` or `"-- FPS"`
     - `FormatFrametime(double frametimeMs, bool isAvailable)` -> `"{0:F1} ms"` or `"-- ms"`
     - `FormatBatteryPowerFlow(BatteryStatusInfo? status)` -> Dual-unit power flow when discharging: `"{0:F1} W ({1:N0} mW)"` while preserving backwards-compatible calls.
     - `FormatRemainingTime(int minutes, bool isPluggedIn, bool isCharging)` -> Turkish format `"{hours} sa {mins} dk"` or `"Adaptöre Bağlı"` or `"Hesaplanıyor..."`.
7. `NitroLite/MainWindow.xaml` & `MainWindow.xaml.cs`
   - Tab 4 Battery Card: Display instantaneous discharge rate with dual units (Watt and mW) and dynamic remaining time (`Kalan Süre: X sa Y dk`).
   - Settings UI: Add selector for DC power preference (Eco vs Balanced) and toggle for automatic refresh rate switching (60Hz / 165Hz).
   - Wire `PowerAutomationService` and `FpsMonitorService` into application lifecycle.
8. `NitroLite/OsdOverlayWindow.xaml` & `OsdOverlayWindow.xaml.cs`
   - Add a 4th row `PnlOsdGame` to the OSD HUD displaying `TxtOsdFps`, `TxtOsdFrametime`, and active game title matching the existing dark monospace theme (#F20F1115, Consolas, high-contrast text).
   - Update `OsdOverlayWindow.xaml.cs` to update this row from `FpsTelemetryInfo` or via `UpdateData`.
9. `NitroLite.Tests/`
   - Create `PowerAutomationTests.cs`: Test AC/DC transitions, Eco vs Balanced mode selection, state save/restore, refresh rate calls via mock `IDisplayRefreshRateService`.
   - Create `BatteryTelemetryTests.cs`: Test EMA smoothing filter, runtime calculation, dual-unit formatting, zero-rate protection, charging states, and 24h clamping.
   - Create `FpsTelemetryTests.cs`: Test RTSS memory parsing, corrupted buffer validation, fallback when unavailable, and FPS/frametime formatting.
   - Verify all 201 existing unit tests continue to pass!

## Strict Boundaries & Constraints (Requirement R4)
- DO NOT TOUCH MUX switch logic, WMI display multiplexer classes, nvlddmkm, or GPU DDS mode switching. Display refresh rate switching must operate strictly through Win32 `ChangeDisplaySettingsEx` on display settings.
- DO NOT break any of the 136 existing `x:Name` UI elements in `MainWindow.xaml` or existing event handlers.
- DO NOT introduce AI buzzwords, em-dashes, or vibecoding styling (gradients, blurred cards, neon borders).

## Mandatory Integrity Warning
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

## Verification Requirements
You MUST execute and verify:
1. `dotnet build NitroLite.sln` -> 0 errors, 0 warnings.
2. `dotnet test NitroLite.sln` -> All 201 existing tests + all new tests pass with 100% pass rate.
3. Document all build and test command outputs in your handoff report.

## Deliverables
Write your complete implementation report to:
`c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_nitrolite_1\handoff.md`
And notify the orchestrator via `send_message`.


## 2026-11-04T14:20:29Z
[Message] timestamp=2026-11-04T14:20:29Z sender=f5e649ef-7d5d-4bf9-80d9-340b359f222c priority=MESSAGE_PRIORITY_HIGH content=You are worker_nitrolite_1.
Your working directory is: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_nitrolite_1\
Read your task instructions in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_nitrolite_1\DISPATCH.md
Read the authoritative user request in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
Read the global project plan in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md
Read the survey handoff reports:
- c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_power_survey_1\handoff.md
- c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_battery_survey_1\handoff.md
- c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_osd_survey_1\handoff.md

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Implement Milestone 1 in NitroLite and NitroLite.Tests:
1. AC/DC Power Automation & Profile Switching (Eco vs Balanced, 60Hz on DC, state restore + 165Hz on AC).
2. Battery instantaneous discharge rate (Watt/mW) and EMA dynamic remaining runtime (saat:dakika).
3. Game OSD FPS and frametime telemetry HUD row via RTSS shared memory bridge with graceful fallback.
4. Settings persistence in settings.json.
5. Add unit tests (PowerAutomationTests, BatteryTelemetryTests, FpsTelemetryTests) to NitroLite.Tests.
6. Verify build (dotnet build NitroLite.sln) and test suite (dotnet test NitroLite.sln - all 201 existing tests + new tests must pass).

Write your completion report to:
c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_nitrolite_1\handoff.md
When done, notify me via send_message with a complete summary and file paths.


## 2026-11-04T14:30:54Z
[Message] timestamp=2026-11-04T14:30:54Z sender=f5e649ef-7d5d-4bf9-80d9-340b359f222c priority=MESSAGE_PRIORITY_HIGH content=**Context**: Milestone 1 Implementation in NitroLite.
**Content**: Heartbeat check: It has been ~10 minutes since dispatch. How is progress proceeding on Step 1–Step 10?
**Action**: Please send a brief status update on which step you are actively working on and update your progress.md.
