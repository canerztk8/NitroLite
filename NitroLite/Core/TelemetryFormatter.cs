using System;
using System.Globalization;

namespace NitroLite.Core;

/// <summary>
/// Pure, testable formatting utility for sensor metrics, battery states, power flow, and hardware telemetry.
/// </summary>
public static class TelemetryFormatter
{
    public const string EstimatedSuffix = " (tahmini)";

    /// <summary>
    /// Marks a value that comes from a model or a fixed baseline instead of a sensor reading.
    /// Empty and placeholder ("--") texts are returned unchanged.
    /// </summary>
    public static string MarkEstimated(string text, bool isEstimated = true)
    {
        if (!isEstimated || string.IsNullOrWhiteSpace(text) || text.Contains("--")) return text;
        return "~" + text + EstimatedSuffix;
    }

    private static readonly CultureInfo TrCulture = new("tr-TR");

    /// <summary>
    /// Formats CPU temperature in °C, handling 0 or uninitialized values cleanly.
    /// </summary>
    public static string FormatCpuTemperature(double temp)
    {
        if (temp <= 0) return "0 °C (Beklemede)";
        return $"{Math.Round(temp):F0} °C";
    }

    /// <summary>
    /// Formats GPU temperature in °C, distinguishing between active reading and D3cold deep sleep.
    /// </summary>
    public static string FormatGpuTemperature(double temp, bool isSleeping = false)
    {
        if (isSleeping || temp <= 0) return "0 °C (Uyku / D3cold)";
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
    /// Formats fan speed in RPM (e.g. "3960 RPM").
    /// </summary>
    public static string FormatFanSpeedRpm(int rpm)
    {
        return $"{Math.Max(0, rpm)} RPM";
    }

    /// <summary>
    /// Formats fan speed percentage (e.g. "%66").
    /// </summary>
    public static string FormatFanSpeedPercent(int rpm, int maxRpm = 6000)
    {
        double pct = CalculateFanPercent(rpm, maxRpm);
        return $"%{Math.Round(pct):F0}";
    }

    /// <summary>
    /// Formats fan speed with both RPM and calculated percentage (e.g. "3960 RPM (%66)").
    /// </summary>
    public static string FormatFanSpeed(int rpm, int maxRpm = 6000)
    {
        double pct = CalculateFanPercent(rpm, maxRpm);
        return $"{Math.Max(0, rpm)} RPM (%{Math.Round(pct):F0})";
    }

    /// <summary>
    /// Formats CPU package power consumption in Watts.
    /// </summary>
    public static string FormatCpuPower(double watts)
    {
        if (watts <= 0) return "0.0 W";
        return string.Format(CultureInfo.InvariantCulture, "{0:F1} W", watts);
    }

    /// <summary>
    /// Formats GPU power consumption in Watts, handling D3cold state.
    /// </summary>
    public static string FormatGpuPower(double watts, bool isSleeping = false)
    {
        if (isSleeping || watts <= 0) return "0.0 W (D3cold)";
        return string.Format(CultureInfo.InvariantCulture, "{0:F1} W", watts);
    }

    /// <summary>
    /// Formats CPU clock frequency in GHz (e.g. "4.25 GHz").
    /// </summary>
    public static string FormatCpuFrequency(double freqMhz)
    {
        if (freqMhz <= 0) return "0.00 GHz (Beklemede)";
        double ghz = freqMhz / 1000.0;
        return string.Format(CultureInfo.InvariantCulture, "{0:F2} GHz", ghz);
    }

    /// <summary>
    /// Formats GPU core clock in MHz (e.g. "2100 MHz" or "0 MHz (Uyku)").
    /// </summary>
    public static string FormatGpuFrequency(double freqMhz, bool isSleeping = false)
    {
        if (isSleeping || freqMhz <= 0) return "0 MHz (Uyku)";
        return $"{Math.Round(freqMhz):F0} MHz";
    }

    /// <summary>
    /// Formats system RAM usage (e.g. "12.4 GB / 31.9 GB (%38.9)").
    /// </summary>
    public static string FormatRamUsage(double usedMB, double totalMB)
    {
        if (totalMB <= 0) return "0.0 GB / 0.0 GB (%0.0)";
        double usedGB = Math.Round(usedMB / 1024.0, 1);
        double totalGB = Math.Round(totalMB / 1024.0, 1);
        double pct = Math.Round((usedMB / totalMB) * 100.0, 1);
        return string.Format(CultureInfo.InvariantCulture, "{0:F1} GB / {1:F1} GB (%{2:F1})", usedGB, totalGB, pct);
    }

    /// <summary>
    /// Formats GPU VRAM usage (e.g. "2048 MB / 8192 MB (%25)").
    /// </summary>
    public static string FormatVramUsage(double usedMB, double totalMB, bool isSleeping = false)
    {
        if (isSleeping || (totalMB <= 0 && usedMB <= 0)) return "0 MB / 0 MB (D3cold)";
        if (totalMB <= 0) return $"{Math.Round(usedMB):F0} MB";
        double pct = Math.Round((usedMB / totalMB) * 100.0, 0);
        return $"{Math.Round(usedMB):F0} MB / {Math.Round(totalMB):F0} MB (%{pct:F0})";
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
    /// Formats battery power flow rate distinguishing discharging, AC charging, AC saturated, and idle on battery.
    /// When discharging with known milliwatts, renders dual-unit format: "{0:F1} W ({1:N0} mW)".
    /// Preserves full backward-compatible output if milliwatts are not provided.
    /// </summary>
    public static string FormatBatteryPowerFlow(BatteryStatusInfo? status, bool includeMilliwatts = true)
    {
        if (status == null) return "0.0 W";

        if (status.IsDischarging && (status.DischargeRateWatts > 0 || status.DischargeRateMilliwatts > 0))
        {
            if (includeMilliwatts && status.DischargeRateMilliwatts > 0)
            {
                double watts = status.DischargeRateWatts > 0 ? status.DischargeRateWatts : status.DischargeRateMilliwatts / 1000.0;
                return string.Format(CultureInfo.InvariantCulture, "{0:F1} W ({1:N0} mW)", watts, (long)status.DischargeRateMilliwatts);
            }

            double w = status.DischargeRateWatts > 0 ? status.DischargeRateWatts : status.DischargeRateMilliwatts / 1000.0;
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} W (Deşarj Oluyor)", w);
        }

        if (status.IsCharging && status.ChargeRateWatts > 0)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} W (Şarj Oluyor)", status.ChargeRateWatts);
        }

        if (status.IsPluggedIn)
        {
            return "0.0 W (Adaptörde / Doygun)";
        }

        return "0.0 W (Pilde / Boşta)";
    }

    /// <summary>
    /// Formats battery estimated runtime or time to full charge.
    /// </summary>
    public static string FormatRemainingTime(int minutes, bool isPluggedIn, bool isCharging)
    {
        if (minutes <= 0 || minutes > 1440)
        {
            if (isCharging) return "Hesaplanıyor...";
            if (isPluggedIn) return "Adaptöre Bağlı";
            return "Hesaplanıyor...";
        }

        int hours = minutes / 60;
        int mins = minutes % 60;
        if (hours > 0)
        {
            return $"{hours} sa {mins} dk";
        }
        return $"{mins} dk";
    }

    /// <summary>
    /// Formats storage capacity and usage details (e.g. "483.3 GB / 1863.0 GB (%25.9 Dolu)").
    /// </summary>
    public static string FormatDiskCapacity(double usedGB, double totalGB)
    {
        if (totalGB <= 0) return "0.0 GB";
        double pct = Math.Round((usedGB / totalGB) * 100.0, 1);
        return string.Format(CultureInfo.InvariantCulture, "{0:F1} GB / {1:F1} GB (%{2:F1} Dolu)", usedGB, totalGB, pct);
    }

    /// <summary>
    /// Formats a physical temperature in Celsius with custom default placeholder.
    /// </summary>
    public static string FormatTemperature(double? temp, string defaultText = "-- °C")
    {
        if (!temp.HasValue || temp.Value <= 0) return defaultText;
        return $"{Math.Round(temp.Value):F0} °C";
    }

    /// <summary>
    /// Formats a physical temperature with 1 decimal place (e.g. for battery).
    /// </summary>
    public static string FormatDetailedTemperature(double? temp, string defaultText = "--.- °C")
    {
        if (!temp.HasValue || temp.Value <= 0) return defaultText;
        return string.Format(CultureInfo.InvariantCulture, "{0:F1} °C", temp.Value);
    }

    /// <summary>
    /// Formats degradation slope per day (e.g. "+0.001 °C/W/gün").
    /// </summary>
    public static string FormatThermalSlope(double slope)
    {
        string sign = slope >= 0 ? "+" : "";
        return string.Format(CultureInfo.InvariantCulture, "{0}{1:F3} °C/W/gün", sign, slope);
    }

    /// <summary>
    /// Formats stability score percentage (e.g. "%98.7").
    /// </summary>
    public static string FormatStabilityScore(double score)
    {
        return string.Format(CultureInfo.InvariantCulture, "%{0:F1}", score);
    }
}
