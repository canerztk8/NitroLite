using System.IO;

namespace NitroLite.Core;

/// <summary>One process the scanner found, reduced to what game detection needs.</summary>
/// <remarks>
/// <see cref="Name"/> is the kernel's short name (on Linux <c>/proc/[pid]/comm</c>, truncated to 15 characters) and
/// <see cref="ExecutablePath"/> is the first argument of the command line. Both are kept because they disagree in
/// exactly the case that matters: a Proton game reports <c>comm</c> as the wine loader while the command line still
/// names the game's own <c>.exe</c>.
/// </remarks>
public readonly record struct RunningProcess(int Pid, string Name, string ExecutablePath)
{
    /// <summary>The executable's file name without a directory, or the short name when the path is empty.</summary>
    public string FileName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExecutablePath)) return Name;
            var trimmed = ExecutablePath.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            return slash >= 0 && slash + 1 < trimmed.Length ? trimmed[(slash + 1)..] : trimmed;
        }
    }

    /// <summary>
    /// True when this process is <paramref name="exeName"/>. Compared case-insensitively against the file name, the
    /// short name and the extension-less stem, because a native Linux build has no extension while the profile was
    /// written on Windows.
    /// </summary>
    public bool Matches(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;

        var wanted = exeName.Trim();
        var fileName = FileName;
        if (fileName.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return true;
        if (Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return true;

        // Both sides lose the extension. A profile written on Windows says "Cyberpunk2077"; the process a Proton
        // prefix starts reports "Cyberpunk2077.exe", and a native Linux build reports "cs2" with no extension at all.
        // Comparing only the wanted name's stem would miss every Proton title.
        var wantedStem = Path.GetFileNameWithoutExtension(wanted);
        if (wantedStem.Length == 0) return false;

        return Path.GetFileNameWithoutExtension(fileName).Equals(wantedStem, StringComparison.OrdinalIgnoreCase) ||
               Name.Equals(wantedStem, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Lists the processes running right now. The one platform-specific part of game detection.
/// </summary>
/// <remarks>
/// An interface rather than a direct <c>/proc</c> read so the detection loop can be tested against a fixed process
/// table: a test that had to start a real game to check "the profile was applied" would never run in CI.
/// </remarks>
public interface IProcessScanner
{
    /// <summary>Every process this user can see. Empty when the platform has no process table to read.</summary>
    IReadOnlyList<RunningProcess> Scan();
}
