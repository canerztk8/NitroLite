namespace NitroLite.Core.Hardware.Verification;

/// <summary>
/// What the machine said after a control was written.
/// <para>
/// The status byte is not an answer. Acer's firmware returns <c>0x00</c> for a write it queued and then dropped, and
/// several models answer success for a selector they do not implement at all. The only trustworthy claim comes from
/// reading the value back, and this enum is how the rest of the program tells the three possible outcomes apart:
/// the value matched, the value contradicted what was written, or the machine has nothing to read.
/// </para>
/// </summary>
public enum ControlVerdict
{
    /// <summary>Nothing has been observed yet.</summary>
    Unknown = 0,

    /// <summary>The value read back after the write was the value that was written.</summary>
    Confirmed,

    /// <summary>The write was accepted and the read-back still reports the old value. The control did not change.</summary>
    Mismatch,

    /// <summary>
    /// The write was accepted, but this machine has no way to read the setting back, so the change is neither
    /// confirmed nor refuted. Treated as "do not claim it worked" rather than as a failure: a control that cannot
    /// be read is not a control that is broken, and disabling it would take a working feature away.
    /// </summary>
    Unverifiable,

    /// <summary>There was no transport to ask, so nothing reached the hardware.</summary>
    TransportMissing
}

/// <summary>
/// One control's verification state for this session, with the values that produced it.
/// <para>
/// The strings are the report's own vocabulary (the values are formatted with the invariant culture), because they end
/// up in <c>--hardware-report</c> and in the log, where a translated sentence would be of no use to whoever reads the
/// issue. The UI shows a translated label beside them.
/// </para>
/// </summary>
public sealed record VerificationNote
{
    /// <summary>The capability key, e.g. <c>wmi.muxMode</c>. Matches <c>ReadBackPolicy</c>'s constants.</summary>
    public required string Control { get; init; }

    /// <summary>What the read-back said.</summary>
    public required ControlVerdict Verdict { get; init; }

    /// <summary>The value that was written, as the log should show it.</summary>
    public string Requested { get; init; } = "";

    /// <summary>The value the machine reported afterwards, or empty when it reported nothing.</summary>
    public string Observed { get; init; } = "";

    /// <summary>The reason in one line: the worker's refusal, the mismatch, or why there is no read-back.</summary>
    public string Detail { get; init; } = "";

    /// <summary>True when the user should be offered a compatibility report for this control.</summary>
    public bool NeedsAttention => Verdict is ControlVerdict.Mismatch or ControlVerdict.Unverifiable;

    /// <summary>One line for the log and the report.</summary>
    public string Describe()
    {
        var what = SessionVerification.LabelFor(Control);
        var observed = string.IsNullOrEmpty(Observed) ? "no answer" : Observed;
        var detail = string.IsNullOrEmpty(Detail) ? "" : $" ({Detail})";

        return Verdict switch
        {
            ControlVerdict.Confirmed => $"{what}: confirmed ({observed})",
            ControlVerdict.Mismatch =>
                $"{what}: wrote {Show(Requested)} but the machine still reports {observed}{detail}",
            ControlVerdict.Unverifiable => $"{what}: wrote {Show(Requested)}, no read-back on this machine{detail}",
            ControlVerdict.TransportMissing => $"{what}: not written, no transport{detail}",
            _ => $"{what}: not checked"
        };
    }

    private static string Show(string value) => string.IsNullOrEmpty(value) ? "?" : value;
}
