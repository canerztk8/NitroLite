using Avalonia;
using NitroLite.Core;

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
        if (mode.IsWmiWorker)
        {
            return WmiWorkerHost.Run(mode);
        }
#endif

        // The one-shot commands. Each of these talks to the hardware and exits without a window, so a script or a
        // shortcut can repair a machine without a user in front of it. Both spellings arrive here: --cli <command>
        // and the legacy --fix-lighting / --optimize-pc flags the shipped .bat files still use.
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
        if (string.IsNullOrWhiteSpace(command))
        {
            Console.Error.WriteLine($"--cli: {string.Join(", ", CliTaskRunner.Commands)}");
            return 2;
        }

        if (CliTaskRunner.RequiresConfirmation(args) && !CliTaskRunner.IsConfirmed(args))
        {
            var flag = CliTaskRunner.ParseLegacyFlag(args);
            Console.Error.WriteLine(Loc.T("Bu komut sistem ayarlarını değiştirir. Onaylamak için --yes ekleyin:"));
            Console.Error.WriteLine($"  NitroLite.exe {flag} --yes");
            return 3;
        }

        try
        {
            var services = AppHost.CreateServices();
            return CliTaskRunner.RunAsync(command, services.Hardware, services.Settings, services.AutoStart)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLog.Error("The CLI command could not be run", $"{command}: {ex}");
            return 1;
        }
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
            var report = services.Platform?.BuildHardwareReport();
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

            bool ok = true;
            if (command.Mode is OperatingMode mode)
                ok &= hardware.SetOperatingModeAsync(mode).GetAwaiter().GetResult();

            if (command.Fan is FanMode fan)
            {
                ok &= command.FanPercent is int percent
                    ? hardware.SetFanModeAsync(fan, percent, percent).GetAwaiter().GetResult()
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
