namespace NitroLite.Core.Hardware.Diagnostics;

/// <summary>
/// The machine facts a diagnostic report is built from, read at the moment of the failure rather than at startup.
/// </summary>
/// <remarks>
/// <para>
/// These are the only fields the error endpoint receives, and every one of them describes the machine's <i>model</i>
/// rather than the machine itself. <c>AN16-61</c>, <c>EV9_SKF</c> and <c>1.53</c> are public SMBIOS strings: the same
/// values on every unit of that build. Nothing here can identify the owner, which is why there is no serial, no
/// account name and no network address in the type at all — a field that does not exist cannot be added to a payload
/// by accident later.
/// </para>
/// <para>
/// The method list is the schema probe's answer, not a hard-coded list of names this build happens to call. On the
/// boards this report exists for, the interesting question is precisely which selectors the firmware advertises and
/// then ignores.
/// </para>
/// </remarks>
public sealed record HardwareDiagnosticIdentity
{
    /// <summary>The model code, e.g. <c>AN16-61</c>. Empty when SMBIOS could not be read.</summary>
    public string Model { get; init; } = "";

    /// <summary>The baseboard product, e.g. <c>EV9_SKF</c>. Empty when SMBIOS could not be read.</summary>
    public string Board { get; init; } = "";

    /// <summary>The BIOS version with the vendor prefix stripped, e.g. <c>1.53</c>.</summary>
    public string BiosVersion { get; init; } = "";

    /// <summary>
    /// Every method the <c>AcerGamingFunction</c> class advertised when the probe walked the schema. Empty when the
    /// probe had not finished, when the class is absent, or when reading it was refused — all three of which are
    /// themselves worth knowing, and none of which this type may invent an answer for.
    /// </summary>
    public IReadOnlyList<string> AvailableWmiMethods { get; init; } = Array.Empty<string>();
}
