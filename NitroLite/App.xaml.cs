using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using NitroLite.Core;
using NitroLite.Core.Hardware;

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

            // Read-only diagnostic: prints what NitroLite can see about this laptop. No --yes, because it changes
            // nothing; a user pasting the output into an issue is the whole point.
            if (HasFlag(e.Args, "--hardware-report"))
            {
                Environment.Exit(RunHardwareReport(e.Args));
                return;
            }

            // One-shot hardware commands: a script, a launcher or a macro pad changes the mode without opening the
            // window. No mutex, because a running NitroLite picks the change up as an external change on its next poll.
            if (CliCommands.IsHardwareCommand(e.Args))
            {
                Environment.Exit(RunHardwareCommand(e.Args));
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
        catch (Exception ex)
        {
            // Without this listener a second launch cannot bring the running window forward; the app still works,
            // but the user sees nothing happen and needs the log to explain why.
            WriteLog("Single-instance wake-up listener could not start", ex.ToString());
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            WriteLog("Domain Exception", args.ExceptionObject?.ToString());
        };

        DispatcherUnhandledException += (s, args) =>
        {
            WriteLog("Dispatcher Exception", args.Exception.ToString());
            // An exception in one click or timer handler must not take the whole app down: NitroLite holds hardware
            // state (game profile, Eco baseline, fan mode) that a crash leaves for the next start to repair.
            // Keep running and tell the user, unless errors arrive in a burst (a handler failing on every tick),
            // where continuing would only loop.
            args.Handled = ShouldSurviveUiException(DateTime.UtcNow) && MainWindow is MainWindow { IsLoaded: true };
            if (!args.Handled) return;
            try
            {
                ((MainWindow)MainWindow).ShowToast(Loc.T("Beklenmeyen bir hata oluştu; ayrıntılar günlük dosyasına yazıldı."), false);
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            WriteLog("Task Exception", args.Exception.ToString());
        };

        try
        {
            base.OnStartup(e);

            bool startMinimized = options.StartMinimized;

            // The language and the palette have to be chosen before the window is built: LocXaml translates the
            // tree once right after InitializeComponent, and every StaticResource in the XAML resolves against the
            // merged theme dictionary.
            var startup = LoadStartupSettings();
            var (language, persistLanguage) = ResolveStartupLanguage(startup);
            Loc.SetLanguage(language);
            ThemeManager.Apply(startup.Settings.Theme);
            if (persistLanguage) PersistStartupLanguage(startup, language);

            // An update renames the previous executable instead of overwriting it; the process that owned it is gone
            // by now, so the leftover can go.
            UpdateService.CleanUpPreviousVersion();

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
                    Loc.T($"NitroLite başlatılamadı ve kapatılacak.\n\n{ex.Message}\n\nAyrıntılar: {LogFile}"),
                    Loc.T("NitroLite"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            Shutdown(1);
        }
    }

    /// <summary>Reads the settings once at startup; a broken file must never stop the window from opening.</summary>
    private static SettingsLoadResult LoadStartupSettings()
    {
        try
        {
            var primary = AppPaths.GetDataFile("settings.json");
            return SettingsManager.LoadSettings(primary, primary + ".bak", ConfigFile);
        }
        catch
        {
            return new SettingsLoadResult();
        }
    }

    /// <summary>
    /// The language to start in, and whether that choice has to be written back. See
    /// <see cref="Loc.ResolveStartupLanguage"/> for the rule; the write is what keeps the second launch honest.
    /// </summary>
    private static (string Language, bool Persist) ResolveStartupLanguage(SettingsLoadResult result)
    {
        var freshInstall = result.Source == SettingsLoadSource.Defaults;
        var language = Loc.ResolveStartupLanguage(result.Settings.Language, freshInstall);
        return (language, !Loc.IsSupported(result.Settings.Language));
    }

    /// <summary>
    /// Writes the language chosen at startup so the next launch reads it instead of guessing. Never throws: a
    /// read-only data folder must not stop the window from opening, and a corrupt settings file is left alone so the
    /// user can still inspect it.
    /// </summary>
    private static void PersistStartupLanguage(SettingsLoadResult result, string language)
    {
        if (result.PrimaryCorrupt) return;
        try
        {
            result.Settings.Language = language;
            SettingsManager.SaveSettingsAtomic(AppPaths.GetDataFile("settings.json"), result.Settings);
        }
        catch (Exception ex)
        {
            WriteLog("Dil kaydedilemedi", ex.Message);
        }
    }

    /// <summary>
    /// Appends to the log without ever throwing (handlers run during crashes). Shares one lock and one rotation
    /// policy with <see cref="AppLog"/> so the two writers cannot interleave or rotate over each other.
    /// </summary>
    internal static void WriteLog(string title, string? details) => AppLog.Error(title, details);

    private static readonly Queue<DateTime> RecentUiExceptions = new();
    internal const int MaxUiExceptionsPerWindow = 5;
    internal static readonly TimeSpan UiExceptionWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// True while UI exceptions are occasional. More than <see cref="MaxUiExceptionsPerWindow"/> within
    /// <see cref="UiExceptionWindow"/> means something fails repeatedly; then the app is allowed to crash.
    /// </summary>
    internal static bool ShouldSurviveUiException(DateTime utcNow)
    {
        lock (RecentUiExceptions)
        {
            while (RecentUiExceptions.Count > 0 && utcNow - RecentUiExceptions.Peek() > UiExceptionWindow)
                RecentUiExceptions.Dequeue();
            RecentUiExceptions.Enqueue(utcNow);
            return RecentUiExceptions.Count <= MaxUiExceptionsPerWindow;
        }
    }

    internal static void ResetUiExceptionHistoryForTesting()
    {
        lock (RecentUiExceptions) RecentUiExceptions.Clear();
    }

    private static bool HasFlag(string[] args, params string[] names) =>
        args.Any(a => names.Any(n => a.Equals(n, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Prints the hardware report to stdout, or writes it to a folder when <c>--out &lt;dir&gt;</c> is given.
    /// <c>--json</c> switches the stdout format. Read-only, so it needs no confirmation flag.
    /// </summary>
    private static int RunHardwareReport(string[] args)
    {
        UseUtf8Console();
        try
        {
            var outDir = ValueAfter(args, "--out");
            if (!string.IsNullOrWhiteSpace(outDir))
            {
                var path = HardwareReport.WriteToDirectory(outDir);
                Console.WriteLine(path);
                return 0;
            }

            Console.WriteLine(HasFlag(args, "--json") ? HardwareReport.BuildJson() : HardwareReport.BuildText());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Loc.T($"Donanım raporu oluşturulamadı: {ex.Message}"));
            return 1;
        }
    }

    /// <summary>The argument following <paramref name="name"/>, or "" when the flag is absent or last.</summary>
    private static string ValueAfter(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return "";
    }

    /// <summary>
    /// Runs one maintenance command with a hard timeout. The old code called Wait(45000) and then .Result, which
    /// blocked forever when the timeout expired instead of reporting it.
    /// </summary>
    private static int RunCliTask(string command, Func<Task<ToolExecutionResult>> start, string[] args)
    {
        UseUtf8Console();
        if (!HasFlag(args, "--yes", "-y", "--force"))
        {
            Console.Error.WriteLine(Loc.T($"{command} sistem ayarlarını değiştirir. Onaylamak için --yes ekleyin:"));
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
            Console.Error.WriteLine(Loc.T($"{command} başlatılamadı: {ex.Message}"));
            return 1;
        }

        try
        {
            if (!task.Wait(45000))
            {
                Console.Error.WriteLine(Loc.T($"{command} 45 saniyede tamamlanmadı."));
                return 2;
            }
        }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine(Loc.T($"{command} başarısız: {ex.GetBaseException().Message}"));
            return 1;
        }

        var result = task.Result;
        if (!string.IsNullOrWhiteSpace(result.Message)) Console.Error.WriteLine(result.Message);
        return result.Success ? 0 : 1;
    }

    /// <summary>
    /// The console defaults to the machine's OEM code page, which turns every Turkish, German or Russian message
    /// into question marks. UTF-8 is what the catalogs are written in. Never throws: a redirected stream or a
    /// console that refuses the change must not stop the command.
    /// </summary>
    private static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch { }
    }

    /// <summary>
    /// Runs <c>--mode</c>, <c>--fan</c> or <c>--profile</c> against the hardware and exits. Deliberately does not
    /// touch the settings file: a script changing the mode must not rewrite the user's saved preferences, and the
    /// window (if it is open) treats the result as an external change, exactly like a change made in NitroSense.
    /// <para>
    /// The work runs on a thread-pool thread. OnStartup runs on the dispatcher, and blocking that thread on an
    /// async call whose continuations want the dispatcher is a deadlock, not a slow command.
    /// </para>
    /// </summary>
    private static int RunHardwareCommand(string[] args)
    {
        UseUtf8Console();
        try
        {
            var task = Task.Run(() => RunHardwareCommandAsync(args));
            if (!task.Wait(TimeSpan.FromSeconds(30)))
            {
                Console.Error.WriteLine(Loc.T("Komut 30 saniyede tamamlanmadı."));
                return 2;
            }
            return task.Result;
        }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine(Loc.T($"Komut başarısız: {ex.GetBaseException().Message}"));
            return 1;
        }
    }

    private static async Task<int> RunHardwareCommandAsync(string[] args)
    {
        var loaded = LoadStartupSettings();
        Loc.SetLanguage(Loc.ResolveStartupLanguage(loaded.Settings.Language, loaded.Source == SettingsLoadSource.Defaults));

        if (!CliCommands.TryParse(args, out var command, out var error))
        {
            Console.Error.WriteLine(error);
            return 3;
        }

        if (command.IsEmpty)
        {
            Console.Error.WriteLine(Loc.T("Uygulanacak bir komut yok."));
            return 3;
        }

        int exit = 0;

        if (command.Mode is { } mode)
        {
            using var agent = new AcerAgentClient();
            if (await agent.SetOperatingModeAsync(mode))
            {
                Console.WriteLine(Loc.T($"Çalışma modu: {ModePolicy.DisplayName(mode)}"));
            }
            else
            {
                Console.Error.WriteLine(Loc.T($"Çalışma modu değiştirilemedi: {ModePolicy.DisplayName(mode)}"));
                exit = 1;
            }
        }

        if (command.Fan is { } fan)
        {
            using var agent = new AcerAgentClient();
            int percent = command.FanPercent ?? loaded.Settings.CustomFanCpuPercent;
            if (await agent.SetFanModeAsync(fan, percent, percent))
            {
                Console.WriteLine(fan == FanMode.Custom
                    ? Loc.T($"Fan: {ModePolicy.FanDisplayName(fan)} %{percent}")
                    : Loc.T($"Fan: {ModePolicy.FanDisplayName(fan)}"));
            }
            else
            {
                Console.Error.WriteLine(Loc.T($"Fan modu değiştirilemedi: {ModePolicy.FanDisplayName(fan)}"));
                exit = 1;
            }
        }

        if (command.MouseProfile is { } mouseProfile)
        {
            var mouse = new AtkMouseService();
            if (mouse.SwitchProfile(mouseProfile))
            {
                Console.WriteLine(Loc.T($"Fare profili: Config {mouseProfile}"));
            }
            else
            {
                Console.Error.WriteLine(Loc.T($"Fare profili değiştirilemedi: Config {mouseProfile}"));
                exit = 1;
            }
        }

        return exit;
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

    /// <summary>
    /// Gives up the single-instance mutex so a replacement process can start before this one has finished exiting.
    /// Used by the in-app updater and by the language and theme restart. <see cref="OnExit"/> then finds nothing to
    /// release.
    /// </summary>
    internal static void ReleaseSingleInstanceMutex()
    {
        try
        {
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
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
