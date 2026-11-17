using System.IO;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests.Fuzzing;

/// <summary>
/// The invariants that hold without a window: the thermal floor, the atomic settings write, and the two clock
/// behaviours the curve depends on.
/// </summary>
/// <remarks>
/// The state-machine walk that used to live here drove the WPF window, which is retired. What is left is the part
/// that was always pure — <see cref="FanCurve"/>, <see cref="FanCurveEvaluator"/> and <see cref="AtomicFile"/> — so
/// the safety floor and the half-written-file guard keep their coverage after the front end they were written for
/// is gone.
/// </remarks>
public class FuzzInvariantTests
{

    /// <summary>
    /// Evaluates and commits, which is what the poll loop does once the hardware accepted the write. The evaluator
    /// only proposes: a refused write must not be remembered as applied.
    /// </summary>
    private static int? Step(FanCurveEvaluator evaluator, double temperatureC, DateTime nowUtc)
    {
        var target = evaluator.Evaluate(temperatureC, nowUtc);
        if (target is int percent) evaluator.Commit(percent, temperatureC, nowUtc);
        return target;
    }

    #region Invariant 1 — the thermal floor

    /// <summary>
    /// The floor rises with the temperature, and the curve can never ask for less than it whatever shape the user
    /// drew. A flat 30 % curve is the worst case a user can produce.
    /// </summary>
    [Theory]
    [InlineData(85, 60)]
    [InlineData(89, 60)]
    [InlineData(90, 80)]
    [InlineData(94, 80)]
    [InlineData(95, 100)]
    [InlineData(98, 100)]
    public void TheSafetyFloorHoldsAtEveryBand(int temperatureC, int expectedFloor)
    {
        var flat = new FanCurve(new[]
        {
            new FanCurvePoint(30, FanCurve.MinPercent),
            new FanCurvePoint(100, FanCurve.MinPercent)
        });

        Assert.Equal(expectedFloor, FanCurve.SafetyFloorAt(temperatureC));
        Assert.Equal(expectedFloor, flat.PercentAt(temperatureC));
    }

    #endregion

    #region Invariant 5 — the settings file is never half-written

    /// <summary>
    /// Two hundred saves per seed, each read back immediately. A reader must never see a truncated file, and no
    /// <c>.tmp</c> file may be left behind when a save succeeds.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void AtomicFileNeverLeavesAHalfWrittenFileOrATempFile(int seed)
    {
        var rng = new DeterministicRandom(seed);
        string path = AppPaths.GetDataFile($"fuzz_atomic_{seed}_{Guid.NewGuid():N}.json");
        string directory = Path.GetDirectoryName(path)!;

        for (int i = 0; i < 200; i++)
        {
            string payload = JsonSerializer.Serialize(new
            {
                Step = i,
                Value = rng.Next(0, 1_000_000),
                Text = new string('x', rng.Next(0, 400))
            });

            AtomicFile.WriteAllText(path, payload);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(i, document.RootElement.GetProperty("Step").GetInt32());
        }

        Assert.Empty(Directory.GetFiles(directory, Path.GetFileName(path) + ".tmp.*"));
    }

    #endregion

    #region Invariant 6 — a clock that jumps never freezes the curve

    /// <summary>
    /// A wall clock that steps backwards used to read as "not enough time has passed", which pinned the fans at
    /// their last speed until the clock caught up. A day forward must not skip the dwell check either.
    /// </summary>
    [Fact]
    public void AClockThatJumpsNeverFreezesTheCurve()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());
        var evaluator = new FanCurveEvaluator(curve);
        var t0 = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(FanCurve.MaxPercent, Step(evaluator, 95, t0));

        // The machine cools and the clock steps back an hour in the same poll.
        int? backwards = Step(evaluator, 40, t0.AddHours(-1));
        Assert.Equal(curve.PercentAt(40), backwards);

        // A day forward, then a normal dwell: the curve still slows down.
        var later = t0.AddDays(1);
        Assert.Equal(FanCurve.MaxPercent, Step(evaluator, 96, later));
        Assert.Equal(curve.PercentAt(40), Step(evaluator, 40, later.AddSeconds(30)));
    }

    /// <summary>
    /// A sensor wobbling on the hysteresis edge must not make the fans hunt: twenty readings either side of the
    /// band produce at most one downward step.
    /// </summary>
    [Fact]
    public void AReadingOnTheHysteresisEdgeDoesNotMakeTheFansHunt()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());
        var evaluator = new FanCurveEvaluator(curve);
        var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

        // Settle at 70 °C.
        Step(evaluator, 70, now);
        int settled = evaluator.AppliedPercent!.Value;

        int downwardSteps = 0;
        for (int i = 0; i < 20; i++)
        {
            now = now.AddSeconds(1);
            double wobble = i % 2 == 0 ? 69.8 : 70.2;
            int? target = Step(evaluator, wobble, now);
            if (target.HasValue && target.Value < settled) downwardSteps++;
        }

        Assert.Equal(0, downwardSteps);
    }

    #endregion
}
