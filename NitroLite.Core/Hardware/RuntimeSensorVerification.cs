using System.Globalization;

namespace NitroLite.Core.Hardware;

/// <summary>
/// Decides how far a machine's live sensor readings can be trusted, from the readings themselves rather than from
/// what a profile file claims about the model.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of the gap between two different questions. A profile answers "who measured these sensor ids
/// on this model?" — and for most models in the table the answer is nobody, so <see cref="SensorLayout.Verified"/> is
/// false and the custom fan curve stays locked even on a machine that is plainly answering with a real die
/// temperature and a real tachometer. The other question is "is this machine, right now, producing readings that
/// behave like a machine?" — which two consecutive samples can answer without anybody owning the hardware.
/// </para>
/// <para>
/// What it deliberately does <em>not</em> do is certify an id mapping. A firmware whose id 1 answers the 55 °C system
/// sensor instead of the die produces perfectly plausible numbers, and no amount of value checking can tell the two
/// apart. That is why <see cref="Observe"/> takes <c>layoutConfirmed</c> and refuses to promote without it: the ids
/// have to be anchored by the live probe (or by a profile somebody measured), and only then does a tight window over
/// consecutive samples add anything. The window here is narrower than <see cref="SensorPlausibility"/> on purpose:
/// plausibility decides whether a number may be shown, this decides whether the fans may act on it.
/// </para>
/// <para>
/// Promotion is one-way for a session. A reading that drops out later — a parked fan reporting 0, a sensor that stops
/// answering — must not take the curve away mid-session and hand the fans back and forth; the dead-sensor hand-back
/// (HW-06) already owns that case, and it looks at the live reading, not at this flag.
/// </para>
/// </remarks>
public sealed class RuntimeSensorVerification
{
    /// <summary>Samples that have to pass before the session is trusted.</summary>
    public const int RequiredSamples = 2;

    /// <summary>Least time between two accepted samples. A burst of ticks from one poll cycle is one observation.</summary>
    public const double MinimumSampleGapSeconds = 1.5;

    /// <summary>Coldest die temperature accepted for a promotion. Below this the reading is a boot artefact.</summary>
    public const int MinimumTemperatureCelsius = 15;

    /// <summary>Hottest die temperature accepted. Above this the value is a decode error, not a laptop.</summary>
    public const int MaximumTemperatureCelsius = 115;

    /// <summary>Highest fan speed accepted for a promotion.</summary>
    public const int MaximumFanRpm = 10000;

    /// <summary>Why the last sample did not count, or <see cref="SensorTrustRefusal.None"/>.</summary>
    public SensorTrustRefusal LastRefusal { get; private set; } = SensorTrustRefusal.None;

    /// <summary>True once the machine has answered like a machine often enough for this session.</summary>
    public bool RuntimeVerified { get; private set; }

    /// <summary>How many samples have been accepted towards the current run.</summary>
    public int AcceptedSamples { get; private set; }

    /// <summary>How many samples were refused. Reported so a bug report says what the machine actually answered.</summary>
    public int RejectedSamples { get; private set; }

    /// <summary>Total samples looked at, accepted or not.</summary>
    public int ObservedSamples => AcceptedSamples + RejectedSamples;

    /// <summary>When the last accepted sample arrived, or null.</summary>
    public DateTime? LastAcceptedUtc { get; private set; }

    /// <summary>Forgets everything. Called on a rescan: "try again" should not inherit the old run.</summary>
    public void Reset()
    {
        RuntimeVerified = false;
        AcceptedSamples = 0;
        RejectedSamples = 0;
        LastAcceptedUtc = null;
        LastRefusal = SensorTrustRefusal.None;
    }

    /// <summary>
    /// Looks at one telemetry sample and returns <see cref="RuntimeVerified"/> as it stands afterwards.
    /// </summary>
    /// <param name="sample">The sample the backend produced.</param>
    /// <param name="timestampUtc">
    /// When the sample was taken. Passed in rather than read from the clock so a test can drive the gap between two
    /// samples without sleeping.
    /// </param>
    /// <param name="layoutConfirmed">
    /// True when the sensor ids are anchored: the profile was measured on this model, or the live probe confirmed the
    /// ids this session. False means nobody knows which id answers what, and no value check may override that.
    /// </param>
    public bool Observe(MonitorData? sample, DateTime timestampUtc, bool layoutConfirmed)
    {
        if (RuntimeVerified) return true;

        if (!layoutConfirmed)
        {
            Refuse(SensorTrustRefusal.LayoutUnconfirmed);
            return false;
        }

        if (sample == null)
        {
            Refuse(SensorTrustRefusal.NoSample);
            return false;
        }

        // Both temperatures, not just the CPU one: the curve follows the CPU, the page shows both, and a GPU sensor
        // that answers nothing is the shape a machine with a bad mapping takes.
        var refusal = Classify(sample.CpuTemperature, sample.GpuTemperature, sample.CpuFanSpeed, sample.GpuFanSpeed);
        if (refusal != SensorTrustRefusal.None)
        {
            Refuse(refusal);
            return false;
        }

        // A second sample too soon after the first is the same observation twice: the interval exists so that two
        // samples are two moments in time, not two ticks of one polling loop.
        if (LastAcceptedUtc is DateTime last &&
            (timestampUtc - last).TotalSeconds < MinimumSampleGapSeconds)
        {
            LastRefusal = SensorTrustRefusal.TooSoon;
            return false;
        }

        AcceptedSamples++;
        LastAcceptedUtc = timestampUtc;
        LastRefusal = SensorTrustRefusal.None;

        if (AcceptedSamples >= RequiredSamples) RuntimeVerified = true;
        return RuntimeVerified;
    }

    /// <summary>Runs the window over one sample's four numbers.</summary>
    public static SensorTrustRefusal Classify(int cpuTemperature, int gpuTemperature, int cpuFanRpm, int gpuFanRpm)
    {
        if (SensorPlausibility.IsSentinel(cpuTemperature) || SensorPlausibility.IsSentinel(gpuTemperature) ||
            SensorPlausibility.IsSentinel(cpuFanRpm) || SensorPlausibility.IsSentinel(gpuFanRpm))
        {
            return SensorTrustRefusal.SentinelValue;
        }

        if (!IsUsableTemperature(cpuTemperature) || !IsUsableTemperature(gpuTemperature))
            return SensorTrustRefusal.TemperatureOutOfRange;

        if (!IsUsableFanRpm(cpuFanRpm) || !IsUsableFanRpm(gpuFanRpm)) return SensorTrustRefusal.FanOutOfRange;

        return SensorTrustRefusal.None;
    }

    /// <summary>
    /// True when a die temperature is inside the promotion window. Zero fails, and that is the point: a firmware that
    /// answers nothing uses zero, so "no reading yet" must not count towards a verified session.
    /// </summary>
    public static bool IsUsableTemperature(int value) =>
        value is >= MinimumTemperatureCelsius and <= MaximumTemperatureCelsius;

    /// <summary>True when a fan speed is inside the promotion window. Zero passes: a stopped fan is a real reading.</summary>
    public static bool IsUsableFanRpm(int value) => value is >= 0 and <= MaximumFanRpm;

    /// <summary>
    /// One line for the hardware report and the log. "runtime-verified" is the word the report has to show: it is what
    /// tells a triager the fan curve was allowed on a model no profile vouches for, and why.
    /// </summary>
    public string Describe()
    {
        if (RuntimeVerified) return $"runtime-verified ({AcceptedSamples} samples)";
        if (ObservedSamples == 0) return "unverified (no samples yet)";
        return $"unverified ({RejectedSamples} of {ObservedSamples} refused, last: {LastRefusal})";
    }

    /// <summary>
    /// The window, so a reader of the report knows what was applied rather than trusting a word. Formatted invariantly
    /// because the report is English and pasted into a public issue: a Turkish or German decimal comma in the middle of
    /// it would read as a different number to whoever triages it.
    /// </summary>
    public static string DescribeWindow() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"temperature {MinimumTemperatureCelsius}..{MaximumTemperatureCelsius} °C, fan 0..{MaximumFanRpm} RPM, "
            + $"every {MinimumSampleGapSeconds:0.#} s, {RequiredSamples} samples");

    private void Refuse(SensorTrustRefusal refusal)
    {
        RejectedSamples++;
        LastRefusal = refusal;

        // A refused sample interrupts the run even when it is inside the window: "consecutive" means the machine has
        // to keep answering, and it lets a machine that recovers from one bad tick start counting again immediately.
        AcceptedSamples = 0;
        LastAcceptedUtc = null;
    }
}

/// <summary>Why a sample did not count towards a runtime verification.</summary>
public enum SensorTrustRefusal
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>No backend has said which sensor id answers what, so the values mean nothing yet.</summary>
    LayoutUnconfirmed,

    /// <summary>The backend produced no sample at all.</summary>
    NoSample,

    /// <summary>A reading was one of the documented "no data" constants.</summary>
    SentinelValue,

    /// <summary>A temperature was outside 15..115 °C, including the zero a silent sensor answers.</summary>
    TemperatureOutOfRange,

    /// <summary>A fan speed was outside 0..10000 RPM.</summary>
    FanOutOfRange,

    /// <summary>The sample arrived less than <see cref="RuntimeSensorVerification.MinimumSampleGapSeconds"/> after the last one.</summary>
    TooSoon
}
