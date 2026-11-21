namespace NitroLite.Linux;

/// <summary>
/// Reads the power-supply class's <c>charge_types</c> attribute: the types the battery offers, space separated, with
/// the active one in brackets — <c>[Standard] Long Life</c>. Type names can contain a space, so the text is not split
/// on whitespace; it is split on the brackets and on the known names.
/// </summary>
public static class LinuxChargeTypes
{
    /// <summary>Every type name the kernel's power-supply class can print, longest first so "Long Life" wins over parts of it.</summary>
    private static readonly string[] KnownTypes =
    {
        "Long Life", "Trickle", "Standard", "Adaptive", "Custom", "Bypass", "Fast", "N/A", "Unknown"
    };

    /// <summary>The offered types and the active one, or null when the text is missing or names no known type.</summary>
    public static (IReadOnlyList<string> Offered, string? Active)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var offered = new List<string>();
        string? active = null;
        var rest = text.Trim();
        while (rest.Length > 0)
        {
            var bracketed = rest[0] == '[';
            var body = bracketed ? rest[1..] : rest;
            var match = KnownTypes.FirstOrDefault(t => body.StartsWith(t, StringComparison.Ordinal));
            if (match == null) return offered.Count == 0 ? null : (offered, active);

            body = body[match.Length..];
            if (bracketed)
            {
                if (!body.StartsWith(']')) return offered.Count == 0 ? null : (offered, active);
                body = body[1..];
                active = match;
            }

            offered.Add(match);
            rest = body.TrimStart();
        }

        return offered.Count == 0 ? null : (offered, active);
    }
}
