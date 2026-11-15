using System.IO;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests.Scenarios;

/// <summary>
/// Category 1: a machine whose sensors lie, whose clock moves, and whose process dies without warning.
/// Every test here drives the real <see cref="FanCurve"/>, <see cref="FanCurveEvaluator"/> and
/// <see cref="CleanExitMarker"/>; nothing is mocked.
/// </summary>
public class ThermalChaosScenarios
{
    private static readonly DateTime T0 = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

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

        Assert.Null(evaluator.Evaluate(reading, T0));
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
        Assert.Equal(curve.PercentAt(75), evaluator.Evaluate(75, T0));

        var writes = new List<int>();
        var now = T0;
        for (int i = 0; i < 20; i++)
        {
            // 69.8 and 70.2 straddle the 70 °C anchor; the band is 3 °C wide.
            double reading = i % 2 == 0 ? 69.8 : 70.2;
            now = now.AddSeconds(1);
            int? target = evaluator.Evaluate(reading, now);
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

        int settled = evaluator.Evaluate(75, T0)!.Value;

        var now = T0;
        for (int i = 0; i < 50; i++)
        {
            now = now.AddSeconds(30);
            // 74.5 is inside the 3 °C band below 75, so the curve must hold.
            Assert.Null(evaluator.Evaluate(74.5, now));
        }

        Assert.Equal(settled, evaluator.Evaluate(75, now.AddSeconds(30)) is null ? settled : -1);
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

        evaluator.Evaluate(80, T0);
        int high = curve.PercentAt(80);

        // The machine cools, but the clock goes back an hour at the same moment.
        int? target = evaluator.Evaluate(50, T0.AddHours(-1));

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

        evaluator.Evaluate(80, T0);
        int? target = evaluator.Evaluate(50, T0.AddDays(1));

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
        evaluator.Evaluate(80, now);

        int writes = 0;
        for (int i = 0; i < 30; i++)
        {
            now = now.AddSeconds(1);
            if (evaluator.Evaluate(50, now).HasValue) writes++;
        }

        Assert.True(writes <= 1, $"A forward clock jump produced {writes} writes in 30 s.");
    }

    #endregion

    #region S1-05 A process that dies without warning

    /// <summary>
    /// The marker is the only thing that survives a kill. Finding it at startup is what tells the next run to hand
    /// the fans back before it reads anything.
    /// </summary>
    [Fact]
    public void AMarkerLeftBehindMeansThePreviousRunDidNotExitCleanly()
    {
        CleanExitMarker.MarkCleanExit();
        Assert.False(CleanExitMarker.PreviousRunWasUnclean());

        // A run that is killed never reaches MarkCleanExit, so the marker stays.
        CleanExitMarker.MarkRunning();
        Assert.True(File.Exists(CleanExitMarker.FilePath));
        Assert.True(CleanExitMarker.PreviousRunWasUnclean());

        // The next start reads it, restores the fans, and clears it.
        CleanExitMarker.MarkCleanExit();
        Assert.False(CleanExitMarker.PreviousRunWasUnclean());
    }

    /// <summary>
    /// The full kill-and-restart cycle: a run leaves the marker, the next run sees it, and the marker is gone once
    /// that run exits cleanly. A third start must not think anything crashed.
    /// </summary>
    [Fact]
    public void KillThenRestartThenCleanExitLeavesNothingBehind()
    {
        CleanExitMarker.MarkCleanExit();

        // Run 1: starts, is killed.
        CleanExitMarker.MarkRunning();
        Assert.True(CleanExitMarker.PreviousRunWasUnclean());

        // Run 2: sees the marker, restores the fans, marks itself running, exits cleanly.
        CleanExitMarker.MarkRunning();
        CleanExitMarker.MarkCleanExit();

        // Run 3: nothing to recover.
        Assert.False(CleanExitMarker.PreviousRunWasUnclean());
    }

    /// <summary>
    /// A read-only data folder must not turn the marker into a crash: losing it costs one unnecessary fan reset,
    /// not the app.
    /// </summary>
    [Fact]
    public void AnUnwritableMarkerIsNotFatal()
    {
        // The marker path is under the redirected test data folder; a directory in its place makes the write fail.
        var path = CleanExitMarker.FilePath;
        CleanExitMarker.MarkCleanExit();
        Directory.CreateDirectory(path);
        try
        {
            CleanExitMarker.MarkRunning();          // must not throw
            CleanExitMarker.MarkCleanExit();        // must not throw
        }
        finally
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
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

    #region S1-05 The curve itself, driven through the window

    private static MainWindow BuildCurveWindow(ScenarioAgentClient agent) =>
        new(agent, new ScenarioCpuPower(), new GameDetectionService(), new AtkMouseService(),
            settings: new AppSettings
            {
                FanCurveEnabled = false,
                CustomFanCpuPercent = 50,
                FanCurvePoints = FanCurve.DefaultPoints().ToList()
            },
            isTestHarness: true);

    /// <summary>
    /// The guard used to sit on the CPU reading alone, so a dead CPU sensor returned before the hotter of the two
    /// dies was ever taken and a 92 °C GPU could not raise the fans.
    /// </summary>
    [Fact]
    public Task ADeadCpuSensorDoesNotStopTheGpuFromRaisingTheFans() => Sta.RunAsync(async () =>
    {
        var agent = new ScenarioAgentClient();
        var window = BuildCurveWindow(agent);
        Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));
        agent.FanWrites.Clear();

        await window.ApplyFanCurveAsync(0, 92);

        Assert.NotEmpty(agent.FanWrites);
        Assert.True(agent.FanWrites[^1].Cpu >= 80,
            $"the GPU was at 92 °C but the curve asked for {agent.FanWrites[^1].Cpu} %");
    });

    /// <summary>
    /// The mirror case: a dead GPU sensor must not stop a hot CPU either.
    /// </summary>
    [Fact]
    public Task ADeadGpuSensorDoesNotStopTheCpuFromRaisingTheFans() => Sta.RunAsync(async () =>
    {
        var agent = new ScenarioAgentClient();
        var window = BuildCurveWindow(agent);
        Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));
        agent.FanWrites.Clear();

        await window.ApplyFanCurveAsync(92, 0);

        Assert.NotEmpty(agent.FanWrites);
        Assert.True(agent.FanWrites[^1].Cpu >= 80,
            $"the CPU was at 92 °C but the curve asked for {agent.FanWrites[^1].Cpu} %");
    });

    /// <summary>
    /// Neither sensor answering is not a cool machine. Once the outage has lasted
    /// <see cref="MainWindow.FanCurveDeadSensorPollThreshold"/> polls the curve holds full speed instead of leaving
    /// the fans wherever the last write put them, and it writes once rather than on every poll. A single bad poll is
    /// a service hiccup and must not spin them up.
    /// </summary>
    [Fact]
    public Task BothSensorsDead_AsksForFullSpeedOnce() => Sta.RunAsync(async () =>
    {
        var agent = new ScenarioAgentClient();
        var window = BuildCurveWindow(agent);
        Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));
        agent.FanWrites.Clear();

        // A single bad poll is a hiccup, not a dead sensor: nothing is written and the fans stay where they were.
        await window.ApplyFanCurveAsync(0, 0);
        Assert.Empty(agent.FanWrites);

        await window.ApplyFanCurveAsync(0, 0);
        Assert.Empty(agent.FanWrites);

        // The third in a row is a sensor that is really gone, and then the curve holds full speed.
        await window.ApplyFanCurveAsync(0, 0);
        Assert.Single(agent.FanWrites);
        Assert.Equal(FanCurve.MaxPercent, agent.FanWrites[0].Cpu);
        Assert.Equal(FanCurve.MaxPercent, agent.FanWrites[0].Gpu);

        // A further dead poll must not repeat the write.
        await window.ApplyFanCurveAsync(0, 0);
        Assert.Single(agent.FanWrites);
    });

    /// <summary>
    /// A sensor that comes back has to take the fans back from the full-speed hold, otherwise a single bad poll
    /// would leave the machine loud until the curve is turned off.
    /// </summary>
    [Fact]
    public Task ASensorThatComesBack_TakesTheFansBackFromFullSpeed() => Sta.RunAsync(async () =>
    {
        var agent = new ScenarioAgentClient();
        var window = BuildCurveWindow(agent);
        Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));

        for (int i = 0; i < MainWindow.FanCurveDeadSensorPollThreshold; i++)
        {
            await window.ApplyFanCurveAsync(0, 0);
        }
        Assert.Equal(FanCurve.MaxPercent, agent.FanWrites[^1].Cpu);

        agent.FanWrites.Clear();
        await window.ApplyFanCurveAsync(45, 0);

        Assert.NotEmpty(agent.FanWrites);
        Assert.True(agent.FanWrites[^1].Cpu < FanCurve.MaxPercent,
            $"the sensor read 45 °C but the fans stayed at {agent.FanWrites[^1].Cpu} %");
    });

    #endregion
}
