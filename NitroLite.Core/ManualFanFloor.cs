namespace NitroLite.Core;

/// <summary>
/// The thermal floor for the manual custom-speed sliders.
/// </summary>
/// <remarks>
/// <para>
/// The two sliders are a preference, and <see cref="FanCurve.SafetyFloorAt"/> is not. A user who set 30 % on a cool
/// machine meant 30 %, and that number stays in the settings file exactly as it was typed; what changes is the speed
/// actually written while the die is hot. At 95 °C the fans run at 100 % whatever the slider says, and when the
/// temperature falls back below the band the fans return to the number the user chose.
/// </para>
/// <para>
/// The floor is held with hysteresis on the way down, for the same reason <see cref="FanCurveEvaluator"/> has one: a
/// sensor wobbling across 85 °C would otherwise swing the fans between the user's target and the floor every second,
/// which is both audible and worse for the fan bearing than a steady speed.
/// </para>
/// <para>
/// Stateful on purpose, and split into propose and commit like the curve evaluator: a write the firmware refused must
/// not be remembered as applied, or the fans stay at the wrong speed until the temperature happens to move.
/// </para>
/// </remarks>
public sealed class ManualFanFloor
{
    private int? _appliedPercent;
    private int _appliedFloor;

    /// <summary>The speed last handed to the fans, or null before the first write.</summary>
    public int? AppliedPercent => _appliedPercent;

    /// <summary>Forgets what was applied, so the next evaluation writes again.</summary>
    public void Reset()
    {
        _appliedPercent = null;
        _appliedFloor = 0;
    }

    /// <summary>
    /// The speed the fans should be running at: the user's target, raised to the floor for this temperature. Never
    /// below the target, so a cool machine is left entirely to the slider.
    /// <para>
    /// Every path that writes a fixed speed goes through this, not just the loop, so a slider drag at 95 °C cannot
    /// write the quiet number the loop would have refused a moment earlier.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The result is clamped to 0..100 as well as raised. The raise is the point of the class; the clamp is there
    /// because this is the last function before the number reaches the firmware, and a target that came from a
    /// hand-edited settings file or a future caller that forgot to clamp would otherwise be written as-is. A fan
    /// percentage above 100 is not a stronger fan, it is a value the embedded controller has to interpret.
    /// </remarks>
    public int EffectivePercent(int targetPercent, double temperatureC) =>
        Math.Clamp(Math.Max(targetPercent, FloorAt(temperatureC)), 0, 100);

    /// <summary>
    /// The speed to write now, or null when the fans are already there. A missing or impossible reading never
    /// changes the fan speed, exactly as in <see cref="FanCurveEvaluator.Evaluate"/>.
    /// </summary>
    public int? Evaluate(int targetPercent, double temperatureC)
    {
        if (double.IsNaN(temperatureC) || double.IsInfinity(temperatureC) || temperatureC <= 0) return null;

        int effective = EffectivePercent(targetPercent, temperatureC);
        return effective == _appliedPercent ? null : effective;
    }

    /// <summary>Records a speed the hardware accepted. Call this only after the write returned true.</summary>
    public void Commit(int percent, double temperatureC)
    {
        _appliedPercent = percent;
        _appliedFloor = FloorAt(temperatureC);
    }

    /// <summary>
    /// The floor in force at this temperature. A band that is already holding the fans is kept until the temperature
    /// has fallen <see cref="FanCurve.DefaultHysteresisC"/> below that band's own threshold, so the release is a
    /// decision rather than a coin flip on the last decimal of a sensor reading.
    /// </summary>
    private int FloorAt(double temperatureC)
    {
        int floor = 0;
        foreach (var (bandTemperature, bandPercent) in FanCurve.SafetyFloor)
        {
            double threshold = _appliedFloor >= bandPercent
                ? bandTemperature - FanCurve.DefaultHysteresisC
                : bandTemperature;

            if (temperatureC >= threshold && bandPercent > floor) floor = bandPercent;
        }

        return floor;
    }
}
