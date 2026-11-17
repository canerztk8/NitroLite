using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// Creates the worker's pipe so that the window that started it can actually connect.
/// <para>
/// An elevated process creates objects with a High mandatory integrity label, and a medium-integrity client cannot
/// write to a High object however generous its DACL is: the label is a second gate that sits above the ACL. A pipe
/// built with <see cref="PipeOptions.CurrentUserOnly"/> alone therefore refuses the very process that spawned the
/// worker. Measured on an AN16-41: <c>Connect</c> threw <c>UnauthorizedAccessException: Access to the path is
/// denied</c>, and a pipe created with the default DACL refused the same way — which is what proves the label, not
/// the ACL, is the gate. Lowering the label to Medium is the fix.
/// </para>
/// <para>
/// Setting a SACL needs <c>SeSecurityPrivilege</c>, which an elevated administrator holds but a test runner does
/// not. A refusal falls back to the plain pipe: the tests run both ends in one process, where both sides carry the
/// same label and it never gets in the way.
/// </para>
/// <para>
/// The two branches are not combined. <see cref="PipeOptions.CurrentUserOnly"/> builds its own DACL, and passing it
/// alongside an explicit <see cref="PipeSecurity"/> is rejected; the explicit descriptor below is the same
/// restriction plus the label, so the fallback is the only place the flag is needed.
/// </para>
/// </summary>
internal static class WorkerPipe
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    /// <summary>
    /// The pipe the worker serves on. Never null: a machine that cannot lower the label still gets a pipe, it just
    /// gets one only a process at the same integrity level can use.
    /// </summary>
    public static NamedPipeServerStream Create(string pipeName)
    {
        var labelled = TryCreateWithMediumLabel(pipeName);
        if (labelled != null) return labelled;

        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    /// <summary>
    /// Builds the pipe with an explicit DACL and a Medium mandatory label. Returns null when this process cannot set
    /// a SACL, which is the normal case for a non-elevated caller.
    /// </summary>
    private static NamedPipeServerStream? TryCreateWithMediumLabel(string pipeName)
    {
        if (!TryEnablePrivilege("SeSecurityPrivilege")) return null;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user == null) return null;

            // Deny the network logon first — deny ACEs are evaluated before allow ACEs — then grant the signed-in
            // user exactly the rights a duplex pipe needs, then lower the mandatory label to Medium so a
            // medium-integrity client can write.
            //
            // The leading P makes the DACL protected. Without it Windows merges in whatever the \Device\NamedPipe
            // container inherits, so the ACL this code wrote is not the ACL the pipe ends up with and neither the
            // code nor a test can say what it is. The mask is FILE_GENERIC_READ | FILE_GENERIC_WRITE rather than
            // GENERIC_ALL: WRITE_DAC and WRITE_OWNER would let a client rewrite the pipe's own security descriptor.
            var sddl = $"D:P(D;;GA;;;NU)(A;;0x12019F;;;{user.Value})S:(ML;;NW;;;ME)";

            var security = new PipeSecurity();
            security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access | AccessControlSections.Audit);

            return NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity: security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException
                                      or PlatformNotSupportedException or IOException or ArgumentException)
        {
            AppLog.Warn("--wmi-worker pipe", $"Medium etiketli pipe oluşturulamadı: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Turns a privilege on for this process. False means the token does not hold it at all, which is the answer for
    /// every non-elevated caller and the reason the fallback exists.
    /// </summary>
    private static bool TryEnablePrivilege(string name)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token)) return false;
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;

            var state = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privilege = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
            };

            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)) return false;

            // AdjustTokenPrivileges answers success even when it changed nothing; the last error is the real answer.
            return Marshal.GetLastWin32Error() == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
