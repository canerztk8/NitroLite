# BRIEFING — 2026-11-04T14:14:40Z

## Mission
Investigate Requirement R2: Instantaneous battery discharge rate (Watt / mW) and dynamic remaining battery runtime estimation (hours:minutes) in NitroLite, designing data models, telemetry sources, algorithm, UI integration, and testing strategy.

## 🔒 My Identity
- Archetype: explorer
- Roles: explorer, investigator, analyst
- Working directory: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_battery_survey_1\
- Original parent: f5e649ef-7d5d-4bf9-80d9-340b359f222c
- Milestone: Requirement R2 Survey & Architecture Design

## 🔒 Key Constraints
- Read-only investigation — do NOT implement
- Do NOT touch or modify MUX switch logic or settings (Requirement R4)
- Maintain all 201 existing unit tests passing without regression
- Working directory boundary: only write to c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_battery_survey_1\
- Keep UI consistent with NitroLite clean design system

## Current Parent
- Conversation ID: f5e649ef-7d5d-4bf9-80d9-340b359f222c
- Updated: 2026-11-04T14:14:40Z

## Investigation State
- **Explored paths**:
  - `NitroLite/Core/BatteryWatchdogService.cs` (and mapping from `BatteryChargeLimitService.cs`)
  - `NitroLite/Core/HardwareMonitorService.cs`
  - `NitroLite/Core/Models.cs` (`BatteryStatusInfo`, `BatteryHealthDetails`)
  - `NitroLite/Core/TelemetryFormatter.cs` (`FormatBatteryPowerFlow`, `FormatRemainingTime`)
  - `NitroLite/MainWindow.xaml` and `MainWindow.xaml.cs` (Tab 4 `TabBattery`, `TxtBatDischargeRate`, `TxtBatRemainingTime`, `BadgeBattery`)
  - `NitroLite.Tests/` (201 automated unit tests across `TelemetryFormatterTests`, `AdversarialChallengerTests`, `IntegrationHealthTests`, etc.)
  - Live empirical ACPI/WMI queries on host Acer Nitro 16 (`root\WMI:BatteryStatus`, `BatteryFullChargedCapacity`, `BatteryRuntime`, `Win32_Battery`, `kernel32!GetSystemPowerStatus`)
- **Key findings**:
  - `root\WMI:BatteryStatus` reliably exposes `RemainingCapacity` in mWh ($66,543\text{ mWh}$) and `DischargeRate` in mW.
  - Previous runtime calculation `(rem / dr) * 60.0` was unsmoothed and untestable in CI due to direct WMI hardware calls.
  - Formulated Exponential Moving Average ($\alpha = 0.20$, $\tau \approx 13.5\text{s}$) with zero-lag initial seeding upon AC disconnection ($S[0] = R[0]$).
  - Designed pure testable methods on `TelemetryFormatter.cs` (`CalculateRemainingMinutes`, `UpdateSmoothedRate`).
  - Formulated UI presentation supporting dual Watt/mW figures (`14.8 W (14.820 mW)`) and remaining time (`Kalan Süre: {H} sa {M} dk`) while strictly protecting all 201 existing unit tests.
- **Unexplored areas**: None for R2 scope.

## Key Decisions Made
- Selected WMI `root\WMI:BatteryStatus` + Win32 `GetSystemPowerStatus` as the authoritative telemetry pair.
- Adopted EMA smoothing ($\alpha = 0.20$) to prevent UI flutter during CPU burst spikes.
- Recommended extracting math calculation to static pure methods on `TelemetryFormatter` for 100% test coverage in `NitroLite.Tests`.
- Preserved existing `FormatBatteryPowerFlow` and `FormatRemainingTime` signatures to safeguard the 201-test regression gate.

## Artifact Index
- `DISPATCH.md` — Task description and message history
- `BRIEFING.md` — Persistent working memory index
- `progress.md` — Liveness heartbeat
- `analysis.md` — Complete technical investigation and architecture survey report
- `handoff.md` — 5-component handoff report conforming to protocol
