using System.IO;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The wear history is the only place NitroLite keeps a number across days, so every test here is about what it
/// refuses to record: an estimate, a reading with no capacity behind it, a clock that moved backwards.
/// </summary>
public class BatteryWearHistoryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public BatteryWearHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NitroLiteWear_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "battery_wear_history.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }

        // Nothing here owns a finalizer; the suppression is the shape the analyzer requires.
        GC.SuppressFinalize(this);
    }

    private static BatteryHealthDetails Details(double health, int? cycles = null) => new()
    {
        DesignCapacityMWh = 90000,
        FullChargeCapacityMWh = 90000 * health / 100.0,
        HealthPercent = health,
        WearLevelPercent = 100 - health,
        CycleCount = cycles
    };

    private static readonly DateTime Day0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstReadingIsRecorded()
    {
        var history = new BatteryWearHistory(_path);

        Assert.True(history.TryRecord(Details(96.0), Day0));
        var samples = history.GetSamples();
        Assert.Single(samples);
        Assert.Equal(96.0, samples[0].HealthPercent, 3);
        Assert.Equal(4.0, samples[0].WearLevelPercent, 3);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void SecondReadingTheSameDayIsNotRecorded()
    {
        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(96.0), Day0);

        Assert.False(history.TryRecord(Details(96.0), Day0.AddHours(2)));
        Assert.Single(history.GetSamples());
    }

    [Fact]
    public void ReadingAfterTheIntervalIsRecorded()
    {
        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(96.0), Day0);

        Assert.True(history.TryRecord(Details(95.8), Day0 + BatteryWearHistory.MinimumInterval));
        Assert.Equal(2, history.GetSamples().Count);
    }

    [Fact]
    public void TheIntervalIsTwoWeeks()
    {
        // Wear moves over months; a reading every two weeks is a curve, a reading every day is noise.
        Assert.Equal(TimeSpan.FromDays(15), BatteryWearHistory.MinimumInterval);

        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(96.0), Day0);

        Assert.False(history.TryRecord(Details(95.8), Day0.AddDays(14)));
        Assert.True(history.TryRecord(Details(95.8), Day0.AddDays(15)));
        Assert.Equal(2, history.GetSamples().Count);
    }

    [Fact]
    public void ARealDropIsRecordedOnceADayHasPassed()
    {
        // A pack that loses a whole point is news, not noise, so the two-week interval does not have to elapse. But
        // the reading still waits a day: the reported capacity moves by more than a point between two reads minutes
        // apart, and letting those in is what drew a wear curve on the first day.
        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(96.0), Day0);

        Assert.True(history.TryRecord(Details(94.9), Day0 + BatteryWearHistory.MinimumDropInterval));
        Assert.Equal(2, history.GetSamples().Count);
    }

    [Fact]
    public void ADropWithinTheSameDayIsNotRecorded()
    {
        // The bug this guards: two reads an hour apart, a point of drift between them, and the page drew a trend out
        // of a single afternoon.
        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(96.0), Day0);

        Assert.False(history.TryRecord(Details(94.9), Day0.AddHours(1)));
        Assert.False(history.TryRecord(Details(94.9), Day0.AddHours(23)));
        Assert.Single(history.GetSamples());
    }

    [Fact]
    public void ASmallDropWithinTheIntervalIsNotRecorded()
    {
        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(96.0), Day0);

        Assert.False(history.TryRecord(Details(95.5), Day0.AddHours(1)));
        Assert.Single(history.GetSamples());
    }

    [Fact]
    public void AnEstimatedCapacityIsNeverRecorded()
    {
        // The UI falls back to a factory constant when Windows reports nothing. A trend drawn from that constant
        // would be a straight line that says nothing about the pack.
        var history = new BatteryWearHistory(_path);

        var estimatedDesign = Details(96.0);
        estimatedDesign.IsDesignCapacityEstimated = true;
        Assert.False(history.TryRecord(estimatedDesign, Day0));

        var estimatedFull = Details(96.0);
        estimatedFull.IsFullChargeCapacityEstimated = true;
        Assert.False(history.TryRecord(estimatedFull, Day0));

        Assert.Empty(history.GetSamples());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void AReadingWithoutCapacityIsNotRecorded()
    {
        var history = new BatteryWearHistory(_path);

        var noDesign = Details(96.0);
        noDesign.DesignCapacityMWh = 0;
        Assert.False(history.TryRecord(noDesign, Day0));

        var noFull = Details(96.0);
        noFull.FullChargeCapacityMWh = 0;
        Assert.False(history.TryRecord(noFull, Day0));

        Assert.Empty(history.GetSamples());
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(101.0)]
    public void AnImpossibleHealthValueIsNotRecorded(double health)
    {
        var history = new BatteryWearHistory(_path);
        Assert.False(history.TryRecord(Details(health), Day0));
        Assert.Empty(history.GetSamples());
    }

    [Fact]
    public void AReadingOlderThanTheLastOneIsNotRecorded()
    {
        // A clock that moved backwards would make the span negative and the trend meaningless.
        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(96.0), Day0);

        Assert.False(history.TryRecord(Details(95.0), Day0.AddDays(-3)));
        Assert.Single(history.GetSamples());
    }

    [Fact]
    public void TheFileIsCappedAtTheOldestReadingsBeingDropped()
    {
        var history = new BatteryWearHistory(_path);
        for (int i = 0; i < BatteryWearHistory.MaxSamples + 5; i++)
        {
            history.TryRecord(Details(99.0 - i * 0.01), Day0.AddDays(i * 15));
        }

        var samples = history.GetSamples();
        Assert.Equal(BatteryWearHistory.MaxSamples, samples.Count);
        // The newest reading is always kept; the oldest ones are the ones that go.
        Assert.Equal(Day0.AddDays((BatteryWearHistory.MaxSamples + 4) * 15), samples[^1].TimestampUtc);
    }

    [Fact]
    public void OneReadingIsNotATrend()
    {
        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(96.0), Day0);

        var trend = history.GetTrend();
        Assert.False(trend.HasTrend);
        Assert.Null(trend.SpanDays);
        Assert.Null(trend.WearChangePercent);
        Assert.Null(trend.WearPerYearPercent);
    }

    [Fact]
    public void TwoReadingsGiveTheChangeAndTheSpan()
    {
        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(96.0), Day0);
        history.TryRecord(Details(95.4), Day0.AddDays(30));

        var trend = history.GetTrend();
        Assert.True(trend.HasTrend);
        Assert.Equal(30.0, trend.SpanDays!.Value, 3);
        Assert.Equal(0.6, trend.WearChangePercent!.Value, 3);
    }

    [Fact]
    public void TheYearlyRateNeedsAWeekOfHistory()
    {
        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(96.0), Day0);
        // A whole point lost in three days is recorded at once, interval or not.
        history.TryRecord(Details(94.9), Day0.AddDays(3));

        // Three days is not enough to say anything about a year.
        Assert.Null(history.GetTrend().WearPerYearPercent);

        history.TryRecord(Details(95.0), Day0.AddDays(100));
        var perYear = history.GetTrend().WearPerYearPercent;
        Assert.NotNull(perYear);
        Assert.Equal(1.0 / 100.0 * 365.0, perYear!.Value, 3);
    }

    [Fact]
    public void ARecalibratedPackGivesANegativeChange()
    {
        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(95.0), Day0);
        history.TryRecord(Details(96.0), Day0.AddDays(20));

        var trend = history.GetTrend();
        Assert.Equal(-1.0, trend.WearChangePercent!.Value, 3);
        Assert.True(trend.WearPerYearPercent < 0);
    }

    [Fact]
    public void TheHistorySurvivesAReload()
    {
        var first = new BatteryWearHistory(_path);
        first.TryRecord(Details(96.0, cycles: 42), Day0);
        first.TryRecord(Details(95.0, cycles: 60), Day0.AddDays(20));

        var second = new BatteryWearHistory(_path);
        var samples = second.GetSamples();
        Assert.Equal(2, samples.Count);
        Assert.Equal(42, samples[0].CycleCount);
        Assert.Equal(60, samples[1].CycleCount);
        Assert.Equal(95.0, samples[1].HealthPercent, 3);
    }

    [Fact]
    public void ACorruptFileIsTreatedAsAnEmptyHistory()
    {
        File.WriteAllText(_path, "{ this is not json");

        var history = new BatteryWearHistory(_path);
        Assert.Empty(history.GetSamples());
        // And it still records: a broken file must not disable the feature for good.
        Assert.True(history.TryRecord(Details(96.0), Day0));
        Assert.Single(history.GetSamples());
    }

    [Fact]
    public void UnusableRowsAreDroppedAndTheRestIsSorted()
    {
        var rows = new[]
        {
            new BatteryWearSample { TimestampUtc = Day0.AddDays(5), HealthPercent = 95.0, WearLevelPercent = 5.0 },
            new BatteryWearSample { TimestampUtc = Day0, HealthPercent = 96.0, WearLevelPercent = 4.0 },
            new BatteryWearSample { TimestampUtc = DateTime.MinValue, HealthPercent = 90.0, WearLevelPercent = 10.0 },
            new BatteryWearSample { TimestampUtc = Day0.AddDays(1), HealthPercent = 400.0, WearLevelPercent = 1.0 }
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(rows));

        var samples = new BatteryWearHistory(_path).GetSamples();
        Assert.Equal(2, samples.Count);
        Assert.Equal(Day0, samples[0].TimestampUtc);
        Assert.Equal(Day0.AddDays(5), samples[1].TimestampUtc);
    }

    [Fact]
    public void ClearRemovesTheFileAndTheSamples()
    {
        var history = new BatteryWearHistory(_path);
        history.TryRecord(Details(96.0), Day0);
        Assert.True(File.Exists(_path));

        Assert.True(history.Clear());
        Assert.Empty(history.GetSamples());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void AnInMemoryHistoryKeepsEverythingInMemory()
    {
        // Not `new BatteryWearHistory(null)`: that is the production file in AppData, and Clear() below deleted it.
        var history = BatteryWearHistory.InMemory();
        Assert.True(history.TryRecord(Details(96.0), Day0));
        Assert.Single(history.GetSamples());
        Assert.True(history.Clear());
    }

    [Fact]
    public void TheRecordedValuesAreTheMeasuredOnes()
    {
        var history = new BatteryWearHistory(_path);
        var details = Details(93.7, cycles: 12);
        details.DesignCapacityMWh = 90000;
        details.FullChargeCapacityMWh = 84330;

        history.TryRecord(details, Day0);
        var sample = history.GetSamples().Single();
        Assert.Equal(90000, sample.DesignCapacityMWh, 0);
        Assert.Equal(84330, sample.FullChargeCapacityMWh, 0);
        Assert.Equal(93.7, sample.HealthPercent, 3);
        Assert.Equal(6.3, sample.WearLevelPercent, 3);
    }
}
