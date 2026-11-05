# Handoff Report: Requirements R3 (Game & OSD Telemetry) and R4 (MUX Switch Protection & Unit Tests)

**From**: `explorer_osd_survey_1`  
**To**: Orchestrator / Implementer  
**Date**: November 4, 2026  
**Artifacts**:
- Analysis: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_osd_survey_1\analysis.md`
- Working Directory: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_osd_survey_1\`

---

## 1. Observation

1. **OSD Window Implementation (`NitroLite/OsdOverlayWindow.xaml` and `.cs`)**:
   - `OsdOverlayWindow.xaml`: Monospace layout (`Consolas, Cascadia Code`), dark semi-transparent root border (`#F20F1115`), drop shadow (`BlurRadius="6"`), three data rows: CPU (Temp, Power, Freq, Usage), GPU (Temp, Power, Freq, Vram), System (Mode, RAM, Fan).
   - `OsdOverlayWindow.xaml.cs`: Uses 64-bit safe `GetWindowLongPtr` and `SetWindowLongPtr` to apply `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`. Four corner positions are calculated against `SystemParameters.WorkArea` with a 20.0px margin.
   - `MainWindow.xaml.cs`: Lines 575–578 poll `_osdWindow.UpdateData(data, _currentMode)` once per second in the main UI telemetry loop.
   - Currently, there is no FPS or frametime display row in `OsdOverlayWindow.xaml`.

2. **Game Detection Subsystem (`NitroLite/Core/GameDetectionService.cs`)**:
   - Maintains game profiles (`game_profiles.json`) with auto-discovery across Steam, Riot, and Epic libraries.
   - Periodic timer (2.0s interval) scans running processes, tracking `ActiveGameProfile` and firing `GameStarted` / `GameEnded`.

3. **RivaTuner Statistics Server (RTSS) Availability & Shared Memory Layout**:
   - Verified that RTSS is installed on this host at `C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe`.
   - Verified official C header at `C:\Program Files (x86)\RivaTuner Statistics Server\SDK\Include\RTSSSharedMemory.h`:
     - Named mapped file: `"RTSSSharedMemoryV2"`.
     - Signature: `dwSignature == 0x53535452` (`'RTSS'`).
     - Structure Version: `dwVersion >= 0x00020000`.
     - App entry offset at byte 12 (`dwAppArrOffset`), entry size at byte 8 (`dwAppEntrySize`), last foreground app index at byte 64 (`dwLastForegroundApp`), PID at byte 68 (`dwLastForegroundAppProcessID`).
     - Each application entry contains: `dwProcessID` (Offset 0), `szName` (Offset 4), `dwFrameTime` in microseconds (Offset 280), `dwStatFramerateAvg` (Offset 308).

4. **Hardware MUX Switch Topology & Protection Boundary (`Requirement R4`)**:
   - Inspection of host PnP devices confirms the Acer Nitro 16 internal panel (`AU Optronics AUOCDAB`) and external monitor (`Lenovo R27q-30`) are hardwired directly to the discrete NVIDIA RTX 4070 (`PCI\VEN_10DE&DEV_2860...`) in dedicated GPU-only mode.
   - `NitroLite/MainWindow.xaml` line 1286 explicitly states:
     ```xaml
     <TextBlock Text="MUX Anahtarı: Otomatik / Donanım Korumalı" Foreground="{StaticResource TextMuted}" FontSize="11" HorizontalAlignment="Right" VerticalAlignment="Center" />
     ```
   - `AcerAgentClient.cs` sends commands over TCP port 46933 only for `OPERATING_MODE`, `FAN_CONTROL`, `LIGHTING`, and `BACKLIGHT_30S`. No MUX or display switching methods exist in `NitroLite`.
   - Peer agent `explorer_power_survey_1` verified display refresh rate switching using Win32 `ChangeDisplaySettingsExA` (`CDS_TEST`) for 60Hz and 165Hz returning `DISP_CHANGE_SUCCESSFUL (0)` without touching display adapters.

5. **Existing Test Suite (`NitroLite.Tests`)**:
   - Executed command: `dotnet test NitroLite.sln`.
   - Output: `Passed! - Failed: 0, Passed: 201, Skipped: 0, Total: 201, Duration: 2 s - NitroLite.Tests.dll (net10.0)`.

---

## 2. Logic Chain

1. **Step 1 (Telemetry Architecture Selection)**:
   - Observation 3 shows that RTSS is installed and provides `RTSSSharedMemoryV2` containing swapchain-exact frametime in microseconds (`dwFrameTime`) and running average framerate (`dwStatFramerateAvg`), tracking the active foreground 3D game.
   - Alternative options (PresentMon/ETW) require elevated Administrator privileges and induce 1–2.5% CPU overhead, while PDH performance counters cannot track FPS.
   - Therefore, the RTSS shared memory bridge is the optimal, zero-overhead, anti-cheat safe primary telemetry source for Requirement R3.

2. **Step 2 (Resilient Fallback Design)**:
   - When RTSS is not running or no 3D game is active, attempting to open `RTSSSharedMemoryV2` throws `FileNotFoundException`.
   - By catching this exception and consulting `GameDetectionService`, `NitroLite` can gracefully display `-- FPS` or `-- FPS | RTSS Kapalı` without crashing or freezing the UI.

3. **Step 3 (OSD Layout Integration)**:
   - Observation 1 shows that `OsdOverlayWindow.xaml` uses a clean 3-row monospace `StackPanel`.
   - Adding a 4th row (`PnlOsdGame`) containing `TxtOsdFps` (formatted via `TelemetryFormatter.FormatFps`), `TxtOsdFrametime` (via `TelemetryFormatter.FormatFrametime`), and `TxtOsdGameName` seamlessly extends the OSD with identical styling, high contrast colors, and zero text shifting.

4. **Step 4 (MUX Switch Isolation Boundary)**:
   - Observations 4 show that the host laptop is hardwired in dGPU mode and the UI already defines MUX as "Donanım Korumalı".
   - Refresh rate switching for Requirement R1 (60Hz on DC, 165Hz on AC) operates cleanly at the User32 `DEVMODE` layer (`ChangeDisplaySettingsEx`).
   - Therefore, establishing an explicit exclusion zone prohibiting any WMI/nvlddmkm/DDS display routing calls completely protects the MUX hardware state with zero regression.

5. **Step 5 (Unit Test Preservation & Expansion)**:
   - Observation 5 confirms all 201 baseline unit tests pass in 2 seconds.
   - By structuring the new features into testable static methods (`TelemetryFormatter.FormatFps`, `TelemetryFormatter.FormatFrametime`) and modular parser classes (`RtssSharedMemoryParser`), we can add unit test fixtures in `NitroLite.Tests` covering valid synthetic buffers, corrupt headers, zero-division edge cases, and power automation transitions while keeping all 201 existing tests green.

---

## 3. Caveats

1. **RTSS Runtime Requirement**:
   - While RTSS is installed on the target machine, it is not currently set as an automatic startup task. If the user launches a game without RTSS running, FPS and frametime will show `-- FPS` until RTSS is started. The application must handle this gracefully and not crash or spam error logs.
2. **Exclusive Fullscreen Overlays**:
   - WPF topmost windows render over borderless fullscreen and modern flip-model DirectX games (which Windows 11 defaults to). In legacy exclusive fullscreen (FSE), standard WPF windows may be hidden behind the exclusive surface unless the user runs borderless or RTSS's built-in OSD slot is written to. Writing to the RTSS text slot (`szOSD`) can be optionally leveraged if exclusive fullscreen rendering is desired.
3. **No Caveats on MUX Protection**:
   - The boundary is completely clear and unambiguous: zero calls to display routing APIs.

---

## 4. Conclusion

1. **R3 Implementation Recommendation**:
   - Implement `FpsMonitorService.cs` in `NitroLite.Core` that reads `RTSSSharedMemoryV2` using forward-compatible dynamic offset calculations (`dwAppArrOffset + index * dwAppEntrySize`).
   - Add `PnlOsdGame` to `OsdOverlayWindow.xaml` displaying FPS, frametime, and active game title, with formatters in `TelemetryFormatter.cs`.
   - Provide graceful fallback displaying `-- FPS` when RTSS is not running or on desktop.
2. **R4 Hardware Protection**:
   - Maintain strict exclusion: No code must touch MUX switch, NVIDIA DDS, or display topology.
   - Requirement R1 display frequency changes (60Hz / 165Hz) must strictly use User32 `ChangeDisplaySettingsEx` on the active display.
3. **Test Fixtures**:
   - Add new test classes `FpsTelemetryTests.cs`, `PowerAutomationTests.cs`, and `BatteryTelemetryTests.cs` to `NitroLite.Tests`.
   - Maintain 100% pass rate on all 201 existing tests.

---

## 5. Verification Method

1. **Test Suite Verification**:
   - Command: `dotnet test NitroLite.sln`
   - Expected Output: All existing 201 tests pass + new R1/R2/R3 tests pass with 0 failures.
2. **Build Verification**:
   - Command: `dotnet build NitroLite.sln`
   - Expected Output: Build succeeds with 0 errors and 0 warnings.
3. **Files to Inspect**:
   - `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_osd_survey_1\analysis.md`
   - `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\OsdOverlayWindow.xaml`
   - `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\OsdOverlayWindow.xaml.cs`
   - `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml` (Line 1286)
4. **Invalidation Conditions**:
   - If any new code attempts to call `root\WMI` display routing classes or modifies `nvlddmkm` registry keys.
   - If `dotnet test NitroLite.sln` fails on any of the baseline 201 tests.
