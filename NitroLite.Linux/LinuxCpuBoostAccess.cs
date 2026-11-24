using System.Globalization;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// The processor's boost control over sysfs, the Linux half of <see cref="ICpuBoostAccess"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is no power scheme on Linux, so there is no per-source (AC/DC) boost value either: the kernel exposes one
/// global switch. <c>targetAc</c> and <c>targetDc</c> are therefore accepted and ignored, which is the honest answer —
/// pretending to write two different values would make the school-mode undo record describe a state the machine
/// cannot be in.
/// </para>
/// <para>
/// Two nodes are tried, in order of how directly they express "boost":
/// <c>cpufreq/boost</c> (0/1) and <c>intel_pstate/no_turbo</c> (0/1, inverted). A machine with neither reports
/// <see cref="IsSupported"/> false and school mode leaves the processor alone instead of logging a failure.
/// <c>amd_pstate/status</c> is deliberately not used: switching it to <c>passive</c> restarts the whole frequency
/// driver, which is far heavier than a boost switch, and the helper policy and the udev rule do not allow that node.
/// </para>
/// </remarks>
public sealed class LinuxCpuBoostAccess : ICpuBoostAccess
{
    private readonly ISysfsFileSystem _fs;
    private readonly string? _boostNode;
    private readonly string? _noTurboNode;

    public LinuxCpuBoostAccess(ISysfsFileSystem fs)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));

        _boostNode = Existing(LinuxPaths.CpuFreqBoost);
        _noTurboNode = Existing(LinuxPaths.IntelNoTurbo);
    }

    /// <summary>True when at least one boost node exists, so a write has somewhere to go.</summary>
    public bool IsSupported => _boostNode != null || _noTurboNode != null;

    /// <summary>The node a write would use, for the hardware report. Null when there is none.</summary>
    public string? Node => _boostNode ?? _noTurboNode;

    /// <summary>
    /// The stored boost state. <see cref="CpuBoostMode.Enabled"/> is what a 1 on <c>cpufreq/boost</c> means: the
    /// kernel has no notion of the Windows "aggressive" and "efficient" gradations, so those read back as Enabled.
    /// </summary>
    public CpuBoostMode? QueryBoostModeExact(bool isAc)
    {
        if (_boostNode != null)
        {
            return ReadInt(_boostNode) switch
            {
                0 => CpuBoostMode.Disabled,
                1 => CpuBoostMode.Enabled,
                _ => null
            };
        }

        if (_noTurboNode != null)
        {
            return ReadInt(_noTurboNode) switch
            {
                1 => CpuBoostMode.Disabled,
                0 => CpuBoostMode.Enabled,
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// Writes the boost state. Every mode other than <see cref="CpuBoostMode.Disabled"/> means "boost on": the kernel
    /// switch is binary, and refusing the Windows gradations would leave a game profile unable to turn boost on.
    /// </summary>
    public Task<bool> SetBoostModeAsync(CpuBoostMode mode, bool targetAc = true, bool targetDc = true)
    {
        if (!targetAc && !targetDc) return Task.FromResult(true);
        if (!IsSupported) return Task.FromResult(false);

        bool enable = mode != CpuBoostMode.Disabled;
        var expected = enable ? CpuBoostMode.Enabled : CpuBoostMode.Disabled;

        if (_boostNode != null) return Task.FromResult(Write(_boostNode, enable ? "1" : "0", expected));
        return Task.FromResult(Write(_noTurboNode!, enable ? "0" : "1", expected));
    }

    /// <summary>
    /// Writes <paramref name="value"/> and reads the state back through the same mapping the query uses, so a node that
    /// accepts the write and keeps its old value is reported as a failure rather than as a success.
    /// </summary>
    /// <param name="expected">
    /// The mode the write should produce. It is passed in rather than derived from <paramref name="value"/> because
    /// <c>intel_pstate/no_turbo</c> is inverted: writing 1 there means boost is <em>off</em>.
    /// </param>
    private bool Write(string path, string value, CpuBoostMode expected)
    {
        var result = _fs.WriteAllText(path, value + "\n");
        if (!result.Ok)
        {
            AppLog.Warn("CPU Boost could not be written", $"{path}: {result.Status} {result.Detail}");
            return false;
        }

        return QueryBoostModeExact(true) == expected;
    }

    private string? Existing(string path) => _fs.FileExists(path) ? path : null;

    private int? ReadInt(string path)
    {
        var text = _fs.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
