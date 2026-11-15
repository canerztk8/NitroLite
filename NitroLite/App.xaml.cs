using System.IO;
using System.Windows;
using NitroLite.Core;
using NitroLite.Core.Hardware;

namespace NitroLite;

public partial class App : System.Windows.Application
{
    private static readonly string LogFile = Path.Combine(AppPaths.DataDirectory, "nitrolite.log");
    private static readonly string ConfigFile = AppPaths.GetDataFile("config.json");

    // Global\ rather than Local\: Local\ is scoped to one Windows session, so a second user signed in through
    // fast user switching (or an RDP session) could start a second NitroLite and both would drive the same EC.
    // Mutexes and events do not need SeCreateGlobalPrivilege, but the default DACL only grants the creating user,
    // so a different user gets access denied: that falls back to the per-session name instead of failing to start.
    private const string MutexName = @"Global\NitroLite_SingleInstance_Mutex_AN16";
    private const string MutexNameFallback = @"Local\NitroLite_SingleInstance_Mutex_AN16";
    private const string EventName = @"Global\NitroLite_SingleInstance_WakeUpEvent_AN16";
    private const string EventNameFallback = @"Local\NitroLite_SingleInstance_WakeUpEvent_AN16";

    // Serialises the headless commands against each other. They run before the GUI mutex on purpose (a script has
    // to be able to change the mode while the window is open), but two of them at once would both write the same
    // EC registers and the second write could land on a half-applied first one.
    private const string CliMutexName = @"Global\NitroLite_Cli_Mutex_AN16";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private const int ASFW_ANY = -1;

    private static Mutex? _mutex;
    private static EventWaitHandle? _wakeUpEvent;
    private static string _wakeUpEventName = EventNameFallback;

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

            // Run this before deleting the folder. NitroLite has no installer, so nothing else undoes the
            // Windows-level changes "PC Optimize" makes or removes the autostart entry.
            if (HasFlag(e.Args, "--uninstall-cleanup"))
            {
                Environment.Exit(RunCliTask("--uninstall-cleanup", SystemToolsService.UninstallCleanupAsync, e.Args));
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
        _mutex = CreateSingleInstanceMutex(out createdNew);

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
                if (EventWaitHandle.TryOpenExisting(_wakeUpEventName, out var wakeEvent))
                {
                    wakeEvent.Set();
                }
            }
            catch { }

            // Exit this duplicate process immediately without launching second UI
            Environment.Exit(0);
            return;
        }

        // 1.5 A previous run that never reached a clean exit may have left the fans at a fixed speed. The marker is
        // read before the window is built, so the firmware is back in charge before anything can read a fan mode
        // and mistake the leftover for the user's choice.
        if (CleanExitMarker.PreviousRunWasUnclean())
        {
            WriteLog("Önceki çalışma temiz kapanmadı", "Fanlar otomatik moda döndürülüyor.");
            AcerAgentClient.TryRestoreFanAutoBlocking(TimeSpan.FromSeconds(4));
        }
        CleanExitMarker.MarkRunning();

        // 2. Setup Wake-Up Event Listener for the primary instance
        try
        {
            _wakeUpEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _wakeUpEventName);
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

            // The process is going down and nothing else will run. A custom fan speed written to the firmware
            // outlives it, so the fans are handed back here rather than left at whatever the curve last asked for.
            // Blocking is correct: there is no dispatcher left to await on, and the alternative is a hot machine
            // running a quiet curve until the next start notices the marker.
            if (AcerAgentClient.TryRestoreFanAutoBlocking(TimeSpan.FromSeconds(4)))
            {
                CleanExitMarker.MarkCleanExit();
            }
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
            // by now, so the leftover can go. A run that died between the two moves of the commit phase left the
            // destination missing and its only good copy under .old, so the pass restores before it sweeps.
            var recovery = UpdateService.RecoverInterruptedUpdate();
            if (recovery.Restored.Count > 0)
                WriteLog("Update Recovery", "Restored: " + string.Join(", ", recovery.Restored));
            if (recovery.Failed.Count > 0)
                WriteLog("Update Recovery", "Could not restore: " + string.Join(", ", recovery.Failed));

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
            // A clock that stepped backwards makes the age negative. Read as "still inside the window" that kept
            // every old entry, so the queue filled up and the next single exception was allowed to crash the app.
            while (RecentUiExceptions.Count > 0)
            {
                var age = utcNow - RecentUiExceptions.Peek();
                if (age >= TimeSpan.Zero && age <= UiExceptionWindow) break;
                RecentUiExceptions.Dequeue();
            }
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
    /// <summary>
    /// Creates the single-instance mutex, preferring the machine-wide name and falling back to the per-session one
    /// when another user already owns it. Never throws: a lock that cannot be created must not stop the app from
    /// starting, so the failure is logged and the instance is allowed to run.
    /// </summary>
    private static Mutex? CreateSingleInstanceMutex(out bool createdNew)
    {
        createdNew = false;
        foreach (var name in new[] { MutexName, MutexNameFallback })
        {
            try
            {
                var mutex = new Mutex(true, name, out createdNew);
                _wakeUpEventName = name == MutexName ? EventName : EventNameFallback;
                return mutex;
            }
            catch (UnauthorizedAccessException)
            {
                // Another user's session owns the machine-wide name. Try the per-session one.
                createdNew = false;
            }
            catch (Exception ex)
            {
                WriteLog("Tek örnek kilidi oluşturulamadı", $"{name}: {ex.Message}");
                createdNew = true;
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Takes the CLI lock for the duration of a headless command. Returns null when another command is already
    /// running, so the caller can say so instead of writing the same hardware register twice.
    /// </summary>
    private static Mutex? TryAcquireCliMutex(TimeSpan timeout)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, CliMutexName);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // The previous command was killed mid-write; the lock is ours now.
                acquired = true;
            }
            if (acquired) return mutex;
            mutex.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            // A lock that cannot be created must not block the command.
            WriteLog("CLI kilidi oluşturulamadı", ex.Message);
            mutex?.Dispose();
            return new Mutex(false);
        }
    }

    private static int RunHardwareReport(string[] args)
    {
        UseUtf8Console();
        try
        {
            var capabilities = ReadCapabilities();
            var outDir = ValueAfter(args, "--out");
            if (!string.IsNullOrWhiteSpace(outDir))
            {
                var path = HardwareReport.WriteToDirectory(outDir, capabilities);
                Console.WriteLine(path);
                return 0;
            }

            Console.WriteLine(HasFlag(args, "--json")
                ? HardwareReport.BuildJson(capabilities)
                : HardwareReport.BuildText(capabilities));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Loc.T($"Donanım raporu oluşturulamadı: {ex.Message}"));
            return 1;
        }
    }

    /// <summary>
    /// Asks AASSvc what this machine can do, with a hard eight-second cap. The report is still useful without it, so
    /// a service that is not running, or one that hangs, must not hold the command open.
    /// </summary>
    private static DeviceCapabilities? ReadCapabilities()
    {
        try
        {
            using var client = new AcerAgentClient();
            var task = Task.Run(() => DeviceCapabilities.ProbeAsync(client));
            return task.Wait(TimeSpan.FromSeconds(8)) ? task.Result : null;
        }
        catch
        {
            return null;
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

        using var cliLock = TryAcquireCliMutex(TimeSpan.FromSeconds(10));
        if (cliLock == null)
        {
            Console.Error.WriteLine(Loc.T("Başka bir NitroLite komutu çalışıyor. Bitmesini bekleyip tekrar deneyin."));
            return 4;
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
        using var cliLock = TryAcquireCliMutex(TimeSpan.FromSeconds(10));
        if (cliLock == null)
        {
            Console.Error.WriteLine(Loc.T("Başka bir NitroLite komutu çalışıyor. Bitmesini bekleyip tekrar deneyin."));
            return 4;
        }

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
            // Reaching here means the process is leaving on its own terms, so the next start must not treat this
            // run as a crash and reset the fans behind the user's back.
            CleanExitMarker.MarkCleanExit();

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
