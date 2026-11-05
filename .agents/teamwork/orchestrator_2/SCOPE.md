# Scope: NitroLite Code, UI, and Functional Audit & Remediation

## Architecture
- NitroLite: .NET 10 / WPF / C# Windows application for Acer Nitro laptop management.
- NitroLite.Tests: Test project for NitroLite.
- Core Subsystems:
  - Telemetry & Health: `StorageHealthService`, `ThermalHealthService`, `HardwareMonitorService`, `TelemetryFormatter`.
  - Hardware Control & Services: `AcerAgentClient`, `CpuPowerService`, `BatteryWatchdogService`, `MemoryCleanerService`, `GameDetectionService`, `RgbProfileService`.
  - UI & Presentation: `MainWindow`, `OsdOverlayWindow`, `app.manifest`, ViewModels, Dispatcher & thread-safety.
  - Test Suite: `NitroLite.Tests` supporting `dotnet test` (119 xUnit tests) and `dotnet run` (13 suites) with comprehensive coverage.

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | Telemetry & Health Audit & Fix | Fix physical-to-logical disk mapping, remove thermal ratio clamp, add sensor fallbacks, fix memory leaks | M1 / M2A | R1 / R2 |
| 2 | Hardware Control & Services Fix | Win32 PowrProf for CPU power, WMI/wake sync for battery watchdog, safe memory cleaning, background game scan, IPC timeout & reconnect | M1 / M2A | R1 / R2 |
| 3 | UI & Overlay Remediation | 64-bit P/Invoke, OsdOverlayWindow lifecycle, PerMonitorV2 manifest, thread safety, 18 async void try-catch, display accuracy fallbacks | M1 / M2B | R1 / R2 |
| 4 | Test Project Modernization | Configure NitroLite.Tests with test runner, remove hardware coupling, add comprehensive unit tests | M2C | AC |
| 5 | Multi-Agent Verification Gate | Reviewers, Challengers, and Forensic Auditor verification | M3 | AC |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| 1 | Deep Survey & Defect Discovery | 3 parallel Explorers analyzing telemetry/health, hardware services, and UI/thread-safety | none | DONE |
| 2A | Core Backend & Services Remediation | Implement fixes in Core services (Storage, Thermal, HardwareMonitor, CpuPower, BatteryWatchdog, MemoryCleaner, GameDetection, AcerAgentClient) | M1 | DONE |
| 2B | UI, Overlay & Presentation Remediation | Implement fixes in MainWindow, OsdOverlayWindow, app.manifest, and presentation formatting | M2A | DONE |
| 2C | Test Suite Expansion & dotnet test Enablement | Modernize NitroLite.Tests, fix existing tests, and add extensive unit tests for all services and formatting | M2B | DONE |
| 3 | Verification Gate | 2 Reviewers, 2 Challengers, 1 Forensic Auditor | M2C | DONE (PASS) |

## Code Layout & Deliverables
- Solution: `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\NitroLite.csproj` (Build: 0 errors, 0 warnings)
- Tests: `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj` (Test run: 119/119 passing via `dotnet test`; 13/13 passing via `dotnet run`)
- Gate Status: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\orchestrator_2\GATE_STATUS.md` (Gate Result: PASS)
