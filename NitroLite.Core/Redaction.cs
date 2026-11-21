using System.Text.RegularExpressions;

namespace NitroLite.Core;

/// <summary>
/// Removes the two things a log or a diagnostics archive must not carry: the name of the user's profile folder and
/// the name of the machine.
/// </summary>
/// <remarks>
/// <para>
/// A log line is written for the person debugging the app, and it is also the file a user attaches to a public issue.
/// A path such as <c>C:\Users\caner\AppData\Local\NitroLite\settings.json</c> names its owner, and a machine name
/// names the machine on a network. Neither is needed to diagnose anything: the app's own folder is the interesting
/// part, and it survives the substitution.
/// </para>
/// <para>
/// The substitution is deliberately conservative. Only the profile segment is replaced, so the rest of the path stays
/// readable, and the machine name is only replaced when it is long enough to be a name rather than a coincidence —
/// a machine called <c>PC</c> would otherwise turn every occurrence of those two letters into a placeholder.
/// </para>
/// </remarks>
public static partial class Redaction
{
    /// <summary>What a user profile folder becomes.</summary>
    public const string UserPlaceholder = "<user>";

    /// <summary>What the machine name becomes.</summary>
    public const string MachinePlaceholder = "<machine>";

    /// <summary>
    /// Shortest machine name that is replaced. A two-letter name matches too much text to be worth the risk of
    /// mangling a log line.
    /// </summary>
    private const int MinimumMachineNameLength = 3;

    /// <summary>
    /// <c>C:\Users\name</c> or <c>C:/Users/name</c>, keeping the drive and the <c>Users</c> segment.
    /// </summary>
    [GeneratedRegex(@"(?<drive>[A-Za-z]:[\\/]Users[\\/])[^\\/\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsProfile();

    /// <summary><c>/home/name</c>, keeping the <c>/home</c> segment.</summary>
    [GeneratedRegex(@"(?<root>/home/)[^/\s""'<>]+")]
    private static partial Regex UnixProfile();

    /// <summary><c>/Users/name</c> on macOS, which the Linux build can also be run from.</summary>
    [GeneratedRegex(@"(?<root>/Users/)[^/\s""'<>]+")]
    private static partial Regex MacProfile();

    /// <summary>
    /// The text with every user profile folder and the machine name replaced. Null and empty pass through.
    /// </summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        // Every path pattern is anchored on a separator: "C:\Users\", "/home/", "/Users/". A line with neither
        // character in it cannot contain a path at all, so the three regex passes are skipped for the lines that make
        // up most of the log. The machine name is still replaced — it needs no separator, and that check is a plain
        // scan.
        if (!text.Contains('\\') && !text.Contains('/')) return ScrubMachineName(text);

        var result = WindowsProfile().Replace(text, "${drive}" + UserPlaceholder);
        result = UnixProfile().Replace(result, "${root}" + UserPlaceholder);
        result = MacProfile().Replace(result, "${root}" + UserPlaceholder);
        return ScrubMachineName(result);
    }

    /// <summary>
    /// The text with the machine name replaced. Separate from <see cref="Scrub"/> for a caller that has already
    /// scrubbed the paths and only needs the name gone.
    /// </summary>
    public static string ScrubMachineName(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        string machine;
        try
        {
            machine = Environment.MachineName;
        }
        catch
        {
            // A sandbox that refuses the query has no name to leak.
            return text;
        }

        if (machine.Length < MinimumMachineNameLength) return text;

        return text.Replace(machine, MachinePlaceholder, StringComparison.OrdinalIgnoreCase);
    }
}
