namespace NitroLite.Core.Hardware;

/// <summary>
/// The optional extras a hardware report can carry on top of the capability snapshot: what the runtime sensor check
/// has seen this session, and which USB HID collections the machine exposes.
/// </summary>
/// <remarks>
/// This exists so that <see cref="HardwareReport.BuildMarkdown"/> keeps its old signature and its old output for every
/// caller that has nothing extra to say. A null context and an empty context produce the same report as before the
/// two sections were added, which is what keeps the existing report tests meaningful.
/// </remarks>
public sealed record HardwareReportContext
{
    public HardwareCapabilities? BackendCapabilities { get; init; }
    /// <summary>The session's runtime sensor verification, when a live backend is updating one.</summary>
    public RuntimeSensorVerification? SensorTrust { get; init; }

    /// <summary>The USB HID collections found on this machine. Empty when nothing was enumerated.</summary>
    public IReadOnlyList<UsbHidDevice> UsbHid { get; init; } = Array.Empty<UsbHidDevice>();

    /// <summary>
    /// What the keyboard-lighting transports have learned this session: the remembered WMI-or-HID choice per
    /// <c>Model|BIOS</c>, and what the HID probe saw. One line each, already worded for the report.
    /// </summary>
    public IReadOnlyList<string> KeyboardLighting { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The last <c>GetGamingMiscSetting(0x02)</c> word the worker answered this session, or null when the MUX has not
    /// been read. The current mode the selector holds, as opposed to the 0x09 "is it switchable" answer.
    /// </summary>
    public ulong? MuxWord { get; init; }

    /// <summary>True when the context carries nothing to add.</summary>
    public bool IsEmpty => BackendCapabilities == null && SensorTrust == null && UsbHid.Count == 0 && KeyboardLighting.Count == 0;

    /// <summary>A context with nothing in it.</summary>
    public static HardwareReportContext Empty { get; } = new();

    /// <summary>A context that only carries USB HID rows.</summary>
    public static HardwareReportContext ForUsbHid(IReadOnlyList<UsbHidDevice> devices) =>
        new() { UsbHid = devices };

    /// <summary>A context that only carries the sensor session.</summary>
    public static HardwareReportContext ForSensors(RuntimeSensorVerification trust) =>
        new() { SensorTrust = trust };

    /// <summary>A context that only carries the keyboard-lighting transports.</summary>
    public static HardwareReportContext ForKeyboardLighting(IReadOnlyList<string> lines) =>
        new() { KeyboardLighting = lines };
}
