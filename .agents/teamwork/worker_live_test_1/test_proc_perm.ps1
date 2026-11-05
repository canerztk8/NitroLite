Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class ProcCheck {
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public static int TestAccess(int pid) {
        IntPtr h = OpenProcess(PROCESS_TERMINATE, false, pid);
        if (h == IntPtr.Zero) {
            return Marshal.GetLastWin32Error();
        }
        CloseHandle(h);
        return 0; // Success
    }
}
"@

$pidToTest = 4756 # AcerEZService
$err = [ProcCheck]::TestAccess($pidToTest)
Write-Host "TestAccess for PID $pidToTest result: $err"
