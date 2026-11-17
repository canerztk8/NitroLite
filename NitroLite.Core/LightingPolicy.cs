
namespace NitroLite.Core;

/// <summary>
/// What each keyboard effect accepts, based on what the AN16-41 keyboard reports (GET_CONNECTED_DEVICE,
/// OpenRGB-style mode flags) and NitroSense's EFFECT_DIRECTION enum.
/// </summary>
public static class LightingPolicy
{
    public const string DefaultColor = "#00E5FF";

    /// <summary>
    /// The nine effects the keyboard offers, in the order the WPF page listed them. OFF is last because it is
    /// NitroLite's own "brightness zero" rather than a device effect, and a list that put it anywhere else would read
    /// as one more animation.
    /// </summary>
    public static IReadOnlyList<string> AllEffects { get; } = new[]
    {
        "STATIC", "WAVE", "BREATHING", "NEON", "SHIFTING", "ZOOM", "METEOR", "TWINKLING", "OFF"
    };

    public static bool IsOff(LightingConfig cfg) =>
        cfg.Brightness <= 0 || string.Equals(cfg.Effect, "OFF", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A colour as <c>#RRGGBB</c>, or <paramref name="fallback"/> when the text is not one. The service rejects a
    /// malformed colour with WRONG_PARAMETERS, so nothing unvalidated may reach it.
    /// </summary>
    public static string NormalizeHex(string? color, string fallback)
    {
        if (string.IsNullOrWhiteSpace(color)) return fallback;
        var hex = color.Trim().TrimStart('#');
        if (hex.Length != 6) return fallback;
        foreach (var ch in hex)
        {
            if (!Uri.IsHexDigit(ch)) return fallback;
        }
        return "#" + hex.ToUpperInvariant();
    }

    /// <summary>
    /// A lighting configuration that can be sent to the service: no null strings, every number inside the range the
    /// keyboard accepts, every colour a valid hex triplet, exactly four zones. Null stays null, because a settings
    /// file with no lighting block means "read the keyboard's own state", not "apply a default".
    /// <para>
    /// This is the boundary for anything read from disk — a settings file, a backup, a preset file. Those are
    /// outside the app's control, and a null <see cref="LightingConfig.Effect"/> used to reach
    /// <c>cfg.Effect.Equals(...)</c> in the startup refresh and abort every step after it.
    /// </para>
    /// </summary>
    public static LightingConfig? Sanitize(LightingConfig? cfg)
    {
        if (cfg == null) return null;

        var effect = string.IsNullOrWhiteSpace(cfg.Effect) ? "STATIC" : cfg.Effect.Trim().ToUpperInvariant();
        var color = NormalizeHex(cfg.Color, DefaultColor);

        var leds = new List<string>(4);
        if (cfg.LEDs != null)
        {
            foreach (var led in cfg.LEDs)
            {
                if (leds.Count == 4) break;
                leds.Add(NormalizeHex(led, color));
            }
        }
        while (leds.Count < 4) leds.Add(color);

        return new LightingConfig
        {
            Effect = effect,
            Brightness = Math.Clamp(cfg.Brightness, 0, 9),
            Speed = Math.Clamp(cfg.Speed, 1, 9),
            Direction = AllowedDirections(effect).Contains(cfg.Direction) ? cfg.Direction : 1,
            Color = color,
            LEDs = leds
        };
    }

    /// <summary>The red every shipped red became: pure red, with no green or blue for the diffuser to wash out.</summary>
    public const string PureRed = "#FF0000";

    /// <summary>
    /// The reds NitroLite itself used to ship, before every red became <see cref="PureRed"/>.
    /// </summary>
    /// <remarks>
    /// <c>#D93829</c> was the palette's coral, <c>#E01B1B</c> the palette's red that replaced it, <c>#FF2A55</c> the
    /// crimson every built-in preset carried, and <c>#FF1A1A</c> / <c>#FF3D00</c> the reds those presets were moved
    /// to next. None of them is offered anywhere in the app any more, so a file that still holds one is describing a
    /// colour the user cannot pick again.
    /// </remarks>
    private static readonly string[] LegacyReds = { "#D93829", "#E01B1B", "#FF2A55", "#FF1A1A", "#FF3D00" };

    /// <summary>
    /// Rewrites one of the reds NitroLite used to ship into <see cref="PureRed"/>, and leaves every other colour
    /// exactly as it is.
    /// </summary>
    /// <remarks>
    /// This is a migration, not a rule: it runs on values read from disk, so a keyboard that was left on the old red
    /// comes back on the new one instead of keeping a colour the palette no longer contains. A colour the user typed
    /// themselves is untouched, because nothing else matches.
    /// </remarks>
    public static string MigrateLegacyRed(string? hex)
    {
        var normalized = NormalizeHex(hex, DefaultColor);
        return LegacyReds.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? PureRed : normalized;
    }

    /// <summary>
    /// <see cref="MigrateLegacyRed"/> applied to a whole configuration: the primary colour and all four zones.
    /// </summary>
    public static LightingConfig? MigrateLegacyReds(LightingConfig? cfg)
    {
        if (cfg == null) return null;

        return new LightingConfig
        {
            Effect = cfg.Effect,
            Brightness = cfg.Brightness,
            Speed = cfg.Speed,
            Direction = cfg.Direction,
            Color = MigrateLegacyRed(cfg.Color),
            LEDs = (cfg.LEDs ?? new List<string>()).Select(MigrateLegacyRed).ToList()
        };
    }

    /// <summary>WAVE and NEON cycle the keyboard's own colours; every other effect uses the chosen colour.</summary>
    public static bool EffectUsesColor(string effect) => effect.ToUpperInvariant() switch
    {
        "WAVE" or "NEON" or "OFF" => false,
        _ => true
    };

    /// <summary>Only the static mode takes four separate zone colours (NitroSense sends STATIC + 4 LEDs).</summary>
    public static bool EffectUsesZoneColors(string effect) =>
        string.Equals(effect, "STATIC", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The effect name to actually send to the firmware for a configuration.
    /// <para>
    /// The keyboard advertises how many colours each of its modes takes, and on the AN16-41 that number is 1 for
    /// STATIC and 4 for DIRECT (see <c>GET_CONNECTED_DEVICE</c>). NitroLite's own "static" means "a fixed colour
    /// per zone", so a configuration carrying more than one zone colour has to go out as DIRECT: sending four
    /// colours under STATIC is why only one of them ever reached the keyboard. A single colour stays STATIC, which
    /// is the mode that also carries a speed and a direction.
    /// </para>
    /// </summary>
    public static string DeviceEffectFor(string effect, IEnumerable<string>? zoneColors)
    {
        var upper = (effect ?? "STATIC").Trim().ToUpperInvariant();
        if (upper != "STATIC" || zoneColors == null) return upper;

        var distinct = zoneColors
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .Distinct()
            .Count();

        return distinct > 1 ? "DIRECT" : upper;
    }

    /// <summary>
    /// The label shown for a device effect name. The catalogue key is the Turkish source label the XAML already
    /// carries, so a rebuilt list reads exactly like the one it replaces. A name the catalogue does not know is
    /// shown as the device reports it rather than hidden.
    /// </summary>
    public static string EffectDisplayName(string effect) => effect.ToUpperInvariant() switch
    {
        "STATIC" => Loc.T("STATIC (Sabit Renk)"),
        "WAVE" => Loc.T("WAVE (Dalga)"),
        "BREATHING" => Loc.T("BREATHING (Nefes)"),
        "NEON" => Loc.T("NEON (Neon Geçiş)"),
        "SHIFTING" => Loc.T("SHIFTING (Renk Kayması)"),
        "ZOOM" => Loc.T("ZOOM (Büyüme)"),
        "METEOR" => Loc.T("METEOR (Kayan Işık)"),
        "TWINKLING" => Loc.T("TWINKLING (Yıldız Parıltısı)"),
        "OFF" => Loc.T("OFF (Işık Kapalı)"),
        _ => effect
    };

    /// <summary>
    /// The effects the dropdown should offer for a keyboard that reported <paramref name="reported"/>, or an empty
    /// list when the report is too thin to act on.
    /// <para>
    /// DIRECT is left out: NitroLite drives per-zone colours through STATIC, and offering a second path to the same
    /// LEDs would be a behaviour nobody has tested. OFF is always last, because it is NitroLite's own "brightness
    /// zero" rather than a device effect.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> OfferedEffects(IEnumerable<string>? reported)
    {
        var list = new List<string>();
        if (reported != null)
        {
            foreach (var name in reported)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var upper = name.Trim().ToUpperInvariant();
                if (upper is "DIRECT" or "OFF") continue;
                if (!list.Contains(upper)) list.Add(upper);
            }
        }

        // A response that names almost nothing is not evidence that the keyboard has almost nothing: the caller
        // keeps the full list rather than leave the user with one option.
        if (list.Count < 3) return Array.Empty<string>();
        list.Add("OFF");
        return list;
    }

    /// <summary>
    /// Directions the effect supports (1 right, 2 left, 3 up, 4 down). WAVE reports left/right and up/down,
    /// SHIFTING left/right only; other effects have none.
    /// </summary>
    public static IReadOnlyList<int> AllowedDirections(string effect) => effect.ToUpperInvariant() switch
    {
        "WAVE" => new[] { 1, 2, 3, 4 },
        "SHIFTING" => new[] { 1, 2 },
        _ => Array.Empty<int>()
    };

    public static string DirectionName(int direction) => direction switch
    {
        1 => Loc.T("Sağa"),
        2 => Loc.T("Sola"),
        3 => Loc.T("Yukarı"),
        4 => Loc.T("Aşağı"),
        _ => Loc.T("Sağa")
    };

    public static LightingConfig Clone(LightingConfig c) => new()
    {
        Effect = c.Effect,
        Brightness = c.Brightness,
        Speed = c.Speed,
        Direction = c.Direction,
        Color = c.Color,
        LEDs = new List<string>(c.LEDs ?? new List<string>())
    };

    private static bool IsBlack(string? hex) =>
        string.IsNullOrWhiteSpace(hex) || hex.Trim().TrimStart('#').Equals("000000", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the "back on" lighting when the user raises brightness or picks an effect while the lights are off:
    /// the last lit configuration (or a cyan static default) with the requested brightness/effect. Black colours
    /// left behind by "off" are replaced so the result is actually visible.
    /// </summary>
    public static LightingConfig ResumeFromOff(LightingConfig? lastOn, int? brightness = null, string? effect = null)
    {
        var cfg = lastOn != null && !IsOff(lastOn)
            ? Clone(lastOn)
            : new LightingConfig { Effect = "STATIC", Brightness = 5, Speed = 3, Direction = 1, Color = DefaultColor };

        if (brightness is > 0) cfg.Brightness = Math.Clamp(brightness.Value, 1, 9);
        if (!string.IsNullOrWhiteSpace(effect) && !effect.Equals("OFF", StringComparison.OrdinalIgnoreCase)) cfg.Effect = effect;
        if (cfg.Brightness <= 0) cfg.Brightness = 5;
        if (string.Equals(cfg.Effect, "OFF", StringComparison.OrdinalIgnoreCase)) cfg.Effect = "STATIC";

        if (IsBlack(cfg.Color)) cfg.Color = DefaultColor;
        var leds = (cfg.LEDs ?? new List<string>()).Take(4).ToList();
        while (leds.Count < 4) leds.Add(cfg.Color);
        cfg.LEDs = leds.Select(l => IsBlack(l) ? cfg.Color : l).ToList();
        if (!AllowedDirections(cfg.Effect).Contains(cfg.Direction)) cfg.Direction = 1;
        return cfg;
    }
}
