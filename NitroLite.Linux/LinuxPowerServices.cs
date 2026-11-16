using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// The Linux answer to <see cref="IPowerSchemeAccess"/>: there are no power schemes.
/// </summary>
/// <remarks>
/// Linux exposes processor and device power policy through sysfs and the desktop's own power daemon, not through a
/// named scheme with per-source values. Rather than invent a mapping, this reports
/// <see cref="IsSupported"/> false and every read and write as a refusal, which makes
/// <see cref="SchoolModeService"/> skip those settings entirely. The alternative — a fake scheme that always answers
/// "written" — would put eight settings into the undo record that nothing on the machine can restore.
/// </remarks>
public sealed class LinuxPowerSchemeAccess : IPowerSchemeAccess
{
    public bool IsSupported => false;

    public bool TryReadDc(Guid subGroup, Guid setting, out uint value)
    {
        value = 0;
        return false;
    }

    public bool TryWriteDc(Guid subGroup, Guid setting, uint value) => false;
}

/// <summary>
/// The Linux answer to <see cref="IDisplayRefreshRateService"/>: the refresh rate is not reachable from sysfs.
/// </summary>
/// <remarks>
/// Changing it needs XRandR, a Wayland output protocol or a compositor-specific call, and each of those is a
/// dependency this assembly deliberately does not take. Reporting <see cref="IsSupported"/> false keeps school mode
/// from claiming it lowered the panel to 60 Hz when it did not.
/// </remarks>
public sealed class LinuxDisplayRefreshRateService : IDisplayRefreshRateService
{
    public bool IsSupported => false;

    public int GetCurrentRefreshRate() => 0;

    public int GetMaxRefreshRate() => 0;

    public bool SetRefreshRate(int hz) => false;
}
