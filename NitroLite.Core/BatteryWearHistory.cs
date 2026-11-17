using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NitroLite.Core;

/// <summary>One battery health reading, as Windows reported it on that day.</summary>
public sealed class BatteryWearSample
{
    [JsonPropertyName("utc")]
    public DateTime TimestampUtc { get; set; }

    [JsonPropertyName("health")]
    public double HealthPercent { get; set; }

    [JsonPropertyName("wear")]
    public double WearLevelPercent { get; set; }

    [JsonPropertyName("full_mwh")]
    public double FullChargeCapacityMWh { get; set; }

    [JsonPropertyName("design_mwh")]
    public double DesignCapacityMWh { get; set; }

    [JsonPropertyName("cycles")]
    public int? CycleCount { get; set; }
}

/// <summary>What the recorded samples say about how fast the pack is wearing.</summary>
public sealed class BatteryWearTrend
{
    /// <summary>
    /// The shortest span a wear curve is drawn over. Below it the readings are measurement noise, not a direction:
    /// the pack's reported capacity moves by a fraction of a percent between two reads minutes apart, and a curve
    /// through those points would claim a trend the data does not have.
    /// </summary>
    public const double MinimumTrendSpanDays = 7;

    public IReadOnlyList<BatteryWearSample> Samples { get; init; } = Array.Empty<BatteryWearSample>();

    /// <summary>Two readings are the minimum for a direction; one reading is a number, not a trend.</summary>
    public bool HasTrend => Samples.Count >= 2;

    /// <summary>
    /// True when the readings span at least <see cref="MinimumTrendSpanDays"/>, which is what the chart and the
    /// per-year rate both need. <see cref="HasTrend"/> alone is not enough: two readings an hour apart are a trend
    /// only in the arithmetic sense.
    /// </summary>
    public bool HasMeaningfulTrend => SpanDays is double days && days >= MinimumTrendSpanDays;

    public BatteryWearSample? First => Samples.Count > 0 ? Samples[0] : null;
    public BatteryWearSample? Last => Samples.Count > 0 ? Samples[^1] : null;

    /// <summary>Days between the first and the last reading; null when there is only one.</summary>
    public double? SpanDays => HasTrend ? (Last!.TimestampUtc - First!.TimestampUtc).TotalDays : null;

    /// <summary>Wear added since the first reading, in percentage points. Negative when the pack recalibrated.</summary>
    public double? WearChangePercent => HasTrend ? Last!.WearLevelPercent - First!.WearLevelPercent : null;

    /// <summary>
    /// Wear per year at the observed rate. Null below <see cref="MinimumTrendSpanDays"/> of history: extrapolating a
    /// year from two readings a day apart would turn measurement noise into a number that looks like a fact.
    /// </summary>
    public double? WearPerYearPercent
    {
        get
        {
            if (SpanDays is not double days || days < MinimumTrendSpanDays) return null;
            return WearChangePercent!.Value / days * 365.0;
        }
    }
}

/// <summary>
/// Keeps a dated history of battery wear so the app can answer "is my battery getting worse, and how fast".
/// <para>
/// Only measured values are recorded. When Windows reports no design or full-charge capacity, the app falls back to
/// a factory constant or a fixed ratio to show something in the UI; a trend built on those numbers would be a
/// straight line that says nothing about the pack, so those readings are skipped instead.
/// </para>
/// <para>
/// Wear moves over months, not days, so a reading is only appended when the last one is at least
/// <see cref="MinimumInterval"/> old, or when health dropped by <see cref="MinimumDropPercent"/> and the last one is
/// at least <see cref="MinimumDropInterval"/> old. The file is capped at <see cref="MaxSamples"/> readings.
/// </para>
/// </summary>
public sealed class BatteryWearHistory
{
    public const int MaxSamples = 400;
    public const double MinimumDropPercent = 1.0;

    /// <summary>
    /// Two weeks is enough for a wear curve: a year of readings is still two dozen points, and a pack that really
    /// degrades is caught by <see cref="MinimumDropPercent"/> long before the interval elapses.
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromDays(15);

    /// <summary>
    /// The shortest gap between two readings when the second one is let in early by <see cref="MinimumDropPercent"/>.
    /// </summary>
    /// <remarks>
    /// Without this the drop rule was the bug behind a wear curve on the first day: the reported capacity moves by
    /// more than a percent between two reads minutes apart, so the second read was recorded, the history held two
    /// points, and the page drew a trend out of a single afternoon. A real one-percent loss takes weeks, so a day is
    /// a generous floor and the curve can no longer appear before the pack has been watched for a while.
    /// </remarks>
    public static readonly TimeSpan MinimumDropInterval = TimeSpan.FromDays(1);

    private readonly string? _path;
    private readonly object _lock = new();
    private List<BatteryWearSample>? _samples;

    /// <summary>Null disables persistence (unit tests).</summary>
    public BatteryWearHistory(string? path = null)
    {
        _path = path ?? AppPaths.GetDataFile("battery_wear_history.json");
    }

    public string? Path => _path;

    /// <summary>Every recorded reading, oldest first.</summary>
    public IReadOnlyList<BatteryWearSample> GetSamples()
    {
        lock (_lock)
        {
            return Load().Select(Clone).ToList();
        }
    }

    public BatteryWearTrend GetTrend()
    {
        lock (_lock)
        {
            return new BatteryWearTrend { Samples = Load().Select(Clone).ToList() };
        }
    }

    /// <summary>
    /// Appends a reading when it carries new information. Returns true when the file was written.
    /// </summary>
    public bool TryRecord(BatteryHealthDetails details, DateTime? nowUtc = null)
    {
        if (details == null) return false;

        // An estimate is not a measurement: recording it would draw a trend out of an assumption.
        if (details.IsDesignCapacityEstimated || details.IsFullChargeCapacityEstimated) return false;
        if (details.DesignCapacityMWh <= 0 || details.FullChargeCapacityMWh <= 0) return false;
        if (details.HealthPercent is < 0 or > 100) return false;
        if (details.WearLevelPercent is < 0 or > 100) return false;

        var now = nowUtc ?? DateTime.UtcNow;

        lock (_lock)
        {
            var samples = Load();
            var last = samples.Count > 0 ? samples[^1] : null;

            if (last != null)
            {
                // A clock that moved backwards would put a reading before the one it followed and make the span
                // negative. Skipping is the honest answer; the next reading a day later records normally.
                if (now <= last.TimestampUtc) return false;

                bool intervalElapsed = now - last.TimestampUtc >= MinimumInterval;
                bool droppedEnough = last.HealthPercent - details.HealthPercent >= MinimumDropPercent
                                     && now - last.TimestampUtc >= MinimumDropInterval;
                if (!intervalElapsed && !droppedEnough) return false;
            }

            samples.Add(new BatteryWearSample
            {
                TimestampUtc = now,
                HealthPercent = Math.Round(details.HealthPercent, 2),
                WearLevelPercent = Math.Round(details.WearLevelPercent, 2),
                FullChargeCapacityMWh = Math.Round(details.FullChargeCapacityMWh, 0),
                DesignCapacityMWh = Math.Round(details.DesignCapacityMWh, 0),
                CycleCount = details.CycleCount
            });

            if (samples.Count > MaxSamples)
            {
                samples.RemoveRange(0, samples.Count - MaxSamples);
            }

            return Write(samples);
        }
    }

    /// <summary>Forgets the whole history. The caller asks the user first.</summary>
    public bool Clear()
    {
        lock (_lock)
        {
            _samples = new List<BatteryWearSample>();
            var path = _path;
            if (string.IsNullOrEmpty(path)) return true;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Pil aşınma geçmişi silinemedi", $"{path}: {ex.Message}");
                return false;
            }
        }
    }

    private List<BatteryWearSample> Load()
    {
        if (_samples != null) return _samples;

        var path = _path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _samples = new List<BatteryWearSample>();
            return _samples;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<BatteryWearSample>>(File.ReadAllText(path));
            // A hand-edited or half-written file must not break the page: unusable rows are dropped, and the rest
            // is sorted so the trend always reads oldest to newest.
            _samples = (parsed ?? new List<BatteryWearSample>())
                .Where(IsUsable)
                .OrderBy(s => s.TimestampUtc)
                .ToList();

            // The cap is enforced on read as well as on write. A file written by an older build, or by hand, could
            // hold thousands of rows, and the chart draws every one of them.
            if (_samples.Count > MaxSamples) _samples.RemoveRange(0, _samples.Count - MaxSamples);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Pil aşınma geçmişi okunamadı", $"{path}: {ex.Message}");
            _samples = new List<BatteryWearSample>();
        }
        return _samples;
    }

    /// <summary>
    /// True when a row read from disk can be drawn.
    /// </summary>
    /// <remarks>
    /// A JSON number can be <c>NaN</c> or an infinity, and both survive deserialisation. A single one of them turns
    /// the whole trend into a line that is not a number, and the chart then draws nothing at all — which reads as
    /// "the history is empty" rather than "one row is broken". The range checks come after the finiteness check on
    /// purpose: every comparison against <c>NaN</c> is false, so a range test alone would let it through.
    /// </remarks>
    private static bool IsUsable(BatteryWearSample? sample)
    {
        if (sample == null) return false;
        if (sample.TimestampUtc <= DateTime.MinValue) return false;
        if (!IsFinite(sample.HealthPercent) || sample.HealthPercent is < 0 or > 100) return false;
        if (!IsFinite(sample.WearLevelPercent) || sample.WearLevelPercent is < 0 or > 100) return false;
        if (!IsFinite(sample.FullChargeCapacityMWh) || sample.FullChargeCapacityMWh < 0) return false;
        if (!IsFinite(sample.DesignCapacityMWh) || sample.DesignCapacityMWh < 0) return false;
        if (sample.CycleCount is < 0) return false;
        return true;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private bool Write(List<BatteryWearSample> samples)
    {
        _samples = samples;
        var path = _path;
        if (string.IsNullOrEmpty(path)) return true;

        try
        {
            var json = JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(path, json);
            return true;
        }
        catch (Exception ex)
        {
            // The reading stays in memory for this session, so the page still shows it; only the file is missing.
            AppLog.Warn("Pil aşınma geçmişi yazılamadı", $"{path}: {ex.Message}");
            return false;
        }
    }

    private static BatteryWearSample Clone(BatteryWearSample s) => new()
    {
        TimestampUtc = s.TimestampUtc,
        HealthPercent = s.HealthPercent,
        WearLevelPercent = s.WearLevelPercent,
        FullChargeCapacityMWh = s.FullChargeCapacityMWh,
        DesignCapacityMWh = s.DesignCapacityMWh,
        CycleCount = s.CycleCount
    };
}
