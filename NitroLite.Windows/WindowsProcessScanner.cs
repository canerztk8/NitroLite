using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>One process as the operating system reports it, before the scanner normalises it.</summary>
/// <remarks>
/// <see cref="Name"/> is <see cref="Process.ProcessName"/>, which has no extension, and <see cref="ExecutablePath"/> is
/// the full image path when the scanner is allowed to ask for it. Both are kept because they disagree in the case that
/// matters: a launcher such as Steam reports its own name while the game's <c>.exe</c> is what the profile names.
/// </remarks>
public readonly record struct WindowsProcessEntry(int Pid, string Name, string ExecutablePath);

/// <summary>
/// The Windows process table, read through <see cref="Process"/>.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <c>LinuxProcessScanner</c>: same <see cref="IProcessScanner"/> contract, same
/// <see cref="RunningProcess"/> shape, so <see cref="GameProfileService"/> matches a profile against either platform
/// without knowing which one it is running on.
/// </para>
/// <para>
/// The image path is fetched with <c>QueryFullProcessImageName</c> rather than <see cref="Process.MainModule"/>.
/// <c>MainModule</c> throws for a process of another user or another bitness, and a scan that died on the first
/// protected process would stop detecting games for the rest of the session. A process that refuses the query is
/// still reported by name, which is enough for every profile that names a plain executable.
/// </para>
/// <para>
/// Nothing here throws. A process can exit between the enumeration and the query, and the enumeration itself can fail
/// on a machine with a corrupted performance counter registry; both read as "no processes" rather than as a crash.
/// </para>
/// </remarks>
public sealed class WindowsProcessScanner : IProcessScanner
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly Func<IReadOnlyList<WindowsProcessEntry>> _enumerate;

    /// <summary>The live process table.</summary>
    public WindowsProcessScanner() : this(EnumerateRunningProcesses)
    {
    }

    /// <summary>
    /// A fixed table instead of the live one, so a test can assert what the matcher does with a game running without
    /// starting a game. The production constructor is the one above.
    /// </summary>
    public WindowsProcessScanner(Func<IReadOnlyList<WindowsProcessEntry>> enumerate)
    {
        _enumerate = enumerate ?? throw new ArgumentNullException(nameof(enumerate));
    }

    /// <summary>Every process this user can see, in the order the operating system returned them.</summary>
    public IReadOnlyList<RunningProcess> Scan()
    {
        var result = new List<RunningProcess>();

        IReadOnlyList<WindowsProcessEntry> entries;
        try
        {
            entries = _enumerate();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Süreç tablosu listelenemedi", ex.Message);
            return result;
        }

        foreach (var entry in entries)
        {
            if (entry.Pid <= 0) continue;

            // A process that exited between the enumeration and the read leaves both fields empty. Reporting it as a
            // nameless process would only make the matcher do work for nothing.
            if (string.IsNullOrWhiteSpace(entry.Name) && string.IsNullOrWhiteSpace(entry.ExecutablePath)) continue;

            result.Add(new RunningProcess(entry.Pid, entry.Name ?? "", entry.ExecutablePath ?? ""));
        }

        return result;
    }

    /// <summary>The live table. Every failure is swallowed per process, never for the whole scan.</summary>
    private static IReadOnlyList<WindowsProcessEntry> EnumerateRunningProcesses()
    {
        var entries = new List<WindowsProcessEntry>();

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Süreç tablosu listelenemedi", ex.Message);
            return entries;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    entries.Add(new WindowsProcessEntry(process.Id, process.ProcessName, TryGetImagePath(process.Id)));
                }
                catch (Exception ex)
                {
                    // A process that vanished mid-enumeration is normal, not an error worth a log line per process.
                    AppLog.Warn("Süreç okunamadı", ex.Message);
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// The full image path, or an empty string when the process refuses the query. An empty path is not a failure:
    /// <see cref="RunningProcess.FileName"/> falls back to the short name, which is what a profile usually names.
    /// </summary>
    public static string TryGetImagePath(int pid)
    {
        if (pid <= 0) return "";

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return "";

            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : "";
        }
        catch (Exception ex)
        {
            AppLog.Warn("Süreç yolu okunamadı", ex.Message);
            return "";
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
