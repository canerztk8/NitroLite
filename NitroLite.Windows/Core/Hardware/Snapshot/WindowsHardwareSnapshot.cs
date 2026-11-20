using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Snapshot;
using NitroLite.Core.Hardware.Wmi;

namespace NitroLite.Windows.Core.Hardware.Snapshot;

/// <summary>
/// Builds a read-only snapshot on Windows: the Acer WMI selectors the elevated worker can answer, plus the vendor HID
/// feature reports this build is willing to read.
/// </summary>
/// <remarks>
/// <para>
/// The WMI half is only included when the worker is already connected. Every controller read goes through
/// <c>EnsureConnectedAsync</c>, which starts the elevated helper — and a diagnostics button that raises a UAC prompt
/// when it is pressed is a diagnostics button a user learns not to press. A session without the worker still gets the
/// HID half, and its report says which half it has; the user who wants the WMI half starts direct WMI from the same
/// settings page, where the elevation question belongs.
/// </para>
/// <para>
/// Nothing here writes. The WMI source calls the same read commands the window calls while it is running, and the HID
/// source opens each interface with read access only.
/// </para>
/// </remarks>
public static class WindowsHardwareSnapshot
{
    /// <summary>
    /// Captures the machine as it is. <paramref name="capturedAt"/> exists for tests and for a caller that wants the two
    /// captures of a pair to share a timestamp; the default is now.
    /// </summary>
    public static Task<HardwareSnapshot> CaptureAsync(
        DeviceIdentity identity,
        WmiHardwareController? wmi,
        DateTimeOffset? capturedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var sources = new List<IHardwareSnapshotSource>();
        sources.Add(wmi is { IsAvailable: true }
            ? new WmiSnapshotSource(wmi)
            : new RefusedSource(
                WmiSnapshotSource.SectionName,
                "the elevated WMI worker is not running (start direct WMI, then capture again)"));

        sources.Add(new HidSnapshotSource());

        var model = string.IsNullOrWhiteSpace(identity.ModelCode) ? identity.Model : identity.ModelCode;
        return HardwareSnapshotService.CaptureAsync(model, identity.BoardProduct, sources, capturedAt, cancellationToken);
    }

    /// <summary>A source that has nothing to read and says why, so the report shows a missing half instead of omitting it.</summary>
    private sealed class RefusedSource(string section, string reason) : IHardwareSnapshotSource
    {
        public string Section => section;

        public Task<IReadOnlyList<HardwareSnapshotEntry>> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HardwareSnapshotEntry>>(
                [HardwareSnapshotEntry.Unavailable(section, "capture", reason)]);
    }
}
