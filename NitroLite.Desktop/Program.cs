using Avalonia;
using NitroLite.Core;
using NitroLite.Core.Hardware;

#if WINDOWS
using NitroLite.Core.Hardware.Wmi;
#endif

namespace NitroLite.Desktop;

/// <summary>
/// The entry point. Kept to the decisions Avalonia needs before it starts so the composition root stays in
/// <see cref="AppHost"/>, where it can be read without knowing anything about the UI framework.
/// </summary>
/// <remarks>
/// <para>
/// The order of the checks below is the whole design. The WMI helper is decided first, because it is a second process
/// of this same executable and the single-instance guard would classify it as a duplicate. The one-shot commands come
/// next, because they must run while the window is already open. Only then does the guard run, and only then does
/// Avalonia start.
/// </para>
/// <para>
/// A second launch does not open a second window: it signals the running one and exits, which is what a user who
/// double-clicked the shortcut twice expects.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// The flag the shell passes to the copy it starts when the user changes the language.
    /// </summary>
    /// <remarks>
    /// It changes one thing: the single-instance slot is waited for instead of being treated as a duplicate launch.
    /// Everything else about the launch is the same, so a restart lands in the same window with the same settings.
    /// </remarks>
    public const string RestartArgument = "--restart";

    [STAThread]
    public static int Main(string[] args)
    {
#if WINDOWS
        // The elevated WMI helper is a second process of this same executable, started with Verb = "runas". It is
        // decided before Avalonia is touched: a worker that started a window would put a second NitroLite on the
        // user's desktop, and the caller would be left waiting on a pipe nobody is listening to.
        var mode = StartupMode.Parse(args);
        if (args.Length == 1 && args[0] == "--pssvc-sensors")
            return NitroLite.Windows.Core.Hardware.LegacySenseSensors.RunAsync(Console.Out).GetAwaiter().GetResult();
        if (args.Length == 1 && args[0] == "--agent-capabilities")
            return NitroLite.Windows.Core.AgentCapabilityDump.RunAsync(Console.Out).GetAwaiter().GetResult();
        if (mode.IsWmiWorker)
        {
            return WmiWorkerHost.Run(mode);
        }
#endif

#if !WINDOWS
        // The root helper the Linux package installs as a systemd socket service. It is decided before anything else
        // runs: the helper has no window, no settings and no user, and it must not touch either.
        if (args.Any(a => string.Equals(a, "--linux-helper", StringComparison.Ordinal)))
        {
            return NitroLite.Linux.LinuxHelperHost.Run(args);
        }

        // One command that checks, on a real Linux machine, everything the Linux build depends on. The report goes to
        // the terminal and to a file in the data folder, so it can be attached to an issue as it is.
        if (args.Any(a => string.Equals(a, "--linux-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var test = NitroLite.Linux.LinuxSelfTest.ForThisMachine();
            var report = test.Run();
            Console.Out.Write(report);
            try
            {
                var path = AppPaths.GetDataFile("nitrolite-selftest.txt");
                File.WriteAllText(path, report);
                Console.Out.WriteLine();
                Console.Out.WriteLine("Saved to " + path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("The report could not be saved: " + ex.Message);
            }

            return test.Failures == 0 ? 0 : 1;
        }
#endif

        // A flag nothing reads is a typo or a flag from another build. Ignoring it silently opened the window when a
        // command was meant, so it is named on stderr (and in the log, for a launch without a console).
        foreach (var unknown in CliTaskRunner.FindUnknownFlags(args))
        {
            Console.Error.WriteLine(Loc.T($"Bilinmeyen seçenek yok sayıldı: {unknown}"));
            AppLog.Warn("Unknown command-line option ignored", unknown);
        }

        // The one-shot commands. Each of these talks to the hardware and exits without a window, so a script or a
        // shortcut can repair a machine without a user in front of it. Both spellings arrive here: --cli <command>
        // and the legacy --fix-lighting / --optimize-pc flags that older shortcuts and scripts still use.
        if (CliTaskRunner.IsCliTask(args))
        {
            return RunCliTask(CliTaskRunner.ParseCommand(args), args);
        }

        if (args.Any(a => string.Equals(a, "--hardware-report", StringComparison.OrdinalIgnoreCase)))
        {
            return RunHardwareReport();
        }

        if (CliCommands.IsHardwareCommand(args))
        {
            return RunHardwareCommand(args);
        }

#if !WINDOWS
        // --linux-capture <file>: the Linux window, driven by a machine recorded with tools/linux-capture. It runs on
        // any OS, so the Linux build can be worked on from Windows. It gets a data folder of its own next to the capture
        // — the Linux build on Windows would otherwise share the Windows app's settings and overwrite them — and it
        // skips the single-instance slot, so it can run beside an installed NitroLite.
        var captureIndex = Array.FindIndex(args, a => string.Equals(a, "--linux-capture", StringComparison.OrdinalIgnoreCase));
        if (captureIndex >= 0 && captureIndex + 1 < args.Length)
        {
            var capturePath = Path.GetFullPath(args[captureIndex + 1]);
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirOverrideVariable)))
            {
                Environment.SetEnvironmentVariable(AppPaths.DataDirOverrideVariable, capturePath + ".data");
            }

            AppHost.LinuxCapturePath = capturePath;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
#endif

        // One NitroLite per desktop. A second launch brings the first one forward instead of opening a second window
        // with a second telemetry loop and a second fan curve writing to the same fans.
        //
        // A restart is the one launch that arrives while the slot is still held: the old process starts this one and
        // only then begins its own exit, because a process cannot start its replacement after it is gone. So this
        // path waits for the slot instead of treating the wait as a duplicate launch.
        var restarting = args.Any(a => string.Equals(a, RestartArgument, StringComparison.OrdinalIgnoreCase));

        using var guard = restarting
            ? SingleInstanceGuard.AcquireWhenFree(TimeSpan.FromSeconds(20))
            : SingleInstanceGuard.Acquire();

        if (!guard.IsFirstInstance)
        {
            // A restart that never got the slot means the old copy is still alive and healthy. Bringing it forward is
            // the honest answer: the user asked for a window, and there is one.
            SingleInstanceGuard.SignalExistingInstance();
            return 0;
        }

        guard.Listen();
        App.SingleInstance = guard;

#if WINDOWS
        // Counts this windowed start against a freshly installed update; App marks the start healthy once the window is
        // up. A build that keeps failing before that point is put back, and the next start runs the previous version.
        if (UpdateService.RecordStartupAttempt() == PendingUpdateState.RolledBack)
            AppLog.Warn("The update was rolled back", "the previous version runs from the next start");
#endif

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Also used by the Avalonia designer, which is why it is public and parameterless.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Runs one one-shot command against the same backend the window would use, and returns its exit code.
    /// </summary>
    /// <remarks>
    /// The legacy spelling changes system state, so it asks first: without <c>--yes</c> the command is refused with
    /// exit code 3 and the line that would work, which is what the Windows front end did. <c>--cli</c> is a command
    /// someone typed on purpose and runs without the second flag.
    /// </remarks>
    private static int RunCliTask(string command, string[] args)
    {
        // An unknown name is refused before anything is built: the composition root probes the hardware and starts
        // the elevated helper, which is a lot of work (and possibly a UAC prompt) for a typo.
        if (!CliTaskRunner.IsKnownCommand(command))
        {
            Console.Error.WriteLine($"--cli: {string.Join(", ", CliTaskRunner.Commands)}");
            return 2;
        }

        if (CliTaskRunner.RequiresConfirmation(args) && !CliTaskRunner.IsConfirmed(args))
        {
            var flag = CliTaskRunner.ParseLegacyFlag(args);
            if (flag.Length == 0) flag = "--cli " + command;
            Console.Error.WriteLine(Loc.T("Bu komut sistem ayarlarını değiştirir. Onaylamak için --yes ekleyin:"));
            Console.Error.WriteLine($"  NitroLite.exe {flag} --yes");
            return 3;
        }

        // The settings file is last-writer-wins between a running window and this process, and the window owns the
        // hardware: the same notice the hardware one-shots print.
        if (SingleInstanceGuard.IsAnotherInstanceRunning())
        {
            Console.Error.WriteLine(Loc.T("NitroLite açık; çalışan pencere bu ayarı bir sonraki döngüsünde değiştirebilir."));
        }

        if (string.Equals(command, "undo-repair", StringComparison.OrdinalIgnoreCase))
        {
            return RunUndoRepair();
        }

        try
        {
            var services = AppHost.CreateServices();
            var steps = string.Equals(command, "uninstall-cleanup", StringComparison.OrdinalIgnoreCase)
                ? BuildUninstallCleanupSteps(services)
                : null;

            int code = CliTaskRunner.RunAsync(command, services.Hardware, services.Settings, services.AutoStart,
                    steps, Console.Out)
                .GetAwaiter().GetResult();

            // The tasks change settings (optimize records the mode restore-optimize goes back to; uninstall-cleanup
            // turns the fan curve and school mode off). Each --cli run is its own process, so a change that is not
            // written here is gone before the next command reads it.
            try
            {
                SettingsManager.SaveSettingsAtomic(services.SettingsPath, services.Settings);
            }
            catch (Exception ex)
            {
                AppLog.Warn("The settings could not be saved after the CLI command", $"{command}: {ex.Message}");
            }

            return code;
        }
        catch (Exception ex)
        {
            AppLog.Error("The CLI command could not be run", $"{command}: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// The undo actions of <c>--cli uninstall-cleanup</c>. The order and the conditions live in
    /// <see cref="CliTaskRunner.BuildUninstallCleanupSteps"/>; this only supplies the two Windows steps.
    /// </summary>
    private static List<CliCleanupStep> BuildUninstallCleanupSteps(PlatformServices services)
    {
        CliCleanupStep? systemCleanup = null;
        CliCleanupStep? elevatedTask = null;

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            // PC Optimize's undo record, and the HKCU Run value removed outright. The elevated-task adapter's
            // DisableAsync is not used for the sign-in entry: it repairs the Run value instead of removing it. Only the
            // optimize restore asks for elevation.
            systemCleanup = new CliCleanupStep(Loc.T("Optimizasyonu Geri Al"), async _ =>
            {
                var result = await SystemToolsService.UninstallCleanupAsync().ConfigureAwait(false);
                return (result.Success, result.Message);
            }, Elevates: File.Exists(SystemToolsService.OptimizeBackupPath));

            elevatedTask = new CliCleanupStep(Loc.T("Yönetici yetkisiyle otomatik başlat (Görev Zamanlayıcı)"), async ct =>
            {
                var task = new ElevatedStartupService();
                if (!await task.IsEnabledAsync(ct).ConfigureAwait(false)) return (true, null);
                var result = await task.DisableAsync(ct).ConfigureAwait(false);
                return (result.Applied, result.Applied ? null : task.LastError);
            }, Elevates: !MemoryCleanerService.IsRunningAsAdministrator());
        }
#endif

        return CliTaskRunner.BuildUninstallCleanupSteps(
            services.Settings, services.SchoolMode, services.Debloat, systemCleanup, elevatedTask);
    }

    /// <summary>
    /// <c>--cli undo-repair</c>: puts back what the last service or MUX repair replaced, from the record the repair
    /// wrote before it ran. Needs no hardware backend.
    /// </summary>
    private static int RunUndoRepair()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var result = SystemToolsService.UndoLastRepairAsync().GetAwaiter().GetResult();
                (result.Success ? Console.Out : Console.Error).WriteLine(result.Message);
                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                AppLog.Error("The repair could not be undone", ex.ToString());
                return 1;
            }
        }
#endif
        AppLog.Warn("The CLI command is not available on this platform", "undo-repair");
        return 1;
    }

    /// <summary>
    /// Prints the privacy-safe hardware report to standard output. The same text the settings page copies, so a bug
    /// report and a support ticket carry the same facts.
    /// </summary>
    private static int RunHardwareReport()
    {
        try
        {
            var services = AppHost.CreateServices();
            var trust = SampleSensorsForReport(services.Hardware);
            var report = services.Platform?.BuildHardwareReport(
                trust == null ? null : HardwareReportContext.ForSensors(trust));
            if (string.IsNullOrWhiteSpace(report))
            {
                Console.Error.WriteLine(Loc.T("Donanım raporu bu makinede oluşturulamadı."));
                return 1;
            }

            Console.Out.Write(report);
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("The hardware report could not be printed", ex.ToString());
            return 1;
        }
    }

    /// <summary>
    /// Two telemetry samples about 1.6 s apart, read-only, so <c>--hardware-report</c> can print
    /// <c>runtime-verified</c> instead of saying a machine that answers perfectly is unverified.
    /// </summary>
    /// <remarks>
    /// The window is the same one the window uses at runtime, through the same object, so the CLI and the app cannot
    /// disagree about a machine. No write is issued and nothing is elevated: a backend that is not up answers null and
    /// the report says so.
    /// </remarks>
    private static RuntimeSensorVerification? SampleSensorsForReport(IHardwareController? hardware)
    {
        // Null rather than an empty session: "the check did not run because no backend was up" and "the check ran and
        // the machine never answered" are different findings, and the report shows them with different words.
        if (hardware == null) return null;

        var trust = new RuntimeSensorVerification();

        try
        {
            if (!hardware.IsAvailable) return null;

            for (int attempt = 0; attempt < RuntimeSensorVerification.RequiredSamples + 1; attempt++)
            {
                var sample = hardware.GetTelemetryAsync().GetAwaiter().GetResult();
                trust.Observe(sample, DateTime.UtcNow, sample?.IsTemperatureMeasured == true);
                if (trust.RuntimeVerified) break;

                Thread.Sleep(TimeSpan.FromMilliseconds(1600));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("The runtime sensor check could not be run for the report", ex.Message);
        }

        return trust;
    }

    /// <summary>
    /// Runs <c>--mode</c>, <c>--fan</c> or <c>--profile</c>. These do not need the single-instance guard: a running
    /// NitroLite sees the change on its next poll as an external change, the same path a change made in NitroSense
    /// takes.
    /// </summary>
    private static int RunHardwareCommand(string[] args)
    {
        if (!CliCommands.TryParse(args, out var command, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

#if WINDOWS
        // A fixed speed written here is meant to stay after this process exits. The elevated helper hands manual fans
        // back to the firmware when its client disappears (that protects a crashed window), so this client says it is
        // a one-shot before the backend is built.
        WmiHardwareController.LeaveFansOnDisconnect = true;
#endif

        // A running window owns the fans and the mode: its next tick (fan curve, game profile, school mode) can put
        // its own value back over this one. The command still runs, but the user is told why it may not stick.
        if (SingleInstanceGuard.IsAnotherInstanceRunning())
        {
            Console.Error.WriteLine(Loc.T("NitroLite açık; çalışan pencere bu ayarı bir sonraki döngüsünde değiştirebilir."));
        }

        try
        {
            var services = AppHost.CreateServices();
            var hardware = services.Hardware;
            if (hardware == null)
            {
                Console.Error.WriteLine(Loc.T("Donanım arka ucu yok."));
                return 1;
            }

            hardware.EnsureConnectedAsync().GetAwaiter().GetResult();

            // A fixed speed goes through the same thermal floor as the window's sliders, and is resolved before
            // anything is written: a command that is going to be refused must not leave half of itself applied.
            // Without a temperature there is no floor to apply, so the fixed write is refused rather than sent blind.
            int customCpu = 0, customGpu = 0;
            if (command.Fan == FanMode.Custom)
            {
                var sample = hardware.GetMonitorDataAsync().GetAwaiter().GetResult();
                if (!CliCommands.TryResolveFixedFanSpeed(
                        command.FanPercent ?? services.Settings.CustomFanCpuPercent,
                        command.FanPercent ?? services.Settings.CustomFanGpuPercent,
                        sample, out customCpu, out customGpu, out var fanError))
                {
                    Console.Error.WriteLine(fanError);
                    return 1;
                }
            }

            bool ok = true;
            if (command.Mode is OperatingMode mode)
                ok &= hardware.SetOperatingModeAsync(mode).GetAwaiter().GetResult();

            if (command.Fan is FanMode fan)
            {
                ok &= fan == FanMode.Custom
                    ? hardware.SetFanModeAsync(fan, customCpu, customGpu).GetAwaiter().GetResult()
                    : hardware.SetFanModeAsync(fan).GetAwaiter().GetResult();
            }

            if (command.MouseProfile is int profile && services.MouseProfiles != null)
                ok &= services.MouseProfiles.ApplyProfileAsync(profile).GetAwaiter().GetResult();

            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            AppLog.Error("The hardware command could not be run", ex.ToString());
            return 1;
        }
    }
}
