namespace NitroLite.Core;

/// <summary>Which die is hot while its fan is not turning.</summary>
public enum FanStallTarget
{
    None = 0,
    Cpu = 1,
    Gpu = 2,
    Both = 3
}

/// <summary>One confirmed stall: the die, the reading that proved it, and how long it had been that way.</summary>
public sealed record FanStallVerdict(FanStallTarget Target, int TemperatureC, int FanRpm, TimeSpan Sustained);

/// <summary>
/// Catches a fan that has stopped while the die it cools is hot.
/// </summary>
/// <remarks>
/// The firmware spins a fan up long before 88 °C, so a hot die with a fan under 400 RPM is not a preference the
/// user set: it is a fan that has failed, a header that has come loose, or an EC that has stopped answering. The
/// app cannot repair any of those, but it can stop adding heat and tell the user before the machine throttles
/// itself into a shutdown.
/// <para>
/// Stateful on purpose: the caller feeds it one poll and acts only when it returns a verdict. A single bad sample
/// is not a fault, which is why the condition has to hold for <see cref="SustainFor"/> before it counts, and why a
/// machine that stays broken is not a toast every second.
/// </para>
/// <para>
/// The caller must not feed it a sample the Acer service did not produce. The fallback path reports 0 RPM because
/// nothing measured the fans, and judging that would fire on every machine without the service.
/// </para>
/// </remarks>
public sealed class FanStallWatchdog
{
    /// <summary>A die at or above this is hot enough that its fan has to be turning.</summary>
    public const int HotTemperatureC = 88;

    /// <summary>Below this the fan is not moving air, whatever the firmware claims.</summary>
    public const int StallRpm = 400;

    /// <summary>How long the condition has to hold before it is a fault rather than a sample.</summary>
    public static readonly TimeSpan SustainFor = TimeSpan.FromSeconds(10);

    /// <summary>Shortest gap between two alerts, so a machine that stays broken is not a toast every second.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private DateTime? _cpuStalledSince;
    private DateTime? _gpuStalledSince;
    private DateTime _lastAlertUtc = DateTime.MinValue;
    private bool _hasAlerted;

    /// <summary>True once an alert has been raised; cleared by <see cref="Reset"/>.</summary>
    public bool HasAlerted => _hasAlerted;

    /// <summary>
    /// Feeds one poll. Returns a verdict when a stall has been confirmed and the cooldown allows another alert,
    /// and null otherwise.
    /// </summary>
    public FanStallVerdict? Observe(int cpuTemperatureC, int gpuTemperatureC, int cpuFanRpm, int gpuFanRpm, DateTime nowUtc)
    {
        _cpuStalledSince = Track(_cpuStalledSince, cpuTemperatureC, cpuFanRpm, nowUtc);
        _gpuStalledSince = Track(_gpuStalledSince, gpuTemperatureC, gpuFanRpm, nowUtc);

        var target = Confirmed(nowUtc);
        if (target == FanStallTarget.None) return null;

        // The cooldown is the only gate after the first alert: the condition is still true, so the next alert is
        // due as soon as it expires. Clearing the timers here instead would make the user wait another ten seconds
        // on top of the cooldown for a fault that never went away.
        if (_hasAlerted && nowUtc - _lastAlertUtc < Cooldown) return null;

        _hasAlerted = true;
        _lastAlertUtc = nowUtc;

        bool cpu = target is FanStallTarget.Cpu or FanStallTarget.Both;
        bool gpu = target is FanStallTarget.Gpu or FanStallTarget.Both;

        // Name the die that has been stalled longest, so the message points at the fan worth looking at.
        DateTime cpuSince = _cpuStalledSince ?? nowUtc;
        DateTime gpuSince = _gpuStalledSince ?? nowUtc;
        bool cpuFirst = cpu && (!gpu || cpuSince <= gpuSince);

        int temperatureC = cpuFirst ? cpuTemperatureC : gpuTemperatureC;
        int fanRpm = cpuFirst ? cpuFanRpm : gpuFanRpm;
        DateTime since = cpuFirst ? cpuSince : gpuSince;

        return new FanStallVerdict(target, temperatureC, fanRpm, nowUtc - since);
    }

    /// <summary>Forgets everything, so the next stall needs a fresh <see cref="SustainFor"/> and can alert at once.</summary>
    public void Reset()
    {
        _cpuStalledSince = null;
        _gpuStalledSince = null;
        _lastAlertUtc = DateTime.MinValue;
        _hasAlerted = false;
    }

    private static DateTime? Track(DateTime? since, int temperatureC, int fanRpm, DateTime nowUtc)
    {
        // A dead sensor is not a cool machine, but it is also not evidence of a stalled fan: with no temperature
        // there is nothing to compare the fan against, so the timer is cleared rather than left running.
        if (temperatureC < HotTemperatureC) return null;
        if (fanRpm >= StallRpm) return null;
        return since ?? nowUtc;
    }

    private FanStallTarget Confirmed(DateTime nowUtc)
    {
        bool cpu = _cpuStalledSince is { } cpuSince && nowUtc - cpuSince >= SustainFor;
        bool gpu = _gpuStalledSince is { } gpuSince && nowUtc - gpuSince >= SustainFor;
        return (cpu, gpu) switch
        {
            (true, true) => FanStallTarget.Both,
            (true, false) => FanStallTarget.Cpu,
            (false, true) => FanStallTarget.Gpu,
            _ => FanStallTarget.None
        };
    }
}
