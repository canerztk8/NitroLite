using System.Globalization;
using System.Text;

namespace NitroLite.Core.Hardware.Snapshot;

/// <summary>What comparing two captures produced: the text and the counts the status line needs.</summary>
/// <param name="Text">The deterministic diff, header included. Empty only when a capture was missing.</param>
/// <param name="Changed">Selectors whose value differs.</param>
/// <param name="Added">Selectors the second capture reads and the first did not.</param>
/// <param name="Removed">Selectors the first capture read and the second did not.</param>
/// <param name="Unchanged">Selectors both captures read with the same value.</param>
public sealed record HardwareSnapshotDiffResult(
    string Text,
    int Changed,
    int Added,
    int Removed,
    int Unchanged)
{
    /// <summary>True when the two captures disagree about anything at all.</summary>
    public bool HasChanges => Changed + Added + Removed > 0;

    /// <summary>How many selectors moved, for the sentence under the button.</summary>
    public int TotalChanges => Changed + Added + Removed;

    /// <summary>Nothing captured, nothing compared.</summary>
    public static HardwareSnapshotDiffResult None { get; } = new("", 0, 0, 0, 0);
}

/// <summary>
/// Compares snapshot A with snapshot B and prints only what moved.
/// </summary>
/// <remarks>
/// <para>
/// The tool this exists for: a user changes one setting in NitroSense, presses the second button, and the issue gets
/// the exact selector and the exact bytes that changed. Printing the unchanged selectors too would bury the two lines
/// that matter in two hundred that do not, so they are counted and left out.
/// </para>
/// <para>
/// The output is deterministic — sorted by label, one fixed format per line, invariant timestamps — because that is
/// what makes it testable, and because two users on the same model should be able to compare their diffs line by line.
/// </para>
/// <para>
/// A byte array is compared byte by byte and, when both sides are the same length, the changed offsets are named. That
/// is the difference between "the keyboard report changed" and "byte 3 went from 0x81 to 0x82", which is the sentence
/// a firmware quirk is fixed from.
/// </para>
/// </remarks>
public static class HardwareSnapshotDiff
{
    /// <summary>
    /// How much diff text an issue carries. The URL budget is roughly 2 300 raw characters once escaped and the
    /// hardware report is already in the body, so the diff is capped well below the whole capture.
    /// </summary>
    public const int MaximumTextLength = 3200;

    /// <summary>Marks a diff that was cut, so nobody reads a half diff as a complete one.</summary>
    public const string TruncatedNote =
        "… (the diff was cut here; the full hardware report is on your clipboard)";

    /// <summary>Printed when nothing moved, so an empty diff is never mistaken for a failed one.</summary>
    public const string NoDifferencesNote = "no differences: every readable selector answered the same both times";

    /// <summary>Line prefixes, so a reader can tell an added selector from a removed one at a glance.</summary>
    public const string AddedPrefix = "+ ";
    public const string RemovedPrefix = "- ";
    public const string ChangedPrefix = "~ ";

    /// <summary>The largest number of changed byte offsets named on one line.</summary>
    private const int MaximumNamedOffsets = 6;

    /// <summary>
    /// Compares the two captures. A missing capture is reported as such rather than as a diff against nothing: a
    /// comparison against an empty machine would call every selector an addition.
    /// </summary>
    public static HardwareSnapshotDiffResult Compare(HardwareSnapshot? before, HardwareSnapshot? after)
    {
        // No pair, no diff. The caller that reached this without state A has a bug, and a fabricated "everything is
        // new" comparison against an empty machine would send that bug to a public issue as if it were hardware.
        if (before == null || after == null) return HardwareSnapshotDiffResult.None;

        var first = Index(before);
        var second = Index(after);

        var lines = new List<string>();
        int changed = 0;
        int added = 0;
        int removed = 0;
        int unchanged = 0;

        foreach (var label in first.Keys.Union(second.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            bool inFirst = first.TryGetValue(label, out var oldValue);
            bool inSecond = second.TryGetValue(label, out var newValue);

            if (inFirst && !inSecond)
            {
                removed++;
                lines.Add($"{RemovedPrefix}{label} = {oldValue}");
                continue;
            }

            if (!inFirst && inSecond)
            {
                added++;
                lines.Add($"{AddedPrefix}{label} = {newValue}");
                continue;
            }

            if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            changed++;
            var detail = DescribeByteChange(oldValue, newValue);
            lines.Add($"{ChangedPrefix}{label} = {oldValue} -> {newValue}{(detail.Length == 0 ? "" : " " + detail)}");
        }

        var text = Render(before, after, changed, added, removed, unchanged, lines);
        return new HardwareSnapshotDiffResult(text, changed, added, removed, unchanged);
    }

    /// <summary>The entries of a capture by label. Ordinal, so a Turkish or Azerbaijani culture cannot reorder a diff.</summary>
    private static Dictionary<string, string> Index(HardwareSnapshot snapshot)
    {
        var map = new Dictionary<string, string>(snapshot.Entries.Count, StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries)
        {
            // Last writer wins: the snapshot already de-duplicated, and a source that repeats a label is a source bug
            // that must not turn into a two-line diff.
            map[entry.Label] = entry.Value;
        }

        return map;
    }

    /// <summary>
    /// <c>(byte 3: 0x81 -> 0x82, byte 7: 0x00 -> 0x01)</c> when both values are hex byte arrays of the same length,
    /// and an empty string for any other change — a status word is not a byte array and must not be described as one.
    /// </summary>
    private static string DescribeByteChange(string? oldValue, string? newValue)
    {
        if (!HardwareSnapshotEntry.TryParseHex(oldValue, out var oldBytes)) return "";
        if (!HardwareSnapshotEntry.TryParseHex(newValue, out var newBytes)) return "";
        if (oldBytes.Length != newBytes.Length) return "";

        var offsets = new List<string>();
        for (int index = 0; index < oldBytes.Length && offsets.Count < MaximumNamedOffsets; index++)
        {
            if (oldBytes[index] == newBytes[index]) continue;
            offsets.Add(string.Create(CultureInfo.InvariantCulture,
                $"byte {index}: 0x{oldBytes[index]:X2} -> 0x{newBytes[index]:X2}"));
        }

        return offsets.Count == 0 ? "" : $"({string.Join(", ", offsets)})";
    }

    /// <summary>
    /// The header, the summary and then the change lines, cut to <see cref="MaximumTextLength"/> with
    /// <see cref="TruncatedNote"/> when it does not fit.
    /// </summary>
    private static string Render(
        HardwareSnapshot before,
        HardwareSnapshot after,
        int changed,
        int added,
        int removed,
        int unchanged,
        IReadOnlyList<string> lines)
    {
        var text = new StringBuilder();
        text.AppendLine("## NitroSense snapshot diff");
        text.Append("A: ").Append(Timestamp(before)).Append("  B: ").Append(Timestamp(after)).AppendLine();
        text.Append("Model: ").Append(after.Model.Length > 0 ? after.Describe() : before.Describe()).AppendLine();

        if (!before.Model.Equals(after.Model, StringComparison.Ordinal) ||
            !before.Board.Equals(after.Board, StringComparison.Ordinal))
        {
            text.Append("Model A: ").Append(before.Describe()).Append(" -> B: ").Append(after.Describe()).AppendLine();
        }

        text.Append(string.Create(CultureInfo.InvariantCulture,
            $"changed {changed}, added {added}, removed {removed}, unchanged {unchanged}")).AppendLine();

        if (lines.Count == 0)
        {
            text.Append(NoDifferencesNote).AppendLine();
            foreach (var entry in after.Entries.Where(entry => entry.Label is
                "wmi.deviceState.operatingModeRaw" or "wmi.deviceState.operatingModeStatus"
                or "wmi.deviceState.muxRaw" or "windows.effectivePowerMode"))
                text.Append("unchanged ").Append(entry.Label).Append(" = ").AppendLine(entry.Value);
        }
        else
        {
            foreach (var line in lines) text.AppendLine(line);
        }

        var rendered = text.ToString();
        if (rendered.Length <= MaximumTextLength) return rendered;

        // Cut on a line boundary and leave room for the note, so the last line a reader sees is a complete one.
        int room = MaximumTextLength - TruncatedNote.Length - 2;
        int cut = room <= 0 ? 0 : rendered.LastIndexOf('\n', Math.Min(room, rendered.Length - 1));
        if (cut < 0) cut = 0;

        return rendered[..cut].TrimEnd() + Environment.NewLine + TruncatedNote;
    }

    /// <summary><c>2026-11-19T19:06:36Z</c> in UTC, never in the user's culture.</summary>
    private static string Timestamp(HardwareSnapshot snapshot) =>
        snapshot.CapturedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
