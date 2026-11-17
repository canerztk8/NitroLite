
namespace NitroLite.Core;

/// <summary>One anchor of the fan curve: at this CPU temperature, run the fans at this percentage.</summary>
public sealed record FanCurvePoint(int TemperatureC, int Percent);

/// <summary>
/// A temperature-to-fan-speed curve. Pure data and interpolation: no timers, no device access, no UI, so the
/// shape of the curve can be reasoned about and tested without a laptop.
/// <para>
/// The curve is the software answer to the firmware's own fan table: NitroLite reads the CPU temperature it
/// already polls and asks the Acer service for a fixed fan speed, which is the only fan control the service
/// exposes. Nothing here touches an EC register or a driver.
/// </para>
/// </summary>
public sealed class FanCurve
{
    /// <summary>Lowest speed the Acer service accepts for a custom fan speed.</summary>
    public const int MinPercent = 30;

    public const int MaxPercent = 100;

    /// <summary>Below this the curve has nothing to say: the sensor is cold or the reading is missing.</summary>
    public const int MinTemperatureC = 30;

    public const int MaxTemperatureC = 100;

    public const int DefaultHysteresisC = 3;

    public const int DefaultMinDwellSeconds = 10;

    /// <summary>
    /// Temperature bands where the curve is not allowed to ask for a slow fan, whatever shape the user drew.
    /// The curve is a preference; keeping a hot die cool is not. It also covers the case the editor cannot: if
    /// NitroLite dies, the last speed it wrote stays in the firmware, so that last write must never be a quiet one
    /// while the machine is hot.
    /// <para>
    /// The floor rises with the temperature. A single threshold at 85 °C left a 95 °C machine running at 60 %,
    /// which is not enough to cool a laptop die: the bands below are what the firmware itself would do.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<(int TemperatureC, int MinPercent)> SafetyFloor = new[]
    {
        (85, 60),
        (90, 80),
        (95, 100)
    };

    /// <summary>The first safety band, kept as a named constant because the editor and the tests talk about it.</summary>
    public const int SafetyTemperatureC = 85;

    /// <summary>Lowest speed the curve may ask for at or above <see cref="SafetyTemperatureC"/>.</summary>
    public const int SafetyMinPercent = 60;

    /// <summary>How many anchors the editor offers. Fixed, so the curve can never be left without a shape.</summary>
    public const int AnchorCount = 6;

    /// <summary>The temperatures the editor puts its anchors on.</summary>
    public static readonly IReadOnlyList<int> AnchorTemperatures = new[] { 40, 50, 60, 70, 80, 90 };

    /// <summary>
    /// The curve a fresh install starts from: the floor at idle, close to what the firmware does through the
    /// middle of the range, and full speed by 90 °C. Turning the feature on must not make the laptop louder than
    /// it already was, or the first thing the user does is turn it off again.
    /// <para>
    /// It is the "balanced" preset rather than a second copy of the same six numbers, so the editor opens with
    /// that template already marked as the one in use.
    /// </para>
    /// </summary>
    public static List<FanCurvePoint> DefaultPoints() =>
        FanCurvePresets.ById("balanced")?.ToPoints() ?? new List<FanCurvePoint>
        {
            new FanCurvePoint(40, 30),
            new FanCurvePoint(50, 32),
            new FanCurvePoint(60, 36),
            new FanCurvePoint(70, 42),
            new FanCurvePoint(80, 62),
            new FanCurvePoint(90, 100)
        };

    public FanCurve(IEnumerable<FanCurvePoint>? points, int hysteresisC = DefaultHysteresisC, int minDwellSeconds = DefaultMinDwellSeconds)
    {
        Points = Sanitize(points);
        HysteresisC = Math.Clamp(hysteresisC, 0, 15);
        MinDwellSeconds = Math.Clamp(minDwellSeconds, 0, 120);
    }

    /// <summary>Anchors sorted by temperature, at least one, every value inside the range the service accepts.</summary>
    public IReadOnlyList<FanCurvePoint> Points { get; }

    /// <summary>How far the temperature must fall below the last applied point before the fans slow down.</summary>
    public int HysteresisC { get; }

    /// <summary>Shortest gap between two downward steps, so a wobbling sensor cannot make the fans hunt.</summary>
    public int MinDwellSeconds { get; }

    /// <summary>
    /// Fan speed for <paramref name="temperatureC"/>, linearly interpolated between the two anchors that surround
    /// it and held flat outside them. Never returns a value the service would reject.
    /// <para>
    /// The result is raised to <see cref="SafetyFloorAt"/> for the current temperature. This is applied here rather
    /// than in the editor so a settings file written by an older build, or by hand, cannot get around it either.
    /// </para>
    /// </summary>
    public int PercentAt(double temperatureC)
    {
        // A reading that is not a number is not a cool machine. The safe answer is full speed, not the quietest
        // anchor: this used to return the lowest point, which turned a broken sensor into a silent fan.
        if (double.IsNaN(temperatureC)) return MaxPercent;

        int percent = InterpolatedPercentAt(temperatureC);
        return Math.Max(percent, SafetyFloorAt(temperatureC));
    }

    /// <summary>
    /// The lowest speed the curve may ask for at this temperature, or 0 below the first band so a cool machine is
    /// left entirely to the user's shape.
    /// </summary>
    public static int SafetyFloorAt(double temperatureC)
    {
        if (double.IsNaN(temperatureC)) return MaxPercent;

        int floor = 0;
        foreach (var (bandTemperature, bandPercent) in SafetyFloor)
        {
            if (temperatureC >= bandTemperature && bandPercent > floor) floor = bandPercent;
        }
        return floor;
    }

    /// <summary>The curve's own shape, before the safety floor is applied.</summary>
    private int InterpolatedPercentAt(double temperatureC)
    {
        if (double.IsNaN(temperatureC)) return MaxPercent;
        if (temperatureC <= Points[0].TemperatureC) return Points[0].Percent;
        if (temperatureC >= Points[^1].TemperatureC) return Points[^1].Percent;

        for (int i = 1; i < Points.Count; i++)
        {
            var upper = Points[i];
            if (temperatureC > upper.TemperatureC) continue;

            var lower = Points[i - 1];
            double span = upper.TemperatureC - lower.TemperatureC;
            if (span <= 0) return upper.Percent;

            double t = (temperatureC - lower.TemperatureC) / span;
            return Math.Clamp((int)Math.Round(lower.Percent + (upper.Percent - lower.Percent) * t), MinPercent, MaxPercent);
        }

        return Points[^1].Percent;
    }

    /// <summary>
    /// Turns whatever came out of a settings file into a curve that can be drawn and applied: anchors sorted,
    /// temperatures and speeds clamped, duplicate temperatures collapsed, and a missing or unusable list replaced
    /// by the default shape rather than leaving the feature on with nothing to follow.
    /// </summary>
    public static List<FanCurvePoint> Sanitize(IEnumerable<FanCurvePoint>? points)
    {
        var cleaned = new List<FanCurvePoint>();
        if (points != null)
        {
            foreach (var point in points)
            {
                if (point == null) continue;
                cleaned.Add(new FanCurvePoint(
                    Math.Clamp(point.TemperatureC, MinTemperatureC, MaxTemperatureC),
                    Math.Clamp(point.Percent, MinPercent, MaxPercent)));
            }
        }

        if (cleaned.Count == 0) return DefaultPoints();

        // One anchor per temperature: the last one written wins, which is what the editor's own rows produce.
        return cleaned
            .GroupBy(p => p.TemperatureC)
            .Select(g => g.Last())
            .OrderBy(p => p.TemperatureC)
            .ToList();
    }
}

/// <summary>
/// Decides when the curve's target is worth writing to the fans. Rising is immediate — heat is the emergency —
/// while falling waits for both the hysteresis band and the dwell time, so a sensor wobbling around one anchor
/// cannot make the fans step up and down every second.
/// <para>
/// Stateful on purpose: the caller feeds it one temperature per poll and writes only when it returns a value.
/// </para>
/// <para>
/// <see cref="Evaluate"/> proposes and <see cref="Commit"/> records, and the split is the point. The evaluator used
/// to record the speed it had just asked for, so a write the firmware refused was remembered as applied: the next
/// poll compared against a speed the fans were never running at and returned null, and the curve stayed wrong until
/// the temperature moved far enough to produce a different target. A refused write is now simply proposed again.
/// </para>
/// </summary>
public sealed class FanCurveEvaluator
{
    private readonly FanCurve _curve;
    private int? _appliedPercent;
    private double _lastAppliedTemperatureC;
    private DateTime _lastWriteUtc = DateTime.MinValue;

    public FanCurveEvaluator(FanCurve curve) => _curve = curve;

    public FanCurve Curve => _curve;

    /// <summary>The speed last handed to the fans, or null before the first write.</summary>
    public int? AppliedPercent => _appliedPercent;

    /// <summary>Forgets what was applied, so the next evaluation writes again. Used when the curve is re-armed.</summary>
    public void Reset()
    {
        _appliedPercent = null;
        _lastAppliedTemperatureC = 0;
        _lastWriteUtc = DateTime.MinValue;
    }

    /// <summary>
    /// The speed to apply now, or null when the fans should be left alone. A missing or impossible reading
    /// (0 °C from a stopped service, a negative or NaN value) never changes the fan speed.
    /// <para>
    /// Nothing is recorded here. The caller writes the value and then calls <see cref="Commit"/> with the same
    /// arguments, so a write that did not land leaves the evaluator where it was.
    /// </para>
    /// </summary>
    public int? Evaluate(double temperatureC, DateTime nowUtc)
    {
        if (double.IsNaN(temperatureC) || double.IsInfinity(temperatureC) || temperatureC <= 0) return null;

        int target = _curve.PercentAt(temperatureC);

        if (_appliedPercent is null) return target;

        if (target == _appliedPercent) return null;

        // Rising is immediate: heat is the emergency, and waiting for a dwell time to pass would leave the die hot
        // for no reason.
        if (target > _appliedPercent) return target;

        // Slowing down: only once the temperature has clearly left the band that produced the current speed,
        // and never more often than the dwell time.
        if (temperatureC > _lastAppliedTemperatureC - _curve.HysteresisC) return null;

        // A wall clock that steps backwards (NTP correction, a manual change, a resume from standby) makes the
        // elapsed time negative. Read as "not enough time has passed" that froze the curve at its last speed until
        // the clock caught up again, so a negative value is treated as the clock having moved, not as no time.
        double elapsedSeconds = (nowUtc - _lastWriteUtc).TotalSeconds;
        if (elapsedSeconds >= 0 && elapsedSeconds < _curve.MinDwellSeconds) return null;

        return target;
    }

    /// <summary>
    /// Records a speed the hardware accepted. Call this only after the write returned true: committing a refused
    /// write is what made the curve believe the fans were somewhere they had never been.
    /// </summary>
    public void Commit(int percent, double temperatureC, DateTime nowUtc)
    {
        _appliedPercent = percent;
        _lastAppliedTemperatureC = temperatureC;
        _lastWriteUtc = nowUtc;
    }
}
