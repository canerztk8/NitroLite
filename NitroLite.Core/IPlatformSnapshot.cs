using NitroLite.Core.Hardware;

namespace NitroLite.Core;

/// <summary>
/// What a platform probe concluded about this machine, in the terms the window shows.
/// </summary>
/// <remarks>
/// <para>
/// The window needs five things from a probe that are not part of <see cref="IHardwareController"/>: who the machine
/// is, which backend tier answered, whether a permission problem is blocking writes, what the probe wanted to say
/// about it, and a privacy-safe report. On Linux that is <c>LinuxCapabilities</c>; on Windows it is the capability
/// snapshot plus the backend that is in use.
/// </para>
/// <para>
/// An interface rather than a shared record so neither platform has to construct the other's type. The view model
/// takes this and nothing else, which is what lets one Avalonia window serve both operating systems.
/// </para>
/// </remarks>
public interface IPlatformSnapshot
{
    /// <summary>Manufacturer, model, model code and BIOS version, as the probe read them.</summary>
    DeviceIdentity Identity { get; }

    /// <summary>
    /// Which tier answered, for the log and the settings page: <c>acer-wmi</c>, <c>linuwu-sense</c>, <c>both</c>,
    /// <c>wmi</c>, <c>aassvc</c> or <c>none</c>.
    /// </summary>
    string Tier { get; }

    /// <summary>
    /// True when a node the backend would write exists but this process may not open it for writing. On Linux that is
    /// the state every user is in until the udev rule is installed; on Windows it is never true, because the elevated
    /// worker is the write path.
    /// </summary>
    bool NeedsUdevRule { get; }

    /// <summary>Human-readable notes about what was missing and why, in the user's language.</summary>
    IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// True when a "not supported" answer from this platform is a real answer rather than "could not ask".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two platforms answer the same question with different confidence, and treating them the same is what put a
    /// "no hardware backend" banner and a "not on this device" badge on a working Nitro 16.
    /// </para>
    /// <para>
    /// Linux enumerates its sysfs nodes once at startup, so a false <see cref="IHardwareController.IsAvailable"/> there
    /// means the kernel has no Acer module and the machine genuinely has nothing to talk to. Windows opens its
    /// transport on demand — the Acer service may not be up yet and the WMI worker is only started when the user
    /// elevates — so a false answer there means "nothing is open yet". The service socket also has no CoolBoost
    /// command, so that capability reads as false on a machine that has it.
    /// </para>
    /// <para>
    /// The window uses this to decide whether it may claim an absence. When it is false, an unsupported capability is
    /// reported as unknown: the control stays hidden, but nothing tells the user their machine lacks a feature it may
    /// well have.
    /// </para>
    /// </remarks>
    bool NegativeAnswersAreConclusive { get; }

    /// <summary>
    /// The privacy-safe Markdown report: no serial number, machine UUID, user name, file path or log content. Built on
    /// demand rather than cached, because the probe state can change after a rescan.
    /// </summary>
    /// <param name="context">
    /// What the session adds to the report: the runtime sensor verification and nothing else, because the USB HID
    /// enumeration belongs to the platform. Null keeps the report exactly as it was before the two sections existed.
    /// </param>
    string BuildHardwareReport(HardwareReportContext? context = null);
}

/// <summary>
/// A backend that can be asked again, which is what the settings page's rescan button needs.
/// </summary>
/// <remarks>
/// Separate from <see cref="IHardwareController"/> because most of that contract is about reading and writing the
/// hardware, and a rescan is about the probe. A backend that cannot re-probe simply does not implement this, and the
/// button does nothing rather than pretending it rescanned.
/// </remarks>
public interface IRescannableBackend
{
    /// <summary>
    /// Clears the read-back counters. A rescan is the user saying "try again", and a capability that was switched off
    /// after three bad sessions would otherwise stay off for good.
    /// </summary>
    void ResetReadBackCounters();

    /// <summary>The probe snapshot as it stands right now.</summary>
    IPlatformSnapshot Snapshot { get; }

    /// <summary>True when a write was refused for lack of permission, so the window can offer the fix.</summary>
    bool PermissionHintRaised { get; }
}
