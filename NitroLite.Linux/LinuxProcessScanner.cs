using System.Globalization;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// The Linux process table, read from <c>/proc</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two files per process, and both are needed. <c>/proc/[pid]/comm</c> is the kernel's short name, which is what a
/// native Linux game reports and what a Proton game does <em>not</em>: under Proton the short name is the wine loader
/// (<c>wine64-preloader</c>) while the game's own <c>.exe</c> only appears in <c>/proc/[pid]/cmdline</c>. Reading one
/// without the other would either miss every Proton title or match the loader for all of them.
/// </para>
/// <para>
/// Reads go through <see cref="ISysfsFileSystem"/> for the same reason the hardware backend does: a test can then
/// present a fixed process table instead of starting a game. <c>/proc</c> and <c>/sys</c> are both kernel
/// pseudo-filesystems, so the abstraction fits without a second interface.
/// </para>
/// <para>
/// Nothing here throws. A process can exit between the directory listing and the read, and a scan that died on that
/// race would stop detecting games for the rest of the session.
/// </para>
/// </remarks>
public sealed class LinuxProcessScanner : IProcessScanner
{
    private readonly ISysfsFileSystem _fs;
    private readonly string _procRoot;

    /// <summary>
    /// <paramref name="fs"/> defaults to the real file system; <paramref name="procRoot"/> exists so a test can point
    /// the scanner at a fixture tree.
    /// </summary>
    public LinuxProcessScanner(ISysfsFileSystem? fs = null, string procRoot = LinuxPaths.ProcRoot)
    {
        _fs = fs ?? RealSysfsFileSystem.Instance;
        _procRoot = string.IsNullOrWhiteSpace(procRoot) ? LinuxPaths.ProcRoot : procRoot.TrimEnd('/');
    }

    /// <summary>Every numeric entry under <c>/proc</c> that answered, in pid order.</summary>
    public IReadOnlyList<RunningProcess> Scan()
    {
        var result = new List<RunningProcess>();

        IReadOnlyList<string> entries;
        try
        {
            entries = _fs.EnumerateDirectories(_procRoot);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Süreç tablosu listelenemedi"), ex.Message);
            return result;
        }

        foreach (var directory in entries)
        {
            var pid = ParsePid(directory);
            if (pid <= 0) continue;

            var name = Read(directory, LinuxPaths.ProcComm);
            var commandLine = Read(directory, LinuxPaths.ProcCmdline);

            // A process that exited between the listing and the read leaves both files empty. Reporting it as a
            // nameless process would only make the matcher do work for nothing.
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(commandLine)) continue;

            result.Add(new RunningProcess(pid, name ?? "", FirstArgument(commandLine)));
        }

        return result;
    }

    /// <summary>The pid in a <c>/proc</c> directory name, or 0 when the name is not a number.</summary>
    public static int ParsePid(string? directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName)) return 0;

        var name = directoryName.TrimEnd('/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];

        return int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : 0;
    }

    /// <summary>
    /// The executable from a NUL-separated command line. An empty command line is normal — a kernel thread has none —
    /// and reads as an empty string rather than as a failure.
    /// </summary>
    public static string FirstArgument(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";

        int end = commandLine.IndexOf('\0');
        var first = end >= 0 ? commandLine[..end] : commandLine;
        return first.Trim();
    }

    private string? Read(string directory, string name)
    {
        try
        {
            return _fs.ReadAllText(LinuxPaths.Combine(directory, name));
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Süreç dosyası okunamadı"), $"{directory}/{name}: {ex.Message}");
            return null;
        }
    }
}
