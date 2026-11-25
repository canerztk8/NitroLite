using NitroLite.Core;
using NitroLite.Core.Hardware;

namespace NitroLite.Desktop;

/// <summary>
/// The one-shot repair commands a script or a shortcut can run without opening the window.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the failures they fix are the ones a user cannot fix from the window: a keyboard that stayed
/// dark after a resume, a MUX left on the discrete GPU after a driver update, a machine left in Turbo by a crash. Each
/// one is a single hardware write, so the whole command is a few lines and the exit code is the answer.
/// </para>
/// <para>
/// The runner takes the controller rather than building one, so a test can drive every command against a fake and the
/// composition root stays the only place that decides which backend exists.
/// </para>
/// </remarks>
public static class CliTaskRunner
{
    /// <summary>The commands the <c>--cli</c> flag accepts.</summary>
    public static readonly string[] Commands =
    [
        "fix-lighting", "fix-services", "restore-mux", "optimize", "restore-optimize", "uninstall-cleanup",
        "undo-repair"
    ];

    /// <summary>
    /// The spellings the Windows front end accepted before <c>--cli</c> existed, mapped to the command each one runs.
    /// </summary>
    /// <remarks>
    /// A shortcut, a scheduled task or a <c>.bat</c> file a user wrote against the old spelling is the reason this
    /// table exists: dropping the flags would break every one of them at once. <c>--optimize-pc</c> maps to <c>optimize</c> because the command was renamed, not
    /// because the behaviour changed.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> LegacyFlags =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--fix-lighting"] = "fix-lighting",
            ["--fix-services"] = "fix-services",
            ["--restore-mux"] = "restore-mux",
            ["--optimize-pc"] = "optimize",
            ["--restore-optimize"] = "restore-optimize",
            ["--uninstall-cleanup"] = "uninstall-cleanup"
        };

    /// <summary>The flags that answer the confirmation a state-changing command asks for.</summary>
    public static readonly string[] ConfirmationFlags = ["--yes", "-y", "--force"];

    /// <summary>
    /// The <c>--cli</c> commands that ask for <c>--yes</c> even in the explicit spelling. <c>restore-mux</c> writes the
    /// GPU switch, a change that leaves the machine on the other GPU after the next restart, so it asks in both
    /// spellings, as the older <c>--restore-mux</c> always did.
    /// </summary>
    public static readonly string[] AlwaysConfirmedCommands = ["restore-mux", "undo-repair"];

    /// <summary>
    /// Every flag any part of the program reads. A flag outside this list is a typo or a flag from another build; it is
    /// reported on stderr instead of being ignored, because an ignored typo opens the window when a command was meant.
    /// </summary>
    public static readonly string[] KnownFlags =
    [
        "--cli", "--autostart", "--minimized", "--restart", "--hardware-report", "--linux-capture",
        "--linux-helper", "--linux-selftest", "--wmi-worker", "--pipe", "--parent-pid", "--pssvc-sensors",
        "--agent-capabilities",
        "--mode", "--fan", "--profile", "--yes", "-y", "--force", "--socket"
    ];

    /// <summary>The flags on the line that nothing reads. Values (anything not starting with '-') are not flags.</summary>
    public static IReadOnlyList<string> FindUnknownFlags(string[]? args)
    {
        var unknown = new List<string>();
        if (args == null) return unknown;

        foreach (var raw in args)
        {
            var arg = raw?.Trim();
            if (string.IsNullOrEmpty(arg) || !arg.StartsWith('-')) continue;

            // "--pipe=name" is read by its name.
            var name = arg.Split('=', 2)[0];
            if (KnownFlags.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (LegacyFlags.ContainsKey(name)) continue;
            unknown.Add(arg);
        }

        return unknown;
    }

    /// <summary>True when <paramref name="command"/> is one of <see cref="Commands"/>.</summary>
    public static bool IsKnownCommand(string? command) =>
        !string.IsNullOrWhiteSpace(command) && Commands.Contains(command.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the command line asks for a one-shot task rather than the window.</summary>
    public static bool IsCliTask(string[]? args) =>
        args != null && args.Any(a => !string.IsNullOrWhiteSpace(a) &&
                                      (a.Trim().Equals("--cli", StringComparison.OrdinalIgnoreCase) ||
                                       LegacyFlags.ContainsKey(a.Trim())));

    /// <summary>
    /// The command the line asks for, from either spelling. Empty when nothing was named.
    /// </summary>
    /// <remarks>
    /// <c>--cli</c> wins over a legacy flag on the same line: an explicit command is never overridden by a spelling
    /// that happens to sit beside it.
    /// </remarks>
    public static string ParseCommand(string[]? args)
    {
        var explicitCommand = ParseExplicitCommand(args);
        return explicitCommand.Length > 0 ? explicitCommand : ParseLegacyCommand(args);
    }

    /// <summary>
    /// The legacy flag the line carries, or empty when the command came from <c>--cli</c>. Used to echo the exact
    /// spelling back in the confirmation message, so the user can copy the line that will work.
    /// </summary>
    public static string ParseLegacyFlag(string[]? args)
    {
        if (args == null) return "";

        foreach (var arg in args)
        {
            var trimmed = arg?.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (LegacyFlags.ContainsKey(trimmed)) return trimmed.ToLowerInvariant();
        }

        return "";
    }

    /// <summary>True when the line carries <c>--yes</c>, <c>-y</c> or <c>--force</c>.</summary>
    public static bool IsConfirmed(string[]? args) =>
        args != null && args.Any(a => !string.IsNullOrWhiteSpace(a) &&
                                      ConfirmationFlags.Contains(a.Trim(), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// True when the command came from a legacy flag, which is the form that asks for confirmation before it runs.
    /// </summary>
    /// <remarks>
    /// The split is deliberate. <c>--cli fix-lighting</c> is a command someone typed on purpose and keeps working
    /// without a second flag; <c>--fix-lighting</c> is the spelling a stray shortcut can carry, so it asks first —
    /// exactly the contract the Windows front end had.
    /// </remarks>
    public static bool RequiresConfirmation(string[]? args)
    {
        var explicitCommand = ParseExplicitCommand(args);
        if (explicitCommand.Length > 0)
            return AlwaysConfirmedCommands.Contains(explicitCommand, StringComparer.OrdinalIgnoreCase);

        return ParseLegacyCommand(args).Length > 0;
    }

    /// <summary>The command named after <c>--cli</c>, or empty when the flag was the last thing on the line.</summary>
    private static string ParseExplicitCommand(string[]? args)
    {
        if (args == null) return "";

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i]?.Trim();
            if (string.IsNullOrEmpty(arg)) continue;
            if (!arg.Equals("--cli", StringComparison.OrdinalIgnoreCase)) continue;

            if (i + 1 >= args.Length) return "";
            var next = args[i + 1]?.Trim() ?? "";
            return next.StartsWith("--", StringComparison.Ordinal) ? "" : next.ToLowerInvariant();
        }

        return "";
    }

    /// <summary>The command a legacy flag names, or empty when the line carries none.</summary>
    private static string ParseLegacyCommand(string[]? args)
    {
        var flag = ParseLegacyFlag(args);
        return flag.Length > 0 && LegacyFlags.TryGetValue(flag, out var command) ? command : "";
    }

    /// <summary>
    /// Runs one command. Returns the process exit code: 0 for success, 1 for a refused write, 2 for an unknown command.
    /// </summary>
    public static async Task<int> RunAsync(
        string command,
        IHardwareController? hardware,
        AppSettings settings,
        IAutoStartService? autoStart = null,
        IReadOnlyList<CliCleanupStep>? cleanupSteps = null,
        TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        if (!Commands.Contains(command, StringComparer.OrdinalIgnoreCase))
        {
            AppLog.Warn("Unknown CLI command", command);
            return 2;
        }

        // The undo steps need no Acer backend: only the last one, the fans, does. They run even when the backend is
        // missing or does not connect, so an uninstall on a machine whose Acer stack is already gone still cleans up.
        if (command.Equals("uninstall-cleanup", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await UninstallCleanupAsync(hardware, settings, autoStart, cleanupSteps, output, cancellationToken)
                    .ConfigureAwait(false) ? 0 : 1;
            }
            catch (Exception ex)
            {
                AppLog.Error("CLI command failed", $"{command}: {ex}");
                return 1;
            }
        }

        // undo-repair is run by the entry point, which can see the platform's repair tools; this runner cannot.
        if (command.Equals("undo-repair", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warn("The CLI command is not available here", command);
            return 1;
        }

        if (hardware == null)
        {
            AppLog.Warn("No hardware backend for the CLI command", command);
            return 1;
        }

        try
        {
            await hardware.EnsureConnectedAsync().ConfigureAwait(false);

            return command.ToLowerInvariant() switch
            {
                "fix-lighting" => await FixLightingAsync(hardware, settings).ConfigureAwait(false) ? 0 : 1,
                "fix-services" => await FixServicesAsync(hardware).ConfigureAwait(false) ? 0 : 1,
                "restore-mux" => await hardware.SetGpuModeAsync(GpuOperatingMode.Optimus).ConfigureAwait(false) ? 0 : 1,
                "optimize" => await OptimizeAsync(hardware, settings).ConfigureAwait(false) ? 0 : 1,
                "restore-optimize" => await RestoreOptimizeAsync(hardware, settings).ConfigureAwait(false) ? 0 : 1,
                _ => 2
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("CLI command failed", $"{command}: {ex}");
            return 1;
        }
    }

    /// <summary>Re-applies the stored keyboard lighting. The fix for a keyboard that stayed dark after a resume.</summary>
    private static async Task<bool> FixLightingAsync(IHardwareController hardware, AppSettings settings)
    {
        // The user turned the lights off: "fix" means put the off state back, not the last lit profile.
        if (settings.IsBacklightOff) return await hardware.TurnOffLightingAsync().ConfigureAwait(false);

        var last = settings.LastActiveLighting ?? settings.KeyboardLighting;
        if (last == null) return false;

        return await hardware.SetLightingAsync(
            last.Effect,
            last.Brightness,
            last.Speed,
            last.Direction,
            last.Color,
            last.LEDs.ToArray()).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-opens the hardware transport. On Windows that is the Acer service or the elevated WMI worker; on Linux it is
    /// the sysfs nodes, which need no service at all, so the reconnect is the whole repair.
    /// </summary>
    private static async Task<bool> FixServicesAsync(IHardwareController hardware) =>
        await hardware.EnsureConnectedAsync().ConfigureAwait(false);

    /// <summary>Turbo and maximum fans: the one-click "give me everything" the WPF window offered.</summary>
    private static async Task<bool> OptimizeAsync(IHardwareController hardware, AppSettings settings)
    {
        // The mode the machine was in is remembered first, so restore-optimize has something to go back to.
        var current = await hardware.GetOperatingModeAsync().ConfigureAwait(false);
        if (current is OperatingMode previous) settings.LastOperatingMode = previous;

        bool mode = await hardware.SetOperatingModeAsync(OperatingMode.Turbo).ConfigureAwait(false);
        bool fan = await hardware.SetFanModeAsync(FanMode.Max).ConfigureAwait(false);
        return mode && fan;
    }

    /// <summary>Puts back the mode the machine was in before <c>optimize</c>, and hands the fans to the firmware.</summary>
    private static async Task<bool> RestoreOptimizeAsync(IHardwareController hardware, AppSettings settings)
    {
        bool mode = await hardware.SetOperatingModeAsync(settings.LastOperatingMode ?? OperatingMode.Default)
            .ConfigureAwait(false);
        bool fan = await hardware.SetFanModeAsync(FanMode.Auto).ConfigureAwait(false);
        return mode && fan;
    }

    /// <summary>
    /// What an uninstall leaves behind: every system change the app made undone, fans back under firmware control,
    /// and no sign-in entry pointing at a program that is about to be deleted.
    /// </summary>
    /// <remarks>
    /// <paramref name="steps"/> are the platform's undo actions (school mode, PC Optimize, the Run value, the Acer
    /// services, the NitroSense launcher, the administrator task), run in order, each reported on its own line. One
    /// that fails does not stop the rest. Without steps (a platform that has none, or a test) only the sign-in entry
    /// is removed through <paramref name="autoStart"/>, as before. The fans go back to the firmware last, after school
    /// mode has put its own fan value back.
    /// </remarks>
    private static async Task<bool> UninstallCleanupAsync(
        IHardwareController? hardware, AppSettings settings, IAutoStartService? autoStart,
        IReadOnlyList<CliCleanupStep>? steps, TextWriter? output, CancellationToken cancellationToken)
    {
        bool ok = true;

        // Said up front, so a series of UAC prompts is expected rather than alarming. "At most": a step whose state is
        // already right (no task registered, for instance) returns without asking.
        int prompts = steps?.Count(s => s.Elevates) ?? 0;
        if (prompts > 0 && output != null)
            output.WriteLine(Loc.T($"Bu işlem en fazla {prompts} kez yönetici izni (UAC) isteyebilir."));

        if (steps != null)
        {
            foreach (var step in steps)
            {
                bool stepOk;
                string? detail;
                try
                {
                    (stepOk, detail) = await step.Run(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    (stepOk, detail) = (false, ex.Message);
                }

                ok &= stepOk;
                Report(output, step.Label, stepOk, detail);
                if (!stepOk) AppLog.Warn("An uninstall cleanup step failed", $"{step.Label}: {detail}");
            }
        }

        bool fan = false;
        string? fanDetail = null;
        if (hardware == null)
        {
            fanDetail = Loc.T("Donanım arka ucu yok.");
        }
        else
        {
            try
            {
                await hardware.EnsureConnectedAsync().ConfigureAwait(false);
                fan = await hardware.SetFanModeAsync(FanMode.Auto).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                fanDetail = ex.Message;
            }
        }

        Report(output, Loc.T("Fan"), fan, fanDetail);

        settings.FanCurveEnabled = false;
        settings.SchoolModeEnabled = false;

        if (steps == null && autoStart != null)
        {
            try { await autoStart.DisableAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Autostart could not be removed", ex.Message); }
        }

        return ok && fan;
    }

    /// <summary>
    /// The undo actions of <c>--cli uninstall-cleanup</c>, in the order they run: school mode, PC Optimize and the
    /// Run value (<paramref name="systemCleanup"/>), the Acer services, the NitroSense launcher, the administrator task
    /// (<paramref name="elevatedTask"/>). The fans go back to the firmware after these. Each Acer step is only added
    /// when there is something to undo, so a machine that never used a feature is not asked for administrator rights
    /// on its behalf. The two platform steps are built by the entry point, which can see the platform's types.
    /// </summary>
    public static List<CliCleanupStep> BuildUninstallCleanupSteps(
        AppSettings settings,
        SchoolModeService? schoolMode,
        IDebloatService? debloat,
        CliCleanupStep? systemCleanup,
        CliCleanupStep? elevatedTask)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var steps = new List<CliCleanupStep>();

        if (schoolMode is { } school &&
            (settings.SchoolModeEnabled || (school.BackupPath is { } backup && File.Exists(backup))))
        {
            steps.Add(new CliCleanupStep(Loc.T("Okul modu"), async _ =>
            {
                var outcome = await school.DisableAsync().ConfigureAwait(false);
                return (outcome.Success, outcome.Success ? null : string.Join(", ", outcome.Failed));
            }));
        }

        if (systemCleanup != null) steps.Add(systemCleanup);

        if (debloat is { IsSupported: true })
        {
            if (debloat.HasRestorableBackup)
            {
                steps.Add(new CliCleanupStep(Loc.T("Acer arka plan servisleri"), async ct =>
                {
                    var result = await debloat.RestoreServicesAsync(ct).ConfigureAwait(false);
                    return (result.Success, result.Error);
                }, Elevates: true));
            }

            if (debloat.IsNitroSenseLauncherBlocked)
            {
                steps.Add(new CliCleanupStep(Loc.T("NitroSense başlatıcısı"), async ct =>
                {
                    var result = await debloat.UnblockNitroSenseLauncherAsync(ct).ConfigureAwait(false);
                    return (result.Success, result.Error);
                }, Elevates: true));
            }
        }

        if (elevatedTask != null) steps.Add(elevatedTask);

        return steps;
    }

    private static void Report(TextWriter? output, string label, bool ok, string? detail)
    {
        if (output == null) return;
        var line = (ok ? "  ok    " : "  FAIL  ") + label;
        if (!string.IsNullOrWhiteSpace(detail)) line += ": " + detail.Trim();
        output.WriteLine(line);
    }
}

/// <summary>
/// One undo action of <c>--cli uninstall-cleanup</c>: a label the user reads and the action, which answers whether it
/// worked and, optionally, a sentence about it. Built by the entry point, which is the only place that can see the
/// platform's services.
/// </summary>
/// <param name="Elevates">True when the step may raise a UAC prompt; counted for the notice printed first.</param>
public sealed record CliCleanupStep(
    string Label, Func<CancellationToken, Task<(bool Ok, string? Detail)>> Run, bool Elevates = false);
