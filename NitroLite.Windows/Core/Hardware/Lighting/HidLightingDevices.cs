using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NitroLite.Core.Hardware.Lighting;

namespace NitroLite.Windows.Core.Hardware.Lighting;

/// <summary>
/// Finds the vendor HID controller that carries this machine's keyboard colour, and opens it for exactly one kind of
/// write: the documented feature report.
/// </summary>
/// <remarks>
/// <para>
/// The walk is two-phase on purpose. Every HID interface is first opened with read access alone, only to learn its
/// vendor and product ids and its usage page; the write handle is opened afterwards, and only for an interface that
/// <see cref="HidLightingControllers.Decide"/> has already accepted. That ordering is what makes the safety rule
/// mechanical rather than a comment: a standard keyboard collection never gets a handle with write access, so no code
/// path in this build can put a lighting packet into one.
/// </para>
/// <para>
/// The result is cached for the process. A lighting write happens on every colour change and every Apply press, and a
/// SetupAPI walk per write would be the slowest part of a slider drag. <see cref="ResetCache"/> exists for the rescan
/// button and for tests.
/// </para>
/// </remarks>
public static class HidLightingDevices
{
    private static readonly object Gate = new();
    private static IHidLightingWriter? _cached;
    private static bool _probed;
    private static string _lastProbeDetail = "";
    private static DateTime _nullProbedAtUtc;

    /// <summary>How long a "no controller" answer is trusted before the next request walks the HID stack again.</summary>
    internal static readonly TimeSpan NullProbeLifetime = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: replaces the SetupAPI walk. Null in production.</summary>
    internal static Func<IHidLightingWriter?>? ProbeOverride { get; set; }

    /// <summary>Test seam: the clock for the "no controller" lifetime.</summary>
    internal static Func<DateTime> UtcNow { get; set; } = static () => DateTime.UtcNow;

    /// <summary>
    /// The controller found last, or null when none was found. A found controller is cached until a write through it
    /// fails; a "none found" answer is trusted for <see cref="NullProbeLifetime"/> only, so a keyboard that is plugged
    /// in or re-enumerated later is picked up without a restart.
    /// </summary>
    public static IHidLightingWriter? FindWriter()
    {
        lock (Gate)
        {
            if (_probed)
            {
                if (_cached != null) return _cached;
                if (UtcNow() - _nullProbedAtUtc < NullProbeLifetime) return null;
            }

            _probed = true;
            var found = ProbeOverride != null ? ProbeOverride() : Probe();
            _cached = found == null ? null : new InvalidatingWriter(found);
            if (_cached == null) _nullProbedAtUtc = UtcNow();
            return _cached;
        }
    }

    /// <summary>
    /// Drops the cached writer (disposing its handle) so the next <see cref="FindWriter"/> probes again. Called by the
    /// cached writer itself when a write fails, which is how a router's "forget the HID path" reaches this cache.
    /// </summary>
    internal static void Invalidate(IHidLightingWriter? only = null)
    {
        lock (Gate)
        {
            if (only != null && !ReferenceEquals(_cached, only)) return;
            _cached?.Dispose();
            _cached = null;
            _probed = false;
        }
    }

    /// <summary>Wraps the probed writer so a failed or throwing write invalidates the cache.</summary>
    private sealed class InvalidatingWriter : IHidLightingWriter
    {
        private readonly IHidLightingWriter _inner;
        private bool _disposed;

        public InvalidatingWriter(IHidLightingWriter inner) => _inner = inner;

        public string Description => _inner.Description;
        public string Protocol => _inner.Protocol;
        public bool CanWrite => _inner.CanWrite;
        public string? Refusal => _inner.Refusal;

        public bool TryWrite(KeyboardLightingFrame frame, out string? error)
        {
            bool ok;
            try
            {
                ok = _inner.TryWrite(frame, out error);
            }
            catch
            {
                Invalidate(this);
                throw;
            }

            if (!ok && _inner.CanWrite) Invalidate(this);
            return ok;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inner.Dispose();
        }
    }

    /// <summary>What the last probe concluded, for the log when no controller was found.</summary>
    public static string LastProbeDetail
    {
        get
        {
            lock (Gate) return _lastProbeDetail;
        }
    }

    /// <summary>Forgets the probe. Called by a rescan and by tests that need a fresh walk.</summary>
    public static void ResetCache()
    {
        lock (Gate)
        {
            _cached?.Dispose();
            _cached = null;
            _probed = false;
            _lastProbeDetail = "";
        }
    }

    /// <summary>
    /// Walks the HID interfaces and returns the first writable controller, or null. Never throws: a machine with a
    /// damaged HID stack answers "no controller", which is the same state as a machine without one.
    /// </summary>
    private static IHidLightingWriter? Probe()
    {
        try
        {
            return ProbeCore();
        }
        catch (Exception ex)
        {
            _lastProbeDetail = $"the HID probe failed ({ex.GetType().Name}: {ex.Message})";
            return null;
        }
    }

    private static IHidLightingWriter? ProbeCore()
    {
        HidD_GetHidGuid(out var hidGuid);
        IntPtr deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
        {
            _lastProbeDetail = "SetupAPI refused the HID class enumeration";
            return null;
        }

        var notes = new List<string>();
        try
        {
            var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };

            for (uint index = 0;
                 SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData);
                 index++)
            {
                var path = InterfacePath(deviceInfoSet, ref interfaceData);
                if (path == null) continue;

                var identity = ReadIdentity(path);
                if (identity == null) continue;

                var decision = HidLightingControllers.Decide(
                    identity.Value.VendorId, identity.Value.ProductId, identity.Value.UsagePage, identity.Value.Usage);

                if (!decision.Candidate) continue;

                if (!decision.Writable)
                {
                    // Recognised but undocumented. It is recorded so the report can name the controller, and it is
                    // never opened for writing.
                    notes.Add($"{decision.Description}: {decision.Reason}");
                    continue;
                }

                var writer = OpenWriter(path, decision, identity.Value);
                if (writer != null) return writer;

                notes.Add($"{decision.Description}: the write handle could not be opened");
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        _lastProbeDetail = notes.Count == 0
            ? "no Acer vendor HID lighting controller is attached"
            : string.Join("; ", notes.Distinct(StringComparer.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>
    /// The vendor and product ids and the usage page of one interface, read through a read-only handle that is closed
    /// before this returns.
    /// </summary>
    private static (int VendorId, int ProductId, int UsagePage, int Usage)? ReadIdentity(string path)
    {
        using SafeFileHandle handle = CreateFile(
            path, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        int vendorId = 0;
        int productId = 0;
        if (HidD_GetAttributes(handle, out var attributes))
        {
            vendorId = attributes.VendorID;
            productId = attributes.ProductID;
        }

        if (vendorId == 0 || productId == 0) return null;

        int usagePage = 0;
        int usage = 0;
        if (HidD_GetPreparsedData(handle, out IntPtr preparsed) && preparsed != IntPtr.Zero)
        {
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                }
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        return (vendorId, productId, usagePage, usage);
    }

    /// <summary>
    /// Opens the interface with write access — after the decision, never before — and binds the documented writer to
    /// it. The handle is owned by the writer from this point.
    /// </summary>
    private static IHidLightingWriter? OpenWriter(
        string path, HidLightingCandidate decision, (int VendorId, int ProductId, int UsagePage, int Usage) identity)
    {
        SafeFileHandle handle = CreateFile(
            path, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        int featureReportLength = 0;
        if (HidD_GetPreparsedData(handle, out IntPtr preparsed) && preparsed != IntPtr.Zero)
        {
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess)
                    featureReportLength = caps.FeatureReportByteLength;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        if (featureReportLength <= 0)
        {
            handle.Dispose();
            return null;
        }

        return new EneKb5130Writer(handle, decision.Description, featureReportLength);
    }

    private static string? InterfacePath(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        uint required = 0;
        SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
        if (required == 0) return null;

        IntPtr detail = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet, ref interfaceData, detail, required, ref required, IntPtr.Zero))
                return null;

            return Marshal.PtrToStringAuto(new IntPtr(detail.ToInt64() + 4));
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    #region Win32 P/Invoke

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, out HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, ref uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    #endregion
}
