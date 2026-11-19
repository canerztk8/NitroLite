using System.Globalization;

namespace NitroLite.Core.Hardware;

/// <summary>
/// Builds the "report this laptop on GitHub" link: the hardware report goes on the clipboard and the browser opens a
/// prefilled new-issue form for it.
/// </summary>
/// <remarks>
/// <para>
/// The report already existed as a text the user could copy and paste by hand. That is one step too many on a machine
/// whose owner does not know what to paste where, and a compatibility report that never arrives is a model that stays
/// unsupported. The link carries the report into the issue body so the owner only has to press Submit.
/// </para>
/// <para>
/// What it deliberately does <em>not</em> do is decide whether the body fits: browsers, terminals and proxies all cap
/// URL length somewhere between 2 000 and 8 192 characters and disagree about the exact point, so the body is cut to
/// <see cref="MaximumUrlLength"/> with a note saying the untruncated report is already on the clipboard. A silent cut
/// would leave a half report in an issue and no way to tell.
/// </para>
/// <para>
/// The title is built from the model code and the board name — public SMBIOS strings — and the body is the sanitised
/// report, which a test asserts contains no serial, UUID, user name or path. This type adds nothing of its own to
/// either, so it cannot be the place a private value leaks into a public issue.
/// </para>
/// </remarks>
public static class CompatibilityReportUrl
{
    /// <summary>
    /// The longest URL produced. 7 000 characters leaves room under the 8 192 limit Chromium and Windows use, and
    /// escaping already triples every newline, so the raw body that fits is roughly 2 300 characters.
    /// </summary>
    public const int MaximumUrlLength = 7000;

    /// <summary>Longest title kept. GitHub truncates the title field itself, but a cut here is predictable.</summary>
    public const int MaximumTitleLength = 120;

    /// <summary>The prefilled new-issue form for this repository.</summary>
    public static string NewIssueUrl => UpdateService.NewIssueUrl;

    /// <summary>
    /// The issue body appended when the report did not fit, so the reader knows the body is incomplete and where the
    /// rest is. Written in English like the report itself.
    /// </summary>
    public const string TruncationNote =
        "\n\n> The hardware report was too long for the URL and is cut here. The full report is already on your clipboard — please paste it over this section.";

    /// <summary>
    /// <c>[Compatibility] AN16-61 (EV9_SKF)</c>, or <c>[Compatibility] AN16-61</c> when the board name is unknown.
    /// </summary>
    public static string BuildTitle(string? model, string? board)
    {
        var modelText = Clean(model);
        var boardText = Clean(board);

        var title = modelText.Length == 0
            ? boardText.Length == 0 ? "[Compatibility]" : $"[Compatibility] ({boardText})"
            : boardText.Length == 0 ? $"[Compatibility] {modelText}" : $"[Compatibility] {modelText} ({boardText})";

        return title.Length <= MaximumTitleLength
            ? title
            : title[..MaximumTitleLength].TrimEnd();
    }

    /// <summary>
    /// The URL for a new issue carrying <paramref name="report"/> as its body. The body is cut when the escaped URL
    /// would exceed <see cref="MaximumUrlLength"/>, always on a character boundary and always with
    /// <see cref="TruncationNote"/> saying so.
    /// </summary>
    public static string Build(string? model, string? board, string? report)
    {
        var title = BuildTitle(model, board);
        var prefix = $"{NewIssueUrl}?title={Uri.EscapeDataString(title)}&body=";
        var budget = MaximumUrlLength - prefix.Length;
        var body = Clean(report);

        if (body.Length == 0) return prefix;

        var escaped = Uri.EscapeDataString(body);
        if (escaped.Length <= budget) return prefix + escaped;

        // Both the note and the report are escaped, so the cut has to leave room for the note as it appears in the URL.
        var noteCost = Uri.EscapeDataString(TruncationNote).Length;
        var room = budget - noteCost;
        if (room <= 0) return prefix + Uri.EscapeDataString(Fit(TruncationNote.TrimStart(), budget));

        var kept = LongestPrefixThatFits(body, room);
        return prefix + Uri.EscapeDataString(body[..kept].TrimEnd() + TruncationNote);
    }

    /// <summary>
    /// The longest prefix of <paramref name="text"/> whose escaped form fits in <paramref name="budget"/> characters,
    /// never splitting a surrogate pair. Binary search because escaping is monotone: a longer prefix never escapes to
    /// a shorter string.
    /// </summary>
    private static int LongestPrefixThatFits(string text, int budget)
    {
        int low = 0;
        int high = text.Length;

        while (low < high)
        {
            int middle = low + ((high - low + 1) / 2);
            if (LengthOfPrefix(text, middle) <= budget) low = middle;
            else high = middle - 1;
        }

        // A cut between the two halves of a surrogate pair would leave a lone half in the text, and EscapeDataString
        // renders that as a replacement character in the issue.
        if (low > 0 && low < text.Length && char.IsHighSurrogate(text[low - 1])) low--;
        return low;
    }

    /// <summary>What <paramref name="length"/> characters of <paramref name="text"/> cost once escaped.</summary>
    private static int LengthOfPrefix(string text, int length) =>
        Uri.EscapeDataString(length >= text.Length ? text : text[..length]).Length;

    /// <summary>A text shortened until it fits, used for the degenerate case where only the note fits.</summary>
    private static string Fit(string text, int budget)
    {
        if (budget <= 0) return "";
        var kept = LongestPrefixThatFits(text, budget);
        return kept >= text.Length ? text : text[..kept];
    }

    /// <summary>
    /// One line of text with the whitespace a paste can carry: CRLF becomes LF, and a lone CR becomes LF, so the
    /// escaped body has one newline per line rather than two.
    /// </summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value.Replace("\r\n", "\n").Replace('\r', '\n');
        return text.Trim();
    }

    /// <summary>For a log line: how long the link turned out, without printing the report again.</summary>
    public static string DescribeLength(string url) =>
        string.Create(CultureInfo.InvariantCulture, $"length {url.Length} of {MaximumUrlLength}");

    /// <summary>True when <paramref name="url"/> is a link this builder could have produced.</summary>
    public static bool IsCompatibilityUrl(string? url) =>
        !string.IsNullOrEmpty(url) &&
        url.Length <= MaximumUrlLength &&
        url.StartsWith(NewIssueUrl, StringComparison.Ordinal) &&
        url.Contains("?title=", StringComparison.Ordinal) &&
        UpdateService.IsSafeHttpsUrl(url);

    /// <summary>
    /// Fragments a compatibility link must never contain. Declared here so the test asserts against the same list the
    /// builder promises, rather than a second copy that could drift.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenFragments { get; } = new[]
    {
        "SerialNumber",
        "serial=",
        "UserName",
        "C:\\Users\\",
        "/home/"
    };
}
