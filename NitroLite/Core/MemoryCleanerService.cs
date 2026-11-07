using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NitroLite.Core;

public class MemoryCleanerResult
{
    /// <summary>Growth of free memory (free + zeroed pages) caused by the purge.</summary>
    public double FreedMegabytes { get; set; }
    public double AvailableGigabytes { get; set; }
    /// <summary>Kept for compatibility; other processes are no longer trimmed, so this is always 0.</summary>
    public int ProcessesOptimized { get; set; }
    public bool StandbyPurged { get; set; }
    /// <summary>True when no purge was possible: not elevated and the no-prompt task is not set up (see <see cref="TaskState"/>).</summary>
    public bool RequiresAdministrator { get; set; }
    /// <summary>State of the no-prompt task when NitroLite is not elevated; null when it was not consulted.</summary>
    public StandbyPurgeTaskState? TaskState { get; set; }
    /// <summary>Exit code of the purge helper run by the task (0 = purged); null when the task did not run or finish.</summary>
    public int? TaskExitCode { get; set; }
    public string? Error { get; set; }
}

/// <summary>Sizes of the Windows memory lists, readable without administrator rights.</summary>
public readonly record struct MemoryListSizes(double FreeGigabytes, double StandbyGigabytes);

public class MemoryCleanerService
{
    [DllImport("ntdll.dll")]
    private static extern uint NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

    /// <summary>
    /// Runs the purge through the elevated scheduled task when NitroLite itself is not elevated.
    /// Null (the default) keeps the old behaviour: purge only when elevated.
    /// </summary>
    public StandbyPurgeTask? PurgeTask { get; set; }

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

    /// <summary>Automatic purge: real free memory below this…</summary>
    public const double AutoCleanFreeBelowGb = 1.0;
    /// <summary>…while the standby list holds more than this (otherwise a purge would free nothing).</summary>
    public const double AutoCleanStandbyAboveGb = 1.0;
    public static readonly TimeSpan AutoCleanInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Automatic purge rule (the same idea as ISLC): purge when the real free list is low while the standby list is
    /// large. That is the situation where Windows hands standby pages back slowly and games stutter. The "RAM used %"
    /// figure cannot see it: it counts standby as available, so it shows ~55% while only 1 GB is truly free.
    /// It also runs during games: that is where the stutter happens, and the purge itself takes ~0.2 s.
    /// At most once per <see cref="AutoCleanInterval"/>; a manual clean also resets the interval.
    /// </summary>
    public bool ShouldAutoClean(MemoryListSizes? lists)
    {
        if (lists is not { } l) return false;
        if (DateTime.Now - _lastAutoCleanTime < AutoCleanInterval) return false;
        return l.FreeGigabytes < AutoCleanFreeBelowGb && l.StandbyGigabytes > AutoCleanStandbyAboveGb;
    }

    public async Task<MemoryCleanerResult> CleanMemoryAsync(int? excludePid = null, string? excludeProcessName = null)
    {
        return await Task.Run(() => CleanMemory(excludePid, excludeProcessName));
    }

    /// <summary>True when the current process runs elevated (needed to purge the standby list).</summary>
    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Free and standby list sizes from SYSTEM_MEMORY_LIST_INFORMATION (no privilege needed to read).
    /// Layout (ULONG_PTR each): ZeroPageCount, FreePageCount, ModifiedPageCount, ModifiedNoWritePageCount,
    /// BadPageCount, PageCountByPriority[8] (the standby list), RepurposedPagesByPriority[8], ModifiedPageCountPageFile.
    /// </summary>
    public static MemoryListSizes? ReadMemoryLists()
    {
        const int size = 22 * 8;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQuerySystemInformation(SystemMemoryListInformation, buffer, size, out int returned) != 0 || returned < 13 * 8) return null;
            var v = new long[13];
            Marshal.Copy(buffer, v, 0, v.Length);
            long standbyPages = 0;
            for (int i = 5; i < 13; i++) standbyPages += v[i];
            const double pageGb = 4096.0 / (1024 * 1024 * 1024);
            return new MemoryListSizes(Math.Round((v[0] + v[1]) * pageGb, 2), Math.Round(standbyPages * pageGb, 2));
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Purges the Windows standby list and trims NitroLite's own memory. Elevated: directly. Not elevated: through
    /// the no-prompt scheduled task when <see cref="PurgeTask"/> is set and installed; otherwise nothing is purged.
    /// </summary>
    /// <remarks>
    /// It does not empty the working sets of other processes. That only pushes their memory to the standby list
    /// (which Windows already counts as available), and every program, a game included, then stutters while paging
    /// it back in. The parameters are kept for compatibility and are not used.
    /// </remarks>
    public MemoryCleanerResult CleanMemory(int? excludePid = null, string? excludeProcessName = null)
    {
        _lastAutoCleanTime = DateTime.Now;
        var result = new MemoryCleanerResult();

        var listsBefore = ReadMemoryLists();
        var memBefore = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(memBefore);

        if (IsRunningAsAdministrator())
        {
            result.StandbyPurged = TryPurgeStandbyList();
        }
        else if (PurgeTask != null)
        {
            var state = PurgeTask.GetState();
            result.TaskState = state;
            if (state == StandbyPurgeTaskState.Ready)
            {
                result.TaskExitCode = PurgeTask.RunAsync().GetAwaiter().GetResult();
                result.StandbyPurged = result.TaskExitCode == 0;
                if (!result.StandbyPurged) result.Error = PurgeTask.LastError;
            }
            else
            {
                result.RequiresAdministrator = true;
            }
        }
        else
        {
            result.RequiresAdministrator = true;
        }

        // Gently trim NitroLite's own heap
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, false, false);
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch { }

        var listsAfter = ReadMemoryLists();
        var memAfter = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(memAfter);

        // A purge moves standby pages to the free list; "available" counts both, so measure the free list.
        double freedGb = listsBefore.HasValue && listsAfter.HasValue
            ? listsAfter.Value.FreeGigabytes - listsBefore.Value.FreeGigabytes
            : ((long)memAfter.ullAvailPhys - (long)memBefore.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
        result.FreedMegabytes = freedGb > 0 ? Math.Round(freedGb * 1024.0, 1) : 0;
        result.AvailableGigabytes = Math.Round(memAfter.ullAvailPhys / (1024.0 * 1024.0 * 1024.0), 2);
        return result;
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

}
