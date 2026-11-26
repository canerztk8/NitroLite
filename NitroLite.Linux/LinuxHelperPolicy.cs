using System.Globalization;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// What the root helper may write: an exact list of sysfs attributes, each with the values it accepts.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole security boundary of the helper, so it is deliberately narrow and deliberately dumb. The list is
/// built by the helper itself, at start-up, from the same probe the app runs — the client never contributes a path.
/// A request names a path, and the path is accepted only when it is <em>string-equal</em> to an entry here: there is
/// no normalisation of client input, so <c>..</c>, a trailing slash, a symlink spelled differently or a path the probe
/// did not find are all simply "not allowed".
/// </para>
/// <para>
/// Each entry checks its value before anything is written: a mode must be one the kernel lists, a fan percentage must
/// be in range, a flag must be 0 or 1, a keyboard line must parse. A value with a line break inside it is refused
/// everywhere, because sysfs takes one value per write and a second line would be a second value.
/// </para>
/// </remarks>
public sealed class LinuxHelperPolicy
{
    /// <summary>The longest value any attribute here takes; anything longer is refused before it is parsed.</summary>
    public const int MaxValueLength = 128;

    private readonly Dictionary<string, Func<string, bool>> _rules = new(StringComparer.Ordinal);

    private LinuxHelperPolicy()
    {
    }

    /// <summary>
    /// The writes that hand the fans back to the firmware, with the values that mean "manual" for each (<c>*</c>: any
    /// value other than the automatic one). The watchdog
    /// uses them: when the last client that put a fan in manual mode goes away, these are written so the fans are
    /// never left at a fixed speed with nobody watching the temperature.
    /// </summary>
    public IReadOnlyList<(string Path, string Manual, string Automatic)> FanResets => _fanResets;

    private readonly List<(string Path, string Manual, string Automatic)> _fanResets = new();

    /// <summary>The attributes the helper may write, for the client's <c>list</c> request and the log.</summary>
    public IReadOnlyCollection<string> Paths => _rules.Keys;

    /// <summary>
    /// Decides one write. Returns null when it may go ahead, or the reason it may not: <c>not-allowed</c> for a path
    /// outside the list, <c>invalid-value</c> for a value the attribute does not take.
    /// </summary>
    public string? Check(string? path, string? value)
    {
        if (path == null || !_rules.TryGetValue(path, out var rule)) return "not-allowed";
        if (value == null) return "invalid-value";

        var trimmed = value.TrimEnd('\n');
        if (trimmed.Length == 0 || trimmed.Length > MaxValueLength) return "invalid-value";
        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains('\0')) return "invalid-value";

        return rule(trimmed) ? null : "invalid-value";
    }

    /// <summary>
    /// Builds the list from a probe of this machine. <paramref name="fs"/> is consulted for the processor boost nodes,
    /// which are not part of the Acer probe.
    /// </summary>
    public static LinuxHelperPolicy For(LinuxCapabilities capabilities, ISysfsFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(fs);

        var policy = new LinuxHelperPolicy();
        var nodes = capabilities.Nodes;

        if (nodes.PlatformProfile != null)
        {
            var choices = (capabilities.PlatformProfileChoicesText ?? fs.ReadAllText(LinuxPaths.PlatformProfileChoices) ?? "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            policy.Add(nodes.PlatformProfile, v => choices.Contains(v));
        }

        policy.Add(nodes.FanSpeed, IsFanPair);
        // A manual speed below the app's floor is refused here too: in manual mode a low pwm is a fan that can stall
        // under load, and the helper must not be the way around the floor the app enforces.
        policy.Add(nodes.AcerPwmCpu, v => IsInt(v, MinPwm, 255));
        policy.Add(nodes.AcerPwmGpu, v => IsInt(v, MinPwm, 255));
        policy.Add(nodes.AcerPwmCpuEnable, v => IsInt(v, 0, 2));
        policy.Add(nodes.AcerPwmGpuEnable, v => IsInt(v, 0, 2));

        // pwmN_enable 2 is automatic; 1 (manual) and 0 (full speed) are both the app's choice and both go back to the
        // firmware when the app is gone — full speed is the safe direction, but nothing would ever end it. Linuwu-Sense's
        // fan_speed is out of automatic for anything but "0,0".
        if (nodes.AcerPwmCpuEnable != null) policy._fanResets.Add((nodes.AcerPwmCpuEnable, "*", "2"));
        if (nodes.AcerPwmGpuEnable != null) policy._fanResets.Add((nodes.AcerPwmGpuEnable, "*", "2"));
        if (nodes.FanSpeed != null) policy._fanResets.Add((nodes.FanSpeed, "*", "0,0"));

        policy.Add(nodes.PerZoneMode, v => LinuxKeyboardProtocol.ParsePerZone(v) != null);
        policy.Add(nodes.FourZoneMode, v => LinuxKeyboardProtocol.ParseFourZone(v) != null);

        policy.Add(nodes.BatteryLimiter, IsFlag);
        policy.Add(nodes.ChargeThreshold, v => IsInt(v, 20, 100));
        policy.Add(nodes.ChargeTypes, v => v is LinuxPaths.ChargeTypeStandard or LinuxPaths.ChargeTypeLongLife);
        policy.Add(nodes.WinKeyLock, IsFlag);
        policy.Add(nodes.CoolBoost, IsFlag);
        policy.Add(nodes.BacklightTimeout, IsFlag);
        policy.Add(nodes.DgpuMode, IsFlag);

        if (fs.FileExists(LinuxPaths.CpuFreqBoost)) policy.Add(LinuxPaths.CpuFreqBoost, IsFlag);
        if (fs.FileExists(LinuxPaths.IntelNoTurbo)) policy.Add(LinuxPaths.IntelNoTurbo, IsFlag);

        return policy;
    }

    private void Add(string? path, Func<string, bool> rule)
    {
        if (!string.IsNullOrEmpty(path)) _rules[path] = rule;
    }

    /// <summary><see cref="FanCurve.MinPercent"/> on hwmon's 0..255 scale, rounded down so the app's floor passes.</summary>
    public static int MinPwm { get; } = FanCurve.MinPercent * 255 / 100;

    private static bool IsFlag(string value) => value is "0" or "1";

    private static bool IsInt(string value, int min, int max) =>
        value.All(char.IsAsciiDigit) && value.Length <= 4 &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max;

    /// <summary>
    /// Linuwu-Sense's <c>cpu,gpu</c> percentages: each 0 ("the firmware decides") or from the app's floor to 100.
    /// </summary>
    private static bool IsFanPair(string value)
    {
        var parts = value.Split(',');
        return parts.Length == 2 && IsFanPercent(parts[0]) && IsFanPercent(parts[1]);
    }

    private static bool IsFanPercent(string value) => value == "0" || IsInt(value, FanCurve.MinPercent, 100);
}
