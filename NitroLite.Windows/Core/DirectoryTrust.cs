using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NitroLite.Core;

/// <summary>
/// Answers the one question that decides whether a directory is a safe home for an executable NitroLite is about to
/// launch with an administrator token: who can drop a file into it.
/// <para>
/// The distinction that matters is not "protected" versus "not protected" — a portable or Scoop install lives under
/// the user's own profile and has to keep working. It is whether <em>another</em> principal can plant a binary. A
/// folder only the signed-in user can write to is the same trust boundary the user already sits behind; a folder
/// <c>Users</c> or <c>Everyone</c> can write to is one where any local account, and any service running as one, can
/// replace the executable between the moment it is checked and the moment it is launched.
/// </para>
/// </summary>
public static class DirectoryTrust
{
    /// <summary><c>WinBuiltinUsersSid</c>: every local account, including the low-privilege ones.</summary>
    private const string UsersSid = "S-1-5-32-545";

    /// <summary><c>WinWorldSid</c>.</summary>
    private const string EveryoneSid = "S-1-1-0";

    /// <summary><c>WinAuthenticatedUserSid</c>: anything that logged on, which includes network and service logons.</summary>
    private const string AuthenticatedUsersSid = "S-1-5-11";

    /// <summary><c>WinInteractiveSid</c>: whoever is sitting at the console.</summary>
    private const string InteractiveSid = "S-1-5-4";

    /// <summary>
    /// True when <paramref name="directory"/> grants write access to a principal wider than the signed-in user, so a
    /// different account could replace a file in it. False when the ACL cannot be read: an unreadable ACL is not
    /// evidence of a hole, and refusing on it would break installs on machines with unusual inheritance.
    /// </summary>
    public static bool IsWorldWritable(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;

        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists) return false;

            var security = info.GetAccessControl(AccessControlSections.Access);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & FileSystemRights.WriteData) == 0) continue;
                if (rule.IdentityReference is not SecurityIdentifier sid) continue;

                if (sid.Value is UsersSid or EveryoneSid or AuthenticatedUsersSid or InteractiveSid) return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException
                                      or InvalidOperationException or ArgumentException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="path"/> sits under a folder the signed-in user owns: the profile, its roaming and
    /// local halves, or the temp folder. Such a path is writable by the user and by nothing else, which is why it is
    /// reported separately from <see cref="IsWorldWritable"/> rather than folded into it.
    /// </summary>
    public static bool IsUnderUserProfile(string? path, IEnumerable<string>? userRoots = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var full = Path.GetFullPath(path);
            foreach (var root in userRoots ?? DefaultUserRoots())
            {
                if (string.IsNullOrWhiteSpace(root)) continue;

                var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                                 + Path.DirectorySeparatorChar;
                if (full.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                      or System.Security.SecurityException)
        {
            return false;
        }

        return false;
    }

    private static IEnumerable<string> DefaultUserRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.GetTempPath();
    }
}
