using System.Globalization;

namespace NitroLite.Core.Hardware.Lighting;

/// <summary>
/// The documented ENE KB5130 lighting frame, as bytes, with no Windows API in sight.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of the HID transport that has to be exactly right, so it lives where a test can read every byte
/// without a keyboard attached: an eleven-byte HID feature report,
/// <c>A4 21 mode brightness speed direction R G B mask 00</c>. The layout comes from the <c>kbrgb</c> project's
/// published <c>PROTOCOL.md</c>, which was verified on a <c>PHN16S-71</c> and confirmed on an <c>ANV16S-41</c> — both of
/// which this project already carries in its model table.
/// </para>
/// <para>
/// Two rules are enforced here rather than at the call site. First, only mode bytes that a person actually observed on
/// hardware can be produced: the identifiers this protocol also defines for <em>firmware update</em> and for
/// <em>black</em> are hostile to a keyboard in normal use, and a mapping table that cannot name them cannot send them.
/// Second, an effect with no documented identifier is refused instead of being approximated, because a guessed mode
/// byte is exactly the kind of unverified hardware write this project has never made.
/// </para>
/// </remarks>
public static class EneKb5130Protocol
{
    /// <summary>The report length the controller expects, report id included.</summary>
    public const int ReportLength = 11;

    /// <summary>The HID feature report id that carries a lighting command.</summary>
    public const byte ReportId = 0xA4;

    /// <summary>The single command byte this build sends.</summary>
    public const byte Command = 0x21;

    /// <summary>The zone mask that addresses all four zones at once.</summary>
    public const byte AllZonesMask = 0x0F;

    /// <summary>The per-zone masks, in zone order.</summary>
    public static IReadOnlyList<byte> ZoneMasks { get; } = new byte[] { 0x01, 0x02, 0x04, 0x08 };

    /// <summary>Static colour, which is the mode the "turn the backlight off" request uses at brightness zero.</summary>
    public const byte ModeStatic = 0x02;

    /// <summary>Breathing, the one animated mode that keeps the colour the user chose.</summary>
    public const byte ModeBreathing = 0x04;

    /// <summary>Neon, which cycles the palette by itself.</summary>
    public const byte ModeNeon = 0x05;

    /// <summary>Wave, also called shifting.</summary>
    public const byte ModeWave = 0x07;

    /// <summary>Zoom.</summary>
    public const byte ModeZoom = 0x09;

    /// <summary>
    /// The mode byte for an app effect name, or false when nobody has documented one. The hostile identifiers
    /// (<c>0x01</c>, <c>0x03</c>, <c>0x06</c>, <c>0x08</c>, <c>0x0C</c>) are deliberately absent.
    /// </summary>
    public static bool TryMapMode(string? effect, out byte mode)
    {
        switch ((effect ?? "").Trim().ToUpperInvariant())
        {
            case "OFF":
            case "STATIC":
                mode = ModeStatic;
                return true;
            case "BREATHING":
                mode = ModeBreathing;
                return true;
            case "NEON":
                mode = ModeNeon;
                return true;
            case "WAVE":
            case "SHIFTING":
                mode = ModeWave;
                return true;
            case "ZOOM":
                mode = ModeZoom;
                return true;
            default:
                mode = 0;
                return false;
        }
    }

    /// <summary>True when the controller uses the per-zone colour for this mode, rather than cycling its own.</summary>
    public static bool UsesZoneColors(byte mode) => mode == ModeStatic || mode == ModeBreathing;

    /// <summary>
    /// Builds the report or reports for one frame: one when all four zones agree or the mode ignores zone colour, four
    /// when they differ. Returns false with a reason for an effect with no documented identifier.
    /// </summary>
    public static bool TryBuild(
        KeyboardLightingFrame frame, out IReadOnlyList<byte[]> reports, out string? error)
    {
        ArgumentNullException.ThrowIfNull(frame);
        reports = Array.Empty<byte[]>();
        error = null;

        if (!TryMapMode(frame.Effect, out var mode))
        {
            error = $"the ENE KB5130 has no documented mode for the '{frame.Effect}' effect";
            return false;
        }

        var brightness = (byte)Math.Clamp(frame.BrightnessPercent, 0, 100);
        var speed = (byte)Math.Clamp(frame.Speed, 1, 10);
        var direction = (byte)(frame.Direction == 2 ? 2 : 1);

        if (frame.IsOff)
        {
            reports = new[] { Build(mode, 0, speed, direction, 0, AllZonesMask) };
            return true;
        }

        if (frame.AllZonesEqual || !UsesZoneColors(mode))
        {
            reports = new[] { Build(mode, brightness, speed, direction, frame.Zone(0), AllZonesMask) };
            return true;
        }

        var each = new byte[KeyboardLightingFrame.ZoneCount][];
        for (int zone = 0; zone < each.Length; zone++)
            each[zone] = Build(mode, brightness, speed, direction, frame.Zone(zone), ZoneMasks[zone]);

        reports = each;
        return true;
    }

    private static byte[] Build(
        byte mode, byte brightness, byte speed, byte direction, int packedColor, byte mask) =>
        new[]
        {
            ReportId,
            Command,
            mode,
            brightness,
            speed,
            direction,
            (byte)((packedColor >> 16) & 0xFF),
            (byte)((packedColor >> 8) & 0xFF),
            (byte)(packedColor & 0xFF),
            mask,
            (byte)0x00
        };

    /// <summary>One report as hex, for the log. Every byte is printed so a wrong one is visible.</summary>
    public static string Describe(IReadOnlyList<byte[]> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var parts = new string[reports.Count];
        for (int i = 0; i < reports.Count; i++)
        {
            var bytes = reports[i];
            var hex = new string[bytes.Length];
            for (int b = 0; b < bytes.Length; b++)
                hex[b] = bytes[b].ToString("X2", CultureInfo.InvariantCulture);

            parts[i] = string.Join(' ', hex);
        }

        return string.Join(" / ", parts);
    }
}
