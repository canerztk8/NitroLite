using System.Diagnostics;

namespace NitroLite.Linux;

/// <summary>What one external command answered.</summary>
/// <param name="ExitCode">The process exit code, or -1 when it could not be started or timed out.</param>
/// <param name="Output">Standard output, trimmed.</param>
/// <param name="Error">Standard error, trimmed, or the reason the command could not run.</param>
public readonly record struct CommandResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    public static CommandResult NotRun(string reason) => new(-1, "", reason);
}

/// <summary>
/// Runs a system tool. An interface so the D-Bus, NVIDIA and pkexec paths can be tested without the tools installed.
/// </summary>
public interface ICommandRunner
{
    /// <summary>The absolute path of <paramref name="name"/> on <c>PATH</c>, or null. Never runs anything.</summary>
    string? Find(string name);

    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> passed one by one (never through a shell).
    /// Never throws: a missing tool, a crash or a timeout is a <see cref="CommandResult"/> with exit code -1.
    /// </summary>
    CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout);
}

/// <summary>The real runner: <see cref="Process"/> with redirected output and a hard timeout.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public static ProcessCommandRunner Instance { get; } = new();

    private ProcessCommandRunner()
    {
    }

    public string? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Contains('/', StringComparison.Ordinal)) return File.Exists(name) ? name : null;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) path = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
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

            // Tools that print in the user's language would defeat the parsers; C keeps their output stable.
            startInfo.Environment["LC_ALL"] = "C";

            using var process = Process.Start(startInfo);
            if (process == null) return CommandResult.NotRun($"{fileName} did not start.");

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return CommandResult.NotRun($"{Path.GetFileName(fileName)} timed out.");
            }

            // WaitForExit(TimeSpan) does not wait for the redirected streams; the parameterless call does.
            process.WaitForExit();
            return new CommandResult(process.ExitCode, output.Result.Trim(), error.Result.Trim());
        }
        catch (Exception ex)
        {
            return CommandResult.NotRun(ex.Message);
        }
    }
}
