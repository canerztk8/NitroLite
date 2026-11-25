using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <c>C:\Users\name</c>, <c>C:/Users/name</c> or the JSON-escaped <c>C:\\Users\\name</c>, keeping the drive and
    /// the <c>Users</c> segment.
    /// <para>
    /// The name runs up to the next character a folder name cannot contain (<c>\ / : * ? " &lt; &gt; |</c> or a line
    /// break), not up to the next space: a profile called <c>John Smith</c> used to come out as <c>&lt;user&gt; Smith</c>.
    /// An apostrophe is part of the name (<c>O'Brien</c>) unless it is followed by a space, punctuation or the end of
    /// the text, which is how a quoted path such as <c>'C:\Users\name'</c> closes. Running a little too far on a bare
    /// profile path followed by prose is the accepted cost: the line loses a few words, the name never leaks.
    /// </para>
    /// <para>
    /// A JSON <c>\uXXXX</c> escape is part of the name too. The settings file is written with the default encoder,
    /// which stores every non-ASCII letter that way: <c>C:\Users\Çağrı</c> is on disk as
    /// <c>C:\\Users\\\u00C7a\u011Fr\u0131</c>, and stopping at the escape's backslash left the whole name in the
    /// diagnostics archive (and half of <c>Güneş</c>).
    /// </para>
    /// </summary>
    [GeneratedRegex(@"(?<drive>[A-Za-z]:(?:\\{1,2}|/)Users(?:\\{1,2}|/))(?:\\u[0-9A-Fa-f]{4}|[^\\/:*?""<>|\r\n']|'(?![\s,;:.)\]}]|$))+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsProfile();

    /// <summary>
    /// <c>/home/name</c>, keeping the <c>/home</c> segment. A name with a space in it is taken whole only when a
    /// <c>/</c> follows it, so <c>/home/John Smith/.config</c> loses the whole name while a bare <c>/home/name</c>
    /// followed by prose keeps the prose.
    /// </summary>
    [GeneratedRegex(@"(?<root>/home/)(?:[^/\s""'<>]+(?: [^/\s""'<>]+)+(?=/)|[^/\s""'<>]+)")]
    private static partial Regex UnixProfile();

    /// <summary><c>/Users/name</c> on macOS, which the Linux build can also be run from. Spaces as for /home.</summary>
    [GeneratedRegex(@"(?<root>/Users/)(?:[^/\s""'<>]+(?: [^/\s""'<>]+)+(?=/)|[^/\s""'<>]+)")]
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
    /// A JSON document with every string in it — values and property names — scrubbed as <see cref="Scrub"/> scrubs
    /// text, and written back out. Null and empty pass through.
    /// </summary>
    /// <remarks>
    /// The text form of a JSON file is the wrong thing to match against: the default encoder stores a non-ASCII name
    /// as <c>\uXXXX</c> escapes and every backslash doubled, and a machine name written that way never matches a plain
    /// replace. Parsing first means each string is matched in its decoded form, and writing it back through the
    /// serializer means the result is always valid JSON — a replacement can never cut an escape in half. Text that is
    /// not JSON at all (a half-written file) falls back to <see cref="Scrub"/>, which also understands the escapes.
    /// </remarks>
    public static string ScrubJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return json ?? string.Empty;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException)
        {
            return Scrub(json);
        }

        if (root == null) return Scrub(json);

        try
        {
            return ScrubNode(root)?.ToJsonString(ScrubbedJsonOptions) ?? Scrub(json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)
        {
            // ArgumentException is a hand-edited file with the same property twice: the text is still scrubbed, as text.
            return Scrub(json);
        }
    }

    /// <summary>
    /// Indented, and with the relaxed encoder: the archive is read by a person, not embedded in a page, and the
    /// default encoder would print every placeholder as <c><user></c>.
    /// </summary>
    private static readonly JsonSerializerOptions ScrubbedJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>A scrubbed copy of <paramref name="node"/>; the original is left as it was.</summary>
    private static JsonNode? ScrubNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    var key = Scrub(name);
                    // Two names that scrub to the same text (two game paths under two profiles) must both survive:
                    // a duplicate key would throw, and dropping one would hide a setting from the person diagnosing.
                    var unique = key;
                    for (var i = 2; copy.ContainsKey(unique); i++) unique = $"{key} ({i})";
                    copy[unique] = ScrubNode(value);
                }
                return copy;
            }
            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var item in array) copy.Add(ScrubNode(item));
                return copy;
            }
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(Scrub(value.GetValue<string>()));
            default:
                return node.DeepClone();
        }
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
