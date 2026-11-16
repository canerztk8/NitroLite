using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;

namespace NitroLite.Windows;

/// <summary>
/// The Windows probe result in the platform-neutral shape the shared Avalonia window binds to.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <c>LinuxCapabilities</c>. Both implement <see cref="IPlatformSnapshot"/>, so
/// <c>MainViewModel</c> shows the device name, the backend tier, the probe notes and the hardware report without
/// knowing which operating system produced them.
/// </para>
/// <para>
/// The probe is a delegate rather than a value because the settings page can rescan at any time, and because the probe
/// itself is expensive: it enumerates <c>root\WMI</c> and reads the registry. Nothing calls it until the settings page
/// asks for the tier or the user asks for the report, so the window opens without waiting for WMI.
/// </para>
/// <para>
/// <see cref="NeedsUdevRule"/> is always false here. The udev rule is a Linux permission fix; on Windows the write
/// path is the elevated worker, and a refused elevation is reported by the controller instead.
/// </para>
/// </remarks>
public sealed class WindowsPlatformSnapshot : IPlatformSnapshot, IRescannableBackend
{
    private readonly Func<CapabilitySnapshot?> _probe;
    private readonly Func<string> _backendName;
    private readonly ReadBackPolicy _readBack;
    private readonly Func<WmiHardwareController?>? _wmi;

    /// <summary>
    /// <paramref name="probe"/> supplies the capability snapshot and <paramref name="backendName"/> the name of the
    /// backend that is actually in use — which is what the tier line reports, because a machine can have both the
    /// Acer service and direct WMI.
    /// </summary>
    public WindowsPlatformSnapshot(
        DeviceIdentity identity,
        Func<CapabilitySnapshot?> probe,
        Func<string> backendName,
        ReadBackPolicy readBack,
        Func<WmiHardwareController?>? wmi = null)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _backendName = backendName ?? throw new ArgumentNullException(nameof(backendName));
        _readBack = readBack ?? throw new ArgumentNullException(nameof(readBack));
        _wmi = wmi;
    }

    public DeviceIdentity Identity { get; }

    /// <summary>
    /// <c>wmi</c>, <c>aassvc</c> or <c>none</c>. The same vocabulary the Linux probe uses, so the settings page needs
    /// one switch rather than one per platform.
    /// </summary>
    public string Tier => _backendName() switch
    {
        "WMI" => "wmi",
        "AASSvc" => "aassvc",
        _ => "none"
    };

    /// <summary>Never true on Windows: the udev rule is a Linux permission fix.</summary>
    public bool NeedsUdevRule => false;

    /// <summary>
    /// What the probe wanted to say: the matched profile's notes and any profile records validation dropped. Empty
    /// when there is nothing to report, which is what hides the Notes card.
    /// </summary>
    public IReadOnlyList<string> Notes
    {
        get
        {
            var probe = _probe();
            if (probe == null) return Array.Empty<string>();

            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(probe.ProfileNotes)) notes.Add(probe.ProfileNotes);
            notes.AddRange(probe.Rejections);
            return notes;
        }
    }

    /// <summary>
    /// The privacy-safe Markdown report. Built from what the backend has already answered: it never raises the
    /// elevation prompt, because a diagnostics button must not be the thing that shows a UAC dialog.
    /// </summary>
    public string BuildHardwareReport()
    {
        var probe = _probe();
        if (probe == null) return "";

        var wmi = _wmi?.Invoke();
        return HardwareReport.BuildMarkdown(
            probe,
            null,
            _readBack,
            wmi?.DiscoveredSensors,
            wmi?.SensorProbeRan == true,
            Identity);
    }

    void IRescannableBackend.ResetReadBackCounters() => _readBack.ResetAll();

    IPlatformSnapshot IRescannableBackend.Snapshot => this;

    /// <summary>Never true on Windows: a refused elevation is reported by the controller, not as a permission hint.</summary>
    bool IRescannableBackend.PermissionHintRaised => false;
}
