using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NitroLite.Core.Hardware;

namespace NitroLite.Windows.Core.Hardware;

/// <summary>
/// Lists the USB HID collections on this machine for the hardware report: vendor id, product id, usage page and
/// usage. Read-only by construction.
/// </summary>
/// <remarks>
/// <para>
/// The report needs this because a Nitro whose four-zone backlight does not answer WMI is usually a Nitro whose
/// keyboard is lit by an ITE (<c>0x048D</c>) or Sonix (<c>0x0C45</c>) controller instead, and the only way to tell
/// which from an issue report is to ask the owner to run a USB viewer — unless the report already says it.
/// </para>
/// <para>
/// Every handle opened here asks for <c>GENERIC_READ</c> only, is shared with everything else, is used for one
/// <c>HidD_GetPreparsedData</c>/<c>HidP_GetCaps</c> pair, and is closed before the next device. No IOCTL is issued,
/// no report is written, and no serial number or product string is read — the strings a HID device exposes are the
/// ones that can carry a serial, so this type never asks for them.
/// </para>
/// <para>
/// Nothing here can throw into a caller: a machine with a damaged HID stack, a device that disappears mid-enumeration
/// or a refused handle all end as "no rows", which the report renders as "none found".
/// </para>
/// </remarks>
public static class UsbHidInventory
{
    /// <summary>Most rows the report shows. A laptop exposes dozens of HID collections and the report is pasted into an issue.</summary>
    public const int MaximumRows = 12;

    private static IReadOnlyList<UsbHidDevice>? _cached;

    /// <summary>
    /// The collections found on this machine, cached for the process because the report is rebuilt every time the
    /// clipboard button is pressed and the enumeration is a SetupAPI walk. Returns an empty list on any failure.
    /// </summary>
    public static IReadOnlyList<UsbHidDevice> Read()
    {
        if (_cached != null) return _cached;

        IReadOnlyList<UsbHidDevice> devices;
        try
        {
            devices = Enumerate();
        }
        catch
        {
            devices = Array.Empty<UsbHidDevice>();
        }

        _cached = devices;
        return devices;
    }

    private static IReadOnlyList<UsbHidDevice> Enumerate()
    {
        HidD_GetHidGuid(out var hidGuid);

        IntPtr deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1)) return Array.Empty<UsbHidDevice>();

        var found = new List<UsbHidDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };

            for (uint index = 0;
                 SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData);
                 index++)
            {
                var path = InterfacePath(deviceInfoSet, ref interfaceData);
                if (path == null) continue;

                var device = Describe(path);
                if (device == null) continue;

                // A device exposes one collection per usage; a keyboard alone can answer three times. The report
                // lists distinct capabilities, not repeats of the same one.
                if (!seen.Add($"{device.VendorId:X4}:{device.ProductId:X4}:{device.UsagePage:X4}:{device.Usage:X4}"))
                    continue;

                found.Add(device);
                if (found.Count >= MaximumRows) break;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        // Keyboards first, then the vendor-defined collections a lighting backend would use, then the rest: the first
        // rows are the ones an issue triager reads.
        return found
            .OrderByDescending(d => d.IsInputDevice)
            .ThenByDescending(d => d.IsVendorDefined)
            .ThenBy(d => d.VendorId)
            .ThenBy(d => d.ProductId)
            .ToList();
    }

    /// <summary>The device interface path for one entry, or null when SetupAPI would not give one.</summary>
    private static string? InterfacePath(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        uint required = 0;
        SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
        if (required == 0) return null;

        IntPtr detail = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 8 on 64-bit and 6 on 32-bit; the struct is variable
            // length, so it is written by hand rather than declared.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detail, required, ref required, IntPtr.Zero))
                return null;

            return Marshal.PtrToStringAuto(new IntPtr(detail.ToInt64() + 4));
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    /// <summary>
    /// Reads the four numbers for one interface path, or returns null when the device is not one the report lists.
    /// </summary>
    private static UsbHidDevice? Describe(string path)
    {
        using SafeFileHandle handle = CreateFile(
            path,
            GenericRead,
            FileShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid) return null;

        int vendorId;
        int productId;
        if (HidD_GetAttributes(handle, out var attributes))
        {
            vendorId = attributes.VendorID;
            productId = attributes.ProductID;
        }
        else
        {
            // Fall back to the path, which carries the ids as text on every Windows version.
            vendorId = ParsePathId(path, "vid_");
            productId = ParsePathId(path, "pid_");
        }

        if (vendorId <= 0 || productId <= 0) return null;

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

        var device = new UsbHidDevice
        {
            VendorId = vendorId,
            ProductId = productId,
            UsagePage = usagePage,
            Usage = usage
        };

        // Keyboard collections and the vendor-defined pages a lighting controller uses. A touchpad, a sensor hub and
        // a mouse are not what this section is for, so they are left out rather than listed for completeness.
        return device.IsInputDevice || device.IsVendorDefined ? device : null;
    }

    /// <summary>The four hex digits after <paramref name="marker"/> in a device interface path, or 0.</summary>
    private static int ParsePathId(string path, string marker)
    {
        int at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return 0;
        at += marker.Length;
        if (at + 4 > path.Length) return 0;
        return int.TryParse(path.AsSpan(at, 4), System.Globalization.NumberStyles.HexNumber, null, out int value)
            ? value
            : 0;
    }

    /// <summary>Forgets the cache. Only used by tests that need a fresh walk.</summary>
    internal static void ResetCache() => _cached = null;

    #region Win32 P/Invoke

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
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
