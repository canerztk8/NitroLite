using System.IO;

namespace NitroLite.Core;

/// <summary>
/// Full paths of the Windows tools NitroLite launches, resolved through <see cref="Environment.SystemDirectory"/> so
/// a same-named exe in the working directory or on PATH is never the one that runs.
/// </summary>
/// <remarks>
/// These live here rather than in <see cref="AppPaths"/> because they are the one part of path resolution that has
/// no meaning off Windows: <see cref="Environment.SystemDirectory"/> is empty on Linux, so a NitroLite.Core that
/// still offered these would be advertising a call that silently returns a relative path. The data-directory half
/// of <see cref="AppPaths"/> is genuinely portable and stays in the core assembly.
/// </remarks>
public static class WindowsPaths
{
    /// <summary>Full path of a tool in System32.</summary>
    public static string SystemTool(string exeName) => Path.Combine(Environment.SystemDirectory, exeName);

    /// <summary>Full path of Windows PowerShell 5.1, which the elevated helper scripts run under.</summary>
    public static string WindowsPowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
}
