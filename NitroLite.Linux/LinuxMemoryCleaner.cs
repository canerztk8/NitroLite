using System.Diagnostics;
using System.Globalization;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>One reading of <c>/proc/meminfo</c>, in the terms the card reports.</summary>
/// <remarks>
/// <see cref="AvailableGigabytes"/> is <c>MemAvailable</c>, the kernel's own estimate of what a new allocation can get
/// without swapping — the same figure Windows calls available physical memory. <see cref="CachedGigabytes"/> is the
/// reclaimable half: page cache, reclaimable slab and buffers. That is the Linux standby list, and its drop is the
/// honest "freed" figure, because free memory swings by gigabytes on its own.
/// </remarks>
public readonly record struct LinuxMemorySnapshot(double AvailableGigabytes, double CachedGigabytes);

/// <summary>How an attempt to run the drop as root ended.</summary>
public enum LinuxElevationStatus
{
    /// <summary>The command ran and the kernel accepted the write.</summary>
    Ok = 0,

    /// <summary>The user dismissed the password prompt, so nothing ran.</summary>
    Refused = 1,

    /// <summary>Neither pkexec nor sudo is installed, so there is no way to ask for the rights.</summary>
    Unavailable = 2,

    /// <summary>The command ran and failed for another reason.</summary>
    Failed = 3
}

/// <summary>The outcome of one elevated command, with the tool's own message when it had one.</summary>
public readonly record struct LinuxElevationResult(LinuxElevationStatus Status, string? Detail)
{
    public static LinuxElevationResult Ok { get; } = new(LinuxElevationStatus.Ok, null);

    public static LinuxElevationResult Refused(string? detail) => new(LinuxElevationStatus.Refused, detail);

    public static LinuxElevationResult Unavailable(string? detail) => new(LinuxElevationStatus.Unavailable, detail);

    public static LinuxElevationResult Failed(string? detail) => new(LinuxElevationStatus.Failed, detail);
}

/// <summary>
/// Releases the Linux page cache, which is what "empty the standby list" means on this platform.
/// </summary>
/// <remarks>
/// <para>
/// WinMemoryCleaner's <c>/StandbyList</c> is a thin wrapper over one call:
/// <c>NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)</c>. The kernel then walks the
/// standby list and hands every clean page back to the free list. Linux has the same list under a different name —
/// the page cache plus reclaimable slab — and the same one-line interface to it: writing to
/// <c>/proc/sys/vm/drop_caches</c>. <c>1</c> drops the page cache, <c>2</c> drops dentries and inodes, <c>3</c> drops
/// both. This writes <c>3</c>, because the Windows purge covers cached file data and the metadata around it, and
/// dropping only half of it would leave the card reporting a saving the user cannot see.
/// </para>
/// <para>
/// The write needs root, so it goes through <c>pkexec</c> (the desktop's own password dialog) and falls back to
/// <c>sudo -n</c>. Nothing is written directly unless NitroLite is already root: a failed direct write would only
/// produce a permission error the user cannot act on.
/// </para>
/// <para>
/// The cost is the same as on Windows and worth stating: the pages dropped are clean, so nothing is lost, but the
/// next read of those files comes from disk again. That is why the button is a button and not a timer.
/// </para>
/// </remarks>
public sealed class LinuxMemoryCleaner : IMemoryCleaner
{
    public const string MemInfoPath = "/proc/meminfo";

    public const string DropCachesPath = "/proc/sys/vm/drop_caches";

    /// <summary>1 = page cache, 2 = dentries and inodes, 3 = both. See the class remarks for why 3.</summary>
    public const string DropCachesValue = "3";

    private readonly ISysfsFileSystem _fileSystem;
    private readonly Func<string, CancellationToken, Task<LinuxElevationResult>> _runAsRoot;

    public LinuxMemoryCleaner()
        : this(new RealSysfsFileSystem(), LinuxElevation.RunAsync)
    {
    }

    /// <summary>The seam the tests use: an in-memory <c>/proc</c> and a command runner that records instead of running.</summary>
    internal LinuxMemoryCleaner(
        ISysfsFileSystem fileSystem,
        Func<string, CancellationToken, Task<LinuxElevationResult>> runAsRoot)
    {
        _fileSystem = fileSystem;
        _runAsRoot = runAsRoot;
    }

    /// <summary>Always true: every Linux kernel since 2.6.16 has <c>drop_caches</c>.</summary>
    public bool IsSupported => true;

    /// <summary>False: the interface is the kernel's, so there is no helper to download.</summary>
    public bool NeedsExternalTool => false;

    public async Task<MemoryCleanResult> CleanStandbyMemoryAsync(CancellationToken cancellationToken = default)
    {
        var before = ReadSnapshot();
        if (before == null)
        {
            return new MemoryCleanResult { Cleaned = false, Error = $"{MemInfoPath} could not be read." };
        }

        LinuxElevationResult run;
        try
        {
            run = await _runAsRoot($"/bin/sh -c 'echo {DropCachesValue} > {DropCachesPath}'", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new MemoryCleanResult { Cleaned = false, Error = "The clean was cancelled." };
        }
        catch (Exception ex)
        {
            return new MemoryCleanResult { Cleaned = false, Error = ex.Message };
        }

        if (run.Status == LinuxElevationStatus.Refused)
        {
            return new MemoryCleanResult { Cleaned = false, ElevationRefused = true, Error = run.Detail };
        }

        if (run.Status != LinuxElevationStatus.Ok)
        {
            return new MemoryCleanResult { Cleaned = false, Error = run.Detail ?? run.Status.ToString() };
        }

        var after = ReadSnapshot();
        if (after == null)
        {
            return new MemoryCleanResult { Cleaned = false, Error = $"{MemInfoPath} could not be read after the clean." };
        }

        // The reclaimable half is the only thing this clean touches, so its drop is the honest figure. Free memory
        // swings by gigabytes on its own and would report a saving that never happened.
        double freedGb = before.Value.CachedGigabytes - after.Value.CachedGigabytes;
        if (freedGb < 0) freedGb = 0;

        return new MemoryCleanResult
        {
            Cleaned = true,
            FreedMegabytes = Math.Round(freedGb * 1024.0, 1),
            AvailableBeforeGigabytes = before.Value.AvailableGigabytes,
            AvailableGigabytes = after.Value.AvailableGigabytes
        };
    }

    /// <summary>
    /// Reads <c>/proc/meminfo</c> and returns the two figures the card needs, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Every value is in kB. <c>MemAvailable</c> is absent on kernels older than 3.14, so the fallback is
    /// <c>MemFree + Buffers + Cached</c>, which is what that field was added to replace.
    /// </remarks>
    public LinuxMemorySnapshot? ReadSnapshot()
    {
        var text = _fileSystem.ReadAllText(MemInfoPath);
        if (string.IsNullOrWhiteSpace(text)) return null;

        double total = 0, free = 0, available = -1, buffers = 0, cached = 0, sReclaimable = 0;

        foreach (var line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var name = line[..colon].Trim();
            var rest = line[(colon + 1)..].Trim();
            int space = rest.IndexOf(' ');
            var number = space < 0 ? rest : rest[..space];
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;

            switch (name)
            {
                case "MemTotal": total = value; break;
                case "MemFree": free = value; break;
                case "MemAvailable": available = value; break;
                case "Buffers": buffers = value; break;
                case "Cached": cached = value; break;
                case "SReclaimable": sReclaimable = value; break;
            }
        }

        if (total <= 0) return null;

        double availableKb = available >= 0 ? available : free + buffers + cached;
        double cachedKb = cached + sReclaimable + buffers;

        const double kbToGb = 1.0 / (1024 * 1024);
        return new LinuxMemorySnapshot(
            Math.Round(availableKb * kbToGb, 2),
            Math.Round(cachedKb * kbToGb, 2));
    }
}

/// <summary>
/// Runs one command as root, through the desktop's own password dialog when it can.
/// </summary>
/// <remarks>
/// <c>pkexec</c> is the right first choice: it is the freedesktop standard, it shows the desktop's own authentication
/// dialog, and it does not need a terminal. <c>sudo -n</c> is the fallback for a machine without PolicyKit, and it is
/// deliberately non-interactive — a password prompt on a pipe would hang the app with no window to type into.
/// </remarks>
public static class LinuxElevation
{
    public static async Task<LinuxElevationResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        // Already root: no dialog, no helper. This is the case for a build run under sudo and for a future
        // privileged helper process.
        if (Environment.IsPrivilegedProcess)
        {
            var direct = await RunAsync("/bin/sh", new[] { "-c", command }, cancellationToken).ConfigureAwait(false);
            return direct.ExitCode == 0
                ? LinuxElevationResult.Ok
                : LinuxElevationResult.Failed(Describe("/bin/sh", direct));
        }

        string? pkexec = FindOnPath("pkexec");
        if (pkexec != null)
        {
            var result = await RunAsync(pkexec, new[] { "/bin/sh", "-c", command }, cancellationToken)
                .ConfigureAwait(false);

            // 126 is "the user dismissed the dialog" and 127 is "not authorized"; both mean nothing ran.
            if (result.ExitCode is 126 or 127) return LinuxElevationResult.Refused(Describe(pkexec, result));
            return result.ExitCode == 0
                ? LinuxElevationResult.Ok
                : LinuxElevationResult.Failed(Describe(pkexec, result));
        }

        string? sudo = FindOnPath("sudo");
        if (sudo != null)
        {
            var result = await RunAsync(sudo, new[] { "-n", "/bin/sh", "-c", command }, cancellationToken)
                .ConfigureAwait(false);

            // sudo -n answers 1 with "a password is required" when it cannot authenticate without a terminal.
            if (result.ExitCode != 0 && result.Error.Contains("password is required", StringComparison.OrdinalIgnoreCase))
            {
                return LinuxElevationResult.Refused(Describe(sudo, result));
            }

            return result.ExitCode == 0
                ? LinuxElevationResult.Ok
                : LinuxElevationResult.Failed(Describe(sudo, result));
        }

        return LinuxElevationResult.Unavailable("Neither pkexec nor sudo is installed.");
    }

    private static string Describe(string fileName, (int ExitCode, string Error) result) =>
        result.Error.Length > 0
            ? $"{fileName} exited with {result.ExitCode}: {result.Error}"
            : $"{fileName} exited with {result.ExitCode}.";

    private static async Task<(int ExitCode, string Error)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process == null) return (-1, $"{fileName} did not start.");

            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return (process.ExitCode, (await error.ConfigureAwait(false)).Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>The absolute path of <paramref name="name"/> on <c>PATH</c>, or null. Never runs anything.</summary>
    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // A PATH entry that cannot be combined must not stop the search.
            }
        }

        return null;
    }
}
