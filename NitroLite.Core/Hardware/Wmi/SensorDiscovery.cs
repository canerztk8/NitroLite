namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// Works out whether an unlisted Acer model answers the per-sensor <c>GetGamingSysInfo</c> call, and which of its
/// readings can be trusted.
/// <para>
/// The shipped profile table names 31 models. Every other Acer laptop falls to the <c>anyAcer</c> catch-all, whose
/// sensor layout is the firmware-wide enum but marked unverified — nobody has measured that machine. Rather than
/// either trusting the ids blindly or refusing to read anything, the first telemetry sample is treated as a probe:
/// the ids are queried, the answers are put through <see cref="SensorPlausibility"/>, and the layout is only adopted
/// when the machine demonstrably answered like a machine.
/// </para>
/// <para>
/// The rules are deliberately conservative, because a wrong "yes" here is worse than a wrong "no". A false positive
/// publishes a fabricated temperature and lets the fan stall watchdog judge a real die against a number that was
/// never a speed; a false negative only leaves the reading blank and the watchdog in standby, which is what an
/// unknown model already gets today.
/// </para>
/// </summary>
public static class SensorDiscovery
{
    /// <summary>Sensor keys as the worker names them in a <c>ReadTelemetry</c> answer.</summary>
    public const string CpuTemp = "cpuTemp";
    public const string CpuFanRpm = "cpuFanRpm";
    public const string Sys1Temp = "sys1Temp";
    public const string GpuFanRpm = "gpuFanRpm";
    public const string GpuTemp = "gpuTemp";

    /// <summary>
    /// Decides whether <paramref name="candidate"/> describes this machine.
    /// <para>
    /// <paramref name="answered"/> maps a sensor key to the value the firmware returned, or to null when the status
    /// byte said the id is not known. Returns a layout with <see cref="SensorLayout.Supported"/> set when the machine
    /// answered plausibly, and null when it did not — in which case the caller keeps the unsupported layout and the
    /// sample falls back to derived fan speeds.
    /// </para>
    /// </summary>
    public static SensorLayout? Resolve(IReadOnlyDictionary<string, int?> answered, SensorLayout candidate)
    {
        if (answered.Count == 0) return null;

        // The die temperature is the one reading every Acer gaming firmware answers. If it is missing or outside the
        // window, the call is not being served the way this build understands it and nothing else is trustworthy.
        if (!TryGet(answered, CpuTemp, out var cpuTemp) || !SensorPlausibility.IsPlausibleTemperature(cpuTemp))
            return null;

        // At least one tachometer has to be turning. A machine that reports both fans at zero is either off, or
        // answering a sentinel for ids it does not have; either way it is not evidence that the layout is right.
        bool cpuFan = TryGet(answered, CpuFanRpm, out var cpuRpm) && SensorPlausibility.IsPresentFanRpm(cpuRpm);
        bool gpuFan = TryGet(answered, GpuFanRpm, out var gpuRpm) && SensorPlausibility.IsPresentFanRpm(gpuRpm);
        if (!cpuFan && !gpuFan) return null;

        // A firmware that echoes one number for every id is not measuring anything. This is the cheapest guard
        // against a provider that answers the same word to every query, and it costs nothing on a real machine:
        // a die temperature and a fan speed are never the same number.
        var distinct = answered.Values.Where(v => v.HasValue).Select(v => v!.Value).Distinct().Count();
        if (distinct <= 1) return null;

        return candidate with { Supported = true, Verified = false };
    }

    private static bool TryGet(IReadOnlyDictionary<string, int?> answered, string key, out int value)
    {
        value = 0;
        if (!answered.TryGetValue(key, out var raw) || raw is not int number) return false;
        value = number;
        return true;
    }
}
