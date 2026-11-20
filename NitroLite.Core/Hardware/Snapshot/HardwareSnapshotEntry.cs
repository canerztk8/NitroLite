using System.Globalization;
using System.Text;

namespace NitroLite.Core.Hardware.Snapshot;

/// <summary>
/// One readable hardware value in a <see cref="HardwareSnapshot"/>: a section, a selector name and the value as the
/// read-only capture found it.
/// </summary>
/// <remarks>
/// <para>
/// The value is always text so that one snapshot can hold a WMI selector word, a firmware status string and a HID
/// feature report side by side, and so that the diff of two snapshots compares the same thing the report prints. A
/// byte array is written by <see cref="Hex"/> — uppercase, one space between bytes, no prefix — because that is the
/// form a maintainer compares against a NitroSense log by eye.
/// </para>
/// <para>
/// A read that failed is an entry too, not a missing one. The whole point of the tool is a user saying "the setting
/// changed but nothing shows", and a capture that silently dropped the selector that threw would look like a
/// selector that does not exist on the machine.
/// </para>
/// </remarks>
public sealed record HardwareSnapshotEntry(string Section, string Key, string Value)
{
    /// <summary>The prefix a failed read carries, so the diff can tell a value from a refusal.</summary>
    public const string UnavailablePrefix = "unavailable: ";

    /// <summary><c>hid.048D:C985.page0xFF00</c> — the name the diff prints and sorts on.</summary>
    public string Label => $"{Section}.{Key}";

    /// <summary>True when this entry records a refused read rather than a value.</summary>
    public bool IsUnavailable => Value.StartsWith(UnavailablePrefix, StringComparison.Ordinal);

    /// <summary>
    /// An entry for a read that threw or was refused. The reason is scrubbed like any other text: a WMI exception
    /// message can carry a class name and, on a bad day, a path.
    /// </summary>
    public static HardwareSnapshotEntry Unavailable(string section, string key, string reason) =>
        new(section, key, UnavailablePrefix + SnapshotRedaction.Scrub(reason).ReplaceLineEndings(" ").Trim());

    /// <summary>An entry whose value is a byte array, formatted by <see cref="Hex"/>.</summary>
    public static HardwareSnapshotEntry FromBytes(string section, string key, ReadOnlySpan<byte> bytes) =>
        new(section, key, Hex(bytes));

    /// <summary>
    /// <c>10 FF 81 00</c>. Uppercase and space separated so the same bytes always produce the same text, which is
    /// what makes the diff deterministic and testable.
    /// </summary>
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return "";

        var text = new StringBuilder(bytes.Length * 3);
        for (int index = 0; index < bytes.Length; index++)
        {
            if (index > 0) text.Append(' ');
            text.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads back what <see cref="Hex"/> wrote: a run of two-digit hex groups separated by single spaces. False for
    /// anything else, so a firmware status word is never mistaken for a byte array.
    /// </summary>
    public static bool TryParseHex(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value)) return false;

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parsed = new byte[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length != 2) return false;
            if (!byte.TryParse(parts[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed[index]))
                return false;
        }

        bytes = parsed;
        return true;
    }
}
