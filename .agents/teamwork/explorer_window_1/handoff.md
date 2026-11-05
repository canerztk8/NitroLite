# Handoff Report: Requirement R2 (Window Launch Behavior Fix: Manual Launch vs Autostart)

## 1. Observation

1. **`NitroLite\App.xaml.cs` lines 99–119**:
   ```csharp
   bool startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
   if (!startMinimized && File.Exists(ConfigFile))
   {
       try
       {
           var cfg = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigFile));
           if (cfg?.StartMinimized == true)
           {
               startMinimized = true;
           }
       }
       catch { }
   }

   var win = new MainWindow(startMinimized);
   MainWindow = win;
   if (!startMinimized)
   {
       win.Show();
   }
   ```
2. **`NitroLite\bin\Release\config.json` lines 1–9**:
   ```json
   {
     "StartMinimized": true,
     "OsdEnabled": false,
     "SelectedChartMetric": "CPU_GHZ",
     "AutoRamCleanerEnabled": false,
     "WindowLeft": null,
     "WindowTop": null,
     "OsdCorner": 0
   }
   ```
3. **`NitroLite\MainWindow.xaml.cs` lines 74–78 & 1398–1406**:
   ```csharp
   if (startMinimized)
   {
       WindowState = WindowState.Minimized;
       Hide();
   }
   ...
   public void ShowWindow()
   {
       Show();
       WindowState = WindowState.Normal;
       Activate();
       Focus();
       Topmost = true;
       Topmost = false;
   }
   ```
4. **`NitroLite\MainWindow.xaml` lines 1173–1175**:
   ```xml
   <TextBlock Text="Sistem Tepsisinde (Simge Durumunda) Sessiz Başlat" Foreground="{StaticResource TextPrimary}" FontWeight="Bold" FontSize="13" />
   <TextBlock Text="PC açıldığında pencereler ekrana gelmez, doğrudan sağ alt sistem tepsisinde sessizce çalışır." Foreground="{StaticResource TextMuted}" FontSize="11" />
   ```
5. **`NitroLite\Core\DebloatService.cs` lines 140–156**:
   ```csharp
   public void SetNitroLiteAutoStart(bool enable, bool startMinimized = false)
   {
       try
       {
           using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
           if (key == null) return;

           if (enable)
           {
               var exePath = Process.GetCurrentProcess().MainModule?.FileName;
               if (!string.IsNullOrEmpty(exePath))
               {
                   var cmd = startMinimized ? $"\"{exePath}\" --minimized" : $"\"{exePath}\"";
                   key.SetValue("NitroLite", cmd);
               }
           }
   ```
6. **`NitroLite\App.xaml.cs` lines 23–39**:
   ```csharp
   _mutex = new Mutex(true, MutexName, out bool createdNew);
   if (!createdNew)
   {
       try
       {
           if (EventWaitHandle.TryOpenExisting(EventName, out var wakeEvent))
           {
               wakeEvent.Set();
           }
       }
       catch { }

       Environment.Exit(0);
       return;
   }
   ```
7. **Test Suite Baseline Execution**:
   Tool command: `dotnet test NitroLite.Tests\NitroLite.Tests.csproj`
   Result: `Passed! - Failed: 0, Passed: 119, Skipped: 0, Total: 119, Duration: 438 ms`

---

## 2. Logic Chain

1. **Root cause of manual launch minimizing (Observation 1 & 2)**:
   In `App.xaml.cs:100-111`, when `startMinimized` is initially `false` (no `--minimized` argument in `e.Args`), the code loads `config.json`. Because `config.json` persists `"StartMinimized": true` (Observation 2), `startMinimized` is forcibly overwritten to `true`.
2. **Window suppression on startup (Observation 1 & 3)**:
   Because `startMinimized == true`, `win = new MainWindow(true)` runs `WindowState = WindowState.Minimized; Hide();` (Observation 3), and `App.xaml.cs:115` skips calling `win.Show()`. Consequently, any manual launch (desktop click, Start menu) suppresses `MainWindow` and leaves the app idling invisibly in the system tray.
3. **Intent vs Implementation discrepancy (Observation 4 & 5)**:
   Observation 4 proves the user-facing purpose of `"StartMinimized"` is exclusively for Windows boot (`"PC açıldığında pencereler ekrana gelmez..."`). However, `DebloatService.SetNitroLiteAutoStart` (Observation 5) only appends `--minimized`, omitting `--autostart`, and `App.xaml.cs` (Observation 1) completely ignores `--autostart`.
4. **Foreground lock failure in single-instance activation (Observation 6 & 3)**:
   When Instance 2 detects the mutex (`createdNew == false`), it signals `EventWaitHandle` and exits immediately without calling Win32 `AllowSetForegroundWindow(ASFW_ANY)`. On Windows 10/11, Windows restricts background processes from stealing focus. When Instance 1 receives the signal and invokes `win.ShowWindow()`, Windows prevents `Activate()` from bringing `MainWindow` in front of the active application, resulting in an unresponsive window or taskbar flashing.
5. **Win32 un-minimize requirement for borderless WPF windows (Observation 3)**:
   Because `MainWindow` uses `WindowStyle="None"` and `AllowsTransparency="True"`, restoring an iconic or hidden window purely via WPF properties can fail to synchronously redraw or restore the native Win32 window geometry. Win32 `ShowWindow(hwnd, SW_RESTORE)` coupled with `SetForegroundWindow(hwnd)` reliably restores and focuses the window.

---

## 3. Caveats

1. **OS Elevation / UAC Levels**: If Instance 1 was started with elevated administrator privileges, and Instance 2 is launched by a non-elevated user, Windows UIPI (User Interface Privilege Isolation) could theoretically block cross-process window messages or handles unless `Local\` session namespace and standard Win32 synchronization objects are used. `EventWaitHandle` with `EventResetMode.AutoReset` in the `Local\` namespace avoids UIPI window message blocking.
2. **Multi-Monitor Coordinate Clamping**: Coordinates restored from `WindowLeft`/`WindowTop` in `MainWindow.LoadSettings()` already clamp within `SystemParameters.VirtualScreen*`. No coordinate regression was found.
3. **Non-R2 Features**: R1 (Thermal Degradation Trend & Sub-Component Matrix) and R3 (Keyboard Backlight State Persistence) are decoupled from R2 and do not impact window launch logic, provided `config.json` schema remains compatible.

---

## 4. Conclusion

The fix for Requirement R2 requires three coordinated modifications:
1. **Startup Argument Parsing**:
   - Introduce `StartupOptions.Parse(string[]? args)` in `NitroLite.Core`.
   - Set `startMinimized = true` ONLY if `args` contains `--autostart` or `--minimized` (case-insensitive).
   - Never inspect `cfg.StartMinimized` to override manual launches in `App.xaml.cs`.
   - On manual launch (`!startMinimized`), call `win.ShowWindow()` to ensure normal window state, visibility, and foreground focus.
2. **DebloatService Registry Command**:
   - In `DebloatService.SetNitroLiteAutoStart`, update the command string to always include `--autostart`:
     `startMinimized ? $"\"{exePath}\" --autostart --minimized" : $"\"{exePath}\" --autostart"`.
3. **Robust Single-Instance Foreground Signaling**:
   - In Instance 2, call Win32 `AllowSetForegroundWindow(ASFW_ANY)` before signaling `wakeEvent.Set()`. Only signal if Instance 2 was a manual launch (`!options.StartMinimized`). Catch `AbandonedMutexException` on mutex acquisition.
   - In `MainWindow.ShowWindow()`, call Win32 `ShowWindow(hwnd, SW_RESTORE)` and `SetForegroundWindow(hwnd)` alongside WPF `Show()`, `WindowState = WindowState.Normal`, `Activate()`, and `Focus()`.
4. **Unit Testing in `NitroLite.Tests`**:
   - Add `StartupOptionsTests.cs` testing empty args, `--autostart`, `--minimized`, combined flags, and command line formatting.

---

## 5. Verification Method

1. **Compilation Check**:
   ```powershell
   dotnet build c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.sln
   ```
   Must compile with 0 warnings/errors.
2. **Unit Test Execution**:
   ```powershell
   dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj
   ```
   All 119 existing tests plus newly added `StartupOptionsTests` must pass cleanly.
3. **Manual Launch Simulation**:
   - When launched with `args = []` and `config.json` has `"StartMinimized": true`, `StartupOptions.Parse` evaluates `StartMinimized = false`.
   - When launched with `args = ["--autostart"]` or `["--minimized"]`, `StartupOptions.Parse` evaluates `StartMinimized = true`.
4. **Invalidation Conditions**:
   - If manual launch (no args) ever results in `startMinimized == true`, the fix is invalid.
   - If launching a second instance fails to bring the first instance's `MainWindow` to the foreground, the fix is invalid.
