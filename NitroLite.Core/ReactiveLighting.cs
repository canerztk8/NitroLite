namespace NitroLite.Core;

/// <summary>
/// The keyboard colour that follows the processor's temperature, and the rule that decides when it is worth writing.
/// </summary>
/// <remarks>
/// <para>
/// The ramp is a pure function of one number, so it can be tested without a keyboard, a backend or a window. It is
/// deliberately short: four stops between 45 °C and 90 °C, which is the range a Nitro actually lives in. Below 45 the
/// colour holds at the cold end rather than drifting to black, because a keyboard that goes dark at idle reads as a
/// fault; above 90 it holds at the hot end, because a colour cannot say "hotter than hot".
/// </para>
/// <para>
/// The temperature is quantised before it becomes a colour. A sensor that reports 61.2 and then 61.4 is not asking for
/// two different colours, and every colour change is a write to the embedded controller — the one thing in this
/// feature that is not free.
/// </para>
/// </remarks>
public static class ReactiveLighting
{
    /// <summary>The temperature at or below which the keyboard holds the cold colour.</summary>
    public const double ColdCelsius = 45;

    /// <summary>The temperature at or above which the keyboard holds the hot colour.</summary>
    public const double HotCelsius = 90;

    /// <summary>How far the temperature has to move before the colour is allowed to change.</summary>
    /// <remarks>
    /// Three degrees, and every stop on the ramp is a multiple of it, so the grid and the stops line up: 45, 60, 72 and
    /// 90 are all reachable exactly. A finer grid would spend writes on noise; a coarser one would skip the amber.
    /// </remarks>
    public const double BucketCelsius = 3;

    /// <summary>The shortest gap between two writes, whatever the temperature does.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

    /// <summary>The colour the keyboard holds when nothing has been read yet.</summary>
    public const string ColdColor = "#00B4FF";

    /// <summary>The colour at the top of the ramp.</summary>
    public const string HotColor = "#FF3B30";

    /// <summary>
    /// The ramp, coldest first. Four stops is enough for the eye to read "warming up" without the keyboard spending
    /// the evening in a colour nobody chose.
    /// </summary>
    private static readonly (double At, byte R, byte G, byte B)[] Stops =
    {
        (ColdCelsius, 0x00, 0xB4, 0xFF), // ice blue
        (60, 0x00, 0xE5, 0xA0),          // mint
        (72, 0xFF, 0xC4, 0x00),          // amber
        (HotCelsius, 0xFF, 0x3B, 0x30)   // red
    };

    /// <summary>
    /// The colour for a temperature, as <c>#RRGGBB</c>.
    /// </summary>
    /// <remarks>
    /// A reading that is not a number — a sensor that answered <c>NaN</c>, which the chaos tests inject — falls back to
    /// the cold colour instead of producing <c>#FFFFFF</c> or throwing. A keyboard is not the place to discover that a
    /// division went wrong.
    /// </remarks>
    public static string ColorFor(double celsius)
    {
        if (double.IsNaN(celsius) || double.IsInfinity(celsius)) return ColdColor;

        var at = Quantize(celsius);

        if (at <= Stops[0].At) return ColdColor;
        if (at >= Stops[^1].At) return HotColor;

        for (int i = 1; i < Stops.Length; i++)
        {
            var (hiAt, hiR, hiG, hiB) = Stops[i];
            if (at > hiAt) continue;

            var (loAt, loR, loG, loB) = Stops[i - 1];
            var span = hiAt - loAt;
            var t = span <= 0 ? 0 : (at - loAt) / span;

            return Hex(Mix(loR, hiR, t), Mix(loG, hiG, t), Mix(loB, hiB, t));
        }

        return HotColor;
    }

    /// <summary>Rounds a temperature to the nearest bucket, clamped to the ramp.</summary>
    /// <remarks>
    /// The grid is anchored at zero rather than at the cold end, so a stop that is a whole number of degrees is a bucket
    /// of its own and the ramp actually reaches the colour it documents. Anchoring at 45 would put the amber stop off
    /// the grid, and the keyboard would never show it.
    /// </remarks>
    public static double Quantize(double celsius)
    {
        if (double.IsNaN(celsius) || double.IsInfinity(celsius)) return ColdCelsius;

        var clamped = Math.Clamp(celsius, ColdCelsius, HotCelsius);
        var steps = Math.Round(clamped / BucketCelsius, MidpointRounding.AwayFromZero);
        return Math.Clamp(steps * BucketCelsius, ColdCelsius, HotCelsius);
    }

    private static byte Mix(byte from, byte to, double t)
    {
        var clamped = Math.Clamp(t, 0, 1);
        return (byte)Math.Round(from + ((to - from) * clamped), MidpointRounding.AwayFromZero);
    }

    private static string Hex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
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
