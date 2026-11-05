## 2026-11-04T01:42:28Z
You are worker_ui_1, an implementation worker subagent for the NitroLite project.
Your working directory is: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_ui_1\
Your parent conversation ID is: e6d296af-2648-48c0-9375-0e4e563c211b

MANDATORY FIRST STEPS:
1. Read the user requirements file: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
2. Read the scope file: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\orchestrator_2\SCOPE.md
3. Read the upstream handoff reports:
   - c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_ui_1\handoff.md
   - c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_core_1\handoff.md
4. Initialize your BRIEFING.md and progress.md in your working directory.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

YOUR ASSIGNMENT: Milestone 2B — UI, Overlay & Presentation Remediation
Files you own exclusively:
- `NitroLite/MainWindow.xaml`
- `NitroLite/MainWindow.xaml.cs`
- `NitroLite/OsdOverlayWindow.xaml`
- `NitroLite/OsdOverlayWindow.xaml.cs`
- `NitroLite/app.manifest`

IMPLEMENTATION TASKS:
1. `app.manifest`:
   - Add `<dpiAwareness>PerMonitorV2</dpiAwareness>` under `<windowsSettings>` to support crisp scaling on mixed-DPI multi-monitor configurations.
2. `OsdOverlayWindow.xaml.cs` & `OsdOverlayWindow.xaml`:
   - Replace legacy 32-bit `GetWindowLong`/`SetWindowLong` with 64-bit `GetWindowLongPtr`/`SetWindowLongPtr` (with `IntPtr` return/arguments).
   - Override `OnClosing` (`e.Cancel = true; Hide();`) so closing hides the window instead of permanently terminating it, preventing `InvalidOperationException` on subsequent `_osdWindow.Show()`.
   - Implement repositioning / corner switching support (e.g., cycling corners via double-click, hotkey, or settings, and persisting position).
   - Integrate `TelemetryFormatter`: format GPU frequency with sleep state handling (no raw 0MHz when dGPU asleep), format fan speeds with both RPM and calculated %, and format GPU power/temperature cleanly.
3. `MainWindow.xaml` & `MainWindow.xaml.cs`:
   - Storage Health Panel (Single-Drive Ghost Fix):
     Give Drive 1 card an `x:Name` (e.g. `CardDrive1` or `BorderDrive1`). In `UpdateStorageHealthUI(List<StorageDriveInfo> drives)`: if `d1 == null` (single-drive systems), set `CardDrive1.Visibility = Visibility.Collapsed;`. If 2+ drives exist, set `Visibility = Visibility.Visible` and populate it. Ensure Drive 0 and Drive 1 selection matches drives cleanly without duplicating on non-zero disk enumerations.
   - Thread Safety & Dispatcher:
     - In `OnGameStarted` and `OnGameEnded`, eliminate unawaited `Dispatcher.Invoke(async () => ...)` task anti-pattern; use safe async handling with try-catch.
     - Wrap all 15 `async void` UI event handlers (lines 563, 663, 871, 980, 1309, 1330, 1336, 1342, 1369, 1394, 1419, 1442, 1452, 1526, 1543, etc.) in robust `try-catch` blocks with toast error notifications, preventing transient socket/IO errors from crashing the app.
     - Call `_memoryCleaner.CleanMemoryAsync()` or offload working set reduction off the UI dispatcher thread so the UI never freezes during memory cleaning.
     - Add `Dispatcher.HasShutdownStarted` checks in background event callbacks (`OnBatteryStatusUpdated`, etc.).
   - Display Accuracy via `TelemetryFormatter`:
     - Use `TelemetryFormatter` methods across `PollTelemetryAsync` and presentation methods for CPU/GPU temperatures, fan speeds (RPM + %), Wattages, clocks, and RAM/VRAM formatting.
     - Fix battery discharge/charge rate text: do NOT show "(Adaptörde / Doygun)" while discharging on battery.
     - Dynamically update `BarMonVram.Maximum` from `data.VramTotalMB` instead of hardcoded 8192 MB.
     - Zero RAM fallback: ensure no `0.0 GB / 0.0 GB` display on transient nulls.
   - Event Cleanup & Window Persistence:
     - In `RealExit()`, cleanly detach all event handlers (`-=`) from services.
     - Fix `ShowToast` timer delegate accumulation.
     - Persist `MainWindow` Left/Top in `AppSettings` and restore cleanly.

VERIFICATION:
- Build `NitroLite.csproj` using `dotnet build c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\NitroLite.csproj`.
- Run tests: `dotnet run --project c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj`.
- Ensure 0 errors, 0 warnings, and 0 test failures.
- Write full details in `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_ui_1\handoff.md`.
- Notify orchestrator via `send_message`.


## 2026-11-04T01:52:37Z
**Context**: Milestone 2B — UI, Overlay & Presentation Remediation
**Content**: Status inquiry: Please report current progress on Tasks 1-5 and estimated time to completion.
**Action**: Reply with brief status update.
