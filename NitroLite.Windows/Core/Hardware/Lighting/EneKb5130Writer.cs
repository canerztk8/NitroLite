using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NitroLite.Core.Hardware.Lighting;

namespace NitroLite.Windows.Core.Hardware.Lighting;

/// <summary>
/// Writes four-zone colours to an ENE KB5130 through its HID feature report.
/// </summary>
/// <remarks>
/// <para>
/// The frame is built by <see cref="EneKb5130Protocol"/>, which is also what the tests read, so the bytes verified on a
/// build machine are the bytes this class sends. All this type adds is the transport: <c>HidD_SetFeature</c> with the
/// report id as the first byte, which is how Windows addresses a feature report. It does not use <c>WriteFile</c>,
/// because that reaches output reports, and the documented protocol is a feature report.
/// </para>
/// <para>
/// Nothing here throws. A controller unplugged mid-write, a driver that rejects the report, or a report length the
/// firmware never offered are all answered with false and a reason: the caller falls back to the service path and the
/// keyboard keeps its last colour.
/// </para>
/// </remarks>
internal sealed class EneKb5130Writer : IHidLightingWriter
{
    private readonly SafeFileHandle _handle;
    private readonly int _featureReportLength;
    private bool _disposed;

    public EneKb5130Writer(SafeFileHandle handle, string description, int featureReportLength)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        Description = description ?? "";
        _featureReportLength = featureReportLength;
    }

    public string Description { get; }

    public string Protocol => "ene-kb5130";

    public bool CanWrite => !_disposed && _featureReportLength >= EneKb5130Protocol.ReportLength;

    public string? Refusal => CanWrite
        ? null
        : _featureReportLength < EneKb5130Protocol.ReportLength
            ? $"the controller offers {_featureReportLength}-byte feature reports, the frame needs {EneKb5130Protocol.ReportLength}"
            : "the controller was closed";

    public bool TryWrite(KeyboardLightingFrame frame, out string? error)
    {
        error = null;

        if (_disposed)
        {
            error = "the controller was closed";
            return false;
        }

        if (_featureReportLength < EneKb5130Protocol.ReportLength)
        {
            error = Refusal;
            return false;
        }

        if (!EneKb5130Protocol.TryBuild(frame, out var reports, out error))
            return false;

        foreach (var report in reports)
        {
            var buffer = new byte[_featureReportLength];
            report.CopyTo(buffer, 0);
            if (!HidD_SetFeature(_handle, buffer, (uint)buffer.Length))
            {
                error = $"HidD_SetFeature failed (win32 {Marshal.GetLastWin32Error()})";
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);
}
