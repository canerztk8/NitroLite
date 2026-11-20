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
    /// The lowest brightness the two automatic colours paint at. One rather than zero: zero is the firmware's "stay
    /// dark", so a slider that reached it would look like a broken mode rather than a dim one.
    /// </summary>
    public const int MinBrightness = 1;

    /// <summary>
    /// A colour as <c>#RRGGBB</c>, or <see cref="DefaultColor"/> when the text is not one. The store rejects a
    /// malformed colour with WRONG_PARAMETERS, so nothing unvalidated may reach it.
    /// </summary>
    public static string Normalize(string? color) =>
        IsColor(color) ? LightingPolicy.NormalizeHex(color, DefaultColor) : DefaultColor;

    /// <summary>A stored level brought into the range the sliders offer. Zero and nonsense come back at the floor.</summary>
    public static int NormalizeBrightness(int level) =>
        Math.Clamp(level <= 0 ? MinBrightness : level, MinBrightness, LightingLimits.MaxBrightnessLevel);

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
    /// The battery percentage a machine with the 80 % charge limit armed rests at. The firmware stops charging here and
    /// reports "plugged in, not charging", which is not the same thing as a full pack and must not be read as one.
    /// </summary>
    public const int ChargeLimitPercent = 80;

    /// <summary>
    /// What the adapter is doing, as much of it as the charging colour needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OnAdapter"/> is the whole trigger, and it comes from the AC line status rather than from the charge
    /// rate or the percentage. The 80 % charge limit is what makes that distinction load-bearing: with the limit armed
    /// and the adapter in, Windows reports the pack at 80 % and the charging flag clear, so any rule written as "the
    /// battery is rising" or "the battery is not full" answers "no charger" on a machine that is plugged in — and a
    /// colour mode that went quiet at the cap would look broken exactly when the user is sitting at their desk.
    /// </para>
    /// <para>
    /// <see cref="AtChargeLimit"/> exists so the page can say what is happening instead of claiming a charge that is
    /// not taking place. It changes no colour.
    /// </para>
    /// </remarks>
    public readonly record struct AdapterState(bool OnAdapter, bool AtChargeLimit, bool Charging);

    /// <summary>
    /// Reads the adapter state from the two figures the platform reports plus the charge-limit switch.
    /// </summary>
    /// <param name="onAc">The AC line status: null when the platform cannot say.</param>
    /// <param name="batteryPercent">The pack's figure, or null when it cannot be read.</param>
    /// <param name="chargeLimitOn">True while the 80 % charge limit is armed on this machine.</param>
    public static AdapterState Read(bool? onAc, int? batteryPercent, bool chargeLimitOn)
    {
        // Null is "this platform cannot say", not "plugged in": a machine with no adapter node would otherwise paint the
        // charging colour for the rest of its life. A failed Windows query is already reported as AC by the backend, so
        // the honest reading here is the one the platform actually gave.
        bool onAdapter = onAc == true;

        bool atLimit = chargeLimitOn && onAdapter && batteryPercent is { } percent && percent >= ChargeLimitPercent;

        // Below the cap the pack is taking a charge. At the cap it is not, and a pack that has reached 100 % with no
        // limit armed is not either: Windows reports the charging flag clear in both of those states, and the old
        // answer — "on the adapter, so charging" — is what made a full machine read as a charge that never finished.
        // The colour does not depend on any of it: the plug is the message.
        bool full = onAdapter && batteryPercent is >= 100;
        bool charging = onAdapter && !atLimit && !full;

        return new AdapterState(onAdapter, atLimit, charging);
    }

    /// <summary>
    /// True when the adapter colour belongs on the keys: the adapter is in, whatever the pack is doing about it. A
    /// capped machine and a charging one look the same on the keyboard on purpose — the message is "the plug is in".
    /// </summary>
    public static bool ShouldPaint(AdapterState state) => state.OnAdapter;

    /// <summary>
    /// True when <paramref name="baseline"/> describes a keyboard that can be painted at all. A page whose lights are
    /// off — blackout, or a brightness of zero — has said the keys should be dark, and a colour mode is not a way to
    /// switch them back on behind the user.
    /// </summary>
    public static bool CanPaint(LightingConfig? baseline) =>
        baseline != null && !LightingPolicy.IsOff(baseline) && baseline.Brightness > 0;

    /// <summary>
    /// The configuration the charging mode writes: <paramref name="color"/> on every zone, with the speed and direction
    /// the page already holds and the brightness the mode's own slider carries. The caller checks
    /// <see cref="CanPaint"/> first; a baseline that is off comes back off rather than lit.
    /// </summary>
    /// <remarks>
    /// The brightness is a parameter rather than the baseline's because the two automatic colours have a level of their
    /// own: the page's slider belongs to the colour the user chose, and a charging colour that jumped to the page's
    /// level every time the plug went in would be a second brightness control the user never asked for. Null keeps the
    /// baseline's.
    /// </remarks>
    public static LightingConfig ConfigFor(string? color, LightingConfig baseline, int? brightness = null)
    {
        var hex = Normalize(color);

        var config = LightingPolicy.Clone(baseline);
        config.Effect = Effect;
        config.Brightness = brightness is { } level
            ? NormalizeBrightness(level)
            : config.Brightness;
        config.Color = hex;
        config.LEDs = new List<string> { hex, hex, hex, hex };
        return config;
    }
}
