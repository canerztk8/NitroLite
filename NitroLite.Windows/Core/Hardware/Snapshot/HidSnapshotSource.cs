using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NitroLite.Core.Hardware.Snapshot;

namespace NitroLite.Windows.Core.Hardware.Snapshot;

/// <summary>
/// The vendor HID half of a snapshot: the feature report of each Acer keyboard-lighting controller, read through a
/// handle that was opened with read access and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// One handle per interface, opened <see cref="GenericRead"/> only, used to ask for the vendor and product ids, the
/// usage page and the feature-report length, and then — for the interfaces
/// <see cref="HidSnapshotFilter.IsEligible"/> accepted — to read one <c>HidD_GetFeature</c> report. No write access is
/// ever requested, so a bug here cannot put a lighting packet into a keyboard.
/// </para>
/// <para>
/// The standard keyboard collection is refused by page before any of that, and the refusal is not logged with an entry:
/// the report is about the lighting controller, and a line saying "there is a keyboard" adds nothing to a diff.
/// </para>
/// <para>
/// The whole walk is wrapped: a machine with a damaged HID stack answers "unavailable", which is the same state as a
/// machine with no controller, and neither is allowed to take the WMI half of the capture down with it.
/// </para>
/// </remarks>
internal sealed class HidSnapshotSource : IHardwareSnapshotSource
{
    /// <summary>The section these entries are filed under.</summary>
    public const string SectionName = "hid";

    /// <summary>How many controllers one capture reads. A chassis has one; a dock with a second keyboard does not double it.</summary>
    private const int MaximumDevices = 8;

    public string Section => SectionName;

    public Task<IReadOnlyList<HardwareSnapshotEntry>> CaptureAsync(CancellationToken cancellationToken)
    {
        var entries = new List<HardwareSnapshotEntry>();
        try
        {
            CaptureCore(entries, cancellationToken);
        }
        catch (Exception ex)
        {
            entries.Add(HardwareSnapshotEntry.Unavailable(SectionName, "capture", $"{ex.GetType().Name}: {ex.Message}"));
        }

        return Task.FromResult<IReadOnlyList<HardwareSnapshotEntry>>(entries);
    }

    private static void CaptureCore(List<HardwareSnapshotEntry> entries, CancellationToken cancellationToken)
    {
        HidD_GetHidGuid(out var hidGuid);
        IntPtr deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
        {
            entries.Add(HardwareSnapshotEntry.Unavailable(SectionName, "capture", "SetupAPI refused the HID class enumeration"));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };

            for (uint index = 0;
                 SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData);
                 index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= MaximumDevices) break;

                var path = InterfacePath(deviceInfoSet, ref interfaceData);
                if (path == null) continue;

                var entry = Read(path);
                if (entry == null) continue;
                if (!seen.Add(entry.Label)) continue;

                entries.Add(entry);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    /// <summary>
    /// One interface: the identity, the eligibility decision, and — only for an accepted vendor lighting page — the
    /// feature report. Null when the interface is not a lighting controller this build reads.
    /// </summary>
    private static HardwareSnapshotEntry? Read(string path)
    {
        using SafeFileHandle handle = CreateFile(
            path, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        if (!HidD_GetAttributes(handle, out var attributes)) return null;
        if (attributes.VendorID == 0 || attributes.ProductID == 0) return null;

        int usagePage = 0;
        int usage = 0;
        int featureLength = 0;
        if (HidD_GetPreparsedData(handle, out IntPtr preparsed) && preparsed != IntPtr.Zero)
        {
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    featureLength = caps.FeatureReportByteLength;
                }
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        if (!HidSnapshotFilter.IsEligible(attributes.VendorID, usagePage, usage)) return null;

        var key = HidSnapshotFilter.Key(attributes.VendorID, attributes.ProductID, usagePage, usage);
        if (featureLength <= 0)
        {
            // A vendor page with no feature report is still worth recording: it is the interface a different firmware
            // would have to be driven through, and "no feature report" is why the diff cannot show it changing.
            return new HardwareSnapshotEntry(SectionName, key, "no feature report");
        }

        var buffer = new byte[featureLength];
        if (HidD_GetFeature(handle, buffer, (uint)buffer.Length))
            return HardwareSnapshotEntry.FromBytes(SectionName, key, buffer);

        return HardwareSnapshotEntry.Unavailable(
            SectionName,
            key,
            string.Create(CultureInfo.InvariantCulture, $"HidD_GetFeature refused (0x{Marshal.GetLastWin32Error():X8})"));
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

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

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
