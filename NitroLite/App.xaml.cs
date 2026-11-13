using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using NitroLite.Core;

namespace NitroLite;

public partial class App : System.Windows.Application
{
    private static readonly string LogFile = Path.Combine(AppPaths.DataDirectory, "nitrolite.log");
    private static readonly string ConfigFile = AppPaths.GetDataFile("config.json");

    private const string MutexName = "Local\\NitroLite_SingleInstance_Mutex_AN16";
    private const string EventName = "Local\\NitroLite_SingleInstance_WakeUpEvent_AN16";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private const int ASFW_ANY = -1;

    private static Mutex? _mutex;
    private static EventWaitHandle? _wakeUpEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 0. Headless CLI Tool & Maintenance Commands (run without mutex or GUI)
        if (e.Args != null && e.Args.Length > 0)
        {
            if (HasFlag(e.Args, "--purge-standby", "--standby-purge"))
            {
                Environment.Exit(MemoryCleanerService.PurgeStandbyDirect());
                return;
            }

            // These change system state (service registrations, GPU mode, temp files), so they need an explicit
            // --yes: a stray shortcut or script must not be able to run them silently.
            if (HasFlag(e.Args, "--fix-lighting"))
            {
                Environment.Exit(RunCliTask("--fix-lighting", SystemToolsService.FixKeyboardLightingAsync, e.Args));
                return;
            }

            if (HasFlag(e.Args, "--fix-services"))
            {
                Environment.Exit(RunCliTask("--fix-services", SystemToolsService.FixAcerServicesAsync, e.Args));
                return;
            }

            if (HasFlag(e.Args, "--restore-mux"))
            {
                Environment.Exit(RunCliTask("--restore-mux", SystemToolsService.RestoreMuxSwitchAsync, e.Args));
                return;
            }

            if (HasFlag(e.Args, "--optimize-pc"))
            {
                Environment.Exit(RunCliTask("--optimize-pc", SystemToolsService.OptimizePcAsync, e.Args));
                return;
            }

            if (HasFlag(e.Args, "--restore-optimize"))
            {
                Environment.Exit(RunCliTask("--restore-optimize", SystemToolsService.RestoreOptimizePcAsync, e.Args));
                return;
            }
        }

        var options = StartupOptions.Parse(e.Args);

        // 1. Single Instance Check via Mutex
        bool createdNew = false;
        try
        {
            _mutex = new Mutex(true, MutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            createdNew = true;
        }

        if (!createdNew)
        {
            try
            {
                if (_mutex != null && _mutex.WaitOne(0))
                {
                    createdNew = true;
                }
            }
            catch (AbandonedMutexException)
            {
                createdNew = true;
            }
        }

        if (!createdNew)
        {
            // Another instance is already running! Signal existing instance to bring its window to front.
            try
            {
                AllowSetForegroundWindow(ASFW_ANY);
                if (EventWaitHandle.TryOpenExisting(EventName, out var wakeEvent))
                {
                    wakeEvent.Set();
                }
            }
            catch { }

            // Exit this duplicate process immediately without launching second UI
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
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
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

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            WriteLog("Domain Exception", args.ExceptionObject?.ToString());
        };

        DispatcherUnhandledException += (s, args) =>
        {
            WriteLog("Dispatcher Exception", args.Exception.ToString());
            args.Handled = false;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            WriteLog("Task Exception", args.Exception.ToString());
        };

        try
        {
            base.OnStartup(e);

            bool startMinimized = options.StartMinimized;

            var win = new MainWindow(startMinimized);
            MainWindow = win;
            if (!startMinimized)
            {
                win.Show();
                win.ShowWindow();
            }
        }
        catch (Exception ex)
        {
            // Without a window the process would stay alive (OnExplicitShutdown) holding the single-instance mutex,
            // and every later launch would silently exit. Report and shut down instead.
            WriteLog("Startup Exception", ex.ToString());
            try
            {
                System.Windows.MessageBox.Show(
                    "NitroLite başlatılamadı ve kapatılacak.\n\n" + ex.Message + "\n\nAyrıntılar: " + LogFile,
                    "NitroLite", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            Shutdown(1);
        }
    }

    /// <summary>
    /// Appends to the log without ever throwing (handlers run during crashes). Shares one lock and one rotation
    /// policy with <see cref="AppLog"/> so the two writers cannot interleave or rotate over each other.
    /// </summary>
    internal static void WriteLog(string title, string? details) => AppLog.Error(title, details);

    private static bool HasFlag(string[] args, params string[] names) =>
        args.Any(a => names.Any(n => a.Equals(n, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Runs one maintenance command with a hard timeout. The old code called Wait(45000) and then .Result, which
    /// blocked forever when the timeout expired instead of reporting it.
    /// </summary>
    private static int RunCliTask(string command, Func<Task<ToolExecutionResult>> start, string[] args)
    {
        if (!HasFlag(args, "--yes", "-y", "--force"))
        {
            Console.Error.WriteLine($"{command} sistem ayarlarını değiştirir. Onaylamak için --yes ekleyin:");
            Console.Error.WriteLine($"  NitroLite.exe {command} --yes");
            return 3;
        }

        Task<ToolExecutionResult> task;
        try
        {
            task = start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{command} başlatılamadı: {ex.Message}");
            return 1;
        }

        try
        {
            if (!task.Wait(45000))
            {
                Console.Error.WriteLine($"{command} 45 saniyede tamamlanmadı.");
                return 2;
            }
        }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine($"{command} başarısız: {ex.GetBaseException().Message}");
            return 1;
        }

        var result = task.Result;
        if (!string.IsNullOrWhiteSpace(result.Message)) Console.Error.WriteLine(result.Message);
        return result.Success ? 0 : 1;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        try
        {
            if (MainWindow is MainWindow win)
            {
                win.FastExitForSessionEnding();
            }
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _wakeUpEvent?.Dispose();
            _wakeUpEvent = null;

            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch { }

        base.OnExit(e);
    }
}
