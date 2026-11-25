namespace NitroLite.Core;

/// <summary>
/// The nine series the telemetry chart can draw, and the rules that turn a sample into a number.
/// </summary>
/// <remarks>
/// <para>
/// Kept out of the view model because the interesting part is not the drawing: it is deciding when a sample has no
/// reading. A GPU that is powered down reports 0 °C, and plotting that as a real temperature would draw a cliff to the
/// floor that never happened and drag the minimum and the average down with it. Every method here returns null for
/// "not measured", and the chart skips those samples instead of inventing a value.
/// </para>
/// <para>
/// The ranges are the WPF page's own: a temperature chart holds a 5 °C grid, a clock chart auto-scales so a 100 MHz
/// wobble is visible, and a load chart switches between a 0–50 and a 0–100 scale so an idle machine is not a flat line
/// at the bottom.
/// </para>
/// </remarks>
public static class TelemetryMetrics
{
    public const string CpuTemp = "CPU_TEMP";
    public const string GpuTemp = "GPU_TEMP";
    public const string CpuWatt = "CPU_WATT";
    public const string GpuWatt = "GPU_WATT";
    public const string CpuGhz = "CPU_GHZ";
    public const string CpuUsage = "CPU_USAGE";
    public const string GpuUsage = "GPU_USAGE";
    public const string RamUsage = "RAM_USAGE";
    public const string VramGb = "VRAM_GB";

    /// <summary>The nine series, in the order the selector lists them.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        CpuTemp, GpuTemp, CpuWatt, GpuWatt, CpuGhz, CpuUsage, GpuUsage, RamUsage, VramGb
    };

    /// <summary>The catalogue key for a series' label.</summary>
    public static string Label(string metric) => metric switch
    {
        CpuTemp => Loc.T("CPU Sıcaklığı (°C)"),
        GpuTemp => Loc.T("GPU Sıcaklığı (°C)"),
        CpuWatt => Loc.T("CPU Güç Tüketimi (W, tahmini)"),
        GpuWatt => Loc.T("GPU Güç Tüketimi (W)"),
        CpuGhz => Loc.T("CPU Saat Frekansı (GHz)"),
        CpuUsage => Loc.T("CPU Kullanımı (%)"),
        GpuUsage => Loc.T("GPU Kullanımı (%)"),
        RamUsage => Loc.T("RAM Kullanımı (%)"),
        VramGb => Loc.T("VRAM Kullanımı (GB)"),
        _ => metric
    };

    public static string Unit(string metric) => metric switch
    {
        CpuTemp or GpuTemp => "°C",
        CpuWatt or GpuWatt => "W",
        CpuGhz => "GHz",
        CpuUsage or GpuUsage or RamUsage => "%",
        VramGb => "GB",
        _ => ""
    };

    /// <summary>True for the two series whose values are small enough to need two decimals.</summary>
    public static bool IsFine(string metric) => metric is CpuGhz or VramGb;

    /// <summary>
    /// The value of <paramref name="metric"/> in <paramref name="data"/>, or null when the sample has no real reading
    /// for it — a sensor that answered 0, or a dGPU that is asleep.
    /// </summary>
    public static double? Value(string metric, MonitorData data)
    {
        if (data == null) return null;

        bool gpuAwake = data.GpuTemperature > 0;
        double? value = metric switch
        {
            CpuTemp => data.CpuTemperature > 0 ? data.CpuTemperature : null,
            GpuTemp => gpuAwake ? data.GpuTemperature : null,
            CpuWatt => data.CpuPowerWatts > 0 ? data.CpuPowerWatts : null,
            GpuWatt => gpuAwake ? data.GpuPowerWatts : null,
            CpuGhz => data.CpuFrequencyMHz > 0 ? data.CpuFrequencyMHz / 1000.0 : null,
            CpuUsage => data.CpuUsage,
            // A usage reading needs no temperature to be real: the engine counters report it while the firmware's GPU
            // sensor still reads 0. A sleeping GPU has neither, and stays a gap rather than a fake 0 %.
            GpuUsage => gpuAwake || data.GpuUsage > 0 ? data.GpuUsage : null,
            RamUsage => data.RamTotalMB > 0 ? data.RamUsage : null,
            VramGb => gpuAwake && data.VramTotalMB > 0 ? data.VramUsedMB / 1024.0 : null,
            _ => data.CpuTemperature > 0 ? data.CpuTemperature : null
        };

        return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) ? value : null;
    }

    /// <summary>
    /// The vertical range to draw <paramref name="metric"/> in, given the samples that were actually measured.
    /// </summary>
    public static (double Low, double High) Range(string metric, IReadOnlyList<double> values)
    {
        if (values == null || values.Count == 0) return (0, 1);

        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (var value in values)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }

        switch (metric)
        {
            case CpuGhz:
            {
                // Auto-scale so a 100 MHz wobble is visible instead of a flat line at the bottom of a 0–5 GHz scale.
                double low = Math.Floor(min * 2.0) / 2.0 - 0.25;
                double high = Math.Ceiling(max * 2.0) / 2.0 + 0.25;
                if (high - low < 0.8)
                {
                    low = Math.Max(0.5, low - 0.4);
                    high = low + 0.8;
                }
                return (Math.Max(0.0, low), high);
            }

            case CpuTemp:
            case GpuTemp:
            {
                double low = Math.Floor(min / 5.0) * 5.0 - 5.0;
                double high = Math.Ceiling(max / 5.0) * 5.0 + 5.0;
                if (high - low < 15.0)
                {
                    low = Math.Max(25.0, low - 5.0);
                    high = low + 15.0;
                }
                return (Math.Max(20.0, low), Math.Min(115.0, high));
            }

            case CpuWatt:
            case GpuWatt:
            {
                double low = Math.Max(0.0, Math.Floor(min / 10.0) * 10.0 - 5.0);
                double high = Math.Ceiling(max / 10.0) * 10.0 + 10.0;
                if (high - low < 25.0) high = low + 25.0;
                return (low, high);
            }

            case CpuUsage:
            case GpuUsage:
            case RamUsage:
                return max < 40.0 ? (0.0, 50.0) : (0.0, 100.0);

            case VramGb:
                return (0.0, Math.Max(8.0, Math.Ceiling(max + 1.0)));

            default:
                return (Math.Max(0.0, min - 5.0), max + 5.0);
        }
    }

    /// <summary>Formats one reading for the statistics strip: two decimals for the fine series, one otherwise.</summary>
    public static string Format(string metric, double value)
    {
        string unit = Unit(metric);
        string number = IsFine(metric)
            ? value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return unit.Length > 0 ? $"{number} {unit}" : number;
    }

    /// <summary>Formats a grid label: whole numbers for the coarse series, one decimal for the fine ones.</summary>
    public static string FormatGrid(string metric, double value)
    {
        string unit = Unit(metric);
        string number = IsFine(metric)
            ? value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            : ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return unit.Length > 0 ? $"{number} {unit}" : number;
    }
}
