# Requirement R2: Window Launch Behavior Fix (Manual Launch vs Autostart) Analysis

## 1. Overview & Problem Definition

In **NitroLite**, launching the application manually (via desktop shortcut, Start Menu, or direct execution of `NitroLite.exe`) currently fails to present an active foreground window to the user. Instead, the application immediately starts minimized to the Windows system tray. 

Furthermore, if the application is already running, executing `NitroLite.exe` a second time often fails to reliably restore, un-minimize, and bring `MainWindow` into the foreground on Windows 10/11 due to Windows foreground lockout restrictions.

### Authoritative Requirements (R2):
1. **Manual Launch**: When `NitroLite.exe` is launched without `--autostart` or `--minimized` flags, it must open directly on screen with `WindowState = WindowState.Normal`, restored, visible, and focused.
2. **Autostart Launch**: When launched with `--autostart` or `--minimized` flags (e.g. during Windows boot), it must start minimized to the system tray.
3. **Single-Instance Second Launch**: When a second instance is started manually while NitroLite is already running, it must signal the existing instance to restore, un-minimize (`WindowState = Normal`), show, and bring `MainWindow` to the foreground.

---

## 2. Program Entrypoint & Current Startup Logic

### 2.1 Entrypoint & App Structure
- **Project Type**: WPF application targeting `.NET 10.0-windows` with `UseWPF=true` and `UseWindowsForms=true` (`NitroLite\NitroLite.csproj`).
- **Entrypoint**: `App.xaml` / `App.xaml.cs`.
- **Startup Configuration**: `App.xaml` has no `StartupUri`, intentionally delegating window initialization to `App.OnStartup(StartupEventArgs e)`.
- **Application Shutdown Mode**: `ShutdownMode = ShutdownMode.OnExplicitShutdown` (`App.xaml.cs:77`), keeping the application process alive even when all windows are closed/hidden, which is essential for system tray operation.

### 2.2 Trace of `App.OnStartup` (`NitroLite\App.xaml.cs`)
```csharp
protected override void OnStartup(StartupEventArgs e)
{
    // 1. Single Instance Check via Mutex
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

    // 2. Setup Wake-Up Event Listener for the primary instance
    try
    {
        _wakeUpEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var thread = new Thread(() =>
        {
            while (_wakeUpEvent != null)
            {
                try
                {
                    if (_wakeUpEvent.WaitOne())
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (MainWindow is MainWindow win)
                            {
                                win.ShowWindow();
                            }
                        });
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceWakeUpListener"
        };
        thread.Start();
    }
    catch { }
    ...
    try
    {
        base.OnStartup(e);

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
    }
    catch (Exception ex)
    {
        File.AppendAllText(LogFile, "Startup Exception:\n" + ex + "\n\n");
    }
}
```

---

## 3. Root Cause: Why Manual Launch Minimizes to System Tray

Direct code observation of lines 99–118 in `NitroLite\App.xaml.cs`:
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

And in `NitroLite\MainWindow.xaml.cs` lines 67–79:
```csharp
public MainWindow(bool startMinimized = false)
{
    InitializeComponent();
    SetupTrayIcon();
    SetupTimers();
    SetupEvents();

    if (startMinimized)
    {
        WindowState = WindowState.Minimized;
        Hide();
    }
}
```

### The Exact Flaws:
1. **Conflation of Boot Setting with Execution Mode**:
   In `config.json` (`NitroLite\bin\Release\config.json:2`), `"StartMinimized": true` is persisted.
   This setting corresponds to the UI option:
   `"Sistem Tepsisinde (Simge Durumunda) Sessiz Başlat - PC açıldığında pencereler ekrana gelmez, doğrudan sağ alt sistem tepsisinde sessizce çalışır"` (`MainWindow.xaml:1173-1175`).
   This user preference was explicitly designed for **Windows boot/autostart**, NOT manual launches.
   However, `App.xaml.cs` lines 100–111 inspected `cfg?.StartMinimized` on *every startup*, unconditionally forcing `startMinimized = true` even when the user double-clicked the desktop icon!
2. **Missing `--autostart` Flag Support**:
   Line 99 only checked for `--minimized`. The `--autostart` flag was not handled at all.
3. **Flawed `DebloatService.SetNitroLiteAutoStart` Command String**:
   In `NitroLite\Core\DebloatService.cs` line 152:
   `var cmd = startMinimized ? $"\"{exePath}\" --minimized" : $"\"{exePath}\"";`
   If `startMinimized` is false, it wrote `"{exePath}"` without any flags into the Windows Run registry key (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`), making it indistinguishable from a manual user launch.

---

## 4. Single-Instance Mechanism & Second Instance Signaling

### 4.1 How NitroLite Detects a Second Instance
NitroLite utilizes a named local mutex:
`_mutex = new Mutex(true, "Local\\NitroLite_SingleInstance_Mutex_AN16", out bool createdNew);`
- If `createdNew == true`, the process is the primary instance.
- If `createdNew == false`, a previous instance is already running.

### 4.2 Deficiencies in Current Second-Instance Handling
1. **Unhandled `AbandonedMutexException`**:
   If a previous instance crashed ungracefully without calling `ReleaseMutex()`, `new Mutex(true, ...)` can throw `AbandonedMutexException`. If unhandled, the application crashes on startup.
   Catching `AbandonedMutexException` and treating it as `createdNew = true` resolves this edge case.
2. **Windows Foreground Lockout Restrictions**:
   When Instance 2 is launched by user interaction (e.g., clicking shortcut in Explorer), Instance 2 has foreground activation rights from the Windows shell.
   However, Instance 1 is an existing background process resting in the system tray.
   When Instance 2 calls `wakeEvent.Set()` and terminates, Instance 1 receives the signal and calls `win.ShowWindow()` -> `Activate()`.
   **Windows OS blocks Instance 1 from stealing the foreground** because Instance 1 does not have the foreground lock. The window either stays minimized/hidden or merely flashes on the taskbar.
   **Solution**: Instance 2 must call `AllowSetForegroundWindow(ASFW_ANY)` prior to signaling `wakeEvent.Set()`.
3. **Instance 2 with `--autostart` or `--minimized`**:
   If a second instance is started automatically (e.g. autostart or automated task), it should exit quietly and NOT signal Instance 1 to restore its window.
4. **Early Startup Race Condition in Primary Instance**:
   In `App.xaml.cs`, `SingleInstanceWakeUpListener` thread starts before `MainWindow` is initialized. If Instance 2 signals while Instance 1 is still initializing, `MainWindow is MainWindow win` evaluates to `null` and the wake-up request is lost.

---

## 5. Win32 & WPF API Patterns for Reliable Window Restoration

### 5.1 Native Win32 P/Invoke Definitions
To guarantee restoration on Windows 10/11 across borderless/transparent WPF windows (`WindowStyle="None"`, `AllowsTransparency="True"`):

```csharp
internal static class NativeMethods
{
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOW = 5;
    public const int SW_RESTORE = 9;
    public const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);
}
```

### 5.2 Handshake Pattern: Instance 2 -> Instance 1
1. **Instance 2 (Sender)**:
   ```csharp
   // Grant permission to primary instance to take foreground
   NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
   if (EventWaitHandle.TryOpenExisting(EventName, out var wakeEvent))
   {
       wakeEvent.Set();
   }
   Environment.Exit(0);
   ```

2. **Instance 1 (Receiver - `MainWindow.ShowWindow()`)**:
   ```csharp
   public void ShowWindow()
   {
       // 1. Ensure visibility (unhides if window was closed to tray via Hide())
       if (!IsVisible)
       {
           Show();
       }

       // 2. Reset WPF WindowState
       if (WindowState == WindowState.Minimized)
       {
           WindowState = WindowState.Normal;
       }

       // 3. Native Win32 window restoration and foreground grab
       var helper = new WindowInteropHelper(this);
       var hwnd = helper.Handle;
       if (hwnd != IntPtr.Zero)
       {
           if (NativeMethods.IsIconic(hwnd))
           {
               NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
           }
           else
           {
               NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
           }
           NativeMethods.SetForegroundWindow(hwnd);
       }

       // 4. WPF activation and input focus
       Activate();
       Focus();

       // 5. Z-order elevation bump
       Topmost = true;
       Topmost = false;
   }
   ```

---

## 6. Implementation Architecture & Proposed Code Changes

### 6.1 Create `StartupOptions` in `NitroLite.Core`
Encapsulating argument parsing into a pure, testable model:
```csharp
namespace NitroLite.Core;

public class StartupOptions
{
    public bool StartMinimized { get; set; }
    public bool IsAutostart { get; set; }
    public bool IsManualLaunch => !StartMinimized && !IsAutostart;

    public static StartupOptions Parse(string[]? args)
    {
        var options = new StartupOptions();
        if (args == null || args.Length == 0)
        {
            // Pure manual launch (desktop icon, start menu, CLI without flags)
            options.StartMinimized = false;
            options.IsAutostart = false;
            return options;
        }

        foreach (var arg in args)
        {
            var clean = arg.Trim().TrimStart('-', '/');
            if (clean.Equals("autostart", StringComparison.OrdinalIgnoreCase))
            {
                options.IsAutostart = true;
                options.StartMinimized = true;
            }
            else if (clean.Equals("minimized", StringComparison.OrdinalIgnoreCase) ||
                     clean.Equals("m", StringComparison.OrdinalIgnoreCase))
            {
                options.StartMinimized = true;
            }
        }

        return options;
    }
}
```

### 6.2 Update `NitroLite\Core\DebloatService.cs`
Update autostart command line generation:
```csharp
public static string BuildAutoStartCommandLine(string exePath, bool startMinimized)
{
    return startMinimized 
        ? $"\"{exePath}\" --autostart --minimized" 
        : $"\"{exePath}\" --autostart";
}

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
                key.SetValue("NitroLite", BuildAutoStartCommandLine(exePath, startMinimized));
            }
        }
        else
        {
            key.DeleteValue("NitroLite", false);
        }
    }
    catch { }
}
```

### 6.3 Refactor `NitroLite\App.xaml.cs`
1. Use `StartupOptions.Parse(e.Args)`.
2. Do **not** override `startMinimized` with `cfg.StartMinimized` on manual launches.
3. In Instance 2, call `NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY)` before signaling `wakeEvent.Set()`, and only signal when `!options.StartMinimized`.
4. Catch `AbandonedMutexException`.
5. Ensure `MainWindow = win` is set and call `win.ShowWindow()` on manual launch (`!options.StartMinimized`).

---

## 7. Unit Test Strategy for `NitroLite.Tests`

Add a dedicated test fixture `StartupOptionsTests.cs` to test all scenarios thoroughly:
1. `Parse_NullOrEmptyArgs_ReturnsManualLaunchNotMinimized`: Validates that empty args (manual desktop launch) always returns `StartMinimized == false`.
2. `Parse_AutostartFlag_ReturnsStartMinimizedTrue`: Validates `--autostart` flag results in `StartMinimized == true` and `IsAutostart == true`.
3. `Parse_MinimizedFlag_ReturnsStartMinimizedTrue`: Validates `--minimized` flag results in `StartMinimized == true`.
4. `Parse_CombinedAutostartAndMinimizedFlags_ReturnsStartMinimizedTrue`: Validates combined `--autostart --minimized`.
5. `Parse_CaseInsensitiveAndSlashPrefixes_ReturnsExpected`: Validates `/autostart`, `-autostart`, `--MINIMIZED`.
6. `Parse_UnrelatedFlags_DoesNotMinimize`: Validates `--something-else` does not trigger minimized launch.
7. `DebloatService_BuildAutoStartCommandLine_GeneratesAccurateCommands`: Validates registry command formatting for both minimized and non-minimized autostart.
8. `EventWaitHandle_SingleInstanceSignaling_PassesSignalBetweenThreads`: Validates cross-thread wake-up event behavior.
9. `AppSettings_StartMinimized_SerializationRoundtrip`: Validates that `AppSettings.StartMinimized` serializes and deserializes accurately in `config.json` without affecting manual launches.

All tests can be registered in `NitroLite.Tests` as xUnit tests and verified via `dotnet test`.
