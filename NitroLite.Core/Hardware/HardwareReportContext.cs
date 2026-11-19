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
    /// <summary>The session's runtime sensor verification, when a live backend is updating one.</summary>
    public RuntimeSensorVerification? SensorTrust { get; init; }

    /// <summary>The USB HID collections found on this machine. Empty when nothing was enumerated.</summary>
    public IReadOnlyList<UsbHidDevice> UsbHid { get; init; } = Array.Empty<UsbHidDevice>();

    /// <summary>True when the context carries nothing to add.</summary>
    public bool IsEmpty => SensorTrust == null && UsbHid.Count == 0;

    /// <summary>A context with nothing in it.</summary>
    public static HardwareReportContext Empty { get; } = new();

    /// <summary>A context that only carries USB HID rows.</summary>
    public static HardwareReportContext ForUsbHid(IReadOnlyList<UsbHidDevice> devices) =>
        new() { UsbHid = devices };

    /// <summary>A context that only carries the sensor session.</summary>
    public static HardwareReportContext ForSensors(RuntimeSensorVerification trust) =>
        new() { SensorTrust = trust };
}
