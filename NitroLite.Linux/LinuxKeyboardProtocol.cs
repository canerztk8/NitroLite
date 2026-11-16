using System.Globalization;
using NitroLite.Core;
using NitroLite.Core.Hardware.OpenRgb;

namespace NitroLite.Linux;

/// <summary>Four zone colours plus a brightness, as <c>per_zone_mode</c> carries them.</summary>
public readonly record struct LinuxZoneLighting(IReadOnlyList<string> Zones, int BrightnessPercent);

/// <summary>One effect with its parameters, as <c>four_zone_mode</c> carries them.</summary>
public readonly record struct LinuxEffectLighting(int Mode, int Speed, int BrightnessPercent, int Direction, string Color);

/// <summary>
/// The two keyboard attributes Linuwu-Sense registers, and the translation between them and NitroLite's own lighting
/// model.
/// </summary>
/// <remarks>
/// <para>
/// The module exposes two different things. <c>per_zone_mode</c> is a static picture: four colours and one brightness,
/// written as <c>RRGGBB,RRGGBB,RRGGBB,RRGGBB,brightness</c>. <c>four_zone_mode</c> is an animation: a mode number, a
/// speed, a brightness, a direction and one colour, written as <c>mode,speed,brightness,direction,r,g,b</c>. They are
/// separate files because they are separate firmware states, so a static write and an effect write do not overwrite
/// each other's parameters.
/// </para>
/// <para>
/// The scales do not match NitroLite's. The app carries brightness as a level from 0 to 9 and speed from 1 to 9; the
/// module wants brightness as a percentage from 0 to 100 and speed from 0 to 9. Brightness is therefore converted at
/// the boundary rather than stored differently, so the rest of the application keeps one meaning for "brightness 5".
/// </para>
/// <para>
/// Colours are written without a leading <c>#</c>. The module's parser reads hex pairs, and a stray <c>#</c> makes the
/// whole line invalid, which the kernel reports as <c>EINVAL</c> and the user sees as a switch that does nothing.
/// </para>
/// </remarks>
public static class LinuxKeyboardProtocol
{
    /// <summary>Zones the module addresses. Four on every model that has the attribute.</summary>
    public const int ZoneCount = LightingLimits.ZoneCount;

    /// <summary>Highest brightness the module accepts, as a percentage.</summary>
    public const int MaxBrightnessPercent = 100;

    /// <summary>Highest brightness NitroLite stores, as a level.</summary>
    public const int MaxBrightnessLevel = LightingLimits.MaxBrightnessLevel;

    /// <summary>Highest effect speed the module accepts.</summary>
    public const int MaxSpeed = LightingLimits.MaxSpeed;

    /// <summary>
    /// Highest direction the module accepts. It knows left and right only, while NitroLite's WAVE effect also offers up
    /// and down, so a vertical direction is clamped rather than rejected.
    /// </summary>
    public const int MaxDirection = LightingLimits.MaxDirection;

    /// <summary>Highest effect mode the module accepts.</summary>
    public const int MaxMode = 7;

    public const int ModeStatic = 0;
    public const int ModeBreathing = 1;
    public const int ModeNeon = 2;
    public const int ModeWave = 3;
    public const int ModeShifting = 4;
    public const int ModeZoom = 5;
    public const int ModeMeteor = 6;
    public const int ModeTwinkling = 7;

    /// <summary>
    /// The module's mode number for an app effect name. <c>DIRECT</c> maps to static because the module has no
    /// separate per-zone mode: four colours are what <c>per_zone_mode</c> is for.
    /// </summary>
    public static int ModeFor(string? effect) => (effect ?? "").Trim().ToUpperInvariant() switch
    {
        "BREATHING" => ModeBreathing,
        "NEON" => ModeNeon,
        "WAVE" => ModeWave,
        "SHIFTING" => ModeShifting,
        "ZOOM" => ModeZoom,
        "METEOR" => ModeMeteor,
        "TWINKLING" => ModeTwinkling,
        _ => ModeStatic
    };

    /// <summary>The app effect name for a mode number. An unknown number reads as static, which is the safe default.</summary>
    public static string EffectFor(int mode) => mode switch
    {
        ModeBreathing => "BREATHING",
        ModeNeon => "NEON",
        ModeWave => "WAVE",
        ModeShifting => "SHIFTING",
        ModeZoom => "ZOOM",
        ModeMeteor => "METEOR",
        ModeTwinkling => "TWINKLING",
        _ => "STATIC"
    };

    /// <summary>
    /// The app's 0..9 brightness level as the module's 0..100 percentage. Integer arithmetic, so the round trip
    /// <c>BrightnessPercentToLevel(BrightnessLevelToPercent(n)) == n</c> holds for every level.
    /// </summary>
    public static int BrightnessLevelToPercent(int level) =>
        Math.Clamp(level, 0, MaxBrightnessLevel) * MaxBrightnessPercent / MaxBrightnessLevel;

    /// <summary>The module's 0..100 percentage as the app's 0..9 brightness level.</summary>
    public static int BrightnessPercentToLevel(int percent) =>
        (int)Math.Round(Math.Clamp(percent, 0, MaxBrightnessPercent) / (double)MaxBrightnessPercent * MaxBrightnessLevel);

    /// <summary>A direction the module can express: 1 or 2.</summary>
    public static int NormalizeDirection(int direction) => Math.Clamp(direction, 1, MaxDirection);

    /// <summary>
    /// A colour as the module wants it: six uppercase hex digits, no <c>#</c>. An unparseable value becomes
    /// <paramref name="fallback"/>, and a fallback that is also unparseable becomes black.
    /// </summary>
    public static string NormalizeColor(string? color, string? fallback = null)
    {
        var parsed = RgbColor.Parse(color);
        if (parsed is { R: 0, G: 0, B: 0 } && !IsBlack(color)) parsed = RgbColor.Parse(fallback);
        return parsed.ToString().TrimStart('#');
    }

    /// <summary>True when the text names black on purpose, rather than being unparseable.</summary>
    private static bool IsBlack(string? color)
    {
        var text = (color ?? "").Trim().TrimStart('#');
        return text.Length == 6 && text.All(c => c == '0');
    }

    /// <summary>
    /// The <c>per_zone_mode</c> line for four colours and a brightness level. A missing or short
    /// <paramref name="zoneColors"/> falls back to <paramref name="primaryColor"/> for every zone, which is what the
    /// user sees when they pick one colour for the whole keyboard.
    /// </summary>
    public static string BuildPerZone(IReadOnlyList<string>? zoneColors, string primaryColor, int brightnessLevel)
    {
        var zones = new string[ZoneCount];
        for (int i = 0; i < ZoneCount; i++)
        {
            var source = zoneColors != null && i < zoneColors.Count ? zoneColors[i] : primaryColor;
            zones[i] = NormalizeColor(source, primaryColor);
        }

        return string.Join(',', zones) + "," + BrightnessLevelToPercent(brightnessLevel).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads a <c>per_zone_mode</c> line back. Null when the text is not four colours and a brightness, so a caller
    /// comparing a write against its read-back treats an unreadable answer as "no answer" rather than as a mismatch.
    /// </summary>
    public static LinuxZoneLighting? ParsePerZone(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Trim().Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < ZoneCount + 1) return null;

        var zones = new string[ZoneCount];
        for (int i = 0; i < ZoneCount; i++)
        {
            if (!IsHexColor(parts[i])) return null;
            zones[i] = "#" + parts[i].ToUpperInvariant();
        }

        if (!int.TryParse(parts[ZoneCount], NumberStyles.Integer, CultureInfo.InvariantCulture, out var brightness))
        {
            return null;
        }

        return new LinuxZoneLighting(zones, Math.Clamp(brightness, 0, MaxBrightnessPercent));
    }

    /// <summary>The <c>four_zone_mode</c> line for one effect and its parameters.</summary>
    public static string BuildFourZone(string effect, int speed, int brightnessLevel, int direction, string color)
    {
        var rgb = RgbColor.Parse(NormalizeColor(color));
        var fields = new[]
        {
            ModeFor(effect).ToString(CultureInfo.InvariantCulture),
            Math.Clamp(speed, 0, MaxSpeed).ToString(CultureInfo.InvariantCulture),
            BrightnessLevelToPercent(brightnessLevel).ToString(CultureInfo.InvariantCulture),
            NormalizeDirection(direction).ToString(CultureInfo.InvariantCulture),
            rgb.R.ToString(CultureInfo.InvariantCulture),
            rgb.G.ToString(CultureInfo.InvariantCulture),
            rgb.B.ToString(CultureInfo.InvariantCulture)
        };

        return string.Join(',', fields);
    }

    /// <summary>Reads a <c>four_zone_mode</c> line back, or null when it is not seven numbers.</summary>
    public static LinuxEffectLighting? ParseFourZone(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Trim().Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 7) return null;

        var numbers = new int[7];
        for (int i = 0; i < numbers.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i])) return null;
        }

        var color = new RgbColor(
            (byte)Math.Clamp(numbers[4], 0, 255),
            (byte)Math.Clamp(numbers[5], 0, 255),
            (byte)Math.Clamp(numbers[6], 0, 255));

        return new LinuxEffectLighting(
            Math.Clamp(numbers[0], 0, MaxMode),
            Math.Clamp(numbers[1], 0, MaxSpeed),
            Math.Clamp(numbers[2], 0, MaxBrightnessPercent),
            NormalizeDirection(numbers[3]),
            color.ToString());
    }

    /// <summary>True when the text is exactly six hex digits, with or without a leading <c>#</c>.</summary>
    public static bool IsHexColor(string? text)
    {
        var value = (text ?? "").Trim().TrimStart('#');
        return value.Length == 6 && value.All(Uri.IsHexDigit);
    }
}
