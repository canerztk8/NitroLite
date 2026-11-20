namespace NitroLite.Core.Hardware.Snapshot;

/// <summary>
/// One read-only capture of everything a user can observe on an Acer laptop: the Acer WMI selectors and the vendor
/// HID feature reports, in the order the diff prints them.
/// </summary>
/// <remarks>
/// <para>
/// Two of these, taken before and after a setting is changed in NitroSense, are what turns "the toggle does nothing"
/// into a maintainer-readable sentence. Nothing here writes anything: the capture path only calls the read commands
/// the app already calls while it is running.
/// </para>
/// <para>
/// The entries are sorted and de-duplicated in the constructor, so two captures of an unchanged machine produce the
/// same text regardless of the order the sources walked their devices. That determinism is what lets the diff be a
/// plain set comparison and lets a test assert an exact string.
/// </para>
/// </remarks>
public sealed class HardwareSnapshot
{
    /// <summary>
    /// How many entries a capture keeps. A machine with a dozen HID interfaces cannot reach this; the cap exists so a
    /// runaway source cannot build a report too large for a GitHub issue.
    /// </summary>
    public const int MaximumEntries = 240;

    public HardwareSnapshot(
        string? model,
        string? board,
        IEnumerable<HardwareSnapshotEntry> entries,
        DateTimeOffset? capturedAt = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Model = SnapshotRedaction.Scrub(model ?? "").Trim();
        Board = SnapshotRedaction.Scrub(board ?? "").Trim();
        CapturedAt = capturedAt ?? DateTimeOffset.UtcNow;

        Entries = entries
            // Every value is scrubbed once, here, so no source can put an identifier into a report by forgetting to.
            .Select(entry => entry with { Value = SnapshotRedaction.Scrub(entry.Value).ReplaceLineEndings(" ") })
            .Where(entry => entry.Label.Length > 0)
            .OrderBy(entry => entry.Section, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .DistinctBy(entry => entry.Label, StringComparer.Ordinal)
            .Take(MaximumEntries)
            .ToArray();
    }

    /// <summary>The model code the capture was taken on, e.g. <c>AN16-61</c>. Public SMBIOS text, never a serial.</summary>
    public string Model { get; }

    /// <summary>The baseboard product name, e.g. <c>EV9_SKF</c>. Empty when the probe did not read one.</summary>
    public string Board { get; }

    public DateTimeOffset CapturedAt { get; }

    /// <summary>Everything the capture could read, ordered by section and then by selector name.</summary>
    public IReadOnlyList<HardwareSnapshotEntry> Entries { get; }

    /// <summary>True when nothing at all could be read, which is a failed capture rather than an empty machine.</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary><c>AN16-61 (EV9_SKF)</c>, or just the model when the board name was not read.</summary>
    public string Describe() => Board.Length == 0 ? Model : $"{Model} ({Board})";

    /// <summary>How many entries the capture holds, for the status line under the button.</summary>
    public int Count => Entries.Count;

    /// <summary>The form the GitHub issue and the clipboard use: a heading and one <c>section.key = value</c> per line.</summary>
    public string ToText()
    {
        var text = new System.Text.StringBuilder();
        text.Append("## NitroSense snapshot — ").Append(Describe()).AppendLine();
        foreach (var entry in Entries)
        {
            text.Append("- ").Append(entry.Label).Append(" = ").Append(entry.Value).AppendLine();
        }

        return text.ToString();
    }

    /// <summary>An empty capture for a platform or a session that could not read anything.</summary>
    public static HardwareSnapshot Empty(string? model = "", string? board = "") => new(model, board, []);
}
