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
        "fix-lighting", "fix-services", "restore-mux", "optimize", "restore-optimize", "uninstall-cleanup"
    ];

    /// <summary>
    /// The spellings the Windows front end accepted before <c>--cli</c> existed, mapped to the command each one runs.
    /// </summary>
    /// <remarks>
    /// A shortcut, a scheduled task or a <c>.bat</c> file written against the old spelling is the reason this table
    /// exists: those files ship in <c>tools/</c> and are documented in the README, so dropping the flags would break
    /// every one of them at once. <c>--optimize-pc</c> maps to <c>optimize</c> because the command was renamed, not
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
    public static bool RequiresConfirmation(string[]? args) =>
        ParseExplicitCommand(args).Length == 0 && ParseLegacyCommand(args).Length > 0;

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
        CancellationToken cancellationToken = default)
    {
        if (!Commands.Contains(command, StringComparer.OrdinalIgnoreCase))
        {
            AppLog.Warn("Bilinmeyen CLI komutu", command);
            return 2;
        }

        if (hardware == null)
        {
            AppLog.Warn("CLI komutu için donanım arka ucu yok", command);
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
                "uninstall-cleanup" => await UninstallCleanupAsync(hardware, settings, autoStart, cancellationToken)
                    .ConfigureAwait(false) ? 0 : 1,
                _ => 2
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("CLI komutu başarısız", $"{command}: {ex}");
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
    /// What an uninstall leaves behind: fans back under firmware control, and no sign-in entry pointing at a program
    /// that is about to be deleted.
    /// </summary>
    private static async Task<bool> UninstallCleanupAsync(
        IHardwareController hardware, AppSettings settings, IAutoStartService? autoStart, CancellationToken cancellationToken)
    {
        bool fan = await hardware.SetFanModeAsync(FanMode.Auto).ConfigureAwait(false);

        settings.FanCurveEnabled = false;
        settings.SchoolModeEnabled = false;

        if (autoStart != null)
        {
            try { await autoStart.DisableAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Otomatik başlatma kaldırılamadı", ex.Message); }
        }

        return fan;
    }
}
