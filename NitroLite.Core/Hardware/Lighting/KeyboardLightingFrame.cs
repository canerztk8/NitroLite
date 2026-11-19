namespace NitroLite.Core.Hardware.Lighting;

/// <summary>
/// One keyboard lighting request, in the terms every transport understands: four zone colours, a brightness, an
/// effect and the two effect parameters.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the transport decision does not have to be made twice. The WMI executor takes a
/// <c>KeyboardLightingRequest</c> and the HID writer takes eleven bytes; both are built from this, which means a
/// fallback cannot quietly change what was asked for — the same four colours reach the keyboard whichever path answers.
/// </para>
/// <para>
/// Colours are packed <c>0xRRGGBB</c>, the same encoding the WMI protocol and the profile table already use.
/// </para>
/// </remarks>
public sealed record KeyboardLightingFrame
{
    /// <summary>Zones on the four-zone keyboards this app supports.</summary>
    public const int ZoneCount = 4;

    /// <summary>One packed <c>0xRRGGBB</c> colour per zone, always exactly <see cref="ZoneCount"/> entries.</summary>
    public int[] ZoneColors { get; init; } = new int[ZoneCount];

    /// <summary>Brightness as a percentage, 0..100. Zero means the request is "off".</summary>
    public int BrightnessPercent { get; init; }

    /// <summary>App effect name: <c>STATIC</c>, <c>BREATHING</c>, <c>NEON</c>, <c>WAVE</c>, <c>SHIFTING</c>, <c>ZOOM</c> or <c>OFF</c>.</summary>
    public string Effect { get; init; } = "STATIC";

    /// <summary>Effect speed, 1..10, in the app's own scale.</summary>
    public int Speed { get; init; } = 4;

    /// <summary>Effect direction, 1 or 2.</summary>
    public int Direction { get; init; } = 1;

    /// <summary>True when the request asks for the backlight to go dark.</summary>
    public bool IsOff => BrightnessPercent <= 0 || string.Equals(Effect, "OFF", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when all four zones carry the same colour, which lets a transport spend one write instead of four.</summary>
    public bool AllZonesEqual => ZoneColors.Length == ZoneCount && ZoneColors.All(c => c == ZoneColors[0]);

    /// <summary>The colour of one zone, clamped to the packed range rather than throwing on a short array.</summary>
    public int Zone(int index) =>
        index >= 0 && index < ZoneColors.Length ? ZoneColors[index] & 0xFFFFFF : 0;

    /// <summary>
    /// Builds a frame the way the WMI controller does: the zone array is padded or truncated to four entries and each
    /// colour falls back to <paramref name="primaryColor"/> when it does not parse.
    /// </summary>
    public static KeyboardLightingFrame Create(
        string? effect, int brightnessPercent, int speed, int direction, string? primaryColor, string[]? zoneColors)
    {
        var requested = (effect ?? "STATIC").Trim().ToUpperInvariant();
        var isOff = requested == "OFF" || brightnessPercent <= 0;

        var zones = new int[ZoneCount];
        for (int i = 0; i < ZoneCount; i++)
        {
            if (isOff) continue;

            var source = zoneColors != null && i < zoneColors.Length ? zoneColors[i] : primaryColor;
            zones[i] = TryParseHexColor(source, out var packed) || TryParseHexColor(primaryColor, out packed)
                ? packed
                : 0;
        }

        return new KeyboardLightingFrame
        {
            ZoneColors = zones,
            BrightnessPercent = isOff ? 0 : Math.Clamp(brightnessPercent, 0, 100),
            Effect = isOff ? "OFF" : requested,
            Speed = Math.Clamp(speed, 1, 10),
            Direction = direction == 2 ? 2 : 1
        };
    }

    /// <summary>Parses <c>#RRGGBB</c> or <c>RRGGBB</c>. Returns false rather than guessing at a malformed colour.</summary>
    public static bool TryParseHexColor(string? text, out int packed)
    {
        packed = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length != 6) return false;

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character)) return false;
        }

        // Six hex digits are exactly a 24-bit number, so the parse is the packing.
        return int.TryParse(
            value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture,
            out packed);
    }
}
