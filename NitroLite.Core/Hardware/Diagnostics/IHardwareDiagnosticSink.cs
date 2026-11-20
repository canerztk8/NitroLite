namespace NitroLite.Core.Hardware.Diagnostics;

/// <summary>
/// The two moments the hardware layer is allowed to say something about a failure: a read-back that contradicted the
/// write, and a keyboard lighting write that exhausted every transport.
/// </summary>
/// <remarks>
/// <para>
/// A sink rather than a direct dependency on <see cref="HardwareDiagnosticClient"/> so the policy and the hybrid
/// controller can be constructed in a test with nothing wired: the default is <c>null</c>, and a null sink costs one
/// field check and no allocation. No test can reach the network, and no host that has not built a client can send.
/// </para>
/// <para>
/// These are events, not questions. Nothing here returns a value the caller could branch on: a diagnostic that
/// changed the behaviour it observes would be measuring something else.
/// </para>
/// </remarks>
public interface IHardwareDiagnosticSink
{
    /// <summary>
    /// A control ended in <c>ControlVerdict.Mismatch</c>: the machine was asked for a value and did not hold it,
    /// whether the contradiction came from the read-back after a write or from the state read that precedes one.
    /// <paramref name="control"/> is the policy's capability key.
    /// </summary>
    void ReadBackMismatch(string control);

    /// <summary>
    /// Both lighting transports were tried and neither applied the colour. The model and BIOS are the ones the
    /// router keyed on, so the report is filed against the same machine the cache forgot.
    /// </summary>
    void LightingFallbackExhausted(string? modelCode, string? biosVersion);
}
