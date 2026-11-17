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

        try
        {
            normalized = Path.GetFullPath(text);
        }
        catch
        {
            return false;
        }

        return normalized.Length > 0;
    }
}
