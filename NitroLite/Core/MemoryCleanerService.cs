using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NitroLite.Core;

public class MemoryCleanerResult
{
    public double FreedMegabytes { get; set; }
    public double AvailableGigabytes { get; set; }
    public int ProcessesOptimized { get; set; }
    public bool StandbyPurged { get; set; }
}

public class MemoryCleanerService
{
    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hwProc);

    [DllImport("ntdll.dll")]
    private static extern uint NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, int BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public LUID Luid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const int SE_PRIVILEGE_ENABLED = 0x00000002;

    private DateTime _lastAutoCleanTime = DateTime.MinValue;

    private static int GetForegroundProcessId()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                return (int)pid;
            }
        }
        catch { }
        return 0;
    }

    public bool ShouldAutoClean(double ramUsagePercent, double ramTotalMB, bool isGameActive = false)
    {
        // Total RAM must be legitimate
        if (ramTotalMB <= 0) return false;

        // Optional game active suppression
        if (isGameActive) return false;

        // Rate-limit auto cleaner to at most once per 120 seconds
        if ((DateTime.Now - _lastAutoCleanTime).TotalSeconds < 120) return false;

        double freeMB = ramTotalMB * (1.0 - (ramUsagePercent / 100.0));
        // Auto-clean only under genuine memory pressure
        return ramUsagePercent >= 88.0 || freeMB <= 2048.0;
    }

    public async Task<MemoryCleanerResult> CleanMemoryAsync(int? excludePid = null, string? excludeProcessName = null)
    {
        return await Task.Run(() => CleanMemory(excludePid, excludeProcessName));
    }

    public MemoryCleanerResult CleanMemory(int? excludePid = null, string? excludeProcessName = null)
    {
        _lastAutoCleanTime = DateTime.Now;

        var memBefore = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(memBefore);

        bool standbyPurged = TryPurgeStandbyList();
        int procCount = TrimProcessWorkingSets(excludePid, excludeProcessName);

        // Gently trim NitroLite's own heap
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, false, false);
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch { }

        var memAfter = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(memAfter);

        long freedBytes = (long)memAfter.ullAvailPhys - (long)memBefore.ullAvailPhys;
        double freedMB = freedBytes > 0 ? Math.Round(freedBytes / (1024.0 * 1024.0), 1) : 0;
        double availGB = Math.Round(memAfter.ullAvailPhys / (1024.0 * 1024.0 * 1024.0), 2);

        return new MemoryCleanerResult
        {
            FreedMegabytes = freedMB,
            AvailableGigabytes = availGB,
            ProcessesOptimized = procCount,
            StandbyPurged = standbyPurged
        };
    }

    private static bool TryPurgeStandbyList()
    {
        try
        {
            if (OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            {
                try
                {
                    if (LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out LUID luid))
                    {
                        var tp = new TOKEN_PRIVILEGES
                        {
                            PrivilegeCount = 1,
                            Luid = luid,
                            Attributes = SE_PRIVILEGE_ENABLED
                        };
                        AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                finally
                {
                    CloseHandle(token);
                }
            }

            GCHandle handle = GCHandle.Alloc(MemoryPurgeStandbyList, GCHandleType.Pinned);
            try
            {
                uint status = NtSetSystemInformation(SystemMemoryListInformation, handle.AddrOfPinnedObject(), Marshal.SizeOf(typeof(int)));
                return status == 0;
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return false;
        }
    }

    private static int TrimProcessWorkingSets(int? excludePid = null, string? excludeProcessName = null)
    {
        int count = 0;
        int currentPid = Environment.ProcessId;
        int foregroundPid = GetForegroundProcessId();

        Process[]? processes = null;
        try
        {
            processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    // Skip system idle, current process, and the active foreground user application
                    if (proc.Id <= 4 || proc.Id == currentPid) continue;
                    if (foregroundPid > 0 && proc.Id == foregroundPid) continue;
                    if (excludePid.HasValue && proc.Id == excludePid.Value) continue;

                    if (!string.IsNullOrEmpty(excludeProcessName))
                    {
                        if (string.Equals(proc.ProcessName, excludeProcessName, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    if (EmptyWorkingSet(proc.Handle) != 0)
                    {
                        count++;
                    }
                }
                catch
                {
                    // Access denied or process exited - safe to ignore
                }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            }
        }
        catch { }

        return count;
    }
}
