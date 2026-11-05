# NitroLite Test Suite Survey & Telemetry Test Specification Report

**Target Solution**: `NitroLite.sln`  
**Test Project**: `NitroLite.Tests` (`NitroLite.Tests.csproj`)  
**Target Framework**: `.NET 10.0 Windows` (`net10.0-windows`)  
**Test Framework**: xUnit 2.9.3 + Microsoft.NET.Test.Sdk 18.11.0  
**Investigator**: Teamwork Explorer (`teamwork_explorer`)  
**Investigation Date**: November 4, 2026  
**Status**: Completed — Strictly Read-Only Investigation  

---

## 1. Executive Summary

This survey report provides a comprehensive, empirical architectural audit of the `NitroLite.Tests` test suite and build pipeline, coupled with rigorous test specifications for:
1. **%0.1 Low FPS Calculation & Filtering**: Permanent elimination of extreme/erroneous values (such as `398798 FPS` in Spotifast or desktop CEF apps), strict microsecond noise rejection (<1ms / 1–2 µs), ring buffer percentile calculations, and physical upper-clamping to average FPS.
2. **CS2 & Anti-Cheat Telemetry Fallback**: Resolving the `FPS: -- (RTSS Bekleniyor)` freeze caused by Valve Trusted Mode / anti-cheat DLL injection blocking in `cs2.exe` via an ETW/PresentMon fallback telemetry provider architecture.

### Key Discoveries & Survey Highlights:
- **Baseline Test Suite**: Exactly **320 unit tests** currently exist across **18 fixture classes**. All 320 tests pass cleanly in both `Debug` and `Release` configurations in ~4.0 seconds.
- **Build Pipeline Verification**: `dotnet build NitroLite.sln -c Release` completes with **0 errors and 0 warnings** in ~6.0 seconds.
- **Build Configuration Hurdle Identified**: The repository root contains both `NitroLite.sln` and an empty placeholder `NitroLite.slnx`. Invoking bare `dotnet build` or `dotnet test` fails with MSBuild error `MSB1011` ("Specify which project or solution file to use..."). Specifying the solution file (`dotnet test NitroLite.sln`) is mandatory.
- **RTSS Test Mocking Patterns**: RTSS shared memory (`RTSSSharedMemoryV2`) is currently tested via synthetic in-memory byte arrays (1024 to 6000 bytes) passed to static pure methods (`FpsMonitorService.ParseSharedMemory` and `ParseAppEntry`). 58 tests already cover RTSS parsing, but zero tests currently exist for ETW fallback or provider switching.
- **Root Cause of 398,798 FPS**: In `FpsMonitorService.cs` line 294 & 325, `low01Raw` is read directly from offset 5092 without upper-bound clamping or sanity validation. In RTSS default configurations (`PercentileCalc = 0`), offset 5092 is uncalculated or uninitialized. Furthermore, the ring buffer accepts frame times down to 501 µs (`ft > 500`), admitting microsecond noise from background desktop CEF/Electron applications (e.g. Spotifast, Discord, Spotify) which evaluate to hundreds of thousands of FPS (`1,000,000 / 2 µs = 500,000 FPS`).
- **Root Cause of CS2 Telemetry Failure**: Valve Trusted Mode in `cs2.exe` rejects third-party DLL injection (`RTSSHooks64.dll`). Consequently, `cs2.exe` either never registers an entry in RTSS shared memory or reports `0 FPS`. In `OsdOverlayWindow.xaml.cs` line 171, when a game is active but RTSS reports unavailable, the UI falls back to `"-- (RTSS Bekleniyor)"` permanently.

---

## 2. Test Environment & Build Architecture

### 2.1 Project & Solution Structure

| Project | File Path | Output Type | Target Framework | Key Dependencies |
|:---|:---|:---|:---|:---|
| **Main Application** | `NitroLite/NitroLite.csproj` | `WinExe` | `net10.0-windows` | `UseWPF`, `UseWindowsForms`, `System.Management 10.0.14` |
| **Test Suite** | `NitroLite.Tests/NitroLite.Tests.csproj` | `Exe` | `net10.0-windows` | `UseWPF`, `xunit 2.9.3`, `Microsoft.NET.Test.Sdk 18.11.0`, `xunit.runner.visualstudio 4.0.1` |
| **Active Solution** | `NitroLite.sln` | VS Solution | Visual Studio 17 | References `NitroLite` and `NitroLite.Tests` |
| **Stale Stub** | `NitroLite.slnx` | XML Stub | Empty (`<Solution></Solution>`) | Triggers `MSB1011` if solution not explicitly targeted |

### 2.2 Dual Test Runners in `NitroLite.Tests`
`NitroLite.Tests` is designed with dual execution capabilities:
1. **Standard xUnit CLI / CI Runner**:
   - Command: `dotnet test NitroLite.sln -c Release`
   - Executes all xUnit test classes annotated with `[Fact]` and `[Theory]`.
2. **Standalone Console Test Harness (`Program.cs`)**:
   - `NitroLite.Tests.csproj` specifies `<GenerateProgramFile>false</GenerateProgramFile>` and `<OutputType>Exe</OutputType>`.
   - `Program.cs` contains an interactive console harness executing 16 discrete test stages with colored terminal output and process exit codes (`0` for success, `1` for failures).

### 2.3 Fixture Breakdown (320 Baseline Tests)

The 320 unit tests are distributed across 18 fixture classes as follows:

| # | Test Fixture Class | File Path | Test Count | Scope of Coverage |
|:---:|:---|:---|:---:|:---|
| 1 | `AdversarialRegressionAndStartupTests` | `NitroLite.Tests/AdversarialRegressionAndStartupTests.cs` | **42** | OLS thermal regression slope, coefficient of variation, CLI startup flags |
| 2 | `AdversarialChallengerTests` | `NitroLite.Tests/AdversarialChallengerTests.cs` | **39** | High-concurrency stress, thermal ratio boundary, storage regex parsing |
| 3 | `AdversarialTelemetryAndPowerStressTests` | `NitroLite.Tests/AdversarialTelemetryAndPowerStressTests.cs` | **34** | Battery runtime clamping, EMA rate smoothing, PowerAutomation state machine |
| 4 | `FpsTelemetryTests` | `NitroLite.Tests/FpsTelemetryTests.cs` | **31** | RTSS synthetic buffer parsing, frametime formatting, ring buffer percentiles |
| 5 | `AdversarialRtssChallengeTests` | `NitroLite.Tests/AdversarialRtssChallengeTests.cs` | **27** | RTSS offset integer overflow, corrupted signatures, non-ASCII names |
| 6 | `TelemetryFormatterTests` | `NitroLite.Tests/TelemetryFormatterTests.cs` | **21** | String formatting for fan RPM/%, CPU/GPU temps, powers, clocks, RAM/VRAM |
| 7 | `BatteryTelemetryTests` | `NitroLite.Tests/BatteryTelemetryTests.cs` | **20** | AC/DC power flow, discharge wattage calculation, status parsing |
| 8 | `AdversarialLifecycleAndPersistenceTests` | `NitroLite.Tests/AdversarialLifecycleAndPersistenceTests.cs` | **16** | Mutex single-instance recovery, EventWaitHandle signaling, backlight persistence |
| 9 | `StorageHealthServiceTests` | `NitroLite.Tests/StorageHealthServiceTests.cs` | **15** | Physical drive regex parsing, NVMe wear percentage, capacity mapping |
| 10 | `ThermalHealthServiceTests` | `NitroLite.Tests/ThermalHealthServiceTests.cs` | **11** | Thermal resistance ratio ($R_{th}$), paste health, idle cooldown debounce |
| 11 | `MemoryCleanerServiceTests` | `NitroLite.Tests/MemoryCleanerServiceTests.cs` | **10** | Working set trimming, auto-clean thresholds, gaming prevention lock |
| 12 | `StartupOptionsTests` | `NitroLite.Tests/StartupOptionsTests.cs` | **10** | `--autostart`, `--minimized` argument combinations and casing |
| 13 | `CpuPowerServiceTests` | `NitroLite.Tests/CpuPowerServiceTests.cs` | **10** | `powercfg` GUID querying, CPU boost modes (Disabled/Aggressive/Efficient) |
| 14 | `AcerAgentClientTests` | `NitroLite.Tests/AcerAgentClientTests.cs` | **9** | TCP packet framing, JSON packet reassembly, header validation |
| 15 | `KeyboardBacklightPersistenceTests` | `NitroLite.Tests/KeyboardBacklightPersistenceTests.cs` | **8** | RGB profile JSON roundtrip, 4-zone lighting state, backlight-off preservation |
| 16 | `IntegrationHealthTests` | `NitroLite.Tests/IntegrationHealthTests.cs` | **6** | Live hardware sanity checks (AcerAgentClient, Storage, Thermals) |
| 17 | `PowerAutomationTests` | `NitroLite.Tests/PowerAutomationTests.cs` | **6** | AC power scheme automation, dynamic refresh rate (165Hz $\leftrightarrow$ 60Hz) |
| 18 | `GameDetectionServiceTests` | `NitroLite.Tests/GameDetectionServiceTests.cs` | **5** | Profile serialization, process scan fast-path, handle disposal safety |
| **Total** | **18 Fixture Classes** | | **320** | **100% Passed (0 Failures, 0 Skipped)** |

---

## 3. Analysis of Existing Telemetry & RTSS Test Patterns

### 3.1 RTSS Shared Memory Architecture (`RTSSSharedMemoryV2`)
Investigation of upstream `C:\Program Files (x86)\RivaTuner Statistics Server\SDK\Include\RTSSSharedMemory.h` confirms the binary layout of RTSS shared memory:
- **Header**:
  - `0x00` (`DWORD dwSignature`): `'RTSS'` (`0x53535452` or `0x52545353`)
  - `0x04` (`DWORD dwVersion`): $\ge$ `0x00020000` (Version 2.0+)
  - `0x08` (`DWORD dwAppEntrySize`): Entry stride (typically 320 bytes in legacy, or 6000+ bytes in v2.13+)
  - `0x0C` (`DWORD dwAppArrOffset`): Offset to application array (typically 128)
  - `0x10` (`DWORD dwAppArrSize`): Capacity of array
  - `0x40` (`DWORD dwLastForegroundApp`): Index of active foreground application
- **Application Descriptor Entry (`RTSS_SHARED_MEMORY_APP_ENTRY`)**:
  - `+0x000` (`DWORD dwProcessID`): Process ID of the hooked application
  - `+0x004` (`char szName[260]`): Executable name (null-terminated ASCII)
  - `+0x108` (`DWORD dwTime0`), `+0x10C` (`DWORD dwTime1`), `+0x110` (`DWORD dwFrames`)
  - `+0x118` (`DWORD dwFrameTime`): Last frame time in **microseconds** (µs)
  - `+0x134` (`DWORD dwStatFramerateAvg`): Average framerate in FPS
  - `+0x39C` (`DWORD dwStatFrameTimeBuf[1024]`): 1024-entry ring buffer of frame times in microseconds (offsets `+924` to `+5019`)
  - `+0x13A0` (`DWORD dwStatFrameTimeBufFramerate`): Fixed-point framerate ($\times 10$, offset `+5024`)
  - `+0x13E0` (`DWORD dwStatFramerate1Dot0PercentLow`): 1.0% Low FPS ($\times 10$, offset `+5088`)
  - `+0x13E4` (`DWORD dwStatFramerate0Dot1PercentLow`): 0.1% Low FPS ($\times 10$, offset `+5092`)

### 3.2 Current Testing & Mocking Methodology
In `NitroLite.Tests/FpsTelemetryTests.cs` and `AdversarialRtssChallengeTests.cs`:
1. **Synthetic Memory Buffer Factory**:
   ```csharp
   private static byte[] CreateSyntheticRtssBuffer(uint pid, string processName, uint frameTimeUs, uint avgFps)
   ```
   Constructs a raw `byte[]` simulating the RTSS header and single app entry.
2. **Pure Static Methods Tested**:
   - `FpsMonitorService.ParseSharedMemory(byte[] buffer, string? defaultGameTitle)`
   - `FpsMonitorService.ParseAppEntry(byte[] entryBytes, string? defaultGameTitle)`
3. **Current Ring Buffer Test**:
   - In `FpsTelemetryTests.ParseAppEntry_WithFrametimeRingBuffer_CalculatesDynamicPercentiles`: Populates 1022 entries with `6944` µs (144 FPS) and 2 entries with `20000` µs (50 FPS). Asserts `info.Low01Fps == 50`.
4. **Current Offline / Concurrency Test**:
   - Instantiates `new FpsMonitorService()` on developer machine where RTSS mapped memory might not exist. Verifies `GetCurrentFps()` returns safe fallback `{ IsAvailable = false, Fps = 0, FrametimeMs = 0 }` across 50 concurrent threads.

---

## 4. Root Cause Analysis: Defects & Vulnerabilities

### 4.1 %0.1 Low FPS Anomalies (`398798 FPS`)

#### Flaw 1: Unvalidated Trust in Fixed-Point Offset 5092 (`low01Raw`)
In `FpsMonitorService.cs` lines 294 and 323–326:
```csharp
uint low01Raw = entryBytes.Length >= 5096 ? BitConverter.ToUInt32(entryBytes, 5092) : 0;
...
if (low01Raw > 0)
{
    low01Fps = low01Raw / 10.0;
}
```
- **The Issue**: Inspection of RTSS's `Global` profile configuration (`C:\Program Files (x86)\RivaTuner Statistics Server\Profiles\Global`) reveals that by default:
  ```ini
  [Statistics]
  PercentileCalc=0
  ```
  When `PercentileCalc=0`, RTSS **does not calculate or update** `dwStatFramerate0Dot1PercentLow`!
- The memory at offset 5092 remains uninitialized, holds stale memory from previous processes, or contains internal flags.
- If offset 5092 holds `3987980` (or `0x003CD3A6`), `low01Fps` evaluates to exactly **`398798 FPS`**!

#### Flaw 2: Total Absence of Upper-Bound Clamping (Physics Invariant Violation)
- In no universe can the 0.1% lowest frame rate of a process exceed its average frame rate:
  $$\text{0.1\% Low FPS} \le \text{Average FPS}$$
- Currently, `FpsMonitorService.cs` has **zero clamping logic**. If average FPS is `60` or `144`, and offset 5092 or a corrupt ring buffer calculates `398,798 FPS`, it is directly passed to the UI and rendered on the OSD.

#### Flaw 3: Inadequate Noise Filter Window (< 1ms / 1–2 µs)
In `FpsMonitorService.cs` line 342:
```csharp
if (ft > 500 && ft < 10_000_000) // between 0.5ms and 10s
{
    validFrameTimes.Add(ft);
}
```
- The threshold `ft > 500` admits frame times down to $501\,\mu\text{s}$ (equivalent to nearly $2,000\,\text{FPS}$).
- Applications like Spotifast, Discord, Spotify, Steam Web Helper, and Windows DWM frequently invoke presentation or swapchain discard cycles that take $1\,\mu\text{s}$ to $200\,\mu\text{s}$. If a microsecond presentation artifact passes into the ring buffer, $1,000,000 / 2 = 500,000\,\text{FPS}$.
- Any frame time under $1,000\,\mu\text{s}$ ($< 1\,\text{ms}$) represents an unrendered frame or presentation glitch and must be strictly discarded.

---

### 4.2 CS2 Telemetry Blindspot (`FPS: -- (RTSS Bekleniyor)`)

#### Flaw 1: Valve Trusted Mode Blocks DLL Injection
- Counter-Strike 2 (`cs2.exe`) enforces Valve Trusted Mode by default. The CS2 engine loads a signature verification driver/hook that terminates or refuses injection of third-party DLLs, including RTSS's `RTSSHooks64.dll`.
- Unless players run CS2 with `-allow_third_party_software` (which permanently degrades Steam Trust Factor and prevents matchmaking on standard competitive servers), RTSS **cannot hook into DirectX 11 / Vulkan swapchains in `cs2.exe`**.

#### Flaw 2: Rigid Coupling to RTSS Shared Memory in `FpsMonitorService`
- In `MainWindow.xaml.cs` line 616:
  ```csharp
  var fpsInfo = _fpsMonitorService.GetCurrentFps(curGame?.ExeName, curGame?.DisplayName);
  _osdWindow.UpdateFpsData(fpsInfo);
  ```
- Because `FpsMonitorService` relies exclusively on `RTSSSharedMemoryV2`, and `cs2.exe` has no hook, `_fpsMonitorService` always returns `IsAvailable = false`.
- In `OsdOverlayWindow.xaml.cs` lines 170–175:
  ```csharp
  bool hasGame = !string.IsNullOrWhiteSpace(fpsInfo.GameTitle);
  TxtOsdFps.Text = hasGame ? "-- (RTSS Bekleniyor)" : "-- FPS";
  ```
  The user is stuck indefinitely on `"-- (RTSS Bekleniyor)"`.

#### Solution Mechanism: ETW / PresentMon Fallback Engine
- Windows Kernel Graphics Subsystem emits Event Tracing for Windows (ETW) events upon every swapchain presentation via the `Microsoft-Windows-DXGI` provider (`{CA11C036-0102-4A2D-A6AD-EE2BEB594443}`) and `Microsoft-Windows-DxgKrnl`.
- ETW operates inside the Windows kernel; anti-cheat engines (VAC, Easy Anti-Cheat, BattlEye) **cannot block ETW events** because no code or DLL is injected into the game process.
- Furthermore, RTSS itself bundles PresentMon in:
  `C:\Program Files (x86)\RivaTuner Statistics Server\Plugins\Client\PresentMonDataProvider\PresentMon-2.3.1-x64.exe`
- By implementing a hybrid fallback telemetry architecture:
  1. Primary: RTSS Shared Memory (zero overhead, hardware accurate).
  2. Fallback: DXGI ETW / PresentMon provider (activated automatically when an active game like `cs2.exe` produces no RTSS hook).

---

## 5. Detailed Test Specifications & Edge Case Matrix

### 5.1 Test Suite A: %0.1 Low FPS Calculation & Filtering

A dedicated test suite `Low01FpsCalculationTests` must be added to `NitroLite.Tests`.

#### Specification Table: Test Cases & Assertions

| Test Method Name | Scenario / Input Vector | Expected Behavior / Assertions | Rationale |
|:---|:---|:---|:---|
| `ParseAppEntry_Low01Fps_StrictlyClampedToAverageFps` | `avgFps = 144`, `low01Raw = 3987980` ($398,798\,\text{FPS}$) | `Assert.Equal(144, info.Low01Fps)` | Physical impossibility: 0.1% low cannot exceed mean FPS. Outlier eliminated. |
| `ParseAppEntry_Low01Fps_ModerateExcessClampedToAverageFps` | `avgFps = 60`, `low01Raw = 750` ($75\,\text{FPS}$) | `Assert.Equal(60, info.Low01Fps)` | Prevents slight over-reporting when average drops faster than percentile buffer. |
| `ParseAppEntry_WhenAverageFpsIsZero_Low01FpsIsZero` | `avgFps = 0`, `dwFrameTime = 0`, `low01Raw = 950` | `Assert.Equal(0, info.Low01Fps)`, `Assert.False(info.IsAvailable)` | Zero average FPS implies no active rendering; 0.1% low must be 0. |
| `ParseAppEntry_MicrosecondNoise_DiscardedFromRingBuffer` | Ring buffer with 1024 entries all $\le 999\,\mu\text{s}$ ($1\,\mu\text{s}, 2\,\mu\text{s}, 50\,\mu\text{s}, 500\,\mu\text{s}$) | `validFrameTimes.Count == 0`, `Assert.Equal(0, info.Low01Fps)` | Completely rejects microsecond glitches ($<1\,\text{ms}$) from Spotifast / CEF. |
| `ParseAppEntry_MixedMicrosecondNoise_CalculatesAccuratePercentile` | 200 entries at $2\,\mu\text{s}$ noise + 824 entries at $16,666\,\mu\text{s}$ ($60\,\text{FPS}$) | `Assert.Equal(60, info.Fps)`, `Assert.Equal(60, info.Low01Fps)` | Verifies noise is pruned without distorting the valid $60\,\text{FPS}$ frames. |
| `ParseAppEntry_BoundaryNoiseThreshold_1000UsAccepted` | Ring buffer with entries at $999\,\mu\text{s}$ (rejected) vs $1000\,\mu\text{s}$ ($1\,\text{ms}$, accepted) | Boundary threshold strictly enforced at $1000\,\mu\text{s}$. | Exactly $1.0\,\text{ms}$ ($1000\,\text{FPS}$) is the physical ceiling for frame times. |
| `ParseAppEntry_EmptyRingBuffer_ReturnsZeroLow01Fps` | Ring buffer completely empty (all 1024 entries are 0) | `Assert.Equal(0, info.Low01Fps)` | Game startup / initial frame state must not produce division by zero or NaN. |
| `ParseAppEntry_SingleSample_SafelyHandledWithoutException` | Ring buffer has exactly 1 valid entry ($16,666\,\mu\text{s}$), rest 0 | Returns safe value ($0$ or $60$), never throws `ArgumentOutOfRangeException`. | Minimum sample threshold requirement ($N < 10$) handles cold start gracefully. |
| `ParseAppEntry_InsufficientSamples_BelowTenSamples_ReturnsZero` | Ring buffer has 5 valid entries ($16,666\,\mu\text{s}$), rest 0 | `Assert.Equal(0, info.Low01Fps)` | Statistical percentile requires minimum sample density. |
| `ParseAppEntry_StandardPercentile_UniformDistribution` | 1024 entries all exactly $6,944\,\mu\text{s}$ ($144.0\,\text{FPS}$) | `Assert.Equal(144, info.Fps)`, `Assert.Equal(144, info.Low01Fps)` | Mathematical baseline: uniform pacing produces identical mean and percentile. |
| `ParseAppEntry_StandardPercentile_IsolatedStutterSpike` | 1022 entries at $6,944\,\mu\text{s}$ ($144\,\text{FPS}$) + 2 entries at $33,333\,\mu\text{s}$ ($30\,\text{FPS}$) | `Assert.Equal(144, info.Fps)`, `Assert.Equal(30, info.Low01Fps)` | $P_{99.9}$ of frametime ($33.3\,\text{ms}$) correctly maps to $30\,\text{FPS}$ 0.1% low. |
| `ParseAppEntry_CorruptedRawStat_PrefersOrFallsBackToRingBuffer` | `low01Raw = 0` (RTSS default `PercentileCalc = 0`), ring buffer populated | `Assert.Equal(expectedFps, info.Low01Fps)` | Ring buffer calculation seamlessly kicks in when RTSS stat field is 0. |
| `FormatLow01Fps_FormattingBoundaries` | `[Theory]` with `144.0` $\rightarrow$ `"144 FPS"`, `0.0` $\rightarrow$ `"-- FPS"`, `NaN` $\rightarrow$ `"-- FPS"`, `-1.0` $\rightarrow$ `"-- FPS"`, `available=false` $\rightarrow$ `"-- FPS"` | Formats correctly and safely across all boundary conditions. | UI consistency check in `TelemetryFormatter`. |

---

### 5.2 Test Suite B: CS2 & Anti-Cheat Telemetry Fallback

A dedicated test suite `Cs2AntiCheatTelemetryTests` must be added to `NitroLite.Tests`.

#### Specification Table: Test Cases & Assertions

| Test Method Name | Scenario / Input Vector | Expected Behavior / Assertions | Rationale |
|:---|:---|:---|:---|
| `GameDetection_CS2Process_IdentifiesAndAppliesEsportsProfile` | Process name `"cs2"` or `"cs2.exe"` | `Assert.Equal("Counter-Strike 2", profile.DisplayName)`, `Assert.Equal(CpuBoostMode.Aggressive, profile.TargetCpuBoost)` | Confirms CS2 profile exists and triggers esports boost configuration. |
| `TelemetryManager_WhenCs2ActiveAndRtssUnhooked_TriggersFallback` | Active game is `"cs2"`, RTSS shared memory has PID 0 or no entry for CS2 | Provider switches state: `ActiveProvider == TelemetryProviderType.EtwFallback` | Verifies detection of RTSS unhooked state for CS2. |
| `TelemetryManager_WithEtwEvents_EmitsValidCs2Fps` | Synthetic ETW present events for PID 14280 (`cs2.exe`) with $6.944\,\text{ms}$ delta | `Assert.True(info.IsAvailable)`, `Assert.Equal(144, info.Fps)`, `Assert.Equal("Counter-Strike 2", info.GameTitle)` | Solves `FPS: -- (RTSS Bekleniyor)`. OSD displays live framerate. |
| `EtwFallback_ProcessFilter_IgnoresUnrelatedProcessPresentEvents` | Interleaved ETW events: PID 14280 (`cs2.exe`) and PID 8912 (`firefox.exe`) | ETW provider filters strictly by target PID; Firefox presentations do not corrupt CS2 telemetry. | Process isolation in shared Windows ETW event stream. |
| `EtwFallback_RollingBuffer_CalculatesLow01FpsWithNoiseRejection` | ETW stream delivers 500 frames with $6.944\,\text{ms}$ pacing + 1 frame with $40\,\text{ms}$ stutter | `Assert.Equal(144, info.Fps)`, `Assert.Equal(25, info.Low01Fps)`, `Assert.True(info.Low01Fps <= info.Fps)` | Guarantees ETW fallback provider enforces the same 0.1% low calculation and clamping rules. |
| `TelemetryManager_ProviderHysteresis_PreventsRapidFlapping` | Single transient missing frame from RTSS | Provider does not immediately drop or flap between providers; debounce window enforced. | Prevents OSD visual flickering during scene transitions. |
| `TelemetryManager_WhenNonAdminOrEtwFails_DegradesGracefully` | ETW session throws `UnauthorizedAccessException` or platform exception | `Assert.False(info.IsAvailable)`, `Assert.Equal("Counter-Strike 2", info.GameTitle)`, no unhandled crash. | Non-admin environments fail gracefully without crashing the UI application. |
| `TelemetryManager_Disposal_CleansUpEtwSessionHandles` | Dispose called on `HybridFpsMonitorService` | Kernel trace session stopped, background threads terminated, zero handle leaks. | Eliminates resource leaks during app shutdown. |

---

## 6. Implementation Architecture & Refactoring Roadmap

To ensure zero disruption to the existing 320 tests, the following architectural approach is recommended for the implementer:

### 6.1 Proposed Provider Abstraction (`IFpsTelemetryProvider`)
```csharp
public interface IFpsTelemetryProvider : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    FpsTelemetryInfo GetCurrentFps(string? activeGameProcessName = null, string? displayName = null);
}
```
1. **`RtssFpsTelemetryProvider : IFpsTelemetryProvider`**:
   - Encapsulates RTSS memory-mapped file access and pure static parsers (`ParseSharedMemory`, `ParseAppEntry`).
   - Retains 100% backward compatibility with all existing 58 RTSS tests.
2. **`EtwFpsTelemetryProvider : IFpsTelemetryProvider`**:
   - Manages ETW kernel presentation events (`Microsoft-Windows-DXGI`).
   - Maintains an internal high-performance ring buffer of frame presentation timestamps.
   - Computes FPS, Frametime, and %0.1 Low with noise filtering (<1ms) and clamping.
3. **`HybridFpsMonitorService`**:
   - Manages priority routing: RTSS first; if active game is detected (e.g. `cs2.exe`) and RTSS returns `IsAvailable == false`, activates ETW fallback.

### 6.2 Proposed Clamping & Noise Filter Algorithm for `ParseAppEntry`
```csharp
// 1. Strict microsecond noise filtering: Reject anything under 1000 us (1 ms)
if (ft >= 1000 && ft < 10_000_000)
{
    validFrameTimes.Add(ft);
}

// 2. Percentile calculation on sorted valid frames (N >= 10)
if (validFrameTimes.Count >= 10)
{
    validFrameTimes.Sort();
    int p999Idx = Math.Min(validFrameTimes.Count - 1, (int)Math.Floor((validFrameTimes.Count - 1) * 0.999));
    uint p999Ft = validFrameTimes[p999Idx];
    if (p999Ft > 0)
    {
        low01Fps = 1_000_000.0 / p999Ft;
    }
}

// 3. Absolute Physics Invariant: Upper Clamping to Average FPS
if (fps > 0.0)
{
    if (low01Fps > fps)
    {
        low01Fps = fps;
    }
}
else
{
    low01Fps = 0.0;
}
```

---

## 7. Verification Method & Commands

### Command Matrix

| Target Verification | Exact Command | Success Criteria |
|:---|:---|:---|
| **Full Solution Build** | `dotnet build NitroLite.sln -c Release` | Exit Code `0`, `0 Warning(s)`, `0 Error(s)` |
| **All Existing 320 Tests** | `dotnet test NitroLite.sln -c Release` | `Passed! - Failed: 0, Passed: 320, Skipped: 0` |
| **Specific FPS Tests** | `dotnet test NitroLite.sln --filter "FullyQualifiedName~Fps"` | All existing 31 `FpsTelemetryTests` pass |
| **Specific RTSS Tests** | `dotnet test NitroLite.sln --filter "FullyQualifiedName~Rtss"` | All existing 27 `AdversarialRtssChallengeTests` pass |
| **New 0.1% Low Suite** | `dotnet test NitroLite.sln --filter "FullyQualifiedName~Low01"` | All newly introduced calculation & filter tests pass |
| **New CS2 Fallback Suite** | `dotnet test NitroLite.sln --filter "FullyQualifiedName~Cs2"` | All newly introduced fallback & ETW mock tests pass |

---
*Report certified by Teamwork Explorer (`teamwork_explorer`). No source code files were modified during this investigation.*
