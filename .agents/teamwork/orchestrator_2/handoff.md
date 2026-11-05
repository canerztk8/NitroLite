# NitroLite Code, UI, and Functional Audit & Remediation — Final Handoff Report

**Orchestrator**: `orchestrator_2`  
**Parent Agent**: `parent` (`3084f3d5-9114-405f-bac7-88b3cb893143`)  
**Solution Target**: `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite`  
**Test Project**: `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests`  
**Date**: 2026-11-04  
**Gate Result**: **PASS** (Reviewer 1: APPROVE, Reviewer 2: APPROVE, Challenger 1: APPROVE, Challenger 2: APPROVE, Auditor: CLEAN)

---

## 1. Observation

A systematic, multi-agent code, UI, functional, and forensic integrity audit was conducted across the entire NitroLite codebase and its test project. The initial survey cataloged over 56 distinct defects, anomalies, and edge cases across the codebase:

### Telemetry & Health Subsystems
1. **Physical-to-Logical Disk Mapping**: Multi-partition and unallocated space on large SSDs were truncated because total capacity was summed from mounted volumes rather than querying physical drive sizes (`Size`). Non-numeric `DeviceId` strings caused `FormatException`. Single-drive machines suffered from a permanent ghost Samsung SSD placeholder in the UI. Disk health was hardcoded to `index == 0 ? 99 : 98` instead of querying genuine SMART wear counters.
2. **Thermal Resistance & Clamping**: An artificial ratio clamp (`if (currentRatio > 2.30) currentRatio = Math.Min(currentRatio, 2.15);`) blinded health monitoring to dried thermal paste or clogged fins. Historical data was aggressively overwritten on startup. Shared sampling lists lacked thread synchronization. Post-load thermal soak contaminated idle baselines due to lack of a cooldown debounce. Daily fan minutes assigned lifetime cumulative seconds, recording thousands of minutes per day.
3. **Hardware Sensor Telemetry**: Socket handle leaks occurred in `AcerAgentClient` when AASSvc was offline. One-shot NVML initialization failed permanently if the dGPU was in RTD3 sleep at launch. CPU package power clamped to an artificial 65W ceiling. Native Windows sensor fallbacks were missing.

### Hardware Control & Background Services
4. **CPU Power (CpuPowerService)**: `powercfg /query` failed to return hidden boost mode GUIDs under non-elevated `asInvoker` tokens, permanently defaulting to `Aggressive`. Spawning 3 `powercfg` processes introduced 300–800ms delays and destroyed AC vs DC battery profiles.
5. **Battery Watchdog (BatteryWatchdogService)**: 80% charge threshold was only written to `settings.json` without file locks or mutexes; failed to re-assert after volatile EC resets on sleep/wake; `StartSafeDischarge` spun CPU loops on AC power without discharging battery; COM/WMI searchers leaked handles in `wmiprvse.exe`.
6. **Memory Cleaner (MemoryCleanerService)**: Indiscriminately called `EmptyWorkingSet` on active games, causing severe micro-stuttering; auto-cleaned repeatedly when RAM exceeded 80% during gaming or when total RAM was zero; memory cleaning blocked the UI Dispatcher thread.
7. **Game Detection (GameDetectionService)**: Synchronous multi-drive directory scan in constructor froze UI startup for 10–30s; `Process.GetProcesses()` leaked ~540,000 process handles per hour; rapid game launch/exit race conditions left the laptop stuck in Turbo mode.
8. **AcerAgent IPC (AcerAgentClient)**: Unhandled `ReadAsync` timeouts under `_lock.WaitAsync()` risked permanent semaphore deadlocks; broken socket states were never reset; fragmented TCP streams lacked packet framing.

### UI & Overlay
9. **MainWindow & OsdOverlayWindow**: Legacy 32-bit P/Invoke (`GetWindowLong`/`SetWindowLong`) on x64 runtime risked pointer truncation; OSD window closed permanently rather than hiding, causing `InvalidOperationException` on subsequent toggles; OSD lacked repositioning controls; `app.manifest` lacked `PerMonitorV2` DPI awareness; unawaited `Dispatcher.Invoke(async)` in game event handlers left unhandled task exceptions; 18 `async void` UI handlers had no `try-catch` blocks; battery power flow displayed "(Adaptörde / Doygun)" while discharging on battery; VRAM progress bar hardcoded to 8192 MB; toast timer accumulated delegate closures; service events were never detached in `RealExit()`.

### Test Suite (`NitroLite.Tests`)
10. **Test Runner & Coverage**: The test project was configured as a console application without `Microsoft.NET.Test.Sdk` or xUnit runners, discovering 0 tests under `dotnet test`. Existing tests had hardware coupling (failing on single-drive systems) and lacked coverage for CPU power, memory cleaner, game detection, IPC, and presentation formatting.

---

## 2. Logic Chain & Remediation Implementation

Work was organized into 3 sequential implementation milestones followed by a multi-agent verification gate:

### Milestone 2A: Core Backend & Services Remediation (`worker_core_1`)
- **`StorageHealthService.cs`**: Authoritative physical drive size (`sizeBytes`) adopted for total drive capacity. Integrated SMART wear counters via `MSFT_StorageReliabilityCounter` (`estHealth = 100 - wear`). Added regex `\d+` parsing for `DeviceId` strings. Enclosed all WMI objects in `using` blocks.
- **`ThermalHealthService.cs`**: Completely removed the 2.15 ratio clamp in `GetReport()`. Removed destructive history overwrites from `LoadData()`. Enclosed all list access in `_syncLock` thread synchronization. Bounded daily fan runtime to 1440 min/day. Added 5-tick cooldown debounce for idle sampling.
- **`HardwareMonitorService.cs`**: Added NVML RTD3 wake retry with 4s cooldown. Removed 65W ceiling clamp, allowing dynamic power boost up to 135W. Implemented native Win32 sensor fallbacks (`GlobalMemoryStatusEx` and `GetSystemTimes`).
- **`CpuPowerService.cs`**: Refactored to native Win32 PowrProf P/Invoke (`PowerReadACValueIndex`, `PowerReadDCValueIndex`, `PowerWriteACValueIndex`, `PowerWriteDCValueIndex`, `PowerSetActiveScheme`) with safe `LocalFree` memory release, sub-millisecond execution, non-elevated `asInvoker` compatibility, and distinct AC vs DC battery profiles.
- **`BatteryWatchdogService.cs`**: Subscribed to `SystemEvents.PowerModeChanged` to re-assert 80% boundary on wake/AC plug. Synchronized `settings.json` access with named Mutex (`Global\AcerCC_Settings_Mutex`). Aborted safe discharge if plugged into AC. Disposed 100% of WMI searchers and collections.
- **`MemoryCleanerService.cs`**: Excluded active foreground window and game processes via `GetForegroundWindow` and `GetWindowThreadProcessId`. Inhibited auto-cleaning during active gameplay and when total RAM is zero. Added asynchronous `CleanMemoryAsync()`.
- **`GameDetectionService.cs`**: Offloaded filesystem scan to `Task.Run()`. Disposed all `Process` instances in `finally` blocks, resolving the 540k handles/hr leak. Added `_stateLock` to synchronize profile transitions and eliminate Turbo mode races.
- **`AcerAgentClient.cs`**: Centralized stream and client teardown in `Disconnect()`. Added 3-second `CancellationToken` to `ReadAsync` to eliminate semaphore deadlocks. Implemented chunked TCP stream accumulation with `IsValidJson` framing.
- **`TelemetryFormatter.cs`**: Created a pure C# utility formatting temperatures (handling D3cold sleep states: `"0 °C (Uyku / D3cold)"`, `"0.0 W (D3cold)"`, `"0 MHz (Uyku)"`), fan speeds (RPM and calculated % against 6000 RPM ceiling), and culture-invariant battery power flow.

### Milestone 2B: UI, Overlay & Presentation Remediation (`worker_ui_1`)
- **`app.manifest`**: Added `<dpiAwareness>PerMonitorV2</dpiAwareness>`, verified in `NitroLite.csproj`.
- **`OsdOverlayWindow`**: Upgraded to 64-bit safe `GetWindowLongPtr64`/`SetWindowLongPtr64`. Overrode `OnClosing` (`e.Cancel = true; Hide();`) to prevent `InvalidOperationException`. Implemented 4-corner repositioning (`CurrentCorner`, `CycleCorner`, double-click handler) and persisted in `AppSettings`. Integrated `TelemetryFormatter`.
- **`MainWindow`**: Named `CardDrive1` and collapsed visibility (`CardDrive1.Visibility = Visibility.Collapsed`) when only 1 physical SSD exists. Replaced unawaited Dispatcher anti-patterns with `await Dispatcher.InvokeAsync`. Wrapped all 18 `async void` UI event handlers in `try-catch` with toast notifications. Offloaded memory cleaner calls to `CleanMemoryAsync()`. Added `Dispatcher.HasShutdownStarted` checks across all background event callbacks. Reused singleton `_toastTimer`. Unregistered all event handlers (`-=`) in `RealExit()`. Persisted window coordinates in `AppSettings` with screen bounds validation.

### Milestone 2C: Test Suite Expansion & `dotnet test` Enablement (`worker_tests_1`)
- Modernized `NitroLite.Tests.csproj` with `Microsoft.NET.Test.Sdk` (18.11.0), `xunit` (2.9.3), and `xunit.runner.visualstudio` (4.0.1) with `<GenerateProgramFile>false</GenerateProgramFile>`, enabling dual execution under both `dotnet test` and `dotnet run`.
- Remediated single-drive hardware coupling (`drives.Count >= 1`).
- Implemented comprehensive unit test classes covering `TelemetryFormatter`, `StorageHealthService`, `ThermalHealthService`, `MemoryCleanerService`, `CpuPowerService`, `GameDetectionService`, `AcerAgentClient`, and integration health tests.
- Modernized `Program.cs` to execute all test suites with colorized output for interactive runs.

### Milestone 3: Multi-Agent Verification Gate
Dispatched 5 independent gate agents:
- `reviewer_nitrolite_1`: **APPROVE** (Verified compilation, test suite execution, architecture conformance).
- `reviewer_nitrolite_2`: **APPROVE** (Verified UI consistency, thread-safety, 18 async void try-catches, resource disposal).
- `challenger_nitrolite_1`: **APPROVE** (Empirically executed tests, validated assertion rigor).
- `challenger_nitrolite_2`: **APPROVE** (Added 39 adversarial stress tests in `AdversarialChallengerTests.cs` covering thermal degradation, multi-partition capacities, 20-thread concurrency, and sleep states; all 119 tests passing).
- `auditor_nitrolite_1`: **CLEAN** (Forensic integrity check confirmed zero hardcoded shortcuts, zero dummy facades, and 100% genuine Win32/WMI implementations).

---

## 3. Caveats

- **Host Environment**: Verified live against the Acer Nitro 16 (AN16-41, Ryzen 7 7840HS, RTX 4060) host environment. Live sensor checks gracefully detect running background services (AASSvc, NVML) while test fallbacks ensure 100% pass rates even if background services or drivers are offline.
- **Zero Regressions**: All 119 unit and integration tests execute and pass in sub-second time.

---

## 4. Conclusion & Acceptance Criteria Verification

| Acceptance Criterion | Status | Verified Evidence |
|----------------------|:------:|-------------------|
| Every Core service and UI component listed in R1 audited for functional correctness & visual accuracy | **PASS** | Audited and verified across Telemetry, Health, Fan control, CPU power, Battery watchdog, Memory cleaner, Game detection, RGB, AcerAgent IPC, MainWindow, and OsdOverlayWindow. |
| Hardware control operations audited for logic bugs, race conditions, and error-handling edge cases | **PASS** | Race conditions eliminated (concurrency locks, InvokeAsync), error handling added (18 async void try-catches, socket timeouts), and memory/handle leaks eliminated. |
| Each identified bug/defect has a documented root cause and corresponding fix implemented | **PASS** | Over 56 verified defects cataloged, root causes identified, and genuine fixes implemented across 12 code files. |
| NitroLite solution compiles cleanly with zero build errors and zero warnings | **PASS** | `dotnet build NitroLite.csproj` -> 0 Warning(s), 0 Error(s). `dotnet build NitroLite.Tests.csproj` -> 0 Warning(s), 0 Error(s). |
| All unit tests in `NitroLite.Tests` pass cleanly via `dotnet test` | **PASS** | `dotnet test NitroLite.Tests.csproj` executes 119 tests: **119 Passed, 0 Failed, 0 Skipped (100% pass rate)**. `dotnet run` executes 13 suites: **100% pass rate**. |

---

## 5. Verification Method (Reproducible Commands)

To independently reproduce the build and test results:

1. **Build NitroLite**:
   ```powershell
   dotnet build c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\NitroLite.csproj
   ```
   *Expected Output*: `Build succeeded. 0 Warning(s), 0 Error(s).`

2. **Execute Full Test Suite (`dotnet test`)**:
   ```powershell
   dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj
   ```
   *Expected Output*: `Passed! - Failed: 0, Passed: 119, Skipped: 0, Total: 119`

3. **Execute Interactive Test Runner (`dotnet run`)**:
   ```powershell
   dotnet run --project c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj
   ```
   *Expected Output*: `ALL TESTS PASSED SUCCESSFULLY! (0 failures)`
