using System.Text.Json.Serialization;

namespace NitroLite.Core;

/// <summary>
/// The keyboard colour that follows the processor's temperature, and the rule that decides when it is worth writing.
/// </summary>
/// <remarks>
/// <para>
/// The ramp is a pure function of one number, so it can be tested without a keyboard, a backend or a window. The
/// built-in profile is deliberately short: four stops between 45 °C and 90 °C, which is the range a Nitro actually
/// lives in. Below the first stop the colour holds at the cold end rather than drifting to black, because a keyboard
/// that goes dark at idle reads as a fault; above the last stop it holds at the hot end, because a colour cannot say
/// "hotter than hot". A custom ramp may use any stops between <see cref="MinStopCelsius"/> and
/// <see cref="MaxStopCelsius"/>.
/// </para>
/// <para>
/// The temperature is quantised before it becomes a colour. A sensor that reports 61.2 and then 61.4 is not asking for
/// two different colours, and every colour change is a write to the embedded controller — the one thing in this
/// feature that is not free.
/// </para>
/// </remarks>
public static class ReactiveLighting
{
    /// <summary>The temperature at or below which the built-in ramp holds the cold colour.</summary>
    public const double ColdCelsius = 45;

    /// <summary>The temperature at or above which the built-in ramp holds the hot colour.</summary>
    public const double HotCelsius = 90;

    /// <summary>How far the temperature has to move before the colour is allowed to change.</summary>
    /// <remarks>
    /// Three degrees, and every stop on the built-in ramp is a multiple of it, so the grid and the stops line up: 45, 60,
    /// 72 and 90 are all reachable exactly. A finer grid would spend writes on noise; a coarser one would skip the amber.
    /// </remarks>
    public const double BucketCelsius = 3;

    /// <summary>The shortest gap between two writes, whatever the temperature does.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

    /// <summary>The colour the keyboard holds when nothing has been read yet.</summary>
    public const string ColdColor = "#00B4FF";

    /// <summary>The colour at the top of the built-in ramp.</summary>
    public const string HotColor = "#FF3B30";

    /// <summary>The coldest temperature a stop on a custom ramp may sit at.</summary>
    public const double MinStopCelsius = 20;

    /// <summary>The hottest temperature a stop on a custom ramp may sit at.</summary>
    public const double MaxStopCelsius = 110;

    /// <summary>The most stops one custom ramp may carry. More than that is a gradient the eye cannot read.</summary>
    public const int MaxStops = 8;

    /// <summary>
    /// The built-in ramp, coldest first. Four stops is enough for the eye to read "warming up" without the keyboard
    /// spending the evening in a colour nobody chose.
    /// </summary>
    private static readonly (double At, byte R, byte G, byte B)[] Stops =
    {
        (ColdCelsius, 0x00, 0xB4, 0xFF), // ice blue
        (60, 0x00, 0xE5, 0xA0),          // mint
        (72, 0xFF, 0xC4, 0x00),          // amber
        (HotCelsius, 0xFF, 0x3B, 0x30)   // red
    };

    /// <summary>The built-in ramp as stops, so the profile list and the evaluator share one definition.</summary>
    internal static IReadOnlyList<ReactiveLightingStop> DefaultStops =>
        Stops.Select(s => new ReactiveLightingStop { AtCelsius = s.At, Color = Hex(s.R, s.G, s.B) }).ToArray();

    /// <summary>
    /// The colour for a temperature on the built-in ramp, as <c>#RRGGBB</c>.
    /// </summary>
    public static string ColorFor(double celsius) => ColorFor(DefaultStops, celsius);

    /// <summary>
    /// The colour for a temperature on any ramp, as <c>#RRGGBB</c>. The stops are sanitised here, so a ramp read from
    /// a settings file needs no preparation: unsorted stops are sorted, duplicates dropped, colours normalised, and a
    /// ramp with fewer than two usable stops falls back to the built-in one.
    /// </summary>
    /// <remarks>
    /// A reading that is not a number — a sensor that answered <c>NaN</c>, which the chaos tests inject — falls back to
    /// the cold end instead of producing <c>#FFFFFF</c> or throwing. A keyboard is not the place to discover that a
    /// division went wrong.
    /// </remarks>
    public static string ColorFor(IReadOnlyList<ReactiveLightingStop>? stops, double celsius)
    {
        var clean = ReactiveLightingProfiles.SanitizeStops(stops);
        if (clean.Count < 2) clean = ReactiveLightingProfiles.SanitizeStops(DefaultStops);

        if (double.IsNaN(celsius) || double.IsInfinity(celsius)) return clean[0].Color;

        // The grid is anchored at zero rather than at the cold end, so a stop that is a whole number of degrees is a
        // bucket of its own and the ramp actually reaches the colour it documents. Anchoring at the cold end would put
        // a stop off the grid, and the keyboard would never show it.
        var min = clean[0].AtCelsius;
        var max = clean[^1].AtCelsius;
        var clamped = Math.Clamp(celsius, min, max);
        var steps = Math.Round(clamped / BucketCelsius, MidpointRounding.AwayFromZero);
        var at = Math.Clamp(steps * BucketCelsius, min, max);

        if (at <= clean[0].AtCelsius) return clean[0].Color;
        if (at >= clean[^1].AtCelsius) return clean[^1].Color;

        for (int i = 1; i < clean.Count; i++)
        {
            var hi = Parse(clean[i]);
            if (at > hi.At) continue;

            var lo = Parse(clean[i - 1]);
            var span = hi.At - lo.At;
            var t = span <= 0 ? 0 : (at - lo.At) / span;

            return Hex(Mix(lo.R, hi.R, t), Mix(lo.G, hi.G, t), Mix(lo.B, hi.B, t));
        }

        return clean[^1].Color;
    }

    /// <summary>Rounds a temperature to the nearest bucket, clamped to the built-in ramp.</summary>
    /// <remarks>
    /// Kept on the built-in range for compatibility: the general form clamps to the ramp's own ends inside
    /// <see cref="ColorFor(IReadOnlyList{ReactiveLightingStop}, double)"/>.
    /// </remarks>
    public static double Quantize(double celsius)
    {
        if (double.IsNaN(celsius) || double.IsInfinity(celsius)) return ColdCelsius;

        var clamped = Math.Clamp(celsius, ColdCelsius, HotCelsius);
        var steps = Math.Round(clamped / BucketCelsius, MidpointRounding.AwayFromZero);
        return Math.Clamp(steps * BucketCelsius, ColdCelsius, HotCelsius);
    }

    private static (double At, byte R, byte G, byte B) Parse(ReactiveLightingStop stop)
    {
        // SanitizeStops has already normalised the colour to "#RRGGBB", so the substrings are safe to convert.
        var hex = LightingPolicy.NormalizeHex(stop.Color, ColdColor);
        return (stop.AtCelsius,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
    }

    private static byte Mix(byte from, byte to, double t)
    {
        var clamped = Math.Clamp(t, 0, 1);
        return (byte)Math.Round(from + ((to - from) * clamped), MidpointRounding.AwayFromZero);
    }

    private static string Hex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}

/// <summary>
/// One stop on a temperature ramp: the colour the keyboard holds at <see cref="AtCelsius"/> and above, until the next
/// stop takes over.
/// </summary>
public sealed class ReactiveLightingStop
{
    public double AtCelsius { get; set; }

    /// <summary>The colour at this temperature, as <c>#RRGGBB</c>.</summary>
    public string Color { get; set; } = ReactiveLighting.ColdColor;

    public ReactiveLightingStop Clone() => new() { AtCelsius = AtCelsius, Color = Color };
}

/// <summary>
/// A named temperature ramp. The built-in profiles translate their name for display; the custom entry keeps the
/// label it is given in the UI.
/// </summary>
public sealed class ReactiveLightingProfile
{
    /// <summary>The stable identity — settings store it, so it must not change with the UI language.</summary>
    public string Name { get; set; } = "";

    public bool IsBuiltIn { get; set; }

    public List<ReactiveLightingStop> Stops { get; set; } = new();

    /// <summary>The name to show in the UI.</summary>
    [JsonIgnore]
    public string DisplayName => IsBuiltIn ? Loc.T(Name) : Name;
}

/// <summary>
/// The ramp profiles the app offers, and the boundary for the ones read back from a settings file.
/// </summary>
public static class ReactiveLightingProfiles
{
    /// <summary>The id of the custom entry: the ramp the user edits stop by stop in the UI.</summary>
    public const string CustomId = "custom";

    /// <summary>The three profiles the app ships. The first is the ramp <see cref="ReactiveLighting"/> has always used.</summary>
    public static readonly IReadOnlyList<ReactiveLightingProfile> BuiltIn = new[]
    {
        new ReactiveLightingProfile
        {
            Name = "Klasik (Mavi → Kırmızı)",
            IsBuiltIn = true,
            Stops = ReactiveLighting.DefaultStops.ToList()
        },
        new ReactiveLightingProfile
        {
            Name = "Sıcak Ateş (Sarı → Kırmızı)",
            IsBuiltIn = true,
            Stops = new List<ReactiveLightingStop>
            {
                new() { AtCelsius = 45, Color = "#FFD600" }, // amber
                new() { AtCelsius = 60, Color = "#FF9100" }, // orange
                new() { AtCelsius = 75, Color = "#FF3D00" }, // red-orange
                new() { AtCelsius = 90, Color = "#D50000" }  // deep red
            }
        },
        new ReactiveLightingProfile
        {
            Name = "Buz → Mor (Soğuk Palet)",
            IsBuiltIn = true,
            Stops = new List<ReactiveLightingStop>
            {
                new() { AtCelsius = 45, Color = "#00E5FF" }, // ice
                new() { AtCelsius = 60, Color = "#448AFF" }, // blue
                new() { AtCelsius = 75, Color = "#7C4DFF" }, // violet
                new() { AtCelsius = 90, Color = "#D500F9" }  // magenta
            }
        }
    };

    /// <summary>The built-in profile with this name, or null when the name is not one.</summary>
    public static ReactiveLightingProfile? FindBuiltIn(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : BuiltIn.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Brings a ramp read from disk or built by the editor into the shape the evaluator can use: sorted by
    /// temperature, at most <see cref="ReactiveLighting.MaxStops"/> stops, no duplicate temperatures, every colour a
    /// valid <c>#RRGGBB</c>, every temperature inside the allowed range. Stops with an unusable temperature are dropped
    /// rather than clamped onto a neighbour, because a NaN would otherwise become a real stop.
    /// </summary>
    public static List<ReactiveLightingStop> SanitizeStops(IEnumerable<ReactiveLightingStop>? stops)
    {
        var result = new List<ReactiveLightingStop>();
        if (stops == null) return result;

        var fallback = ReactiveLighting.ColdColor;
        foreach (var stop in stops)
        {
            if (stop == null) continue;
            if (double.IsNaN(stop.AtCelsius) || double.IsInfinity(stop.AtCelsius)) continue;

            var color = LightingPolicy.NormalizeHex(stop.Color, fallback);
            fallback = color;

            result.Add(new ReactiveLightingStop
            {
                AtCelsius = Math.Clamp(stop.AtCelsius, ReactiveLighting.MinStopCelsius, ReactiveLighting.MaxStopCelsius),
                Color = color
            });
        }

        return result
            .OrderBy(s => s.AtCelsius)
            .Take(ReactiveLighting.MaxStops)
            .GroupBy(s => s.AtCelsius)
            .Select(g => g.First())
            .ToList();
    }
}

/// <summary>
/// Decides when a reactive colour is worth another write to the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// Two rules, both there to keep the embedded controller out of the telemetry loop. A colour that has not changed is
/// never written again, so a machine sitting at 52 °C writes once and then nothing. A colour that has changed waits for
/// <see cref="ReactiveLighting.MinInterval"/>, so a temperature oscillating across a bucket boundary cannot turn into a
/// write every second.
/// </para>
/// <para>
/// Not thread-safe on purpose: it is owned by the view model and only touched from the telemetry tick.
/// </para>
/// </remarks>
public sealed class ReactiveLightingThrottle
{
    private string? _lastColor;
    private DateTime _lastWrite = DateTime.MinValue;

    /// <summary>The colour of the last write, or null before the first one.</summary>
    public string? LastColor => _lastColor;

    /// <summary>
    /// True when this colour should be written now, and records the write when it says yes.
    /// </summary>
    /// <remarks>
    /// The recording is part of the answer rather than a second call, because a caller that asked and then forgot to
    /// record would write on every tick — the exact failure this class exists to prevent.
    /// </remarks>
    public bool TryBeginWrite(string color, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(color)) return false;

        if (string.Equals(color, _lastColor, StringComparison.OrdinalIgnoreCase)) return false;
        if (_lastColor != null && now - _lastWrite < ReactiveLighting.MinInterval) return false;

        _lastColor = color;
        _lastWrite = now;
        return true;
    }

    /// <summary>Forgets the last write, so the next colour is written whatever it is.</summary>
    public void Reset()
    {
        _lastColor = null;
        _lastWrite = DateTime.MinValue;
    }
}
