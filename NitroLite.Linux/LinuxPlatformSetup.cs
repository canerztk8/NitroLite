using System.Globalization;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// The one-time Linux setup, behind one <c>pkexec</c> password prompt: the udev rule, the <c>nitrolite</c> and
/// <c>input</c> groups, and — only when the user asks — the <c>acer_wmi predator_v4</c> option.
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
        "getent group input >/dev/null || groupadd -f -r input\n" +
        "usermod -aG nitrolite,input \"$user\"\n" +
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

    public const string PredatorV4Script =
        "set -e\n" +
        "printf 'options acer_wmi predator_v4=1\\n' > " + PredatorV4ConfPath + "\n";

    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(3);

    private readonly ICommandRunner _runner;
    private readonly ISysfsFileSystem _fs;
    private readonly Func<string?> _rulePath;
    private readonly Func<string?>? _ruleDigest;
    private readonly string _user;
    private readonly string? _pkexec;
    private readonly KernelSetupHint _hint;

    public LinuxPlatformSetup(
        LinuxCapabilities capabilities,
        Func<string?> rulePath,
        ICommandRunner? runner = null,
        ISysfsFileSystem? fs = null,
        string? user = null,
        Func<string?>? ruleDigest = null)
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

    public bool NeedsInputGroup => !IsInGroup("input");

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
        Run(PredatorV4Script, Array.Empty<string>(),
            Loc.T("predator_v4 açıldı. Etkili olması için bilgisayarı yeniden başlatın.")));

    private PlatformSetupResult Run(string script, IReadOnlyList<string> arguments, string success)
    {
        if (!CanElevate)
        {
            return new PlatformSetupResult(false, Loc.T("pkexec bulunamadı; aşağıdaki komutu terminalde çalıştırın."));
        }

        // pkexec /bin/sh -c SCRIPT nitrolite-setup ARG...: the name after the script is $0, the rest are $1...
        var args = new List<string> { "/bin/sh", "-c", script, "nitrolite-setup" };
        args.AddRange(arguments);

        var result = _runner.Run(_pkexec!, args, PromptTimeout);
        if (result.Ok)
        {
            AppLog.Info("The Linux setup step completed", script == GrantScript ? "access" : "predator_v4");
            return new PlatformSetupResult(true, success);
        }

        AppLog.Warn("The Linux setup step failed",
            $"exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}: {result.Error}");

        return new PlatformSetupResult(false, result.ExitCode switch
        {
            126 => Loc.T("Şifre penceresi kapatıldı; hiçbir şey değişmedi."),
            127 => Loc.T("Yetki verilmedi ya da şifre penceresi açılamadı; aşağıdaki komutu terminalde çalıştırın."),
            _ => Loc.T("Kurulum tamamlanamadı; aşağıdaki komutu terminalde çalıştırın.")
        });
    }

    /// <summary>True when this process carries the group's id among its supplementary groups.</summary>
    private bool IsInGroup(string name)
    {
        var gid = GroupId(_fs.ReadAllText("/etc/group"), name);
        if (gid == null) return false;

        var status = _fs.ReadAllText("/proc/self/status");
        return ProcessGroups(status).Contains(gid.Value);
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
