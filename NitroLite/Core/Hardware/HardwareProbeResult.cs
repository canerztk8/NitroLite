namespace NitroLite.Core.Hardware;

/// <summary>
/// What a capability probe found. The distinction that matters is between "the hardware said no" and "we were not
/// allowed to ask": the first means the feature does not exist on this machine and the control should be hidden, the
/// second means it may well exist and the probe has to run somewhere with more rights.
/// </summary>
public enum HardwareProbeOutcome
{
    /// <summary>Not asked yet, or the answer could not be interpreted.</summary>
    Unknown = 0,

    /// <summary>The hardware answered and the capability is there.</summary>
    Supported = 1,

    /// <summary>The hardware answered and the capability is not there. Hide the control.</summary>
    Unsupported = 2,

    /// <summary>The call was refused for lack of rights. Not evidence either way.</summary>
    AccessDenied = 3,

    /// <summary>The provider did not answer in time. Not evidence either way.</summary>
    TimedOut = 4,

    /// <summary>The call failed for another reason: a broken provider, a bad parameter, a missing driver.</summary>
    Failed = 5
}

/// <summary>
/// One capability answer, shared by every probe so the report, the settings page and the backend selection all read
/// the same shape.
/// <para>
/// This is deliberately not a bool. A probe that collapses "no MUX switch" and "access denied" into <c>false</c>
/// produces a UI that hides a working feature on a machine where the probe simply ran unelevated — which is exactly
/// what the <c>AcerGamingFunction</c> class does on an AN16-41.
/// </para>
/// </summary>
public sealed record HardwareProbeResult
{
    /// <summary>Stable capability name, e.g. "fan.speed" or "battery.chargeLimit". Used as a cache key.</summary>
    public string Capability { get; init; } = "";

    public HardwareProbeOutcome Outcome { get; init; } = HardwareProbeOutcome.Unknown;

    /// <summary>Human-readable evidence: the method called, the value read, or the error code.</summary>
    public string Detail { get; init; } = "";

    /// <summary>True only when the hardware positively confirmed the capability.</summary>
    public bool IsSupported => Outcome == HardwareProbeOutcome.Supported;

    /// <summary>
    /// True when the answer is worth retrying with more rights or later. A capability that came back
    /// <see cref="HardwareProbeOutcome.AccessDenied"/> must not be cached as unsupported.
    /// </summary>
    public bool IsInconclusive =>
        Outcome is HardwareProbeOutcome.AccessDenied or HardwareProbeOutcome.TimedOut or HardwareProbeOutcome.Unknown;

    public static HardwareProbeResult Supported(string capability, string detail = "") =>
        new() { Capability = capability, Outcome = HardwareProbeOutcome.Supported, Detail = detail };

    public static HardwareProbeResult Unsupported(string capability, string detail = "") =>
        new() { Capability = capability, Outcome = HardwareProbeOutcome.Unsupported, Detail = detail };

    public static HardwareProbeResult AccessDenied(string capability, string detail = "") =>
        new() { Capability = capability, Outcome = HardwareProbeOutcome.AccessDenied, Detail = detail };

    public static HardwareProbeResult TimedOut(string capability, string detail = "") =>
        new() { Capability = capability, Outcome = HardwareProbeOutcome.TimedOut, Detail = detail };

    public static HardwareProbeResult Failed(string capability, string detail = "") =>
        new() { Capability = capability, Outcome = HardwareProbeOutcome.Failed, Detail = detail };

    /// <summary>Not asked, or asked and the answer could not be interpreted. Never treated as a "no".</summary>
    public static HardwareProbeResult Unknown(string capability, string detail = "") =>
        new() { Capability = capability, Outcome = HardwareProbeOutcome.Unknown, Detail = detail };

    /// <summary>
    /// The verdict as prose, lower case, for the report and the settings line. Kept in one place so the two never
    /// disagree, and so <see cref="HardwareProbeOutcome.AccessDenied"/> reads as "access denied" rather than the
    /// enum name run through a case change.
    /// </summary>
    public string Verdict => Outcome switch
    {
        HardwareProbeOutcome.Supported => "supported",
        HardwareProbeOutcome.Unsupported => "not supported",
        HardwareProbeOutcome.AccessDenied => "access denied",
        HardwareProbeOutcome.TimedOut => "timed out",
        HardwareProbeOutcome.Failed => "failed",
        _ => "unknown"
    };

    /// <summary>One line for the hardware report: the capability, the verdict, and the evidence behind it.</summary>
    public string Summary
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Capability) ? "capability" : Capability;
            return string.IsNullOrWhiteSpace(Detail) ? $"{name}: {Verdict}" : $"{name}: {Verdict} ({Detail})";
        }
    }
}
