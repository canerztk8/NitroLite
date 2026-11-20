using NitroLite.Core.Hardware.Snapshot;

namespace NitroLite.Core.Hardware;

/// <summary>
/// A platform that can take a read-only <see cref="HardwareSnapshot"/>. Implemented by the Windows probe, which has the
/// Acer WMI worker and the HID interfaces; a platform that cannot answer simply does not implement it, and the settings
/// page hides the two capture buttons.
/// </summary>
/// <remarks>
/// Separate from <see cref="IPlatformSnapshot"/> on purpose. That interface is the report the window already binds to
/// and the Linux front end implements it too; a diagnostic that needs a WMI worker and a SetupAPI walk does not belong
/// in a contract every platform must satisfy.
/// </remarks>
public interface IHardwareSnapshotBackend
{
    /// <summary>
    /// Captures the current read-only hardware state. Never writes, never raises an elevation prompt: a machine whose
    /// worker is not running answers with the selectors that could be read and an <c>unavailable</c> line for the ones
    /// that could not.
    /// </summary>
    Task<HardwareSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
}
