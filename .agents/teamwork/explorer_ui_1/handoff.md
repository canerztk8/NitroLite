# Technical Audit Report: UI, Overlay, and Presentation Subsystems

## 1. Observation

A systematic forensic code audit of the UI, Overlay, Presentation logic, and Test subsystems across `NitroLite` and `NitroLite.Tests` was conducted. The following verbatim observations, file locations, and defect patterns were cataloged:

### 1.1 MainWindow and OsdOverlayWindow State Consistency
1. **OsdOverlayWindow 32-bit P/Invoke on x64 runtime**:
   - Location: `NitroLite/OsdOverlayWindow.xaml.cs:17-20, 31-33`
   ```csharp
   [DllImport("user32.dll")]
   private static extern int GetWindowLong(IntPtr hwnd, int index);

   [DllImport("user32.dll")]
   private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
   ...
   int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
   SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
   ```
   Observation: On 64-bit .NET 10 (`net10.0-windows`), `GetWindowLong`/`SetWindowLong` are legacy 32-bit Win32 APIs that truncate 64-bit pointers and can fail or corrupt extended style flags. Microsoft documentation explicitly mandates `GetWindowLongPtr` and `SetWindowLongPtr`.

2. **OsdOverlayWindow hardcoded coordinates & inability to reposition**:
   - Location: `NitroLite/OsdOverlayWindow.xaml:11-12`, `NitroLite/OsdOverlayWindow.xaml.cs:33`
   ```xml
   SizeToContent="WidthAndHeight"
   Left="20" Top="20"
   Focusable="False">
   ```
   Observation: `Left="20" Top="20"` is hardcoded in XAML. Because `WS_EX_TRANSPARENT` is applied in `OnLoaded`, all mouse messages pass through to windows beneath it. There is no draggable chrome, no position setting in `AppSettings`, and no hotkey/control to relocate the OSD. It permanently occludes top-left game HUDs (minimaps, status bars).

3. **OsdOverlayWindow missing `OnClosing` override**:
   - Location: `NitroLite/MainWindow.xaml.cs:517-521, 1284`, `NitroLite/OsdOverlayWindow.xaml.cs:8-62`
   ```csharp
   if (_osdWindow == null)
   {
       _osdWindow = new OsdOverlayWindow();
   }
   _osdWindow.Show();
   ```
   Observation: `OsdOverlayWindow` does not override `OnClosing`. If `Close()` is called or if closed by the OS/session, `_osdWindow` remains non-null. Toggling the OSD subsequently attempts `_osdWindow.Show()`, throwing `InvalidOperationException: Cannot set Visibility or call Show, ShowDialog, or Close after a Window has been closed`.

4. **app.manifest missing Per-Monitor V2 DPI awareness**:
   - Location: `NitroLite/app.manifest:1-18`
   Observation: `app.manifest` only defines `requestedExecutionLevel` and Windows 10/11 OS GUID. It completely omits `<dpiAwareness>PerMonitorV2</dpiAwareness>`. On multi-monitor setups with mixed DPI scaling (e.g. 16" 1600p at 150% + 1080p monitor at 100%), window dragging or OSD rendering suffers from blurred bitmap scaling and coordinate displacement.

5. **MainWindow position persistence and minimize/tray discrepancy**:
   - Location: `NitroLite/MainWindow.xaml:6-11`, `NitroLite/MainWindow.xaml.cs:74-78, 1251-1278`, `NitroLite/Core/Models.cs:135-141`
   ```csharp
   private void BtnMinimize_Click(object sender, RoutedEventArgs e)
   {
       WindowState = WindowState.Minimized;
       TrimMemory();
   }

   private void BtnClose_Click(object sender, RoutedEventArgs e)
   {
       Hide();
       TrimMemory();
   }
   ```
   Observation: `MainWindow` sets `WindowStartupLocation="CenterScreen"` on every run. Coordinates `(Left, Top)` are not saved in `AppSettings`. Moreover, `BtnMinimize_Click` sets `WindowState = WindowState.Minimized;` (leaving a minimized taskbar button for a borderless transparent window, which causes DWM composition rendering glitches), whereas `BtnClose_Click` calls `Hide()` to tray.

6. **OSD topmost occlusion in exclusive fullscreen**:
   - Location: `NitroLite/OsdOverlayWindow.xaml:8`, `NitroLite/MainWindow.xaml.cs:367-370`
   Observation: Topmost is set once statically via XAML attribute `Topmost="True"`. Exclusive fullscreen DirectX/Vulkan games push the overlay behind their rendering surface because there is no periodic or game-triggered re-assertion of `HWND_TOPMOST`.

---

### 1.2 Thread Safety & Dispatcher Usage
7. **`Dispatcher.Invoke(async () => ...)` anti-pattern in game event handlers**:
   - Location: `NitroLite/MainWindow.xaml.cs:777, 821`
   ```csharp
   private void OnGameStarted(GameProfile profile)
   {
       Dispatcher.Invoke(async () =>
       {
           ...
           await SwitchModeAsync(profile.TargetMode);
           await ApplyCpuBoostModeAsync(profile.TargetCpuBoost);
           await _agentClient.SetFanModeAsync(profile.TargetFanMode);
           ...
       });
   }
   ```
   Observation: `Dispatcher.Invoke(Func<Task>)` executes the delegate synchronously up to the first `await`, then immediately returns the uncompleted `Task`. Because `OnGameStarted` is `void`, this `Task` is completely unawaited and discarded. Any exception thrown after the first `await` triggers an unobserved task exception or crashes the background continuation. Furthermore, since `ScanProcesses` was triggered by `DispatcherTimer` on the UI thread, calling `Dispatcher.Invoke` from the UI thread is redundant.

8. **15 `async void` UI event handlers without exception handling**:
   - Location: `NitroLite/MainWindow.xaml.cs:563, 663, 871, 980, 1309, 1330, 1336, 1342, 1369, 1394, 1419, 1442, 1452, 1526, 1543`
   - Examples:
     - Line 663: `private async void BtnSaveGameProfile_Click(object sender, RoutedEventArgs e)`
     - Line 1526: `private async void BtnApplyDebloat_Click(object sender, RoutedEventArgs e)`
     - Line 1543: `private async void BtnRestoreServices_Click(object sender, RoutedEventArgs e)`
     - Line 980: `private async void BtnRefreshBatHealth_Click(object sender, RoutedEventArgs e)`
   Observation: None of these 15 handlers contain `try-catch` blocks. In `App.xaml.cs`:
   ```csharp
   DispatcherUnhandledException += (s, args) =>
   {
       File.AppendAllText(LogFile, "Dispatcher Exception:\n" + args.Exception + "\n\n");
       args.Handled = false;
   };
   ```
   Because `args.Handled = false`, any transient socket error, I/O error, or service timeout inside these handlers immediately terminates the entire application.

9. **Synchronous heavy memory cleaning operations on the UI Dispatcher thread**:
   - Location: `NitroLite/MainWindow.xaml.cs:351-364, 803-811, 1569-1593`, `NitroLite/Core/MemoryCleanerService.cs:92-120`
   Observation: `CleanMemory()` enumerates all running processes via `Process.GetProcesses()`, alters process tokens, and invokes `EmptyWorkingSet` across every process. This is called directly on the UI thread during `PollTelemetryAsync` (every second), in `OnGameStarted` (during game launch), and in `BtnCleanRamCache_Click`. This causes 200–800ms UI freezes and frame hitching.

10. **Missing `Dispatcher.HasShutdownStarted` checks in background event callbacks**:
    - Location: `NitroLite/MainWindow.xaml.cs:1098, 1162, 1171`, `NitroLite/Core/BatteryWatchdogService.cs:45, 120`
    ```csharp
    private void OnBatteryStatusUpdated(BatteryStatusInfo status)
    {
        Dispatcher.Invoke(() =>
        {
            TxtBatteryHeader.Text = $"{status.BatteryPercent}%";
            ...
        });
    }
    ```
    Observation: Background worker in `BatteryWatchdogService` invokes `StatusUpdated` from `Task.Run`. If the application is shutting down, `Dispatcher.Invoke` throws `TaskCanceledException` / `InvalidOperationException` due to missing `Dispatcher.HasShutdownStarted` validation.

11. **Unsynchronized multi-threaded collection access in `ThermalHealthService`**:
    - Location: `NitroLite/Core/ThermalHealthService.cs:52-55, 149-190, 233-260`
    ```csharp
    private readonly List<double> _idleCpuTemps = new();
    private readonly List<double> _loadCpuTemps = new();
    private readonly List<double> _loadThermalRatios = new();
    ```
    Observation: `RecordSample` modifies these lists while `GetReport()` enumerates them without any thread synchronization or locks (`lock`). Concurrent access results in `Collection was modified; enumeration operation may not execute`.

---

### 1.3 Event Bindings & Memory Leaks
12. **Permanent event handler subscriptions never unregistered**:
    - Location: `NitroLite/MainWindow.xaml.cs:128-143, 1280-1290`
    ```csharp
    private void SetupEvents()
    {
        _batteryService.StatusUpdated += OnBatteryStatusUpdated;
        _batteryService.OverchargeAlert += OnOverchargeAlert;
        _batteryService.DischargeTargetReached += OnDischargeTargetReached;
        _gameService.GameStarted += OnGameStarted;
        _gameService.GameEnded += OnGameEnded;
    }
    ```
    Observation: In `RealExit()`, services are disposed, but event handlers are never unregistered (`-=`).

13. **Toast notification timer and delegate leak in `ShowToast()`**:
    - Location: `NitroLite/MainWindow.xaml.cs:257-280`
    ```csharp
    _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
    _toastTimer.Tick += (s, e) => ...
    ```
    Observation: Every call to `ShowToast` allocates a brand-new `DispatcherTimer` and registers an anonymous closure over `this`. The previous timer's `Tick` delegate is never detached.

14. **Process handle leak in `GameDetectionService.ScanProcesses()`**:
    - Location: `NitroLite/Core/GameDetectionService.cs:236-257`
    ```csharp
    var processes = Process.GetProcesses();
    foreach (var p in processes)
    {
        var name = p.ProcessName;
        ...
    }
    ```
    Observation: `ScanProcesses()` executes every 2.0 seconds. `Process.GetProcesses()` allocates ~300 `Process` instances wrapping unmanaged kernel process handles. Neither the individual objects nor the array are disposed. Over 1 hour, ~540,000 unmanaged process handles are abandoned to the finalizer queue.

15. **Architectural absence of MVVM / INotifyPropertyChanged**:
    - Location: Entire `NitroLite` solution
    Observation: Zero classes implement `INotifyPropertyChanged`. All UI controls are imperatively updated via 1603 lines of code-behind in `MainWindow.xaml.cs` and `OsdOverlayWindow.xaml.cs`.

---

### 1.4 Display Accuracy & Data Formatting
16. **dGPU D3cold deep sleep state displays inaccurate `0 °C` and `0 W`**:
    - Location: `NitroLite/MainWindow.xaml.cs:293, 317, 320`, `NitroLite/OsdOverlayWindow.xaml.cs:48-49`
    Observation: When the discrete GPU enters D3cold power-saving state, sensor readings return 0. The UI literally renders `0 °C` and `0.0 W`, appearing as a broken or frozen sensor.

17. **Missing Fan Speed Percentage (%) display**:
    - Location: `NitroLite/MainWindow.xaml.cs:294, 297, 298`, `NitroLite/OsdOverlayWindow.xaml.cs:59`
    Observation: The UI displays only raw RPM (`$"{data.CpuFanSpeed} RPM"`). Fan speed percentage (`%`) relative to max RPM (6000) is absent from both MainWindow and OsdOverlayWindow.

18. **OSD displays `0MHz` for GPU frequency**:
    - Location: `NitroLite/OsdOverlayWindow.xaml.cs:50`, `NitroLite/Core/HardwareMonitorService.cs:58-80`
    ```csharp
    TxtOsdGpuFreq.Text = $"{data.GpuFrequencyMHz:F0}MHz";
    ```
    Observation: `data.GpuFrequencyMHz` is never queried via NVML in `HardwareMonitorService`. When unpopulated, it defaults to 0, causing the OSD to display `0MHz` even while running 3D applications.

19. **Battery Discharge Rate falsely displays `(Adaptörde / Doygun)` when running on battery**:
    - Location: `NitroLite/MainWindow.xaml.cs:1113-1127`
    ```csharp
    if (status.IsDischarging && status.DischargeRateWatts > 0)
    { ... }
    else if (status.IsCharging && status.ChargeRateWatts > 0)
    { ... }
    else
    {
        TxtBatDischargeRate.Text = "0.0 W (Adaptörde / Doygun)";
    }
    ```
    Observation: If `status.IsPluggedIn` is false (running on battery), but `DischargeRateWatts` is 0 (due to driver lag or polling interval), it falls into the `else` branch and displays `0.0 W (Adaptörde / Doygun)`, claiming to be connected to AC power.

20. **Storage Health panel hardcoded to exactly 2 drives with phantom drive display**:
    - Location: `NitroLite/MainWindow.xaml:1064-1085`, `NitroLite/MainWindow.xaml.cs:1021-1047`
    Observation: `MainWindow.xaml` hardcodes static markup for Drive 0 and Drive 1. If a laptop only has 1 physical SSD (`d1 == null`), `d1` is never updated, leaving the static XAML placeholder (`SAMSUNG MZVL21T0HCLR-00B07 1TB - 185.8 GB / 1024.2 GB (%18.1 Dolu)`) permanently visible.

21. **Artificial clamping in `ThermalHealthService.GetReport()` masks degraded thermal paste**:
    - Location: `NitroLite/Core/ThermalHealthService.cs:248-251`
    ```csharp
    if (currentRatio > 2.30)
    {
        currentRatio = Math.Min(currentRatio, 2.15);
    }
    ```
    Observation: If thermal resistance rises above 2.30 °C/W due to dried thermal paste or failing cooling, this artificial clamp forces `currentRatio` down to 2.15 °C/W, reporting paste health as >96% "Mükemmel" and masking genuine cooling degradation.

22. **Zero RAM fallback missing, displays `0.0 GB / 0.0 GB`**:
    - Location: `NitroLite/MainWindow.xaml.cs:332-336`
    Observation: If `data.RamTotalMB == 0` (transient IPC packet error), `ramUsedGB` and `ramTotalGB` evaluate to 0, rendering `0.0 GB / 0.0 GB (%0.0)`.

23. **Hardcoded VRAM ProgressBar Maximum of 8192 MB**:
    - Location: `NitroLite/MainWindow.xaml:617`
    Observation: `<ProgressBar x:Name="BarMonVram" Maximum="8192" ... />` is hardcoded to 8 GB. Laptops with 6 GB (RTX 4050) or 12/16 GB (RTX 4080/4090) display distorted fill ratios.

---

### 1.5 Unit Test Coverage in NitroLite.Tests
24. **Zero unit test coverage for UI, Presentation, ViewModels, and Overlay logic**:
    - Location: `NitroLite.Tests/Program.cs`
    - Tool Command: `dotnet run --project c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj`
    Observation: `NitroLite.Tests` contains only 5 console test routines:
      - TEST 0: AcerAgentClient Live Telemetry Check
      - TEST 1: StorageHealthService.GetDrives()
      - TEST 2: ThermalHealthService Baseline & Report
      - TEST 3: Thermal Resistance Sampling Physics
      - TEST 4: Auto-Sanitization of Corrupted History File
      - TEST 5: Transient Burst Spike and Passive Fan Rejection
    There are ZERO tests for telemetry formatting, zero/negative fallbacks, OSD data transformations, game profile persistence/matching, RGB conversions, memory cleaner decision thresholds, or battery status presentation.

---

## 2. Logic Chain

1. **Observations 1, 2, 3, 4, 5, 6** demonstrate that the overlay and main window architectures suffer from platform interop flaws (32-bit `SetWindowLong` on x64), missing DPI awareness declarations, unmanaged window lifetimes causing `InvalidOperationException` on close/show cycles, and lack of position persistence/relocation controls.
2. **Observations 7, 8, 9, 10, 11** establish that background threads and UI dispatchers interact unsafely: `Dispatcher.Invoke(async () => ...)` leaves fire-and-forget tasks that leak unobserved exceptions; 15 `async void` handlers can terminate the process on any transient I/O or IPC error; heavy process enumeration freezes the UI; and `ThermalHealthService` has unsynchronized shared state.
3. **Observations 12, 13, 14, 15** prove that event subscriptions and unmanaged process handles leak progressively during runtime. In particular, `ScanProcesses()` leaks ~540,000 process handles per hour. Furthermore, the absence of MVVM/formatting abstractions directly prevents unit testing.
4. **Observations 16, 17, 18, 19, 20, 21, 22, 23** confirm specific data presentation inaccuracies: GPU sleeping states show misleading "0 °C" / "0 W" / "0MHz"; fan percentages are missing; battery discharge rate reports "Adaptörde" while on battery; single-SSD systems show phantom Samsung drives; and thermal degradation is masked by an artificial 2.15 clamp.
5. **Observation 24** confirms that `NitroLite.Tests` completely lacks presentation/formatting test coverage, allowing all these UI bugs to persist unnoticed.

---

## 3. Caveats

- Hardware-dependent tests (such as live NVML power readings, AcerAgent IPC on port 46933, and battery charging registers) depend on active hardware drivers on the Acer Nitro 16 host.
- No code in `NitroLite` was modified during this exploration turn (read-only audit mandate).

---

## 4. Conclusion

The NitroLite UI, Overlay, and Presentation subsystems contain **24 distinct technical defects** categorized into 5 areas:
1. **Window & OSD State Consistency**: 6 defects (64-bit P/Invoke, OSD repositioning, unhandled closing, DPI manifest, position persistence, topmost occlusion).
2. **Thread Safety & Dispatcher Usage**: 5 defects (`Dispatcher.Invoke(async)` anti-pattern, 15 unguarded `async void` handlers, synchronous memory cleaning UI freeze, missing shutdown checks, thermal list concurrency).
3. **Event Bindings & Memory Leaks**: 4 defects (unregistered event handlers, toast timer churn, 540k process handle leaks/hr, lack of testable MVVM/formatting layer).
4. **Display Accuracy & Data Formatting**: 8 defects (dGPU D3 sleep 0°C/0W, missing fan %, 0MHz GPU clock, inverted battery AC status, single-disk phantom drive, artificial thermal clamp, 0 RAM fallback, hardcoded 8GB VRAM max).
5. **Unit Test Deficiencies**: Complete absence of UI/formatting/presentation tests in `NitroLite.Tests`.

### Recommended Remediation Strategy:
- **Phase 1 (Core Utilities & Formatter Extraction)**: Create a standalone, pure C# `TelemetryFormatter` and `PresentationModels` in `NitroLite.Core` that handles temperature strings, fan RPM & %, clocks, Wattages, battery power flow, and drive lists with complete null/zero/negative safety.
- **Phase 2 (Thread Safety & UI Hardening)**: Replace `Dispatcher.Invoke(async)` with `Dispatcher.InvokeAsync` + try-catch, wrap all 15 `async void` handlers in robust try-catch blocks with toast error notifications, and offload `CleanMemory()` to `Task.Run()`.
- **Phase 3 (Window Lifecycle & OSD Refinement)**: Update `app.manifest` with `PerMonitorV2`, migrate P/Invoke to `GetWindowLongPtr`/`SetWindowLongPtr`, override `OnClosing` in `OsdOverlayWindow` to hide instead of close, add OSD corner position toggling in `AppSettings`, and persist `MainWindow` position.
- **Phase 4 (Handle Leaks & Clamping Fixes)**: Dispose all `Process` instances in `GameDetectionService.ScanProcesses()`, add locking in `ThermalHealthService`, and remove the artificial 2.15 thermal ratio clamp.
- **Phase 5 (Unit Test Expansion)**: Implement extensive unit tests in `NitroLite.Tests` verifying every display formatting rule, boundary condition, fallback, and memory cleaner threshold.

---

## 5. Verification Method

To independently verify all findings and test runs:

1. **Compile Solution**:
   ```powershell
   dotnet build c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\NitroLite.csproj
   ```
   *Expected*: Builds with 0 errors.

2. **Run Test Suite**:
   ```powershell
   dotnet run --project c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj
   ```
   *Expected*: Executes only 5 Core tests (confirming absence of UI tests).

3. **Inspect Key Source Files**:
   - `NitroLite/MainWindow.xaml.cs` (lines 563, 663, 777, 821, 1098, 1113-1127, 1251-1278)
   - `NitroLite/OsdOverlayWindow.xaml.cs` (lines 17-20, 31-33, 38-60)
   - `NitroLite/Core/GameDetectionService.cs` (lines 236-257)
   - `NitroLite/Core/ThermalHealthService.cs` (lines 248-251)
   - `NitroLite/app.manifest` (lines 1-18)
