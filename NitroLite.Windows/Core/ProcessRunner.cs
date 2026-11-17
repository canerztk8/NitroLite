using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace NitroLite.Core;

/// <summary>One process invocation, reduced to what the task-scheduler wrapper needs.</summary>
/// <param name="ExitCode">The process exit code, or -1 when it never started.</param>
/// <param name="StandardOutput">Captured stdout. Empty when the call was elevated: a shell-executed process cannot be redirected.</param>
/// <param name="StandardError">Captured stderr, or the exception message when the process did not start.</param>
/// <param name="Started">False when the process could not be created at all.</param>
/// <param name="Cancelled">True when the user said No to the elevation prompt.</param>
public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool Started,
    bool Cancelled = false)
{
    public bool Success => Started && !Cancelled && ExitCode == 0;
}

/// <summary>
/// Runs an external command. An interface rather than a direct <see cref="Process"/> call so the task-scheduler
/// wrapper can be tested without registering a real scheduled task or raising a real UAC prompt.
/// </summary>
public interface IProcessRunner
{
    /// <param name="elevate">
    /// True to run the command with <c>Verb = "runas"</c>, which is what raises the UAC prompt. A declined prompt is
    /// reported as <see cref="ProcessRunResult.Cancelled"/> rather than thrown.
    /// </param>
    Task<ProcessRunResult> RunAsync(string fileName, string arguments, bool elevate, CancellationToken cancellationToken = default);
}

/// <summary>The real runner: <see cref="Process.Start(ProcessStartInfo)"/> with output capture where Windows allows it.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary><c>ERROR_CANCELLED</c>: the user clicked No on the UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>
    /// Full path of the tool to launch. A bare name is resolved against System32 rather than left to
    /// <c>CreateProcess</c>, which searches the working directory first and then PATH — both of which a user can
    /// write to, and one of which is inherited from whatever launched NitroLite.
    /// </summary>
    public static string ResolveExecutable(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fileName;
        if (Path.IsPathRooted(fileName)) return fileName;
        if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
            return fileName;

        var system = Environment.SystemDirectory;
        return string.IsNullOrEmpty(system) ? fileName : Path.Combine(system, fileName);
    }

    /// <summary>
    /// The directory the child starts in. Pinned to the tool's own folder so a relative path inside the command
    /// cannot be resolved against a folder the user controls.
    /// </summary>
    public static string WorkingDirectoryFor(string resolvedExecutable)
    {
        try
        {
            var directory = Path.GetDirectoryName(resolvedExecutable);
            if (!string.IsNullOrEmpty(directory)) return directory;
        }
        catch (ArgumentException)
        {
            // A malformed path falls through to the system directory below.
        }

        var system = Environment.SystemDirectory;
        return string.IsNullOrEmpty(system) ? AppContext.BaseDirectory : system;
    }

    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        bool elevate,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolveExecutable(fileName);

        var startInfo = new ProcessStartInfo(resolved, arguments)
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = WorkingDirectoryFor(resolved),

            // A verb is only honoured with UseShellExecute, and redirecting the output is only possible without it.
            // The elevated path therefore gets an exit code and nothing else, which is all schtasks needs.
            UseShellExecute = elevate
        };

        if (elevate)
        {
            startInfo.Verb = "runas";
        }
        else
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new ProcessRunResult(-1, "", "the process did not start", Started: false);
            }

            string stdout = "";
            string stderr = "";
            if (!elevate)
            {
                stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessRunResult(process.ExitCode, stdout, stderr, Started: true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ProcessRunResult(ErrorCancelled, "", ex.Message, Started: false, Cancelled: true);
        }
        catch (OperationCanceledException)
        {
            return new ProcessRunResult(-1, "", "cancelled", Started: false);
        }
        catch (Exception ex)
        {
            return new ProcessRunResult(-1, "", $"{ex.GetType().Name}: {ex.Message}", Started: false);
        }
    }
}
