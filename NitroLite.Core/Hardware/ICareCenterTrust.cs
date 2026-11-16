namespace NitroLite.Core.Hardware;

/// <summary>
/// The one thing the battery page needs from a backend that pins the Acer Care Center certificate.
/// </summary>
/// <remarks>
/// <para>
/// The agent's certificate is trusted on first use and remembered. An Acer update can rotate it, and the connection is
/// then refused — correct, but it leaves the 80 % charge limit broken with no explanation. This interface is how the
/// page learns that the refusal was a certificate change rather than a missing agent, and how the user says "trust the
/// new one".
/// </para>
/// <para>
/// Separate from <see cref="IHardwareController"/> because only one backend has a certificate to pin. A backend that
/// does not implement it simply never shows the button.
/// </para>
/// </remarks>
public interface ICareCenterTrust
{
    /// <summary>True when the agent answered with a certificate other than the pinned one.</summary>
    bool CertificateMismatch { get; }

    /// <summary>
    /// Drops the stored thumbprint so the next connection trusts whatever the agent presents. Returns false when the
    /// pin file could not be removed, which is what a read-only data folder does.
    /// </summary>
    bool ForgetPinnedCertificate();
}
