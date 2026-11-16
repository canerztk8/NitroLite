using NitroLite.Core;
using NitroLite.Core.Hardware;

namespace NitroLite.Linux;

/// <summary>
/// The sysfs attributes this machine actually has, resolved once at probe time.
/// </summary>
/// <remarks>
/// Every member is nullable and null means "this machine does not expose it". That is deliberately different from
/// "the write failed": a control whose node is absent is hidden, while a control whose node exists but is not
/// writable is shown with an explanation. Collapsing the two would either hide a working feature or offer a broken
/// one.
/// </remarks>
public sealed record LinuxNodes
{
    /// <summary><c>/sys/firmware/acpi/platform_profile</c>, when the kernel registered it.</summary>
    public string? PlatformProfile { get; init; }

    /// <summary>The profiles that file accepts.</summary>
    public string? PlatformProfileChoices { get; init; }

    /// <summary><c>nitro_sense</c> or <c>predator_sense</c>, whichever the module registered.</summary>
    public string? SenseRoot { get; init; }

    /// <summary>Fan speed as <c>cpu,gpu</c> percentages.</summary>
    public string? FanSpeed { get; init; }

    /// <summary>30-second keyboard backlight timeout.</summary>
    public string? BacklightTimeout { get; init; }

    /// <summary>80 % charge limit, from the Linuwu-Sense module.</summary>
    public string? BatteryLimiter { get; init; }

    /// <summary>80 % charge limit, from the standard kernel battery class.</summary>
    public string? ChargeThreshold { get; init; }

    /// <summary>LCD overdrive.</summary>
    public string? LcdOverride { get; init; }

    /// <summary>CoolBoost, when the module exposes it.</summary>
    public string? CoolBoost { get; init; }

    /// <summary>GPU mode, when the module exposes it.</summary>
    public string? DgpuMode { get; init; }

    /// <summary>Four zone colours plus brightness.</summary>
    public string? PerZoneMode { get; init; }

    /// <summary>Effect, speed, brightness, direction and colour.</summary>
    public string? FourZoneMode { get; init; }

    /// <summary>Every hwmon device that answered.</summary>
    public IReadOnlyList<LinuxHwmonDevice> Hwmon { get; init; } = Array.Empty<LinuxHwmonDevice>();

    /// <summary>True when nothing at all was found, so the backend has no path to the hardware.</summary>
    public bool IsEmpty =>
        PlatformProfile == null && SenseRoot == null && PerZoneMode == null && ChargeThreshold == null && Hwmon.Count == 0;
}

/// <summary>
/// What the Linux probe concluded: who this machine is, which profile it matched, which nodes exist, and what that
/// adds up to in <see cref="HardwareCapabilities"/>.
/// </summary>
public sealed record LinuxCapabilities : IPlatformSnapshot
{
    public DeviceIdentity Identity { get; init; } = new();

    public ModelProfile Profile { get; init; } = new();

    public HardwareCapabilities Hardware { get; init; } = HardwareCapabilities.Unknown;

    public LinuxNodes Nodes { get; init; } = new();

    /// <summary>Which tier answered, for the log and the report: "acer-wmi", "linuwu-sense", "both" or "none".</summary>
    public string Tier { get; init; } = "none";

    /// <summary>
    /// True when a node the backend would write exists but this process may not open it for writing, which is the
    /// state every user is in until <c>packaging/linux/99-nitrolite.rules</c> is installed. The settings page turns
    /// this into the one-line install command instead of letting the user discover it by pressing a control.
    /// </summary>
    public bool NeedsUdevRule { get; init; }

    /// <summary>Human-readable notes about what was missing and why, in the user's language.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Always true here: the sysfs tree is enumerated once at startup, so a node that is not there is not there. A
    /// false <c>IsAvailable</c> on this platform really does mean the kernel has no Acer module.
    /// </summary>
    public bool NegativeAnswersAreConclusive => true;

    /// <summary>
    /// The privacy-safe Markdown report: DMI, the acer-wmi and Linuwu-Sense nodes, hwmon and the udev state. No serial
    /// number, machine UUID, user name or personal file path, which is what makes it safe to paste into an issue.
    /// </summary>
    public string BuildHardwareReport() => LinuxHardwareReport.Build(this);
}
