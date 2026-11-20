namespace NitroLite.Core;

/// <summary>
/// The colour the keyboard shows while the machine is on the charger, and the shape it is written in.
/// </summary>
/// <remarks>
/// <para>
/// A second automatic colour, beside <see cref="ReactiveLighting"/>, and deliberately a simpler one: the temperature
/// ramp is a function of a sensor that moves all evening, while the charger state is a single bit that changes when
/// the plug does. So this one is transition-driven — one write when the adapter goes in, one when it comes out — and
/// it never touches the settings that describe the user's own colour. Turning it off, unplugging, or blacking the
/// page out all leave the keyboard on the colour the page holds.
/// </para>
/// <para>
/// The colour is painted as one solid STATIC colour across all four zones rather than as four. A zone pattern is a
/// choice the user makes about their own lighting; "the charger is in" is a single fact, and it should not quietly
/// replace a deck the user arranged.
/// </para>
/// </remarks>
public static class ChargingLighting
{
    /// <summary>The colour written when the user has not chosen one: amber, the colour a charging LED uses.</summary>
    public const string DefaultColor = "#F59E0B";

    /// <summary>The effect the charging colour goes out as. A solid colour is the whole message.</summary>
    public const string Effect = "STATIC";

    /// <summary>
    /// A colour as <c>#RRGGBB</c>, or <see cref="DefaultColor"/> when the text is not one. The store rejects a
    /// malformed colour with WRONG_PARAMETERS, so nothing unvalidated may reach it.
    /// </summary>
    public static string Normalize(string? color) =>
        IsColor(color) ? LightingPolicy.NormalizeHex(color, DefaultColor) : DefaultColor;

    /// <summary>
    /// True when <paramref name="color"/> is a complete <c>#RRGGBB</c> (or <c>RRGGBB</c>) the store accepts. The card's
    /// hex box is typed into, so a half-finished value is kept on screen and neither stored nor written.
    /// </summary>
    public static bool IsColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return false;

        var hex = color.Trim().TrimStart('#');
        if (hex.Length != 6) return false;

        foreach (var ch in hex)
        {
            if (!Uri.IsHexDigit(ch)) return false;
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="baseline"/> describes a keyboard that can be painted at all. A page whose lights are
    /// off — blackout, or a brightness of zero — has no brightness to paint the charging colour at, and the firmware
    /// reads a colour written at zero as "stay dark".
    /// </summary>
    public static bool CanPaint(LightingConfig? baseline) =>
        baseline != null && !LightingPolicy.IsOff(baseline) && baseline.Brightness > 0;

    /// <summary>
    /// The configuration the charging mode writes: <paramref name="color"/> on every zone, with the brightness,
    /// speed and direction the page already holds so the keyboard does not jump to full brightness when the plug goes
    /// in. The caller checks <see cref="CanPaint"/> first; a baseline that is off comes back off rather than lit.
    /// </summary>
    public static LightingConfig ConfigFor(string? color, LightingConfig baseline)
    {
        var hex = Normalize(color);

        var config = LightingPolicy.Clone(baseline);
        config.Effect = Effect;
        config.Color = hex;
        config.LEDs = new List<string> { hex, hex, hex, hex };
        return config;
    }
}
