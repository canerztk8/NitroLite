using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NitroLite.Core;

/// <summary>
/// The drive temperature straight from the storage stack: <c>IOCTL_STORAGE_QUERY_PROPERTY</c> with
/// <c>StorageDeviceTemperatureProperty</c>, then <c>StorageAdapterTemperatureProperty</c>, on <c>\\.\PhysicalDriveN</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the fallback for <c>MSFT_StorageReliabilityCounter.Temperature</c>, which is 0 or missing on many NVMe
/// drivers and needs elevation. The query is read-only and the handle is opened with no access rights at all
/// (<c>IOCTL_STORAGE_QUERY_PROPERTY</c> is <c>FILE_ANY_ACCESS</c>), so it works without admin and cannot change the disk.
/// </para>
/// <para>
/// Every constant and layout here was taken from the Windows SDK 10.0.26100.0 headers, not from memory; the comments
/// name the line each one comes from. Every failure is quiet (null): the cell stays "--" rather than logging per tick.
/// </para>
/// </remarks>
internal static class StorageTemperatureIoctl
{
    // um\winioctl.h:213  #define FILE_DEVICE_MASS_STORAGE 0x0000002d
    // um\winioctl.h:342  #define IOCTL_STORAGE_BASE FILE_DEVICE_MASS_STORAGE
    // um\winioctl.h:422  #define IOCTL_STORAGE_QUERY_PROPERTY CTL_CODE(IOCTL_STORAGE_BASE, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS)
    // um\winioctl.h:269  CTL_CODE = (DeviceType << 16) | (Access << 14) | (Function << 2) | Method
    // um\winioctl.h:287  METHOD_BUFFERED 0;  um\winioctl.h:316  FILE_ANY_ACCESS 0
    internal const uint IoctlStorageQueryProperty = (0x0000002dU << 16) | (0U << 14) | (0x0500U << 2) | 0U; // 0x002D1400

    // um\winioctl.h:1149-1172  enum _STORAGE_PROPERTY_ID: StorageDeviceIoCapabilityProperty = 48,
    // StorageAdapterProtocolSpecificProperty (49), StorageDeviceProtocolSpecificProperty (50),
    // StorageAdapterTemperatureProperty (51), StorageDeviceTemperatureProperty (52).
    internal const int StorageAdapterTemperatureProperty = 51;
    internal const int StorageDeviceTemperatureProperty = 52;

    // um\winioctl.h:1112-1114  enum _STORAGE_QUERY_TYPE: PropertyStandardQuery = 0
    internal const int PropertyStandardQuery = 0;

    // um\winioctl.h:1198-1217  STORAGE_PROPERTY_QUERY { STORAGE_PROPERTY_ID PropertyId; STORAGE_QUERY_TYPE QueryType;
    // BYTE AdditionalParameters[1]; } -> 4 + 4 + 1, padded to the enum alignment = 12 bytes.
    internal const int PropertyQuerySize = 12;

    // um\winioctl.h:2815-2840  STORAGE_TEMPERATURE_DATA_DESCRIPTOR:
    //   DWORD Version @0, DWORD Size @4, SHORT CriticalTemperature @8, SHORT WarningTemperature @10, WORD InfoCount @12,
    //   BYTE Reserved0[2] @14, DWORD Reserved1[2] @16, STORAGE_TEMPERATURE_INFO TemperatureInfo[ANYSIZE_ARRAY] @24.
    internal const int DescriptorHeaderSize = 24;

    // um\winioctl.h:2800-2813  STORAGE_TEMPERATURE_INFO:
    //   WORD Index @0, SHORT Temperature @2, SHORT OverThreshold @4, SHORT UnderThreshold @6,
    //   BOOLEAN x3 @8..10, BYTE Reserved0 @11, DWORD Reserved1 @12 -> 16 bytes.
    internal const int TemperatureInfoSize = 16;

    // sizeof(STORAGE_TEMPERATURE_DATA_DESCRIPTOR) with ANYSIZE_ARRAY = 1 (um\winnt.h:51). um\winioctl.h:2778-2779:
    // "the older properties used the sizeof as a version", so Version is at least this on any driver built against it.
    internal const int DescriptorStructSize = DescriptorHeaderSize + TemperatureInfoSize; // 40

    // um\winioctl.h:2796-2798  "the smallest value of SHORT type - 0x8000 indicates the value is not reported".
    internal const short TemperatureNotReported = short.MinValue;

    /// <summary>Readings outside this window are a driver fault (or Kelvin), never a drive temperature.</summary>
    internal const int MinPlausibleCelsius = -40;
    internal const int MaxPlausibleCelsius = 125;

    /// <summary>NVMe allows a composite and eight sensors; 512 bytes holds 30 entries, far more than any drive reports.</summary>
    private const int OutputBufferSize = 512;

    // um\winnt.h:15303-15304  FILE_SHARE_READ 0x00000001, FILE_SHARE_WRITE 0x00000002
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    // um\fileapi.h:33  OPEN_EXISTING 3
    private const uint OpenExisting = 3;

    /// <summary>
    /// The drive's temperature in °C, or null when the driver answers neither property or the answer is implausible.
    /// </summary>
    public static int? QueryCelsius(int diskNumber)
    {
        if (diskNumber < 0) return null;

        try
        {
            // Zero desired access: only the query is possible on this handle, and it needs no elevation.
            using SafeFileHandle handle = CreateFile(
                $@"\\.\PhysicalDrive{diskNumber}", 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            return QueryProperty(handle, StorageDeviceTemperatureProperty)
                   ?? QueryProperty(handle, StorageAdapterTemperatureProperty);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException
                                       or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static int? QueryProperty(SafeFileHandle handle, int propertyId)
    {
        var query = new byte[PropertyQuerySize];
        BinaryPrimitives.WriteInt32LittleEndian(query.AsSpan(0, 4), propertyId);
        BinaryPrimitives.WriteInt32LittleEndian(query.AsSpan(4, 4), PropertyStandardQuery);

        var output = new byte[OutputBufferSize];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, output, output.Length,
                out int returned, IntPtr.Zero))
        {
            return null;
        }

        if (returned <= 0 || returned > output.Length) return null;
        return ParseDescriptor(output.AsSpan(0, returned));
    }

    /// <summary>
    /// Reads a <c>STORAGE_TEMPERATURE_DATA_DESCRIPTOR</c>. Pure: no I/O, every field bounds-checked, null on anything
    /// that does not look like the header's layout.
    /// </summary>
    /// <remarks>
    /// The entry whose <c>Index</c> is 0 wins, because the header says index 0 "may indicate a composite value"
    /// (um\winioctl.h:2802) — on NVMe that is the composite temperature the drive itself throttles on. When index 0 is
    /// missing or not reported, the first plausible sensor in array order is used.
    /// </remarks>
    public static int? ParseDescriptor(ReadOnlySpan<byte> data)
    {
        if (data.Length < DescriptorStructSize) return null;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        int infoCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12, 2));

        // Version is sizeof(descriptor) (40 in this SDK, larger if the struct ever grows); a smaller value is a zeroed
        // or foreign buffer. Size is the total the driver filled in and must fit inside what it actually returned.
        if (version < DescriptorStructSize || version > OutputBufferSize) return null;
        if (size < DescriptorStructSize || size > (uint)data.Length) return null;
        if (infoCount == 0) return null;

        int entriesThatFit = ((int)size - DescriptorHeaderSize) / TemperatureInfoSize;
        int count = Math.Min(infoCount, entriesThatFit);

        int? firstPlausible = null;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = data.Slice(DescriptorHeaderSize + i * TemperatureInfoSize, TemperatureInfoSize);
            ushort index = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            short celsius = BinaryPrimitives.ReadInt16LittleEndian(entry.Slice(2, 2));

            if (!IsPlausible(celsius)) continue;
            if (index == 0) return celsius;
            firstPlausible ??= celsius;
        }

        return firstPlausible;
    }

    internal static bool IsPlausible(int celsius) =>
        celsius != TemperatureNotReported && celsius is >= MinPlausibleCelsius and <= MaxPlausibleCelsius;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer,
        int nInBufferSize, [Out] byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);
}
