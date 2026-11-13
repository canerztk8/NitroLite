using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NitroLite.Core;

internal enum AtkMouseResult
{
    Confirmed,
    ReceiverNotFound,
    NoResponse,
    CommunicationError,
    ProfileNotConfirmed,
    Rejected
}

public class AtkMouseService
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private const int ERROR_IO_PENDING = 997;
    private const int ERROR_NO_MORE_ITEMS = 259;
    private const int ERROR_OPERATION_ABORTED = 995;

    // ATK Official Vendor ID (14139 in decimal).
    // Note: VID 3554 is VXE mechanical keyboard dongle and MUST NEVER be targeted!
    public const string VID_ATK = "373b";

    // Known Output Report IDs and Lengths for ATK Mouse Controllers
    public const byte REPORT_ID_ATK_COL05 = 8; // 0x08
    public const int REPORT_LEN_ATK_COL05 = 17;

    // ATK Hardware Protocol Opcodes (ATK A9 Wireless Charging & Compx Mouse Protocol):
    // GetCurrentConfig = 14 (0x0E)
    // SetCurrentConfig = 15 (0x0F) -> Directly switches between Config File 1, 2, 3, 4
    public const byte CMD_GET_CURRENT_CONFIG = 14; // 0x0E
    public const byte CMD_SET_CURRENT_CONFIG = 15; // 0x0F

    private static readonly object _hardwareLock = new();

    public int CurrentActiveProfile { get; private set; } = 1;

    /// <summary>True once the mouse has acknowledged a switch or answered a profile query in this session.</summary>
    public bool HasConfirmedProfile { get; private set; }

    /// <summary>
    /// Switches the ATK A9 mouse to the target configuration file (Config File 1..4).
    /// Sends raw HID Output Report with hardware checksum directly to the ATK mouse endpoint.
    /// </summary>
    /// <param name="configNumber">1-indexed configuration number (1..4 corresponding to Config File 1..4)</param>
    /// <returns>True if the command was successfully written and accepted by mouse hardware, false otherwise.</returns>
    public bool SwitchProfile(int configNumber) => ApplyProfile(configNumber) == AtkMouseResult.Confirmed;

    internal AtkMouseResult ApplyProfile(int configNumber)
    {
        if (configNumber < 1 || configNumber > 4) return AtkMouseResult.Rejected;

        byte configIndex = (byte)(configNumber - 1); // 0=Config File1, 1=Config File2, 2=Config File3, 3=Config File4

        try
        {
            // Build verified 17-byte ATK SetCurrentConfig packet with checksum
            byte[] packet = BuildSetCurrentConfigPacket(configIndex);

            // Send strictly to ATK hardware (VID_373B) matching col05 endpoint and verify ACK
            byte[]? ack = SendAndReceivePacket("vid_" + VID_ATK, "col05", packet, CMD_SET_CURRENT_CONFIG, out var result);
            if (ack != null)
            {
                if (ack[2] != 0) return AtkMouseResult.Rejected;
                CurrentActiveProfile = configNumber;
                HasConfirmedProfile = true;
                return AtkMouseResult.Confirmed;
            }

            // A sent command with no acknowledgement may still have applied.
            if (result != AtkMouseResult.NoResponse) return result;
            int? current = QueryCurrentProfile(out var queryResult);
            if (current == configNumber) return AtkMouseResult.Confirmed;
            return current.HasValue ? AtkMouseResult.ProfileNotConfirmed : queryResult;
        }
        catch
        {
            return AtkMouseResult.CommunicationError;
        }
    }

    /// <summary>
    /// Queries the currently active onboard configuration file from the ATK mouse hardware.
    /// </summary>
    /// <returns>1-indexed configuration number (1..4), or null if query fails.</returns>
    public int? QueryCurrentProfile() => QueryCurrentProfile(out _);

    internal int? QueryCurrentProfile(out AtkMouseResult result)
    {
        result = AtkMouseResult.CommunicationError;
        try
        {
            byte[] queryPacket = BuildGetCurrentConfigPacket();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                byte[]? response = SendAndReceivePacket("vid_" + VID_ATK, "col05", queryPacket, CMD_GET_CURRENT_CONFIG, out result);
                if (response != null && response.Length >= 7 && response[1] == CMD_GET_CURRENT_CONFIG && response[2] == 0)
                {
                    byte activeIdx = response[6];
                    if (activeIdx < 4)
                    {
                        int activeNumber = activeIdx + 1;
                        CurrentActiveProfile = activeNumber;
                        HasConfirmedProfile = true;
                        return activeNumber;
                    }
                }
                if (response != null) result = AtkMouseResult.ProfileNotConfirmed;
                if (result == AtkMouseResult.ReceiverNotFound) return null;
                if (attempt < 2) Thread.Sleep(100);
            }
        }
        catch { result = AtkMouseResult.CommunicationError; }

        return null;
    }

    /// <summary>
    /// Constructs the raw HID output report buffer for SetCurrentConfig (switching Config File 1..4).
    /// Buffer layout:
    ///   [0] ReportID (8)
    ///   [1] Command (15 / 0x0F = SetCurrentConfig)
    ///   [2] CommandStatus (0)
    ///   [3] EEPROM address high (0)
    ///   [4] EEPROM address low (0)
    ///   [5] DataValidLength (1)
    ///   [6] CurrentConfigFile (0=Config File1, 1=Config File2, 2=Config File3, 3=Config File4)
    ///   [7..15] 0x00 padding
    ///   [16] Checksum: 0x55 - (sum(buf[0..15]) & 0xFF)
    /// </summary>
    public static byte[] BuildSetCurrentConfigPacket(byte configIndex)
    {
        byte[] buffer = new byte[REPORT_LEN_ATK_COL05];
        buffer[0] = REPORT_ID_ATK_COL05;
        buffer[1] = CMD_SET_CURRENT_CONFIG; // 15
        buffer[2] = 0x00;
        buffer[3] = 0x00;
        buffer[4] = 0x00;
        buffer[5] = 0x01;                   // Length = 1 byte
        buffer[6] = configIndex;            // 0..3

        int sum = 0;
        for (int i = 0; i < 16; i++) sum += buffer[i];
        buffer[16] = (byte)(0x55 - (sum & 0xFF));

        return buffer;
    }

    /// <summary>
    /// Constructs the raw HID output report buffer for GetCurrentConfig.
    /// </summary>
    public static byte[] BuildGetCurrentConfigPacket()
    {
        byte[] buffer = new byte[REPORT_LEN_ATK_COL05];
        buffer[0] = REPORT_ID_ATK_COL05;
        buffer[1] = CMD_GET_CURRENT_CONFIG; // 14
        buffer[2] = 0x00;
        buffer[3] = 0x00;
        buffer[4] = 0x00;
        buffer[5] = 0x00;
        buffer[6] = 0x00;

        int sum = 0;
        for (int i = 0; i < 16; i++) sum += buffer[i];
        buffer[16] = (byte)(0x55 - (sum & 0xFF));

        return buffer;
    }

    private byte[]? SendAndReceivePacket(string vidMatch, string colMatch, byte[] packet, byte expectedCmdId, out AtkMouseResult result)
    {
        lock (_hardwareLock)
        {
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);

            result = AtkMouseResult.CommunicationError;
            IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x10 | 0x02);
            if (hDevInfo == IntPtr.Zero || hDevInfo == new IntPtr(-1)) return null;
            bool matched = false;
            bool enumerationFailed = false;

            try
            {
                SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = (uint)Marshal.SizeOf(ifData);

                for (uint i = 0; SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
                {
                    uint reqSize = 0;
                    SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, IntPtr.Zero, 0, ref reqSize, IntPtr.Zero);
                    if (reqSize == 0)
                    {
                        enumerationFailed = true;
                        continue;
                    }

                    IntPtr detail = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, detail, reqSize, ref reqSize, IntPtr.Zero))
                        {
                            IntPtr pPath = new IntPtr(detail.ToInt64() + 4);
                            string? path = Marshal.PtrToStringAuto(pPath);
                            if (!string.IsNullOrEmpty(path) &&
                                path.Contains(vidMatch, StringComparison.OrdinalIgnoreCase) &&
                                path.Contains(colMatch, StringComparison.OrdinalIgnoreCase))
                            {
                                matched = true;
                                var response = WriteAndReadPacket(path, packet, expectedCmdId, out var deviceResult);
                                if (response != null)
                                {
                                    result = AtkMouseResult.Confirmed;
                                    return response;
                                }
                                if (deviceResult == AtkMouseResult.NoResponse) result = deviceResult;
                            }
                        }
                        else enumerationFailed = true;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
                if (!matched && !enumerationFailed && Marshal.GetLastWin32Error() == ERROR_NO_MORE_ITEMS)
                    result = AtkMouseResult.ReceiverNotFound;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }

            return null;
        }
    }

    private byte[]? WriteAndReadPacket(string devicePath, byte[] packet, byte expectedCmdId, out AtkMouseResult result)
    {
        result = AtkMouseResult.CommunicationError;
        using SafeFileHandle handle = CreateFile(
            devicePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle.IsInvalid) return null;

        IntPtr hEvent = CreateEvent(IntPtr.Zero, true, false, null);
        if (hEvent == IntPtr.Zero)
        {
            handle.Close();
            return null;
        }

        // The device's own input report length, not the outgoing packet length: a longer report made ReadFile
        // fail with ERROR_INVALID_PARAMETER and the mouse looked unresponsive.
        int inputLength = GetInputReportLength(handle);
        if (inputLength <= 0) inputLength = packet.Length;

        byte[] rxBuf = new byte[inputLength];
        GCHandle rxBufHandle = GCHandle.Alloc(rxBuf, GCHandleType.Pinned);
        NativeOverlapped ov = new NativeOverlapped { EventHandle = hEvent };
        bool ioPending = false;

        try
        {
            HidD_FlushQueue(handle);

            uint readBytes = 0;
            bool readOk = ReadFile(handle, rxBuf, (uint)inputLength, out readBytes, ref ov);
            if (!readOk)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_IO_PENDING)
                {
                    ioPending = true;
                }
                else
                {
                    // Immediate I/O failure (device disconnected or bad handle)
                    return null;
                }
            }
            else
            {
                // Completed synchronously
                ioPending = false;
            }

            // Send Output Report
            bool txOk = HidD_SetOutputReport(handle, packet, packet.Length);
            if (!txOk)
            {
                // Transmission failed; exit early. Finally block safely cancels and drains pending ReadFile.
                return null;
            }
            result = AtkMouseResult.NoResponse;

            // Wait for response packet
            if (ioPending)
            {
                uint waitRes = WaitForSingleObject(hEvent, 600);
                if (waitRes == WAIT_OBJECT_0)
                {
                    if (GetOverlappedResult(handle, ref ov, out readBytes, false) && readBytes > 0)
                    {
                        ioPending = false; // Successfully reaped
                        if (readBytes == REPORT_LEN_ATK_COL05 && rxBuf[0] == REPORT_ID_ATK_COL05 && rxBuf[1] == expectedCmdId)
                        {
                            return rxBuf;
                        }
                    }
                    else
                    {
                        ioPending = false;
                        result = AtkMouseResult.CommunicationError;
                    }
                }
                else if (waitRes == WAIT_FAILED) result = AtkMouseResult.CommunicationError;
                // If waitRes == WAIT_TIMEOUT or WAIT_FAILED, ioPending remains true to be canceled in finally
            }
            else if (readBytes > 0)
            {
                if (readBytes == REPORT_LEN_ATK_COL05 && rxBuf[0] == REPORT_ID_ATK_COL05 && rxBuf[1] == expectedCmdId)
                {
                    return rxBuf;
                }
            }
        }
        catch
        {
            result = AtkMouseResult.CommunicationError;
            return null;
        }
        finally
        {
            try
            {
                if (ioPending)
                {
                    // Cancel pending I/O and wait for kernel driver to complete cancellation
                    // before unpinning memory buffers or closing handles (eliminates memory corruption).
                    CancelIoEx(handle, ref ov);
                    GetOverlappedResult(handle, ref ov, out _, true);
                    ioPending = false;
                }
            }
            catch
            {
                // Suppress driver/handle teardown errors
            }
            finally
            {
                CloseHandle(hEvent);
                if (rxBufHandle.IsAllocated)
                {
                    rxBufHandle.Free();
                }
                handle.Close();
            }
        }

        return null;
    }

    /// <summary>Input report length the device declares, or 0 when its caps cannot be read.</summary>
    private static int GetInputReportLength(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out IntPtr preparsed) || preparsed == IntPtr.Zero) return 0;
        try
        {
            return HidP_GetCaps(preparsed, out HIDP_CAPS caps) == HIDP_STATUS_SUCCESS
                ? caps.InputReportByteLength
                : 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    #region Win32 P/Invoke

    private const int HIDP_STATUS_SUCCESS = 0x00110000;

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

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FlushQueue(SafeFileHandle hidDeviceObject);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, ref uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, [Out] byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIo(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid interfaceClassGuid;
        public uint flags;
        public IntPtr reserved;
    }

    #endregion
}
