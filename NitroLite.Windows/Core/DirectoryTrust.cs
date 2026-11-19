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
    /// Rights that let a principal change or replace an existing file: write, append, delete, or take ownership of
    /// it. <see cref="FileSystemRights.ReadAndExecute"/> and <see cref="FileSystemRights.Synchronize"/> are deliberately
    /// absent — every reader gets those, and they let nobody modify anything.
    /// </summary>
    private const FileSystemRights ReplacingRights =
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.Delete |
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.TakeOwnership |
        FileSystemRights.ChangePermissions;

    /// <summary>
    /// The subset that matters on a <em>directory</em>: rights that let a principal remove or replace files that
    /// already exist in it. <see cref="FileSystemRights.WriteData"/> on a directory only means "create new files",
    /// which cannot overwrite an existing executable, so it is deliberately excluded here.
    /// <para>
    /// That exclusion does not make a drive-root install trusted, and must not be read as if it did: a file created
    /// in such a folder inherits <c>Modify</c> from the drive root itself, which <see cref="ReplacingRights"/> does
    /// read on the file. It only keeps the folder's own "create" grant from being counted a second time.
    /// </para>
    /// </summary>
    private const FileSystemRights ReplacingChildRights =
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.TakeOwnership |
        FileSystemRights.ChangePermissions;

    private static bool GrantsRights(FileSystemAccessRule rule, FileSystemRights mask)
    {
        if (rule.AccessControlType != AccessControlType.Allow) return false;
        if ((rule.FileSystemRights & mask) == 0) return false;
        if (rule.IdentityReference is not SecurityIdentifier sid) return false;
        return sid.Value is UsersSid or EveryoneSid or AuthenticatedUsersSid or InteractiveSid;
    }

    /// <summary>
    /// True when the executable itself is safe to launch with an administrator token: no principal wider than the
    /// signed-in user may modify, delete or replace <em>this file</em>.
    /// <para>
    /// This is deliberately a file-level check, not the directory-level <see cref="IsWorldWritable"/>: what decides
    /// the answer is the right a wider principal actually holds, and reading it from the file covers the ones the
    /// file inherits from its folder as well as the ones written on the folder itself. Reading only the folder used
    /// to refuse every portable install that was not under the profile or Program Files, which on those machines
    /// meant the elevated WMI worker never started at all.
    /// </para>
    /// <para>
    /// A portable install under the profile is trusted because no other local account can write there, and Program
    /// Files is trusted because writing there needs an administrator. A folder created directly under a drive root
    /// (for example <c>C:\WinTools</c>) is refused on a stock Windows: the drive root grants <c>Authenticated
    /// Users</c> modify to everything created beneath it, so the files in that folder are replaceable by any local
    /// account between this check and the launch of the elevated helper. The refusal message and the README say so,
    /// and send the user to a location where the answer is yes.
    /// </para>
    /// <para>
    /// False only when a wider principal verifiably holds a replacing right on the file or on its folder. When
    /// neither exists yet, or the ACL cannot be read, the answer is true — an unreadable ACL is not evidence of a
    /// hole, and refusing on it would break installs on machines with unusual inheritance.
    /// </para>
    /// </summary>
    public static bool IsExecutableTrusted(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        if (IsUnderUserProfile(executablePath)) return true;

        try
        {
            var file = new FileInfo(Path.GetFullPath(executablePath));
            if (file.Exists)
            {
                var fileSecurity = file.GetAccessControl(AccessControlSections.Access);
                foreach (FileSystemAccessRule rule in fileSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (GrantsRights(rule, ReplacingRights)) return false;
                }
            }

            // Deleting the file out from under the launcher is the other way to swap the binary, and that right
            // lives on the parent directory, not on the file. Creating new files in the folder is not enough to
            // replace an existing one, so the directory check uses only the child-replacing subset.
            var directory = file.Directory;
            if (directory is { Exists: true })
            {
                var dirSecurity = directory.GetAccessControl(AccessControlSections.Access);
                foreach (FileSystemAccessRule rule in dirSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (GrantsRights(rule, ReplacingChildRights)) return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException
                                      or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            return true;
        }
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
