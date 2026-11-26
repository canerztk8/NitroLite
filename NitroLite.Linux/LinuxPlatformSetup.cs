using System.Globalization;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// The one-time Linux setup, behind one <c>pkexec</c> password prompt: the udev rule and the <c>nitrolite</c> group, and
/// — only when the user asks — the <c>acer_wmi predator_v4</c> option (and its undo).
/// </summary>
/// <remarks>
/// <para>
/// The script is a constant in this file, passed to <c>/bin/sh -c</c> with the user name and the rule path as
/// positional arguments, so nothing the user or a file supplies is ever spliced into shell text. <c>pkexec</c> shows
/// the desktop's own authentication dialog; the app never sees the password.
/// </para>
/// <para>
/// Group membership is read by the session at login, so the result always says to log out and back in. The udev
/// trigger applies the seat ACLs right away, which is what makes most controls work before that.
/// </para>
/// <para>
/// The user is no longer added to the <c>input</c> group. That group reads every keyboard and mouse on the machine, from
/// every process of the user, which is a keylogger's whole wish list; the Nitro key needs one device, and the udev rule
/// now hands exactly that one (<c>Acer WMI hotkeys</c>) to the user at the seat. A membership an older version added is
/// left in place — removing a group from a user is not something to do behind their back — and can be dropped with
/// <c>sudo gpasswd -d "$USER" input</c>.
/// </para>
/// </remarks>
public sealed class LinuxPlatformSetup : IPlatformSetup
{
    /// <summary>
    /// The grant script. <c>$1</c> is the user, <c>$2</c> the rule file, <c>$3</c> the SHA-256 the app computed for
    /// that file's content just before the prompt. The rule file lives in the user's data folder, which any process of
    /// that user can write during the password prompt, so root never installs it from there directly: it copies the
    /// file into a root-only temporary directory, checks the copy against the digest, and installs the verified copy
    /// (the same scheme as the update installer). Every step is idempotent, so pressing the button twice is harmless.
    /// </summary>
    public const string GrantScript =
        "set -e\n" +
        "user=\"$1\"; rule=\"$2\"; sha=\"$3\"\n" +
        "[ -n \"$user\" ] && [ -f \"$rule\" ] || exit 2\n" +
        "case \"$sha\" in ''|*[!0-9a-f]*) exit 3 ;; esac\n" +
        "[ \"${#sha}\" -eq 64 ] || exit 3\n" +
        "dir=\"$(mktemp -d /var/tmp/nitrolite-setup.XXXXXXXX)\"\n" +
        "trap 'rm -rf -- \"$dir\"' EXIT\n" +
        "cp -- \"$rule\" \"$dir/rule\"\n" +
        "echo \"$sha  $dir/rule\" | sha256sum -c --status - || exit 4\n" +
        "groupadd -f nitrolite\n" +
        "usermod -aG nitrolite \"$user\"\n" +
        "install -m 644 \"$dir/rule\" /etc/udev/rules.d/99-nitrolite.rules\n" +
        "udevadm control --reload-rules\n" +
        "udevadm trigger\n" +
        "if [ -e /sys/firmware/acpi/platform_profile ]; then\n" +
        "  chgrp nitrolite /sys/firmware/acpi/platform_profile && chmod 0664 /sys/firmware/acpi/platform_profile || true\n" +
        "fi\n" +
        "for f in /sys/devices/system/cpu/cpufreq/boost /sys/devices/system/cpu/intel_pstate/no_turbo; do\n" +
        "  if [ -e \"$f\" ]; then chgrp nitrolite \"$f\" && chmod 0664 \"$f\" || true; fi\n" +
        "done\n";

    /// <summary>The modprobe option file the predator_v4 step writes.</summary>
    public const string PredatorV4ConfPath = "/etc/modprobe.d/nitrolite-acer.conf";

    /// <summary>What the predator_v4 scripts print when they reloaded <c>acer_wmi</c> and it came back.</summary>
    public const string LiveMarker = "nitrolite:live";

    /// <summary>Exit code of <see cref="PredatorV4Script"/> when the reload failed and the machine was put back as it was.</summary>
    public const int ReloadFailedExitCode = 10;

    /// <summary>Exit code when even loading <c>acer_wmi</c> back failed: the machine has no driver until it restarts.</summary>
    public const int DriverMissingExitCode = 11;

    private const string PinnedPath = "PATH=/usr/sbin:/usr/bin:/sbin:/bin\nexport PATH\n";

    /// <summary>
    /// Writes the option file, then tries to apply it at once by reloading <c>acer_wmi</c>. A module in use stays as it
    /// is (the file waits for the next boot). If the reload itself fails, the file is deleted and the module is loaded
    /// again without it. If even that fails the script says so (11) rather than claiming a restore. No arguments.
    /// </summary>
    public const string PredatorV4Script =
        "set -e\n" +
        PinnedPath +
        "printf 'options acer_wmi predator_v4=1\\n' > " + PredatorV4ConfPath + "\n" +
        "[ -d /sys/module/acer_wmi ] || exit 0\n" +
        "modprobe -r acer_wmi || exit 0\n" +
        "if modprobe acer_wmi; then udevadm settle --timeout=5 2>/dev/null || true; echo " + LiveMarker + "; exit 0; fi\n" +
        "rm -f -- " + PredatorV4ConfPath + "\n" +
        "modprobe acer_wmi || exit 11\n" + // = DriverMissingExitCode
        "udevadm settle --timeout=5 2>/dev/null || true; echo " + LiveMarker + "\n" +
        "exit 10\n"; // = ReloadFailedExitCode

    /// <summary>
    /// The undo of <see cref="PredatorV4Script"/>: removes the option file, then reloads <c>acer_wmi</c> without it when it
    /// can. A reload that fails is retried once; if the driver still will not load the script exits 11.
    /// </summary>
    public const string DisablePredatorV4Script =
        "set -e\n" +
        PinnedPath +
        "rm -f -- " + PredatorV4ConfPath + "\n" +
        "[ -d /sys/module/acer_wmi ] || exit 0\n" +
        "modprobe -r acer_wmi || exit 0\n" +
        "if modprobe acer_wmi; then udevadm settle --timeout=5 2>/dev/null || true; echo " + LiveMarker + "; exit 0; fi\n" +
        "modprobe acer_wmi || exit 11\n" + // = DriverMissingExitCode
        "udevadm settle --timeout=5 2>/dev/null || true; echo " + LiveMarker + "\n";

    private const string PredatorV4ParameterPath = "/sys/module/acer_wmi/parameters/predator_v4";

    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(3);

    private readonly ICommandRunner _runner;
    private readonly ISysfsFileSystem _fs;
    private readonly Func<string?> _rulePath;
    private readonly Func<string?>? _ruleDigest;
    private readonly string _user;
    private readonly string? _pkexec;
    private readonly KernelSetupHint _hint;
    private readonly Func<string, bool> _canReadDevice;

    private static readonly Func<string, bool> RealCanRead = CanOpenForReading;

    /// <param name="canReadDevice">
    /// Whether this process may open a <c>/dev/input/eventN</c> node for reading. Defaults to really opening it (read
    /// only, closed at once) on Linux and to "yes" elsewhere, so a capture replayed on Windows never asks for a fix.
    /// </param>
    public LinuxPlatformSetup(
        LinuxCapabilities capabilities,
        Func<string?> rulePath,
        ICommandRunner? runner = null,
        ISysfsFileSystem? fs = null,
        string? user = null,
        Func<string?>? ruleDigest = null,
        Func<string, bool>? canReadDevice = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _ruleDigest = ruleDigest;
        _rulePath = rulePath ?? throw new ArgumentNullException(nameof(rulePath));
        _runner = runner ?? ProcessCommandRunner.Instance;
        _fs = fs ?? RealSysfsFileSystem.Instance;
        _user = string.IsNullOrWhiteSpace(user) ? Environment.UserName : user;
        _pkexec = _runner.Find("pkexec");
        _hint = capabilities.KernelHint;
        ModelNote = capabilities.ModelNote;
        _canReadDevice = canReadDevice ?? (OperatingSystem.IsLinux() ? RealCanRead : static _ => true);
    }

    public string? ModelNote { get; }

    public Task<string?> WriteCaptureAsync(string directory) => Task.Run(() =>
    {
        try
        {
            var path = new LinuxCaptureWriter(_fs, _runner).WriteTo(directory);
            AppLog.Info("A Linux capture was written", Path.GetFileName(path));
            return (string?)path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("The Linux capture could not be written", ex.Message);
            return null;
        }
    });

    public string CaptureSummary
    {
        get
        {
            var os = LinuxDiagnosticsService.OsName(_fs.ReadAllText("/etc/os-release"));
            var kernel = _fs.ReadAllText("/proc/sys/kernel/osrelease")?.Trim() ?? "unknown";
            return $"- Distribution: {os}\n- Kernel: {kernel}\n- Suggested kernel step: {_hint}";
        }
    }

    public bool CanElevate => _pkexec != null && _runner.Find("sh") != null;

    /// <summary>
    /// True when the Nitro key's input device exists and this process cannot read it: the one thing the setup still has
    /// to fix for the key. The name is the interface's. It used to mean "not in the input group", which the setup no
    /// longer adds anyone to, so a user outside that group would have been offered the button for ever.
    /// </summary>
    public bool NeedsInputGroup => HotkeyEventNode() is { } node && !_canReadDevice(node);

    /// <summary>
    /// <c>/dev/input/eventN</c> of the <c>Acer WMI hotkeys</c> device, found through <c>/sys/class/input</c> (which
    /// anyone may read), or null when the kernel registered no such device.
    /// </summary>
    internal string? HotkeyEventNode()
    {
        foreach (var directory in _fs.EnumerateDirectories(InputClassRoot))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith("event", StringComparison.Ordinal)) continue;

            var deviceName = _fs.ReadAllText(directory + "/device/name")?.Trim();
            if (string.Equals(deviceName, LinuxNitroKeyListener.AcerDeviceName, StringComparison.Ordinal))
            {
                return "/dev/input/" + name;
            }
        }

        return null;
    }

    internal const string InputClassRoot = "/sys/class/input";

    private static bool CanOpenForReading(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when the <c>predator_v4</c> option file NitroLite writes is in place, so the undo can be offered.</summary>
    public bool PredatorV4OptionInstalled => _fs.FileExists(PredatorV4ConfPath);

    public KernelSetupHint KernelHint => _hint;

    public Task<PlatformSetupResult> GrantAccessAsync() => Task.Run(() =>
    {
        var rule = _rulePath();
        if (rule == null)
        {
            return new PlatformSetupResult(false, Loc.T("udev kuralı dosyası hazırlanamadı."));
        }

        // The digest comes from the trusted embedded rule when a provider is given, so a file swapped in the user's data
        // folder at any time fails root's check. Without a provider it is hashed from the file as a fallback. A missing
        // or empty digest is refused by the script (exit 3), so nothing is installed.
        var digest = _ruleDigest != null ? (_ruleDigest() ?? string.Empty).ToLowerInvariant() : RuleDigest(rule);
        return Run(GrantScript, new[] { _user, rule, digest },
            Loc.T("İzinler verildi. Değişikliklerin tamamen geçerli olması için oturumu kapatıp yeniden açın."));
    });

    /// <summary>Lower-case hex SHA-256 of the rule file's bytes, or an empty string when it cannot be read.</summary>
    internal static string RuleDigest(string path)
    {
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public Task<PlatformSetupResult> EnablePredatorV4Async() => Task.Run(() =>
        RunPredatorV4(PredatorV4Script, expectOn: true,
            Loc.T("predator_v4 açıldı ve hemen uygulandı; yeniden başlatma gerekmez."),
            Loc.T("predator_v4 açıldı. Etkili olması için bilgisayarı yeniden başlatın."),
            new Dictionary<int, string>
            {
                [ReloadFailedExitCode] = Loc.T("predator_v4 açılamadı: acer-wmi modülü yeniden yüklenemedi, her şey eski haline döndürüldü."),
                [DriverMissingExitCode] = Loc.T("acer-wmi sürücüsü yeniden yüklenemedi; bilgisayarı yeniden başlatın.")
            }));

    /// <summary>
    /// Takes the <c>predator_v4</c> option back out: removes <see cref="PredatorV4ConfPath"/> and reloads the module
    /// without it when it can, behind the same password prompt.
    /// </summary>
    public Task<PlatformSetupResult> DisablePredatorV4Async() => Task.Run(() =>
        RunPredatorV4(DisablePredatorV4Script, expectOn: false,
            Loc.T("predator_v4 kapatıldı ve hemen uygulandı; yeniden başlatma gerekmez."),
            Loc.T("predator_v4 kapatıldı. Etkili olması için bilgisayarı yeniden başlatın."),
            new Dictionary<int, string>
            {
                [DriverMissingExitCode] = Loc.T("acer-wmi sürücüsü yeniden yüklenemedi; bilgisayarı yeniden başlatın.")
            }));

    /// <summary>
    /// Runs one of the two predator_v4 scripts. <see cref="PlatformSetupResult.Live"/> says the module was touched in
    /// the running system: the script printed its marker after a reload (also on the exit-10 path, where the module came
    /// back without the option) or it ended with the driver gone (11). Either way the old input device is replaced or
    /// missing, so the caller restarts the Nitro key listener and rescans, whether the step counts as a success or not.
    /// The "no restart needed" sentence is stricter: it also needs the module's own parameter to read as intended.
    /// </summary>
    private PlatformSetupResult RunPredatorV4(string script, bool expectOn, string appliedMessage, string restartMessage,
        IReadOnlyDictionary<int, string> exitMessages)
    {
        var moduleTouched = false;
        var result = Run(script, Array.Empty<string>(),
            output => Applied(output, expectOn) ? appliedMessage : restartMessage,
            exitMessages,
            output => moduleTouched = output.Output.Contains(LiveMarker, StringComparison.Ordinal)
                                      || output.ExitCode == DriverMissingExitCode);
        return result with { Live = moduleTouched };
    }

    /// <summary>
    /// True when the script said it reloaded the module. For the enable step the module's own parameter must also read
    /// on, so a driver that came back without the option is never reported as applied.
    /// </summary>
    private bool Applied(CommandResult result, bool expectOn)
    {
        if (!result.Output.Contains(LiveMarker, StringComparison.Ordinal)) return false;

        var value = _fs.ReadAllText(PredatorV4ParameterPath)?.Trim();
        var on = value is "Y" or "y" or "1";
        return on == expectOn;
    }

    private PlatformSetupResult Run(string script, IReadOnlyList<string> arguments, string success) =>
        Run(script, arguments, _ => success);

    private PlatformSetupResult Run(string script, IReadOnlyList<string> arguments, Func<CommandResult, string> success,
        IReadOnlyDictionary<int, string>? exitMessages = null, Action<CommandResult>? observe = null)
    {
        if (!CanElevate)
        {
            return new PlatformSetupResult(false, Loc.T("pkexec bulunamadı; aşağıdaki komutu terminalde çalıştırın."));
        }

        // pkexec /bin/sh -c SCRIPT nitrolite-setup ARG...: the name after the script is $0, the rest are $1...
        var args = new List<string> { "/bin/sh", "-c", script, "nitrolite-setup" };
        args.AddRange(arguments);

        var result = _runner.Run(_pkexec!, args, PromptTimeout);
        observe?.Invoke(result);
        if (result.Ok)
        {
            AppLog.Info("The Linux setup step completed",
                script == GrantScript ? "access" : script == DisablePredatorV4Script ? "predator_v4 off" : "predator_v4");
            return new PlatformSetupResult(true, success(result));
        }

        AppLog.Warn("The Linux setup step failed",
            $"exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}: {result.Error}");

        if (exitMessages != null && exitMessages.TryGetValue(result.ExitCode, out var specific))
        {
            return new PlatformSetupResult(false, specific);
        }

        return new PlatformSetupResult(false, result.ExitCode switch
        {
            126 => Loc.T("Şifre penceresi kapatıldı; hiçbir şey değişmedi."),
            127 => Loc.T("Yetki verilmedi ya da şifre penceresi açılamadı; aşağıdaki komutu terminalde çalıştırın."),
            _ => Loc.T("Kurulum tamamlanamadı; aşağıdaki komutu terminalde çalıştırın.")
        });
    }

    /// <summary>The numeric id of <paramref name="name"/> in an <c>/etc/group</c> file, or null.</summary>
    public static int? GroupId(string? groupFile, string name)
    {
        if (string.IsNullOrEmpty(groupFile)) return null;

        foreach (var raw in groupFile.Split('\n'))
        {
            var fields = raw.Trim().Split(':');
            if (fields.Length < 3 || fields[0] != name) continue;
            return int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gid) ? gid : null;
        }

        return null;
    }

    /// <summary>The ids on the <c>Groups:</c> line of <c>/proc/self/status</c>.</summary>
    public static IReadOnlySet<int> ProcessGroups(string? status)
    {
        var groups = new HashSet<int>();
        if (string.IsNullOrEmpty(status)) return groups;

        foreach (var raw in status.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Groups:", StringComparison.Ordinal)) continue;

            foreach (var token in line["Groups:".Length..].Split(' ', '\t'))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gid)) groups.Add(gid);
            }
        }

        return groups;
    }
}
