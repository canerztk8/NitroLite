using NitroLite.Core;

namespace NitroLite.Core.Hardware;

/// <summary>
/// What the settings page's "rescan" drops so the next capability read asks the machine again: the capability cache
/// kept in the settings, and the Acer service answers kept for the session. Without this a Timeout or AccessDenied
/// recorded once would outlive every rescan.
/// </summary>
public static class CapabilityRescan
{
    /// <summary>
    /// Clears both caches. The caller saves the settings file and starts the new probe; neither is done here so the
    /// method touches no disk and no hardware.
    /// </summary>
    public static void Reset(AppSettings settings, AgentHardwareController? agent)
    {
        ArgumentNullException.ThrowIfNull(settings);

        CapabilityProbe.Invalidate(settings);
        agent?.InvalidateForRescan();
    }
}
