using System.IO;
using System.Reflection;
using NitroLite.Core;

namespace NitroLite.Desktop;

/// <summary>
/// Writes the shipped udev rule next to the settings file and builds the one-line command that installs it.
/// </summary>
/// <remarks>
/// The rule is embedded in this assembly rather than read from the repository, so the command works on a machine that
/// only has the published build. The command is a single line on purpose: the settings page offers one button that
/// copies it, and a user pasting three separate commands into a terminal is three chances to paste the wrong one. It is
/// the fallback for a desktop without <c>pkexec</c>; the settings page's "Enable" button runs the same steps itself.
/// </remarks>
public static class UdevGuide
{
    /// <summary>File name the rule is installed under. The <c>99-</c> prefix is what makes it win over the defaults.</summary>
    public const string RuleFileName = "99-nitrolite.rules";

    private const string ResourceName = "NitroLite.Desktop.99-nitrolite.rules";

    /// <summary>
    /// Extracts the rule to the data folder and returns its path, or null when the resource is missing. Never throws:
    /// a read-only data folder means the user cannot be given the file, not that the app should fail.
    /// <para>
    /// The copy is rewritten whenever it differs from the embedded one, so an update that adds a device to the rule
    /// does not leave the user installing the previous version from the data folder.
    /// </para>
    /// </summary>
    public static string? EnsureRuleFile()
    {
        try
        {
            using var stream = typeof(UdevGuide).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                AppLog.Warn("The embedded udev rule resource was not found", ResourceName);
                return null;
            }

            // udev reads LF-terminated lines; a build from a Windows checkout would otherwise embed CRLF and the rule's
            // match keys would carry a stray carriage return.
            using var reader = new StreamReader(stream);
            var rule = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);

            var path = AppPaths.GetDataFile(RuleFileName);
            if (!File.Exists(path) || File.ReadAllText(path) != rule) File.WriteAllText(path, rule);
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The udev rule could not be written to the data folder", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Lower-case hex SHA-256 of the embedded rule, LF-normalised: the exact bytes <see cref="EnsureRuleFile"/> writes.
    /// Null when the resource is missing. The root setup script checks its private copy of the file against this, so a
    /// file swapped in the data folder is refused.
    /// </summary>
    public static string? EmbeddedRuleDigest()
    {
        try
        {
            using var stream = typeof(UdevGuide).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return null;

            using var reader = new StreamReader(stream);
            var rule = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new System.Text.UTF8Encoding(false).GetBytes(rule)))
                .ToLowerInvariant();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The embedded udev rule could not be hashed", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The install command for <paramref name="rulePath"/>. Quoted because a data folder can contain a space, and
    /// chained with <c>&amp;&amp;</c> so a failed copy does not reload a udev daemon that has nothing new to read. The group
    /// steps come first: the rule hands <c>platform_profile</c> to the <c>nitrolite</c> group, and without the group the
    /// rule installs cleanly and still leaves mode switching root-only. The <c>input</c> group is not added: it would let
    /// every program of the user read every keyboard, and the rule itself hands the Nitro key's one device to the seat.
    /// </summary>
    public static string InstallCommand(string rulePath) =>
        "sudo groupadd -f nitrolite && sudo usermod -aG nitrolite \"$USER\" " +
        $"&& sudo install -m 644 \"{rulePath}\" /etc/udev/rules.d/{RuleFileName} " +
        "&& sudo udevadm control --reload && sudo udevadm trigger";
}
