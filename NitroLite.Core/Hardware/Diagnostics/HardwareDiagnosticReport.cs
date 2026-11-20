using System.Text.Json.Serialization;

namespace NitroLite.Core.Hardware.Diagnostics;

/// <summary>
/// The body of one diagnostic report: seven fields, every one of them about the board rather than about its owner.
/// </summary>
/// <remarks>
/// <para>
/// The property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than derived from a naming
/// policy. The endpoint is a Worker that parses this shape, and a rename that only changed the casing would be a
/// silent break in a contract nobody looks at until an error needs triaging.
/// </para>
/// <para>
/// The type is serialised by <see cref="HardwareDiagnosticJsonContext"/>, which is source-generated. That is not
/// tidiness: the app publishes with <c>PublishTrimmed</c> and <c>TrimMode=partial</c>, and a reflection-based
/// serializer is exactly the kind of code the trimmer cannot see through.
/// </para>
/// </remarks>
public sealed record HardwareDiagnosticReport
{
    /// <summary>The model code, e.g. <c>AN16-61</c>. Public SMBIOS text, never a serial.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>The baseboard product, e.g. <c>EV9_SKF</c>.</summary>
    [JsonPropertyName("board")]
    public required string Board { get; init; }

    /// <summary>The BIOS version, e.g. <c>1.53</c>.</summary>
    [JsonPropertyName("biosVersion")]
    public required string BiosVersion { get; init; }

    /// <summary>
    /// Which control could not be confirmed: the policy's own capability key (<c>wmi.muxMode</c>) or
    /// <c>keyboard.lighting</c> for the HID path. The key rather than a translated label, because the reader of this
    /// payload is triaging a firmware bug, not using the app.
    /// </summary>
    [JsonPropertyName("failedControl")]
    public required string FailedControl { get; init; }

    /// <summary><c>WMI</c> or <c>HID</c>: the transport that was tried and did not answer.</summary>
    [JsonPropertyName("attemptedTransport")]
    public required string AttemptedTransport { get; init; }

    /// <summary>
    /// The method names the firmware advertised. An empty array is a fact, not a missing value: it means the probe
    /// found no class or was never allowed to read one.
    /// </summary>
    [JsonPropertyName("availableWmiMethods")]
    public required IReadOnlyList<string> AvailableWmiMethods { get; init; }

    /// <summary>When the failure was observed, always UTC.</summary>
    [JsonPropertyName("timestampUtc")]
    public required DateTimeOffset TimestampUtc { get; init; }
}
