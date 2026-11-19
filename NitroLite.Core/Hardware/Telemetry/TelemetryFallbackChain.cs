namespace NitroLite.Core.Hardware;

/// <summary>Where a telemetry reading came from, cheapest and most accurate first.</summary>
public enum TelemetrySource
{
    /// <summary>Nothing answered.</summary>
    None = 0,

    /// <summary>Acer WMI (<c>GetGamingSysInfo</c> or the Acer service socket).</summary>
    Firmware,

    /// <summary>Acer's own service (AASSvc), read over its loopback socket.</summary>
    AcerService,

    /// <summary><c>CallNtPowerInformation</c> / <c>NtPowerInformation</c> through ntdll.</summary>
    NtPower,

    /// <summary>A Windows performance counter through PDH, e.g. the ACPI thermal zone.</summary>
    Pdh,

    /// <summary>NVIDIA's user-mode library (nvml.dll).</summary>
    Nvml,

    /// <summary>
    /// DXGI's video-memory budget. Declared so a future reader can wire it up without inventing a source name; nothing
    /// in this build queries it, because a wrong vtable in a hand-written COM interface is an access violation, not an
    /// exception, and this project does not gamble the process on a reading it can get from NVML.
    /// </summary>
    Dxgi,

    /// <summary>AMD's display library (atiadlxx.dll). Detected, not queried, for the same reason as <see cref="Dxgi"/>.</summary>
    Adl,

    /// <summary>A model, not a measurement. The CPU wattage is the one reading in this category.</summary>
    Estimate
}

/// <summary>
/// The result of walking one metric's fallback chain: the first usable value, the source that produced it, and what was
/// tried before it.
/// </summary>
public sealed record TelemetryReading(
    string Metric,
    double? Value,
    TelemetrySource Source,
    IReadOnlyList<string> Tried)
{
    public bool HasValue => Value.HasValue;

    /// <summary>One line for the hardware report, e.g. <c>cpu-temp: Pdh (Firmware rejected: 0)</c>.</summary>
    public string Describe()
    {
        var answer = HasValue ? $"{Source}" : "none";
        var tried = Tried.Count == 0 ? "" : $" [{string.Join(", ", Tried)}]";
        return $"{Metric}: {answer}{tried}";
    }
}

/// <summary>
/// Pure, testable fallback chain for one telemetry metric.
/// <para>
/// The rule it enforces is the one the AN16-61 report taught us: a firmware that does not know a sensor id answers
/// status zero and a sentinel, and AASSvc reports <c>CPU_FREQUENCY 0</c> on machines where it never implemented the
/// field. Publishing either would show "0 MHz" or "6553.5 °C" as a measurement, and — worse — a fan curve would act on
/// it. So every candidate is checked against the same plausibility window before it is accepted, and a rejected
/// candidate is remembered in <see cref="TelemetryReading.Tried"/> so the report can say which layer answered.
/// </para>
/// <para>
/// Nothing here touches the machine: the providers are supplied by the platform layer, which lets the precedences be
/// tested without a laptop.
/// </para>
/// </summary>
public sealed class TelemetryFallbackChain
{
    private readonly List<(TelemetrySource Source, Func<double?> Read)> _providers = new();

    public TelemetryFallbackChain(string metric, double minimum, double maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximum, minimum);

        Metric = metric;
        Minimum = minimum;
        Maximum = maximum;
    }

    public string Metric { get; }

    /// <summary>Lowest value that counts as a measurement. Zero is a sentinel for every metric this class is used for.</summary>
    public double Minimum { get; }

    public double Maximum { get; }

    /// <summary>Adds a provider. Order is precedence: the first usable answer wins.</summary>
    public TelemetryFallbackChain Add(TelemetrySource source, Func<double?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _providers.Add((source, read));
        return this;
    }

    /// <summary>
    /// Walks the chain. A provider that throws is skipped, not fatal: a driver that disappears mid-sample must not take
    /// the telemetry loop with it.
    /// </summary>
    public TelemetryReading Resolve()
    {
        var tried = new List<string>();

        foreach (var (source, read) in _providers)
        {
            double? value;
            try
            {
                value = read();
            }
            catch (Exception ex)
            {
                tried.Add($"{source} failed: {ex.GetType().Name}");
                continue;
            }

            if (IsUsable(value, Minimum, Maximum)) return new TelemetryReading(Metric, value, source, tried);

            tried.Add($"{source} rejected: {Show(value)}");
        }

        return new TelemetryReading(Metric, null, TelemetrySource.None, tried);
    }

    /// <summary>
    /// True when a reading may be shown and acted on: finite, inside the window, and not the sentinel zero. The upper
    /// bound is the caller's, because a 130 °C GPU and a 105 °C GPU are both real limits on different machines and this
    /// class has no business deciding which.
    /// </summary>
    public static bool IsUsable(double? value, double minimum, double maximum) =>
        value is { } reading
        && double.IsFinite(reading)
        && reading >= minimum
        && reading <= maximum
        && !(minimum > 0 && reading == 0);

    private static string Show(double? value) =>
        value is { } reading
            ? reading.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            : "nothing";
}
