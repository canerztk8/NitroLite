using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NitroLite.Core;

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
    public bool SwitchProfile(int configNumber)
    {
        if (configNumber < 1 || configNumber > 4) return false;

        byte configIndex = (byte)(configNumber - 1); // 0=Config File1, 1=Config File2, 2=Config File3, 3=Config File4

        try
        {
            // Build verified 17-byte ATK SetCurrentConfig packet with checksum
            byte[] packet = BuildSetCurrentConfigPacket(configIndex);

            // Send strictly to ATK hardware (VID_373B) matching col05 endpoint and verify ACK
            byte[]? ack = SendAndReceivePacket("vid_" + VID_ATK, "col05", packet, CMD_SET_CURRENT_CONFIG);
            if (ack != null && ack.Length >= 7 && ack[1] == CMD_SET_CURRENT_CONFIG && ack[2] == 0)
            {
                CurrentActiveProfile = configNumber;
                HasConfirmedProfile = true;
                return true;
            }

            // An explicit negative acknowledgement is a failure. A missing acknowledgement
            // is only successful if a separate read verifies the requested profile.
            if (ack == null && QueryCurrentProfile() == configNumber)
            {
                CurrentActiveProfile = configNumber;
                HasConfirmedProfile = true;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Queries the currently active onboard configuration file from the ATK mouse hardware.
    /// </summary>
    /// <returns>1-indexed configuration number (1..4), or null if query fails.</returns>
    public int? QueryCurrentProfile()
    {
        try
        {
            byte[] queryPacket = BuildGetCurrentConfigPacket();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                byte[]? response = SendAndReceivePacket("vid_" + VID_ATK, "col05", queryPacket, CMD_GET_CURRENT_CONFIG);
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
                if (attempt < 2) Thread.Sleep(100);
            }
        }
        catch { }

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

    private bool SendPacketToMatchingDevice(string vidMatch, string colMatch, byte[] packet)
    {
        lock (_hardwareLock)
        {
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);

            IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x10 | 0x02);
            if (hDevInfo == IntPtr.Zero) return false;

            bool success = false;
            try
            {
                SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = (uint)Marshal.SizeOf(ifData);

                for (uint i = 0; SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
                {
                    uint reqSize = 0;
                    SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, IntPtr.Zero, 0, ref reqSize, IntPtr.Zero);
                    if (reqSize == 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 5);
                        if (SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, detail, reqSize, ref reqSize, IntPtr.Zero))
                        {
                            IntPtr pPath = new IntPtr(detail.ToInt64() + 4);
                            string? path = Marshal.PtrToStringAuto(pPath);
                            if (!string.IsNullOrEmpty(path) &&
                                path.Contains(vidMatch, StringComparison.OrdinalIgnoreCase) &&
                                path.Contains(colMatch, StringComparison.OrdinalIgnoreCase))
                            {
                                if (WritePacketToDevice(path, packet))
                                {
                                    success = true;
                                    break;
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }

            return success;
        }
    }

    private byte[]? SendAndReceivePacket(string vidMatch, string colMatch, byte[] packet, byte expectedCmdId)
    {
        lock (_hardwareLock)
        {
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);

            IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x10 | 0x02);
            if (hDevInfo == IntPtr.Zero) return null;

            try
            {
                SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = (uint)Marshal.SizeOf(ifData);

                for (uint i = 0; SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
                {
                    uint reqSize = 0;
                    SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, IntPtr.Zero, 0, ref reqSize, IntPtr.Zero);
                    if (reqSize == 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 5);
                        if (SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, detail, reqSize, ref reqSize, IntPtr.Zero))
                        {
                            IntPtr pPath = new IntPtr(detail.ToInt64() + 4);
                            string? path = Marshal.PtrToStringAuto(pPath);
                            if (!string.IsNullOrEmpty(path) &&
                                path.Contains(vidMatch, StringComparison.OrdinalIgnoreCase) &&
                                path.Contains(colMatch, StringComparison.OrdinalIgnoreCase))
                            {
                                var response = WriteAndReadPacket(path, packet, expectedCmdId);
                                if (response != null) return response;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }

            return null;
        }
    }

    private bool WritePacketToDevice(string devicePath, byte[] packet)
    {
        SafeFileHandle handle = CreateFile(
            devicePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle.IsInvalid) return false;

        try
        {
            HidD_FlushQueue(handle);

            // 1. Try native HidD_SetOutputReport
            if (HidD_SetOutputReport(handle, packet, packet.Length))
            {
                return true;
            }

            // 2. Fallback to async Overlapped WriteFile
            IntPtr hEvent = CreateEvent(IntPtr.Zero, true, false, null);
            if (hEvent == IntPtr.Zero) return false;

            GCHandle packetHandle = GCHandle.Alloc(packet, GCHandleType.Pinned);
            bool ioPending = false;
            NativeOverlapped ov = new NativeOverlapped { EventHandle = hEvent };
            try
            {
                uint written;
                bool ok = WriteFile(handle, packet, (uint)packet.Length, out written, ref ov);
                if (ok) return true;

                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_IO_PENDING)
                {
                    ioPending = true;
                    uint waitRes = WaitForSingleObject(hEvent, 500);
                    if (waitRes == WAIT_OBJECT_0)
                    {
                        if (GetOverlappedResult(handle, ref ov, out written, false) && written > 0)
                        {
                            ioPending = false;
                            return true;
                        }
                    }
                    return false;
                }
            }
            finally
            {
                try
                {
                    if (ioPending)
                    {
                        CancelIoEx(handle, ref ov);
                        // Per Microsoft CancelIoEx documentation: must wait for cancelled I/O to fully complete
                        // so kernel driver releases the buffer and NativeOverlapped before handles/memory are freed.
                        GetOverlappedResult(handle, ref ov, out _, true);
                        ioPending = false;
                    }
                }
                catch { }
                finally
                {
                    CloseHandle(hEvent);
                    if (packetHandle.IsAllocated)
                    {
                        packetHandle.Free();
                    }
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            handle.Close();
        }

        return false;
    }

    private byte[]? WriteAndReadPacket(string devicePath, byte[] packet, byte expectedCmdId)
    {
        SafeFileHandle handle = CreateFile(
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

        byte[] rxBuf = new byte[packet.Length];
        GCHandle rxBufHandle = GCHandle.Alloc(rxBuf, GCHandleType.Pinned);
        NativeOverlapped ov = new NativeOverlapped { EventHandle = hEvent };
        bool ioPending = false;

        try
        {
            HidD_FlushQueue(handle);

            uint readBytes = 0;
            bool readOk = ReadFile(handle, rxBuf, (uint)packet.Length, out readBytes, ref ov);
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
                    }
                }
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

    #region Win32 P/Invoke

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
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, ref NativeOverlapped lpOverlapped);

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
