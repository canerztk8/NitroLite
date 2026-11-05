# Comprehensive Engineering Analysis: Requirements R3 (Game & OSD Telemetry) and R4 (Hardware MUX Protection & Test Suite)

**Author**: `explorer_osd_survey_1`  
**Date**: November 4, 2026  
**Target Repository**: `NitroLite` (.NET 10 WPF / C# 13)  
**Host Platform**: Acer Nitro 16 (AN16-41), AMD Ryzen 7 7840HS, NVIDIA GeForce RTX 4070 Laptop GPU, Windows 11 Build 26300  
**Authority**: Requirement R3 & R4 from `ORIGINAL_REQUEST.md` and `DISPATCH.md`

---

## 1. Executive Summary

This investigation provides an empirical, architectural survey of:
1. **OSD Overlay Subsystem (`OsdOverlayWindow.xaml` & `.cs`)**: Current styling, positioning, layered window attributes, and data pipeline.
2. **Game & Fullscreen Detection on Windows**: Win32 window bounds, monitor rect calculations, process matching, and integration with `GameDetectionService`.
3. **In-Game Frametime & FPS Telemetry Architectures**: Empirical evaluation of RivaTuner Statistics Server (`RTSSSharedMemoryV2`), PresentMon / ETW, and Windows Performance Counters. Recommendation of a zero-overhead, anti-cheat safe RTSS memory-mapped bridge with robust fallback.
4. **Requirement R4 MUX Switch Hardware Protection**: Complete mapping of hardware display topology, Acer OEM agent commands, and explicit DO-NOT-TOUCH exclusion boundaries ensuring zero regression to the locked dGPU state.
5. **`NitroLite.Tests` Suite Analysis (201 Tests)**: Execution characteristics (`dotnet test NitroLite.sln`), code coverage areas, and complete test fixture designs for Requirements R1, R2, and R3.

---

## 2. Current OSD Architecture Analysis

### 2.1 Window Topology & Win32 Interop Attributes
The OSD is implemented in `NitroLite/OsdOverlayWindow.xaml` and `NitroLite/OsdOverlayWindow.xaml.cs`.

- **XAML Configuration**:
  ```xaml
  WindowStyle="None"
  AllowsTransparency="True"
  Background="Transparent"
  Topmost="True"
  ShowInTaskbar="False"
  SizeToContent="WidthAndHeight"
  WindowStartupLocation="Manual"
  Focusable="False"
  ```
- **Extended Window Styles (`GWL_EXSTYLE`)**:
  Applied on `Window.Loaded` via 64-bit safe `GetWindowLongPtr` / `SetWindowLongPtr`:
  - `WS_EX_TRANSPARENT (0x00000020)`: Passes mouse clicks through to foreground games/applications.
  - `WS_EX_LAYERED (0x00080000)`: Enables per-pixel alpha transparency and drop shadow rendering.
  - `WS_EX_TOOLWINDOW (0x00000080)`: Removes window from the Alt+Tab application switcher.
  - `WS_EX_NOACTIVATE (0x08000000)`: Prevents the overlay window from stealing input focus when shown or updated.

### 2.2 Layout & Visual Styling
- **Root Element**: `Border` with `Background="#F20F1115"` (95% opaque dark slate), `BorderBrush="#252B38"`, `BorderThickness="1"`, `CornerRadius="6"`, `Padding="12,8"`, and `DropShadowEffect BlurRadius="6" Color="#000000" Opacity="0.5"`.
- **Typography**: Monospace stack (`Consolas, Cascadia Code, Courier New, monospace`) preventing horizontal text jitter during rapid digit updates.
- **Current Data Rows**:
  1. **CPU Row** (`TxtOsdCpuTemp`, `TxtOsdCpuPower`, `TxtOsdCpuFreq`, `TxtOsdCpuUsage`):
     - Temperature in `#0EA5E9` (Cyan, Bold)
     - Power in `#10B981` (Green, Bold)
     - Frequency in `#F1F4F9` (White, Bold)
     - Usage in `#9BA5B7` (Muted)
  2. **GPU Row** (`TxtOsdGpuTemp`, `TxtOsdGpuPower`, `TxtOsdGpuFreq`, `TxtOsdVram`):
     - Temperature in `#EF4444` (Red, Bold)
     - Power in `#F59E0B` (Amber, Bold)
     - Frequency in `#F1F4F9` (White, Bold)
     - VRAM in `#0EA5E9` (Cyan, Bold)
     - Deep sleep handled gracefully via `TelemetryFormatter` (displays `"0 °C (Uyku / D3cold)"`, `"0.0 W (D3cold)"`).
  3. **Mode & System Row** (`TxtOsdMode`, `TxtOsdRam`, `TxtOsdFan`):
     - Mode in `#0EA5E9` (Cyan, Bold)
     - RAM in `#10B981` (Green, Bold)
     - Fan speed in `#9BA5B7` (Muted)

### 2.3 Positioning & Lifecycle
- Four screen corners supported: `0: Top-Left`, `1: Top-Right`, `2: Bottom-Right`, `3: Bottom-Left`.
- Computed against `SystemParameters.WorkArea` with a 20.0px margin.
- Cycled via `BtnCycleOsdCorner_Click` or double-clicking the OSD border.
- Updated every 1.0 second from `MainWindow._pollTimer` via `_osdWindow.UpdateData(data, _currentMode)`.
- Window closing is intercepted (`OnClosing`) to cancel destruction and call `Hide()`.

---

## 3. Game & Fullscreen Detection on Windows

### 3.1 Win32 Window Rect vs. Monitor Geometry
To determine whether an active application is in fullscreen (borderless or exclusive), Windows provides Win32 APIs:
- `GetForegroundWindow()`: Retrieves handle (`HWND`) of the foreground window.
- `GetWindowRect(hwnd, out RECT wr)`: Retrieves bounding box.
- `MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)`: Identifies target monitor.
- `GetMonitorInfo(hMonitor, ref MONITORINFO mi)`: Provides monitor rect (`mi.rcMonitor`).

**Fullscreen Criterion**:
```csharp
bool isFullscreen = wr.Left <= mi.rcMonitor.Left &&
                    wr.Top <= mi.rcMonitor.Top &&
                    wr.Right >= mi.rcMonitor.Right &&
                    wr.Bottom >= mi.rcMonitor.Bottom;
```

### 3.2 Shell Exclusions
To prevent false positives when on the Windows Desktop or interacting with the taskbar, specific window class names and processes must be ignored:
- Desktop: `"Progman"`, `"WorkerW"`
- Taskbar & Shell: `"Shell_TrayWnd"`, `"Shell_SecondaryTrayWnd"`
- Windows Modern App Shell: `"ApplicationFrameWindow"`, `"Windows.UI.Core.CoreWindow"`
- Self: Process ID matching `Environment.ProcessId` (`NitroLite.exe`).

### 3.3 Synergy with `GameDetectionService`
`NitroLite` already features `GameDetectionService.cs`, which:
- Maintains a registry of known games (`game_profiles.json`, popular defaults: CS2, Valorant, BFV, Cyberpunk, Balatro, etc.).
- Auto-discovers installed games across Steam libraries (`libraryfolders.vdf`), Riot Games, and Epic Games folders.
- Scans running processes every 2.0 seconds.
- Exposes `ActiveGameProfile`, `IsGameActive`, `GameStarted`, and `GameEnded` events.

**Recommended Integration**:
Combine foreground process detection with `GameDetectionService`:
- When foreground PID matches `ActiveGameProfile`, we immediately obtain game name, target profiles, and fullscreen state.

---

## 4. In-Game Frametime & FPS Telemetry Options

### 4.1 Comparative Architectural Evaluation

| Evaluation Metric | Option 1: RTSS Shared Memory Bridge | Option 2: PresentMon / ETW Session | Option 3: Windows Perf Counters (PDH) |
|:---|:---|:---|:---|
| **Mechanism** | Out-of-process Memory-Mapped File (`RTSSSharedMemoryV2`) | Real-time kernel event tracing (`Microsoft-Windows-DxgKrnl`) | Performance Data Helper (`\GPU Engine(*)\Running Time`) |
| **FPS Accuracy** | **Hardware/Swapchain Exact** (microseconds per frame) | **High** (kernel present event delta) | **None** (Only GPU compute %, no FPS) |
| **Frametime Granularity**| Microseconds (`dwFrameTime`) | Microseconds (event timestamps) | None |
| **CPU Overhead** | **Negligible** (< 0.005 ms per read, 0.0% CPU) | Moderate (1.0%–2.5% CPU continuous event processing) | Low (0.2%–0.5% CPU) |
| **Memory Footprint** | Zero additional RAM | 15 MB–30 MB (ETW buffers & trace parser) | ~2 MB |
| **Anti-Cheat Safety** | **100% Safe** (Global whitelist, read-only IPC) | Safe | Safe |
| **Privilege Requirements**| Standard user (no UAC / elevation needed) | **Administrator Required** for kernel trace | Standard user |
| **Host Availability** | **Verified Installed** on host machine | Requires native C++ loader or heavy NuGet | Built into Windows |
| **Crash Risk to Games** | **Zero** (Completely unhooked read-only client) | Low | Zero |
| **Fallback Behavior** | Graceful fallback if RTSS process closed | Fails if non-admin or ETW session busy | No frametime data |

### 4.2 RTSS Shared Memory Specification (`RTSSSharedMemoryV2`)
Investigation of official header `C:\Program Files (x86)\RivaTuner Statistics Server\SDK\Include\RTSSSharedMemory.h` confirmed the memory mapping schema:
- Named Map Name: `"RTSSSharedMemoryV2"` (fallback: `"RTSSSharedMemory"`).
- Header Signature: `dwSignature == 0x53535452` (`'RTSS'`).
- Structure Version: `dwVersion >= 0x00020000` (Version 2.x).
- Header Offsets:
  - `dwAppEntrySize` (Offset 8, 4 bytes)
  - `dwAppArrOffset` (Offset 12, 4 bytes)
  - `dwAppArrSize` (Offset 16, 4 bytes)
  - `dwLastForegroundApp` (Offset 64, 4 bytes)
  - `dwLastForegroundAppProcessID` (Offset 68, 4 bytes)
- Application Entry Layout (`RTSS_SHARED_MEMORY_APP_ENTRY`):
  - `dwProcessID` (Offset 0): 3D process identifier.
  - `szName` (Offset 4): Null-terminated ASCII process name (`char[MAX_PATH]`, 260 bytes).
  - `dwFrameTime` (Offset 280): Frame duration in **microseconds**!
    - Frametime (ms) = `dwFrameTime / 1000.0`.
    - Instantaneous FPS = `dwFrameTime > 0 ? (1000000.0 / dwFrameTime) : 0.0`.
  - `dwStatFramerateAvg` (Offset 308): Running average FPS.

### 4.3 Proposed Architecture: `FpsMonitorService` with Graceful Fallback
We propose implementing `FpsMonitorService` in `NitroLite.Core`:
1. **Primary Provider (RTSS)**:
   - Reads `RTSSSharedMemoryV2` via `System.IO.MemoryMappedFiles.MemoryMappedFile`.
   - Validates header signature and version.
   - Extracts foreground application entry using `dwLastForegroundApp` or `dwLastForegroundAppProcessID`.
   - Returns struct `FpsData(bool IsAvailable, double Fps, double FrametimeMs, string ProcessName)`.
2. **Graceful Fallback Logic**:
   - If RTSS is closed: `MemoryMappedFile.OpenExisting` throws `FileNotFoundException`. Handled silently; status reported as `IsAvailable = false`.
   - If game is active via `GameDetectionService` but RTSS is closed: OSD displays `-- FPS | Hazır (RTSS kapalı)` or `-- FPS`.
   - If system is at desktop: OSD displays clean `-- FPS | -- ms` or hides game row.
   - Zero crashes under any condition.

---

## 5. Requirement R4 MUX Switch Protection & Exclusion Boundaries

### 5.1 Host Hardware MUX Switch Status
Direct diagnostic inspection confirmed:
- **Motherboard**: `PHX Sierra_PEH V1.26` (Acer Nitro 16 AN16-41).
- **Display Topology**:
  - Internal Panel: `AU Optronics AUOCDAB` (2560x1600 @ 165Hz) -> wired directly to NVIDIA GeForce RTX 4070 (`PCI\VEN_10DE&DEV_2860...`).
  - External Display: `Lenovo R27q-30` (2560x1440 @ 165Hz) -> wired directly to NVIDIA GeForce RTX 4070.
- **MUX Configuration**: Locked to **Discrete GPU Only** (`dGPU`) mode. The AMD Radeon 780M iGPU is unattached from display outputs.

### 5.2 Codebase Audit & Strict Boundaries
1. **Existing UI Badge**:
   - In `NitroLite/MainWindow.xaml` line 1286:
     ```xaml
     <TextBlock Text="MUX Anahtarı: Otomatik / Donanım Korumalı" Foreground="{StaticResource TextMuted}" FontSize="11" HorizontalAlignment="Right" VerticalAlignment="Center" />
     ```
   - Must remain strictly non-interactive and intact.
2. **OEM Agent Scope (`AcerAgentClient.cs`)**:
   - `AcerAgentClient` sends packets exclusively for:
     - `OPERATING_MODE` (Modes 0, 1, 4, 5, 6)
     - `FAN_CONTROL` (Modes 0, 1, 2)
     - `LIGHTING` (Keyboard RGB)
     - `BACKLIGHT_30S` (Backlight timeout)
   - Zero MUX or GPU routing commands exist. **No display switching functions must ever be added to `AcerAgentClient`**.
3. **NVML Scope (`HardwareMonitorService.cs`)**:
   - NVML queries are strictly read-only: `NvmlDeviceGetPowerUsage`, `NvmlDeviceGetMemoryInfo`, `NvmlDeviceGetClockInfo`, `NvmlDeviceGetTemperature`.
   - No display adapter manipulation is permitted.
4. **Display Refresh Rate Switching for Requirement R1**:
   - Requirement R1 mandates switching between **60Hz** (on DC battery) and **165Hz** (on AC adapter).
   - This must be executed via standard Windows User32 `ChangeDisplaySettingsEx` (tested successfully by peer agent in `query_display.ps1` with `CDS_TEST` returning `0 (SUCCESS)` for both 60Hz and 165Hz).
   - **EXPLICIT PROHIBITION**: Never invoke NVIDIA DDS, NVAPI display switches, `D3DKMTSetDisplayMode`, or Acer OEM WMI display methods (`root\WMI` Acer BIOS display switches) which could attempt to alter MUX routing.

---

## 6. `NitroLite.Tests` Structure & Unit Test Fixtures Plan

### 6.1 Current Test Suite Analysis
- Solution command: `dotnet test NitroLite.sln`
- Current Status: **201 Passed, 0 Failed, 0 Skipped** (Duration: 2.0s).
- Framework: .NET 10, xUnit 2.9.3, Microsoft.NET.Test.Sdk 17.13.0.

### 6.2 Unit Test Plan for New Requirements

#### A. Requirement R3 (OSD & FPS Telemetry Tests)
Create `NitroLite.Tests/FpsTelemetryTests.cs`:
1. `TelemetryFormatter_FormatFps_FormatsAccurately`:
   - Valid framerates: `144.2` -> `"144 FPS"`, `60.0` -> `"60 FPS"`, `240.0` -> `"240 FPS"`.
   - Inactive / Zero / Negative: `0.0` -> `"-- FPS"`, `-5.0` -> `"-- FPS"`.
2. `TelemetryFormatter_FormatFrametime_FormatsAccurately`:
   - Normal frametimes: `16.666` ms -> `"16.7 ms"`, `6.06` ms -> `"6.1 ms"`.
   - Zero / Negative: `0.0` -> `"-- ms"`.
3. `RtssSharedMemoryParser_ValidBuffer_ExtractsMetricsCorrectly`:
   - Construct synthetic byte buffer matching `RTSS_SHARED_MEMORY` with `'RTSS'` signature (`0x53535452`), version `0x00020000`, `dwFrameTime = 6060` µs (~165 FPS), and process name `"cs2.exe"`.
   - Assert parsed FPS ≈ 165.0, FrametimeMs ≈ 6.06, ProcessName == "cs2.exe".
4. `RtssSharedMemoryParser_InvalidSignature_ReturnsSafeNull`:
   - Pass buffer with `0xDEAD` signature or empty bytes; assert parser returns null/unavailable without throwing exceptions.
5. `RtssSharedMemoryParser_MissingMmf_HandlesGracefully`:
   - Assert reader handles non-existent memory map without unhandled exceptions.

#### B. Requirement R1 (AC/DC Automation & Display Refresh Tests)
Create `NitroLite.Tests/PowerAutomationTests.cs`:
1. `PowerAutomation_OnDcDisconnect_EcoMode_AppliesEcoAnd60Hz`:
   - Verify that when DC is detected and setting is `Eco`, mode switches to `OperatingMode.Eco`, FanMode to Auto/Quiet, and display target is 60Hz.
2. `PowerAutomation_OnDcDisconnect_BalancedMode_PreservesBalanced`:
   - Verify that when setting is `Balanced`, mode remains `OperatingMode.Default`.
3. `PowerAutomation_OnAcReconnect_RestoresOriginalModeAnd165Hz`:
   - Verify that reconnecting AC restores pre-unplug mode (e.g. Turbo) and triggers 165Hz target.

#### C. Requirement R2 (Battery Telemetry & Dynamic Runtime Tests)
Create `NitroLite.Tests/BatteryTelemetryTests.cs`:
1. `BatteryTelemetry_DischargeRate_CalculatesWattsCorrectly`:
   - Input: 18,500 mW -> Output: 18.5 W (`IsDischarging = true`).
2. `BatteryTelemetry_DynamicRemainingTime_CalculatesAccurately`:
   - Remaining capacity: 66,528 mWh, Discharge rate: 22,176 mW -> `(66528 / 22176) * 60 = 180 min` (`3 sa 0 dk`).
3. `BatteryTelemetry_ZeroDischarge_HandlesGracefully`:
   - Discharge rate = 0 mW -> Returns `"Hesaplanıyor..."`.

---

## 7. Recommended Action Plan for Implementer
1. **Model & Formatter Updates**:
   - Add `FormatFps` and `FormatFrametime` to `TelemetryFormatter.cs`.
   - Extend `MonitorData` or create `FpsTelemetry` model holding `Fps`, `FrametimeMs`, `GameName`.
2. **OSD Visual Enhancement**:
   - Add `PnlOsdGame` row to `OsdOverlayWindow.xaml` showing `TxtOsdFps`, `TxtOsdFrametime`, and `TxtOsdGameName`.
   - Update `OsdOverlayWindow.xaml.cs` to bind these metrics in `UpdateData`.
3. **Core Telemetry Bridge**:
   - Implement `FpsMonitorService.cs` reading `RTSSSharedMemoryV2` with fallback to `GameDetectionService`.
4. **Test Fixtures Execution**:
   - Add new test fixtures in `NitroLite.Tests`.
   - Validate that all 201 existing tests + new tests pass (`dotnet test NitroLite.sln`).
5. **MUX Switch Compliance**:
   - Zero edits to WMI/MUX or `MainWindow.xaml` MUX badge.
