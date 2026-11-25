using System.Globalization;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// Panel backlight brightness over <c>/sys/class/backlight</c>, the Linux half of
/// <see cref="IScreenBrightnessService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The kernel exposes a raw level and the maximum that level can reach, not a percentage, so both directions are a
/// conversion. The device is resolved once at construction (by <c>type</c>, see <see cref="Resolve"/>): a laptop has one
/// internal panel, and re-scanning the class
/// on every read would make the school-mode state machine do file-system work inside its undo record.
/// </para>
/// <para>
/// The backlight attribute is root-owned, so a write from the desktop session usually fails. The session's own
/// <c>systemd-logind</c> then does it: <c>org.freedesktop.login1.Session.SetBrightness</c> lets the active session set
/// its panel without a password, which is how GNOME and KDE change brightness. That is the path school mode takes on a
/// stock install.
/// </para>
/// <para>
/// A write is read back before it is reported as applied. Some panels clamp the value to their own table, and a
/// brightness the app claims to have set but did not is worse than a refusal: school mode records the previous value
/// and would restore a number that was never in effect.
/// </para>
/// </remarks>
public sealed class LinuxScreenBrightnessService : IScreenBrightnessService
{
    private static readonly TimeSpan LogindTimeout = TimeSpan.FromSeconds(3);

    private readonly ISysfsFileSystem _fs;
    private readonly ICommandRunner? _runner;
    private readonly string? _brightnessNode;
    private readonly string? _maxNode;

    /// <summary>
    /// Resolves the backlight device to drive (see <see cref="Resolve"/>). <paramref name="runner"/> reaches logind when
    /// the attribute is not writable; left null it is the real one on the real file system only, so a test on a fixture
    /// tree never talks to the build machine's bus.
    /// </summary>
    public LinuxScreenBrightnessService(ISysfsFileSystem fs, string root = LinuxPaths.BacklightRoot,
        ICommandRunner? runner = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _runner = runner ?? (ReferenceEquals(fs, RealSysfsFileSystem.Instance) ? ProcessCommandRunner.Instance : null);

        if (Resolve(fs, root) is { } chosen)
        {
            _brightnessNode = LinuxPaths.Combine(chosen, LinuxPaths.Brightness);
            _maxNode = LinuxPaths.Combine(chosen, LinuxPaths.MaxBrightness);
        }
    }

    /// <summary>
    /// The backlight directory to drive, or null. Every device with both a level and a maximum is a candidate, and the
    /// one whose <c>type</c> is <c>raw</c> wins over <c>platform</c>, which wins over <c>firmware</c>.
    /// </summary>
    /// <remarks>
    /// A hybrid laptop on an older kernel or driver can register <c>acpi_video0</c> (firmware) next to
    /// <c>nvidia_0</c> or <c>amdgpu_bl1</c> (raw), and only one of them is wired to the panel. The ACPI one is the one
    /// that is usually dead on these machines: it accepts the write and reads it back, and nothing on the screen
    /// changes. The kernel itself picks the native (raw) interface over ACPI video on firmware made for Windows 8 and
    /// later, which every Nitro and Predator is, so the same order is used here. (The backlight ABI document states the
    /// opposite order as a general rule, written for machines from before that change.) Ties keep the name order, so
    /// the choice is stable from run to run. One exception goes first: <see cref="NvidiaWmiEcBacklight"/>.
    /// </remarks>
    public static string? Resolve(ISysfsFileSystem fs, string root = LinuxPaths.BacklightRoot)
    {
        ArgumentNullException.ThrowIfNull(fs);

        string? best = null;
        var bestRank = int.MaxValue;
        foreach (var directory in fs.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!fs.FileExists(LinuxPaths.Combine(directory, LinuxPaths.Brightness)) ||
                !fs.FileExists(LinuxPaths.Combine(directory, LinuxPaths.MaxBrightness)))
            {
                continue;
            }

            var rank = Path.GetFileName(directory) == NvidiaWmiEcBacklight
                ? -1
                : TypeRank(fs.ReadAllText(LinuxPaths.Combine(directory, "type")));
            if (rank < bestRank)
            {
                best = directory;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>
    /// One line for the hardware report: the device that was chosen and the ones that were passed over, each with its
    /// type. Null when the machine has no backlight device.
    /// </summary>
    public static string? Describe(ISysfsFileSystem fs, string root = LinuxPaths.BacklightRoot)
    {
        ArgumentNullException.ThrowIfNull(fs);

        var chosen = Resolve(fs, root);
        if (chosen == null) return null;

        string Entry(string directory) =>
            $"{Path.GetFileName(directory)} ({fs.ReadAllText(LinuxPaths.Combine(directory, "type"))?.Trim() ?? "unknown type"})";

        var others = fs.EnumerateDirectories(root)
            .Where(d => d != chosen && fs.FileExists(LinuxPaths.Combine(d, LinuxPaths.Brightness)))
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(Entry)
            .ToArray();

        return others.Length == 0 ? Entry(chosen) : $"{Entry(chosen)}; not used: {string.Join(", ", others)}";
    }

    /// <summary>
    /// The EC backlight of NVIDIA's Advanced Optimus (Predator and some Nitro boards). Its type is <c>firmware</c>, but on
    /// those boards it is the only interface wired to the panel while the raw GPU devices beside it do nothing, so it
    /// is taken ahead of every type. The kernel's own ACPI backlight selection treats it the same way.
    /// </summary>
    public const string NvidiaWmiEcBacklight = "nvidia_wmi_ec_backlight";

    private static int TypeRank(string? type) => type?.Trim() switch
    {
        "raw" => 0,
        "platform" => 1,
        "firmware" => 2,
        _ => 3
    };

    /// <summary>True when a panel answered the probe. False means the machine has no dimmable backlight.</summary>
    public bool IsAvailable => _brightnessNode != null && _maxNode != null;

    /// <summary>The node this service writes, for the hardware report. Null when there is none.</summary>
    public string? Node => _brightnessNode;

    public int? GetBrightness()
    {
        int? max = ReadInt(_maxNode);
        int? raw = ReadInt(_brightnessNode);
        if (max is not > 0 || raw is not int level) return null;

        return ToPercent(level, max.Value);
    }

    /// <summary>
    /// Sets the brightness in percent. The level is clamped to at least one step: zero turns the panel off, and a
    /// school mode that blacks the screen is not one the user can switch back off.
    /// </summary>
    public bool SetBrightness(int percent)
    {
        if (!IsAvailable) return false;

        int max = ReadInt(_maxNode) ?? 0;
        if (max <= 0) return false;

        int target = ToRaw(Math.Clamp(percent, 1, 100), max);
        var result = _fs.WriteAllText(_brightnessNode!, target.ToString(CultureInfo.InvariantCulture) + "\n");
        if (!result.Ok && !(result.NeedsUdevRule && SetThroughLogind(target)))
        {
            AppLog.Warn("The screen brightness could not be written", $"{_brightnessNode}: {result.Status} {result.Detail}");
            return false;
        }

        // Read back: a panel that clamps to its own table still reports the level it settled on, and that is what the
        // undo record has to hold.
        return ReadInt(_brightnessNode) is int observed && Math.Abs(observed - target) <= 1;
    }

    /// <summary>
    /// Asks logind to set the level. The device is the backlight directory's name (<c>nvidia_0</c>,
    /// <c>amdgpu_bl1</c>); <c>session/auto</c> is the caller's own session, which is the one polkit allows.
    /// </summary>
    private bool SetThroughLogind(int raw)
    {
        if (_runner?.Find("busctl") is not { } busctl) return false;

        var parts = _brightnessNode!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        var device = parts[^2];

        var answer = _runner.Run(busctl, new[]
        {
            "--system", "call", "org.freedesktop.login1", "/org/freedesktop/login1/session/auto",
            "org.freedesktop.login1.Session", "SetBrightness", "ssu", "backlight", device,
            raw.ToString(CultureInfo.InvariantCulture)
        }, LogindTimeout);

        if (!answer.Ok) AppLog.Warn("logind refused the brightness change", $"{device}: {answer.Error}");
        return answer.Ok;
    }

    /// <summary>A raw level as a percentage, rounded to the nearest whole percent.</summary>
    public static int ToPercent(int raw, int max) =>
        max <= 0 ? 0 : (int)Math.Round(Math.Clamp(raw, 0, max) / (double)max * 100, MidpointRounding.AwayFromZero);

    /// <summary>A percentage as a raw level, never below one step.</summary>
    public static int ToRaw(int percent, int max) =>
        max <= 0 ? 0 : Math.Clamp((int)Math.Round(Math.Clamp(percent, 1, 100) / 100.0 * max, MidpointRounding.AwayFromZero), 1, max);

    private int? ReadInt(string? path)
    {
        if (path == null) return null;
        var text = _fs.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
