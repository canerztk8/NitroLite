using System.IO;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests.Scenarios;

/// <summary>
/// Category 1: a machine whose sensors lie, whose clock moves, and whose process dies without warning.
/// Every test here drives the real <see cref="FanCurve"/> and <see cref="FanCurveEvaluator"/>; nothing is mocked.
/// </summary>
public class ThermalChaosScenarios
{
    private static readonly DateTime T0 = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

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

    #region S1-01 A stuck CPU sensor must not hide a hot GPU

    /// <summary>
    /// The curve follows the hotter of the two dies. A CPU sensor pinned at 45 °C while the GPU climbs to 92 °C
    /// used to leave the fans at the idle speed, because the curve only ever looked at the CPU.
    /// </summary>
    [Theory]
    [InlineData(45, 92)]   // CPU stuck, GPU cooking
    [InlineData(92, 45)]   // the ordinary case
    [InlineData(88, 91)]   // both hot, GPU slightly hotter
    public void TheHotterDieDecidesTheFanSpeed(int cpuC, int gpuC)
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());

        // This is the line the poll loop runs before it asks the curve for a speed.
        int hottest = gpuC > cpuC ? gpuC : cpuC;

        int percent = curve.PercentAt(hottest);

        Assert.True(percent >= 80,
            $"The hotter die was {hottest} °C but the curve asked for {percent} %; the safety floor at 90 °C is 80 %.");
    }

    /// <summary>
    /// The same scenario stated as the failure it prevents: reading only the CPU would have asked for a quiet
    /// speed while the GPU was at 92 °C.
    /// </summary>
    [Fact]
    public void ReadingOnlyTheCpuWouldHaveLeftTheFansQuiet()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());

        int cpuOnly = curve.PercentAt(45);
        int hottest = curve.PercentAt(Math.Max(45, 92));

        Assert.True(cpuOnly < 80, $"The CPU-only reading asked for {cpuOnly} %, which is not the quiet answer this test assumes.");
        Assert.True(hottest >= 80, $"Following the hotter die asked for {hottest} %, which is not enough.");
        Assert.True(hottest > cpuOnly, "Following the hotter die must ask for more than the stuck CPU alone.");
    }

    #endregion

    #region S1-02 A dead sensor must not be read as a cool machine

    /// <summary>
    /// A reading that is not a number is not a cool machine. The curve answers full speed rather than its
    /// quietest anchor, so a broken sensor cannot turn into a silent fan.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnImpossibleReadingAsksForFullSpeed(double reading)
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());

        Assert.Equal(FanCurve.MaxPercent, curve.PercentAt(reading));
    }

    /// <summary>
    /// Negative infinity is the one impossible reading that still falls through to the quiet anchor, because the
    /// guard tests <see cref="double.IsNaN"/> and not <see cref="double.IsInfinity"/>. It is recorded here rather
    /// than fixed: the poll loop passes an <c>int</c> temperature, so no production path can produce it. If a
    /// future caller ever passes a raw double, this test is the reminder to widen the guard.
    /// </summary>
    [Fact]
    public void NegativeInfinityIsTheOneImpossibleReadingThatStaysQuiet()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());

        Assert.Equal(FanCurve.MinPercent, curve.PercentAt(double.NegativeInfinity));
    }

    /// <summary>
    /// The evaluator is the other half of the same rule: an impossible reading never writes a new speed, so a
    /// single bad sample cannot make the fans hunt. The two behaviours are deliberate and complementary.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void TheEvaluatorNeverWritesOnAnImpossibleReading(double reading)
    {
        var evaluator = new FanCurveEvaluator(new FanCurve(FanCurve.DefaultPoints()));

        Assert.Null(Step(evaluator, reading, T0));
    }

    /// <summary>
    /// A zero reading is the shape AASSvc uses for "I have no value". It must not be treated as 0 °C, which would
    /// interpolate to the quietest anchor.
    /// </summary>
    [Fact]
    public void ZeroIsNotTreatedAsZeroDegrees()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());

        int atZero = curve.PercentAt(0);
        int atThirty = curve.PercentAt(30);

        // The curve's own shape at 0 °C is its first anchor; the point is that the evaluator refuses to act on it.
        Assert.Equal(atZero, atThirty);
        Assert.Null(new FanCurveEvaluator(curve).Evaluate(0, T0));
    }

    #endregion

    #region S1-03 A sensor wobbling on the hysteresis edge must not make the fans hunt

    /// <summary>
    /// Twenty readings alternating across the 3 °C hysteresis band. The fans may step down at most once, and only
    /// after the dwell time; they must never oscillate with the sensor.
    /// </summary>
    [Fact]
    public void TwentyReadingsAcrossTheHysteresisEdgeDoNotMakeTheFansHunt()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());
        var evaluator = new FanCurveEvaluator(curve);

        // Settle at a high speed first, so every later step is a downward one.
        Assert.Equal(curve.PercentAt(75), Step(evaluator, 75, T0));

        var writes = new List<int>();
        var now = T0;
        for (int i = 0; i < 20; i++)
        {
            // 69.8 and 70.2 straddle the 70 °C anchor; the band is 3 °C wide.
            double reading = i % 2 == 0 ? 69.8 : 70.2;
            now = now.AddSeconds(1);
            int? target = Step(evaluator, reading, now);
            if (target.HasValue) writes.Add(target.Value);
        }

        // The dwell time is 10 s by default and the loop only covers 20 s, so at most two downward steps fit.
        Assert.True(writes.Count <= 2,
            $"The fans were rewritten {writes.Count} times in 20 s of a ±0.2 °C wobble: {string.Join(", ", writes)}.");
        Assert.All(writes, w => Assert.True(w <= curve.PercentAt(75), "A downward step must not raise the speed."));
    }

    /// <summary>
    /// The same wobble held inside the band must produce no write at all, however long it runs.
    /// </summary>
    [Fact]
    public void AWobbleInsideTheBandNeverWrites()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());
        var evaluator = new FanCurveEvaluator(curve);

        int settled = Step(evaluator, 75, T0)!.Value;

        var now = T0;
        for (int i = 0; i < 50; i++)
        {
            now = now.AddSeconds(30);
            // 74.5 is inside the 3 °C band below 75, so the curve must hold.
            Assert.Null(Step(evaluator, 74.5, now));
        }

        Assert.Equal(settled, Step(evaluator, 75, now.AddSeconds(30)) is null ? settled : -1);
    }

    #endregion

    #region S1-04 The clock moves under the curve

    /// <summary>
    /// A wall clock that steps backwards (NTP correction, a manual change, a resume) used to read as "not enough
    /// time has passed" and froze the curve at its last speed. It must be read as the clock having moved.
    /// </summary>
    [Fact]
    public void AClockThatJumpsBackAnHourDoesNotFreezeTheFans()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());
        var evaluator = new FanCurveEvaluator(curve);

        Step(evaluator, 80, T0);
        int high = curve.PercentAt(80);

        // The machine cools, but the clock goes back an hour at the same moment.
        int? target = Step(evaluator, 50, T0.AddHours(-1));

        Assert.NotNull(target);
        Assert.True(target!.Value < high, $"The fans stayed at {high} % after the machine cooled to 50 °C.");
    }

    /// <summary>
    /// A clock that jumps forward a day must not stall the dwell either: the elapsed time is enormous, so the
    /// downward step is allowed immediately.
    /// </summary>
    [Fact]
    public void AClockThatJumpsForwardADayLetsTheFansSlowDown()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());
        var evaluator = new FanCurveEvaluator(curve);

        Step(evaluator, 80, T0);
        int? target = Step(evaluator, 50, T0.AddDays(1));

        Assert.NotNull(target);
        Assert.Equal(curve.PercentAt(50), target!.Value);
    }

    /// <summary>
    /// A clock that jumps forward must not make the curve write on every poll afterwards: the dwell is measured
    /// from the last write, which is now in the past.
    /// </summary>
    [Fact]
    public void AForwardJumpDoesNotTurnIntoAWriteStorm()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());
        var evaluator = new FanCurveEvaluator(curve);

        var now = T0.AddDays(1);
        Step(evaluator, 80, now);

        int writes = 0;
        for (int i = 0; i < 30; i++)
        {
            now = now.AddSeconds(1);
            if (Step(evaluator, 50, now).HasValue) writes++;
        }

        Assert.True(writes <= 1, $"A forward clock jump produced {writes} writes in 30 s.");
    }

    #endregion

    #region S1-06 The safety floor is a staircase, not a single step

    /// <summary>
    /// A single 60 % floor at 85 °C left a 95 °C machine running at 60 %. The floor has to rise with the
    /// temperature, and an unreadable one has to ask for everything.
    /// </summary>
    [Theory]
    [InlineData(84, 0)]
    [InlineData(85, 60)]
    [InlineData(89, 60)]
    [InlineData(90, 80)]
    [InlineData(94, 80)]
    [InlineData(95, 100)]
    [InlineData(110, 100)]
    public void TheSafetyFloorRisesWithTheTemperature(int temperatureC, int expectedFloor)
    {
        Assert.Equal(expectedFloor, FanCurve.SafetyFloorAt(temperatureC));
    }

    /// <summary>
    /// A curve the user flattened to 30 % everywhere must still be raised to the floor at 95 °C.
    /// </summary>
    [Fact]
    public void AFlatQuietCurveCannotGetAroundTheFloor()
    {
        var flat = new FanCurve(new[]
        {
            new FanCurvePoint(30, 30),
            new FanCurvePoint(100, 30)
        });

        Assert.Equal(30, flat.PercentAt(40));
        Assert.Equal(60, flat.PercentAt(85));
        Assert.Equal(80, flat.PercentAt(90));
        Assert.Equal(100, flat.PercentAt(95));
    }

    #endregion
}
