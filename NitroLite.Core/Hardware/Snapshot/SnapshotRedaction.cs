using System.Text.RegularExpressions;

namespace NitroLite.Core.Hardware.Snapshot;

/// <summary>
/// The last line of defence between a snapshot and a public GitHub issue: it removes the values that identify a
/// machine rather than describe it.
/// </summary>
/// <remarks>
/// <para>
/// The capture only ever asks for selectors and feature reports, so in theory nothing identifying is in it. The theory
/// is not good enough for a button that opens a public issue: a firmware that answers a status word with a device path
/// and a driver that returns a hardware UUID both exist, and neither is something the owner would know to delete before
/// pressing Submit.
/// </para>
/// <para>
/// The scrub is applied twice — to each value as it is captured, and to the finished diff — so a value that is
/// assembled out of two entries at diff time cannot carry an identifier the per-entry pass would have caught.
/// </para>
/// </remarks>
public static partial class SnapshotRedaction
{
    /// <summary>What a hardware UUID becomes.</summary>
    public const string UuidPlaceholder = "<uuid>";

    /// <summary>What the value after a <c>Serial=</c>-style label becomes.</summary>
    public const string ValuePlaceholder = "<redacted>";

    /// <summary>A GUID in any of the three common spellings the firmware uses.</summary>
    [GeneratedRegex(@"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b")]
    private static partial Regex Uuid();

    /// <summary><c>SerialNumber=XYZ</c>, <c>UUID: …</c>, <c>UserName = …</c>: a label that names an identifier.</summary>
    [GeneratedRegex(
        @"(?<label>\b(?:serial(?:\s*number|\s*no\.?)?|uuid|machine\s*id|user\s*name|username)\s*[=:]\s*)[^\s,;]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex NamedValue();

    /// <summary>
    /// The text with the identifiers replaced. Null and empty pass through, and the result never throws: a redaction
    /// that failed would be a report that did not arrive.
    /// </summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = NamedValue().Replace(text, match => match.Groups["label"].Value + ValuePlaceholder);
        result = Redaction.Scrub(result);
        return Uuid().Replace(result, UuidPlaceholder);
    }
}
