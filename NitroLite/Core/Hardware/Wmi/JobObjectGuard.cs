using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// Ties the elevated worker's lifetime to the window that started it, using a Windows job object.
/// <para>
/// The job is created with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: when the last handle to the job closes — which
/// happens when this process exits, crashes or is killed — the operating system terminates every process in it. That
/// is a kernel guarantee, not a polling loop, so there is no window in which an orphaned administrator process is
/// still holding hardware control after the window is gone.
/// </para>
/// <para>
/// <b>It is not the only guarantee, and on the common path it is not even the working one.</b> A medium-integrity
/// process cannot obtain <c>PROCESS_SET_QUOTA</c> on a high-integrity child, so <see cref="Assign"/> fails with
/// access denied exactly when the child is the elevated worker. The worker therefore also watches
/// <c>--parent-pid</c> itself and exits when the parent disappears; that watchdog is what actually protects the
/// elevated case. The job object still covers a non-elevated child, and it costs nothing to keep both.
/// </para>
/// </summary>
public sealed class JobObjectGuard : IDisposable
{
    /// <summary>Terminate every process in the job when the last job handle closes.</summary>
    public const uint LimitKillOnJobClose = 0x2000;

    /// <summary><c>JobObjectExtendedLimitInformation</c>, the only class that carries the limit flags we need.</summary>
    public const int ExtendedLimitInformationClass = 9;

    private IntPtr _handle;
    private bool _disposed;

    private JobObjectGuard(IntPtr handle) => _handle = handle;

    /// <summary>True when a process was successfully placed in the job.</summary>
    public bool IsAssigned { get; private set; }

    /// <summary>The Win32 error from the last failed call, for the log. 0 when nothing failed.</summary>
    public int LastError { get; private set; }

    /// <summary>
    /// Creates the job and arms the kill-on-close limit. Returns null when the OS refuses, which is not fatal: the
    /// worker's own parent watchdog still applies.
    /// </summary>
    public static JobObjectGuard? Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return null;

        var guard = new JobObjectGuard(handle);
        if (!guard.ArmKillOnClose())
        {
            guard.Dispose();
            return null;
        }

        return guard;
    }

    private bool ArmKillOnClose()
    {
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = LimitKillOnJobClose }
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (SetInformationJobObject(_handle, ExtendedLimitInformationClass, buffer, (uint)size)) return true;
            LastError = Marshal.GetLastWin32Error();
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Places <paramref name="process"/> in the job. Returns false — with <see cref="LastError"/> set — when the
    /// handle cannot be opened, which is the expected outcome for an elevated child.
    /// </summary>
    public bool Assign(Process process)
    {
        if (_disposed || _handle == IntPtr.Zero || process == null) return false;

        try
        {
            if (!AssignProcessToJobObject(_handle, process.Handle))
            {
                LastError = Marshal.GetLastWin32Error();
                return false;
            }

            IsAssigned = true;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // process.Handle throws when the process has exited or when this process lacks the rights to open it.
            LastError = ex is System.ComponentModel.Win32Exception win32 ? win32.NativeErrorCode : 0;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
