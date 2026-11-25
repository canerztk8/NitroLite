using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NitroLite.Core;

public class MemoryCleanerResult
{
    /// <summary>Drop of the standby list caused by the clean, in megabytes.</summary>
    public double FreedMegabytes { get; set; }
    /// <summary>Available physical memory before the clean, in gigabytes.</summary>
    public double AvailableBeforeGigabytes { get; set; }
    public double AvailableGigabytes { get; set; }
    /// <summary>True when the standby list was actually released.</summary>
    public bool Cleaned { get; set; }
    /// <summary>True when WinMemoryCleaner is not installed; the caller offers the download page.</summary>
    public bool ToolMissing { get; set; }
    /// <summary>
    /// True when a file called WinMemoryCleaner.exe exists but is not a build NitroLite recognises, so it was not
    /// launched. The caller offers the download page and says why.
    /// </summary>
    public bool ToolUntrusted { get; set; }
    /// <summary>True when the elevation prompt was dismissed, so nothing ran.</summary>
    public bool ElevationRefused { get; set; }
    /// <summary>Exit code of WinMemoryCleaner; null when it did not run or the code could not be read.</summary>
    public int? ExitCode { get; set; }
    /// <summary>Technical failure text, not localized; the caller wraps it in a translated sentence.</summary>
    public string? Error { get; set; }
}

/// <summary>Sizes of the Windows memory lists, readable without administrator rights.</summary>
public readonly record struct MemoryListSizes(double FreeGigabytes, double StandbyGigabytes);

/// <summary>
/// Releases the Windows standby list through WinMemoryCleaner
/// (github.com/IgorMundstein/WinMemoryCleaner).
/// </summary>
/// <remarks>
/// The release package ships an unmodified copy under <c>tools\</c>, so the card works without a separate
/// installation. WinMemoryCleaner is GPL-3.0 and NitroLite is MIT; the two travel together as an aggregate
/// (GPL-3.0 section 5) because NitroLite only starts the tool as a child process and never links against it.
/// The package carries the tool's licence, a notice and its source archive; see THIRD-PARTY-NOTICES.md.
/// A copy the user drops into NitroLite's data folder still wins, so a newer upstream build can be tried
/// without waiting for a NitroLite release.
/// </remarks>
public class MemoryCleanerService
{
    public const string ToolFileName = "WinMemoryCleaner.exe";

    // Both URLs come from the cross-platform constant, so the card that offers the download and the launcher that
    // looks for the file cannot disagree about where it comes from.
    public const string ToolProjectUrl = MemoryCleanerTool.ProjectUrl;
    public const string ToolReleasesUrl = MemoryCleanerTool.ReleasesUrl;
    public const string ToolWingetId = "IgorMundstein.WinMemoryCleaner";

    /// <summary>
    /// Only the standby list. WinMemoryCleaner's own demonstration uses this area alone, and trimming the
    /// working sets of other processes would only push their pages back into the list we just released.
    /// </summary>
    public const string CleanArguments = "/StandbyList";

    /// <summary>
    /// SHA-256 of the WinMemoryCleaner builds NitroLite is willing to launch. The tool runs with an administrator
    /// token, so "a file with the right name" is not enough: a copy dropped into a folder the user can write to
    /// would otherwise turn the RAM card into a way to run an arbitrary executable elevated.
    /// <para>
    /// The list is a set rather than a single value so a release can add the next upstream build without dropping
    /// the one users already have. <c>scripts/publish-release.ps1</c> pins the same digest for the copy it bundles;
    /// the two must be updated together.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> KnownGoodSha256 { get; } = new[]
    {
        // WinMemoryCleaner 3.0.8, the build the release package ships.
        "8b68d56c6ee28740f76513f21b21f5a1018f0ef291467b3f3d39d43daf0c0f2f"
    };

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private const int SystemMemoryListInformation = 80;

    /// <summary>True when the current process runs elevated; WinMemoryCleaner needs that to release the list.</summary>
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
    /// The copy that ships inside the release package, or null when NitroLite runs from a build folder that has
    /// no <c>tools\</c> directory. The path is returned whether or not the file exists; <see cref="FindTool"/>
    /// does the existence check.
    /// </summary>
    public static string? BundledToolPath
    {
        get
        {
            try
            {
                string baseDirectory = AppContext.BaseDirectory;
                if (string.IsNullOrEmpty(baseDirectory)) return null;
                return Path.Combine(baseDirectory, "tools", ToolFileName);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Full path of the first WinMemoryCleaner NitroLite can see, or null when there is none. This answers "is the
    /// tool installed", not "may it be launched": <see cref="FindVerifiedTool"/> is the one that decides that.
    /// </summary>
    public static string? FindTool()
    {
        foreach (string candidate in ToolCandidates())
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // A path the environment cannot even build must not stop the search.
            }
        }
        return null;
    }

    /// <summary>
    /// Full path of the first WinMemoryCleaner whose contents match a build NitroLite recognises, or null when every
    /// candidate is either missing or unrecognised. A candidate that fails the digest check is skipped rather than
    /// reported, so a tampered copy in the data folder falls through to the bundled one instead of disabling the
    /// card.
    /// </summary>
    public static string? FindVerifiedTool()
    {
        using var held = OpenVerifiedTool();
        return held?.Name;
    }

    /// <summary>
    /// The first candidate whose contents match a build NitroLite recognises, returned <em>open</em>: read access,
    /// shared for reading only. Null when there is none. The caller keeps it open until the launched process has
    /// started.
    /// </summary>
    /// <remarks>
    /// Hashing a path and then launching the same path is a check and a use with a gap between them, and the data
    /// folder candidate is one any same-user process can write to: swap the file after the hash and the elevated
    /// launch runs the replacement. The handle closes that gap. It is opened once, the digest is read through it,
    /// and while it stays open nobody can open the file for writing, delete it or rename it — <see cref="FileShare.Read"/>
    /// grants neither write nor delete sharing — while the loader can still open it to run it, because it only asks
    /// to read.
    /// </remarks>
    internal static FileStream? OpenVerifiedTool()
    {
        foreach (string candidate in ToolCandidates())
        {
            FileStream? stream = null;
            try
            {
                if (!File.Exists(candidate)) continue;
                stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (IsKnownGoodContent(stream))
                {
                    var verified = stream;
                    stream = null;
                    return verified;
                }
            }
            catch
            {
                // An unreadable candidate is not a candidate.
            }
            finally
            {
                stream?.Dispose();
            }
        }
        return null;
    }

    /// <summary>
    /// True when the file's SHA-256 is one of <see cref="KnownGoodSha256"/>. A file that cannot be read is not
    /// trusted: the answer has to be "no" when the check cannot be made.
    /// </summary>
    public static bool IsKnownGood(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return IsKnownGoodContent(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The same check, over an already opened stream, read from its start.</summary>
    internal static bool IsKnownGoodContent(Stream stream)
    {
        try
        {
            if (stream.CanSeek) stream.Position = 0;
            var digest = System.Security.Cryptography.SHA256.HashData(stream);
            var hex = Convert.ToHexString(digest).ToLowerInvariant();

            foreach (var known in KnownGoodSha256)
            {
                if (string.Equals(known, hex, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException or ObjectDisposedException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// True when the file sits under the signed-in user's profile, which is where a portable or winget copy lands.
    /// Reported separately from the digest so the log can say which of the two reasons applied.
    /// </summary>
    public static bool IsInUserWritableFolder(string? path) => DirectoryTrust.IsUnderUserProfile(path);

    private static IEnumerable<string> ToolCandidates()
    {
        // A portable copy the user dropped into NitroLite's own data folder wins over everything else, so a
        // newer upstream build can be tried without waiting for a NitroLite release.
        string dataDirectory;
        try
        {
            dataDirectory = AppPaths.DataDirectory;
        }
        catch
        {
            dataDirectory = "";
        }
        if (!string.IsNullOrEmpty(dataDirectory))
            yield return Path.Combine(dataDirectory, "tools", ToolFileName);

        // The copy the release package ships. Preferred over a system-wide install because it is the build
        // this NitroLite version was tested against.
        string? bundled = BundledToolPath;
        if (bundled != null) yield return bundled;

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            // The winget shim.
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", ToolFileName);
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
            yield return Path.Combine(programFiles, "WinMemoryCleaner", ToolFileName);

        // PATH is deliberately not searched. The tool is launched with Verb = "runas", so a directory the user can
        // write to (a per-user PATH entry, a redirected profile) would turn "clean the standby list" into "run
        // whatever is called WinMemoryCleaner.exe as administrator".
        //
        // The candidates above are not all protected — the data folder and the winget shim both live under the
        // user's profile — so the location is not what makes them safe. The digest is: FindVerifiedTool refuses to
        // return a file whose SHA-256 is not a build NitroLite recognises, which is what stops a replaced copy from
        // being launched elevated.
    }

    /// <summary>Exit code of the launch script when the copy in the protected folder did not match a known digest.</summary>
    public const int ExitCopyUntrusted = 0x4E4C;

    /// <summary>
    /// The elevated launch: copy <paramref name="verifiedTool"/> alone into a new folder under <c>%SystemRoot%\Temp</c>
    /// whose DACL grants only Administrators and SYSTEM (inheritance off), re-check the copy's SHA-256 against
    /// <see cref="KnownGoodSha256"/>, run it from there with <see cref="CleanArguments"/>, and delete the folder.
    /// The script exits with the tool's exit code, or <see cref="ExitCopyUntrusted"/> when the copy does not match.
    /// </summary>
    /// <remarks>
    /// <c>%SystemRoot%\Temp</c> lets users create entries but not delete or rename other users' entries, so the folder
    /// cannot be swapped once it exists, and its name is a fresh GUID. Nothing in it can be written by the user, so no
    /// <c>.config</c> or DLL can be placed beside the exe; the working directory is the same folder.
    /// </remarks>
    internal static string BuildIsolatedLaunchScript(string verifiedTool)
    {
        var known = string.Join(", ", KnownGoodSha256.Select(h => "'" + Loc.PsQuote(h.ToLowerInvariant()) + "'"));
        return @"
$ErrorActionPreference = 'Stop'
$src = '" + Loc.PsQuote(verifiedTool) + @"'
$known = @(" + known + @")
$dir = Join-Path (Join-Path $env:SystemRoot 'Temp') ('nitrolite-wmc-' + [guid]::NewGuid().ToString('N'))
$code = 1
try {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') {
        $id = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    if (Test-Path -LiteralPath $dir) { throw 'the folder already exists' }
    [void][System.IO.Directory]::CreateDirectory($dir, $acl)
    $dst = Join-Path $dir '" + ToolFileName + @"'
    [System.IO.File]::Copy($src, $dst, $false)
    $hash = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($known -notcontains $hash) {
        $code = " + ExitCopyUntrusted + @"
    } else {
        $p = Start-Process -FilePath $dst -ArgumentList '" + CleanArguments + @"' -WorkingDirectory $dir -WindowStyle Hidden -PassThru -Wait
        $code = $p.ExitCode
    }
} catch {
    $code = 1
} finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
exit $code
";
    }

    /// <summary>
    /// Runs WinMemoryCleaner with <see cref="CleanArguments"/> and reports how much the standby list shrank.
    /// Asks for elevation when NitroLite itself is not elevated.
    /// </summary>
    /// <remarks>
    /// Only a build whose digest is in <see cref="KnownGoodSha256"/> is launched. The tool runs with an
    /// administrator token, so a file that merely has the right name is not enough: a copy planted in the data
    /// folder would otherwise be an arbitrary executable started elevated. A candidate that fails the check is
    /// skipped, which lets a tampered portable copy fall through to the bundled one.
    /// </remarks>
    public async Task<MemoryCleanerResult> CleanMemoryAsync()
    {
        var result = new MemoryCleanerResult();

        // Held open from the digest check until this method returns, which is after the process has started: see
        // OpenVerifiedTool for why the path alone is not enough.
        using var verifiedTool = OpenVerifiedTool();
        string? tool = verifiedTool?.Name;
        if (tool == null)
        {
            // "Not installed" and "installed but not a build we trust" are different answers: the first offers a
            // download, the second has to explain why the file that is already there was refused.
            result.ToolMissing = FindTool() == null;
            result.ToolUntrusted = !result.ToolMissing;
            if (result.ToolUntrusted)
            {
                result.Error = "the WinMemoryCleaner copy on this machine is not a build NitroLite recognises";
                AppLog.Warn("The memory cleaner was not run",
                    $"WinMemoryCleaner.exe is not a known version: {FindTool()}");
            }
            return result;
        }

        if (IsInUserWritableFolder(tool))
        {
            // Informational: the digest already pins the contents, so a profile folder is fine. The line exists so
            // a support log shows which copy ran.
            AppLog.Info("The memory cleaner is running from the user folder", tool);
        }

        var listsBefore = ReadMemoryLists();
        var memBefore = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(memBefore);

        try
        {
            // The verified exe is not started where it lies. The digest pins the exe, not its folder: a
            // WinMemoryCleaner.exe.config (appDomainManager) or a side-loaded DLL planted next to it in a folder the user
            // can write to — the data folder, the winget links, a portable or scoop install — would run inside the
            // elevated process. An elevated helper copies the exe alone into a fresh folder only administrators can
            // write to, checks the copy's digest again there, runs it from that folder and deletes it.
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(BuildIsolatedLaunchScript(tool)));
            var startInfo = new ProcessStartInfo(WindowsPaths.WindowsPowerShell)
            {
                UseShellExecute = true,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (!IsRunningAsAdministrator()) startInfo.Verb = "runas";

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                result.Error = "WinMemoryCleaner did not start.";
                return result;
            }

            // An elevated process started through the shell does not always hand back a waitable handle, so the
            // wait is best-effort: the standby drop below is what decides the outcome either way.
            try
            {
                // 45 rather than 30 seconds: the copy and the PowerShell start-up now sit in front of the tool.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await process.WaitForExitAsync(timeout.Token);
                result.ExitCode = process.ExitCode;
                if (result.ExitCode == ExitCopyUntrusted)
                {
                    result.ToolUntrusted = true;
                    result.Error = "the copied WinMemoryCleaner did not match a build NitroLite recognises";
                    AppLog.Warn("The memory cleaner was not run", "the isolated copy failed the digest check");
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                result.Error = "WinMemoryCleaner did not finish within 45 seconds.";
            }
            catch
            {
                // No waitable handle; fall through and measure.
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the elevation prompt was dismissed.
            result.ElevationRefused = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }

        var listsAfter = ReadMemoryLists();
        var memAfter = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(memAfter);

        // The standby list is the only thing this clean touches, so its drop is the honest figure. Free/zero
        // page counts swing by gigabytes on their own and would report a saving that never happened.
        double freedGb = 0;
        if (listsBefore.HasValue && listsAfter.HasValue)
        {
            double standbyDecreased = listsBefore.Value.StandbyGigabytes - listsAfter.Value.StandbyGigabytes;
            if (standbyDecreased > 0) freedGb = Math.Min(standbyDecreased, listsBefore.Value.StandbyGigabytes);
        }

        result.FreedMegabytes = freedGb > 0 ? Math.Round(freedGb * 1024.0, 1) : 0;
        result.AvailableBeforeGigabytes = Math.Round(memBefore.ullAvailPhys / (1024.0 * 1024.0 * 1024.0), 2);
        result.AvailableGigabytes = Math.Round(memAfter.ullAvailPhys / (1024.0 * 1024.0 * 1024.0), 2);
        result.Cleaned = result.Error == null && result.ExitCode is null or 0;
        if (!result.Cleaned && result.Error == null) result.Error = $"WinMemoryCleaner exited with code {result.ExitCode}.";
        return result;
    }
}
