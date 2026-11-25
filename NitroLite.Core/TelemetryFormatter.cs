namespace NitroLite.Core;

/// <summary>
/// Pure, testable formatting utility for sensor metrics, battery states, power flow, and hardware telemetry.
/// </summary>
public static class TelemetryFormatter
{
    /// <summary>Source text of the estimate marker; <see cref="EstimatedSuffix"/> translates it.</summary>
    internal const string EstimatedSuffixSource = " (tahmini)";

    public static string EstimatedSuffix => Loc.T(EstimatedSuffixSource);

    /// <summary>
    /// Highest temperature a laptop CPU or GPU can report. Anything above it is a sentinel from a sensor that did
    /// not answer (0xFF is common), not a reading, and must not be shown as one.
    /// </summary>
    public const double MaxPlausibleTemperature = 120.0;

    /// <summary>
    /// Formats CPU temperature in °C, handling 0 or uninitialized values cleanly.
    /// </summary>
    public static string FormatCpuTemperature(double temp)
    {
        if (temp <= 0 || temp > MaxPlausibleTemperature) return "-- °C"; // no reading (a running CPU is never at 0 °C)
        return $"{Math.Round(temp):F0} °C";
    }

    /// <summary>
    /// Formats GPU temperature in °C, distinguishing between active reading and D3cold deep sleep.
    /// </summary>
    public static string FormatGpuTemperature(double temp, bool isSleeping = false, bool available = true)
    {
        if (isSleeping) return "0 °C (Uyku / D3cold)";
        // No reading is not the same as a sleeping GPU: a running GPU is never at 0 °C.
        if (!available || temp <= 0 || temp > MaxPlausibleTemperature) return "-- °C";
        return $"{Math.Round(temp):F0} °C";
    }

    /// <summary>
    /// Calculates fan speed percentage relative to hardware maximum RPM (default: 6000 RPM).
    /// </summary>
    public static double CalculateFanPercent(int rpm, int maxRpm = 6000)
    {
        if (maxRpm <= 0 || rpm <= 0) return 0.0;
        return Math.Round(Math.Clamp((double)rpm / maxRpm * 100.0, 0.0, 100.0), 1);
    }

    /// <summary>
    /// Formats a tachometer reading, or the placeholder when the fan reported nothing.
    /// </summary>
    /// <remarks>
    /// A fan that is not turning reports 0, and 0 is not a reading: showing "0 RPM" would claim the fan has stopped,
    /// which is the one thing the number must never say by accident.
    /// </remarks>
    public static string FormatFanRpm(int rpm, string defaultText = "-- RPM")
    {
        if (rpm <= 0) return defaultText;
        return $"{rpm} RPM";
    }

    /// <summary>
    /// Formats a load percentage, or the placeholder when the sensor reported nothing.
    /// </summary>
    /// <remarks>
    /// Zero load is not a reading either: a running CPU is never idle at exactly 0.0 %. The number keeps the active
    /// culture's decimal separator, because it sits beside the temperature and the clock in the same readout and the
    /// three must agree.
    /// </remarks>
    public static string FormatUsagePercent(double percent, string defaultText = "-- %")
    {
        if (percent <= 0 || double.IsNaN(percent) || double.IsInfinity(percent)) return defaultText;
        return $"{percent:F1} %";
    }

    /// <summary>
    /// Calculates estimated remaining runtime in minutes based on remaining capacity (mWh) and discharge rate (mW).
    /// Clamped between 1 and 1440 minutes (24 hours). Returns 0 on zero or invalid inputs.
    /// </summary>
    public static int CalculateRemainingMinutes(double remMWh, double dischargeMw)
    {
        if (remMWh <= 0 || dischargeMw <= 0 || double.IsNaN(remMWh) || double.IsNaN(dischargeMw) || double.IsInfinity(remMWh) || double.IsInfinity(dischargeMw))
        {
            return 0;
        }

        int minutes = (int)((remMWh / dischargeMw) * 60.0);
        return Math.Clamp(minutes, 1, 1440);
    }

    /// <summary>
    /// Applies an Exponential Moving Average (EMA) smoothing filter to damp instantaneous power spikes.
    /// Uses zero-lag cold-start seeding (S[0] = R[0]) on first reading.
    /// </summary>
    public static double UpdateSmoothedRate(double current, double instant, ref bool initialized, double alpha = 0.20)
    {
        if (instant <= 0 || double.IsNaN(instant) || double.IsInfinity(instant))
        {
            return initialized ? current : 0.0;
        }

        if (!initialized)
        {
            initialized = true;
            return instant;
        }

        return (alpha * instant) + ((1.0 - alpha) * current);
    }

    /// <summary>
    /// Formats a physical temperature in Celsius with custom default placeholder.
    /// </summary>
    public static string FormatTemperature(double? temp, string defaultText = "-- °C")
    {
        if (!temp.HasValue || temp.Value <= 0) return defaultText;
        return $"{Math.Round(temp.Value):F0} °C";
    }
}
