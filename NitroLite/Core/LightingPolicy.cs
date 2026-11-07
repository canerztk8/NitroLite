using System;
using System.Collections.Generic;
using System.Linq;

namespace NitroLite.Core;

/// <summary>
/// What each keyboard effect accepts, based on what the AN16-41 keyboard reports (GET_CONNECTED_DEVICE,
/// OpenRGB-style mode flags) and NitroSense's EFFECT_DIRECTION enum.
/// </summary>
public static class LightingPolicy
{
    public const string DefaultColor = "#00E5FF";

    public static bool IsOff(LightingConfig cfg) =>
        cfg.Brightness <= 0 || string.Equals(cfg.Effect, "OFF", StringComparison.OrdinalIgnoreCase);

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
        1 => "Sağa",
        2 => "Sola",
        3 => "Yukarı",
        4 => "Aşağı",
        _ => "Sağa"
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
        if (cfg.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase)) cfg.Effect = "STATIC";

        if (IsBlack(cfg.Color)) cfg.Color = DefaultColor;
        var leds = (cfg.LEDs ?? new List<string>()).Take(4).ToList();
        while (leds.Count < 4) leds.Add(cfg.Color);
        cfg.LEDs = leds.Select(l => IsBlack(l) ? cfg.Color : l).ToList();
        if (!AllowedDirections(cfg.Effect).Contains(cfg.Direction)) cfg.Direction = 1;
        return cfg;
    }
}
