using System.IO;

namespace NitroLite.Core;

/// <summary>
/// Turns a library path out of <c>libraryfolders.vdf</c> into something safe to probe.
/// </summary>
/// <remarks>
/// The file is plain text in a folder the user can edit, and the value is used as a directory to enumerate. A value
/// like <c>..\..\..\Windows</c> walks the scan outside the library, and a UNC value such as <c>\\host\share</c> opens
/// an SMB session — which hands this machine's NTLM hash to whoever answered and blocks the scan until the connection
/// times out. Neither is a Steam library, so both are refused.
/// </remarks>
public static class SteamLibraryPaths
{
    /// <summary>
    /// True when <paramref name="raw"/> is an absolute local path. <paramref name="normalized"/> is the full form,
    /// with any <c>..</c> segments already resolved.
    /// </summary>
    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        if (!Path.IsPathFullyQualified(text)) return false;
        if (!GameProfile.IsLocalPath(text)) return false;

        // On Windows only a drive-letter path is a local library. The UNC check above does not catch the device forms
        // (\\?\UNC\host\share, \??\UNC\…, \\.\…) and those reach a network share or a raw device all the same.
        if (OperatingSystem.IsWindows() && !IsDriveLetterPath(text)) return false;

        try
        {
            normalized = Path.GetFullPath(text);
        }
        catch
        {
            return false;
        }

        if (OperatingSystem.IsWindows() && !IsDriveLetterPath(normalized))
        {
            normalized = "";
            return false;
        }

        return normalized.Length > 0;
    }

    /// <summary>True for <c>X:\…</c> or <c>X:/…</c>: a drive letter, a colon and a separator.</summary>
    public static bool IsDriveLetterPath(string? path) =>
        path is { Length: >= 3 } &&
        char.IsAsciiLetter(path[0]) &&
        path[1] == ':' &&
        (path[2] == '\\' || path[2] == '/');
}
