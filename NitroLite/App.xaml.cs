using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using NitroLite.Core;

namespace NitroLite;

public partial class App : System.Windows.Application
{
    private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nitrolite.log");
    private static readonly string ConfigFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    private const string MutexName = "Local\\NitroLite_SingleInstance_Mutex_AN16";
    private const string EventName = "Local\\NitroLite_SingleInstance_WakeUpEvent_AN16";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private const int ASFW_ANY = -1;

    private static Mutex? _mutex;
    private static EventWaitHandle? _wakeUpEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
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
            File.AppendAllText(LogFile, "Domain Exception:\n" + args.ExceptionObject + "\n\n");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            File.AppendAllText(LogFile, "Dispatcher Exception:\n" + args.Exception + "\n\n");
            args.Handled = false;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            File.AppendAllText(LogFile, "Task Exception:\n" + args.Exception + "\n\n");
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
            File.AppendAllText(LogFile, "Startup Exception:\n" + ex + "\n\n");
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        try
        {
            if (MainWindow is MainWindow win)
            {
                win.RealExit();
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
