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
/// copies it, and a user pasting three separate commands into a terminal is three chances to paste the wrong one.
/// </remarks>
public static class UdevGuide
{
    /// <summary>File name the rule is installed under. The <c>99-</c> prefix is what makes it win over the defaults.</summary>
    public const string RuleFileName = "99-nitrolite.rules";

    private const string ResourceName = "NitroLite.Desktop.99-nitrolite.rules";

    /// <summary>
    /// Extracts the rule to the data folder and returns its path, or null when the resource is missing. Never throws:
    /// a read-only data folder means the user cannot be given the file, not that the app should fail.
    /// </summary>
    public static string? EnsureRuleFile()
    {
        try
        {
            var path = AppPaths.GetDataFile(RuleFileName);
            if (File.Exists(path)) return path;

            using var stream = typeof(UdevGuide).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                AppLog.Warn("udev kuralı gömülü kaynağı bulunamadı", ResourceName);
                return null;
            }

            using var reader = new StreamReader(stream);
            File.WriteAllText(path, reader.ReadToEnd());
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Warn("udev kuralı veri klasörüne yazılamadı", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The install command for <paramref name="rulePath"/>. Quoted because a data folder can contain a space, and
    /// chained with <c>&amp;&amp;</c> so a failed copy does not reload a udev daemon that has nothing new to read.
    /// </summary>
    public static string InstallCommand(string rulePath) =>
        $"sudo install -m 644 \"{rulePath}\" /etc/udev/rules.d/{RuleFileName} " +
        "&& sudo udevadm control --reload && sudo udevadm trigger";
}
