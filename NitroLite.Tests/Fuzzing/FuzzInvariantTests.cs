using System.IO;
using System.Text.Json;
using NitroLite.Core;
using NitroLite.Tests.Scenarios;
using Xunit;

namespace NitroLite.Tests.Fuzzing;

/// <summary>
/// Deterministic chaos: 50 seeds, 500 events each, 25 000 state transitions through the real window, the real
/// school-mode service and the real power-automation service. Every step re-checks the six invariants, and a breach
/// names the seed and the step so it can be replayed on its own.
/// </summary>
/// <remarks>
/// The events are picked from <see cref="FuzzWorld.Events"/>, which lists <see cref="FuzzEvent.Poll"/> three times:
/// a real machine polls far more often than the user touches a control, and the thermal invariants only mean
/// something if the curve is actually driven.
/// </remarks>
public class FuzzInvariantTests
{
    private const int StepsPerSeed = 500;

    #region Invariants 1, 2, 3 and 6 — the whole state machine

    /// <summary>
    /// The main run. Any invariant breach, any unhandled exception and any deadlock fails the seed that produced it;
    /// the message carries the seed, the step and the event.
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
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(35)]
    [InlineData(36)]
    [InlineData(37)]
    [InlineData(38)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(44)]
    [InlineData(45)]
    [InlineData(46)]
    [InlineData(47)]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(50)]
    public Task FiveHundredRandomEventsKeepEveryInvariant(int seed) => Sta.RunAsync(async () =>
    {
        using var world = new FuzzWorld(seed);
        var rng = new DeterministicRandom(seed);

        await world.RunAsync(rng, StepsPerSeed);

        Assert.True(world.Violations.Count == 0,
            $"seed {seed} broke {world.Violations.Count} invariant(s):{Environment.NewLine}" +
            string.Join(Environment.NewLine, world.Violations));

        // A green run only means something if the invariants were actually reached. 500 events over 11 slots hit
        // every event type about 45 times, so each of these is a floor, not a target.
        Assert.True(world.CurveArmings > 0, $"seed {seed} never armed the fan curve");
        Assert.True(world.ThermalFloorChecks > 0, $"seed {seed} never evaluated the thermal floor");
        Assert.True(world.DeadSensorChecks > 0, $"seed {seed} never polled with both sensors dead");
        Assert.True(world.FanSpeedRangeChecks > 0, $"seed {seed} never wrote a custom fan speed");
        Assert.True(world.SchoolModeRoundTrips > 0, $"seed {seed} never completed a school-mode round trip");
        Assert.True(world.AcDcFlips > 0, $"seed {seed} never changed the power source");
    });

    #endregion

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

    /// <summary>
    /// A dead sensor pair is not a cool machine: once the outage has lasted
    /// <see cref="MainWindow.FanCurveDeadSensorPollThreshold"/> polls the window holds full speed instead of leaving
    /// the fans wherever the last good reading put them. A single bad poll is a hiccup and must not spin them up.
    /// </summary>
    [Fact]
    public Task BothSensorsDeadHoldsFullSpeed() => Sta.RunAsync(async () =>
    {
        using var world = new FuzzWorld(seed: 0);
        Assert.True(await world.Window.SetFanCurveEnabledAsync(true, quiet: true));

        // A good reading first, so the fans are somewhere quiet before the sensors die.
        await world.Window.ApplyFanCurveAsync(40, 40);
        Assert.True(world.Agent.CustomCpuPercent < FanCurve.MaxPercent);

        // One bad poll is a hiccup, not a dead sensor: the fans stay where the last good reading put them.
        await world.Window.ApplyFanCurveAsync(0, 0);
        Assert.True(world.Agent.CustomCpuPercent < FanCurve.MaxPercent,
            $"a single dead poll took the fans to {world.Agent.CustomCpuPercent} %");

        // A run of them is a dead sensor, and then the curve holds full speed.
        for (int i = 1; i < MainWindow.FanCurveDeadSensorPollThreshold; i++)
        {
            await world.Window.ApplyFanCurveAsync(0, 0);
        }

        Assert.Equal(FanCurve.MaxPercent, world.Agent.CustomCpuPercent);
        Assert.Equal(FanCurve.MaxPercent, world.Agent.CustomFanGpuPercent);
    });

    /// <summary>
    /// A stuck CPU sensor must not hide a hot GPU: the curve follows the hotter of the two dies.
    /// </summary>
    [Fact]
    public Task AStuckCpuSensorDoesNotHideAHotGpu() => Sta.RunAsync(async () =>
    {
        using var world = new FuzzWorld(seed: 0);
        Assert.True(await world.Window.SetFanCurveEnabledAsync(true, quiet: true));

        await world.Window.ApplyFanCurveAsync(45, 96);

        Assert.True(world.Agent.CustomCpuPercent >= 100,
            $"the GPU was at 96 °C but the fans were at {world.Agent.CustomCpuPercent} %");
    });

    #endregion

    #region Invariant 2 — the fan speed range

    /// <summary>
    /// Every custom speed the window hands over is one the Acer service accepts. A value below 30 % is rejected by
    /// the firmware, and one above 100 % is not a speed at all.
    /// </summary>
    [Fact]
    public Task EveryCustomWriteStaysInsideTheAcceptedRange() => Sta.RunAsync(async () =>
    {
        using var world = new FuzzWorld(seed: 0);
        var rng = new DeterministicRandom(7);

        await world.Window.SetFanCurveEnabledAsync(true, quiet: true);
        for (int i = 0; i < 200; i++)
        {
            await world.Window.ApplyFanCurveAsync(rng.Next(0, 100), rng.Next(0, 100));
        }

        var custom = world.Agent.FanWrites.Where(w => w.Mode == FanMode.Custom).ToList();
        Assert.NotEmpty(custom);
        Assert.All(custom, w =>
        {
            Assert.InRange(w.Cpu, FanCurve.MinPercent, FanCurve.MaxPercent);
            Assert.InRange(w.Gpu, FanCurve.MinPercent, FanCurve.MaxPercent);
        });
    });

    #endregion

    #region Invariant 3 — school mode always puts everything back

    /// <summary>
    /// School mode records every power setting before it writes it, so switching it off restores the machine even
    /// after a sleep, a wake and two adapter changes in between.
    /// </summary>
    [Fact]
    public Task SchoolModeRestoresEverySettingAfterSleepAndAcDc() => Sta.RunAsync(async () =>
    {
        using var world = new FuzzWorld(seed: 0);

        // Give every setting a distinct non-zero value, so a restore that writes the wrong one is visible.
        uint next = 1;
        foreach (var setting in SchoolModeService.PowerSettings)
        {
            world.Scheme.Dc[(setting.SubGroup, setting.Setting)] = next++;
        }
        var before = new Dictionary<(Guid SubGroup, Guid Setting), uint>(world.Scheme.Dc);

        await world.School.EnableAsync();
        Assert.True(world.School.IsEnabled);

        // The machine sleeps, wakes, is unplugged and plugged back in while school mode is on.
        await world.Power.EvaluatePowerStateAsync(false);
        await world.Power.EvaluatePowerStateAsync(true);
        await world.Power.EvaluatePowerStateAsync(false);

        await world.School.DisableAsync();
        Assert.False(world.School.IsEnabled);

        foreach (var pair in before)
        {
            Assert.True(world.Scheme.Dc.TryGetValue(pair.Key, out uint now),
                $"{pair.Key.Setting} was not restored at all");
            Assert.Equal(pair.Value, now);
        }
    });

    /// <summary>
    /// A setting the machine refuses to read is never written, so switching school mode off cannot leave it in a
    /// state the undo record does not know about.
    /// </summary>
    [Fact]
    public Task ASettingThatCannotBeReadIsNeverWritten() => Sta.RunAsync(async () =>
    {
        using var world = new FuzzWorld(seed: 0);

        // Only the first setting is readable; the rest report "this scheme does not carry it".
        var readable = SchoolModeService.PowerSettings[0];
        world.Scheme.Dc.Clear();
        world.Scheme.Dc[(readable.SubGroup, readable.Setting)] = 42;

        await world.School.EnableAsync();

        var written = world.Scheme.Writes.Select(w => (w.SubGroup, w.Setting)).Distinct().ToList();
        Assert.All(written, key => Assert.Equal((readable.SubGroup, readable.Setting), key));

        await world.School.DisableAsync();
        Assert.Equal(42u, world.Scheme.Dc[(readable.SubGroup, readable.Setting)]);
    });

    #endregion

    #region Invariant 4 — the dGPU is never woken on battery

    /// <summary>
    /// The chain that keeps a sleeping dGPU asleep: on battery <see cref="PowerAutomationService.LastAcStatus"/> is
    /// false, the poll passes <c>allowNvml: false</c>, and the monitor service then refuses to touch NVML.
    /// </summary>
    /// <remarks>
    /// The P/Invoke call count itself cannot be observed without a driver, so this asserts the two ends of the chain
    /// and the manual checklist covers the middle. It is recorded here rather than claimed as a live measurement.
    /// </remarks>
    [Fact]
    public Task OnBatteryTheDgpuIsNeverAskedForMetrics() => Sta.RunAsync(async () =>
    {
        using var world = new FuzzWorld(seed: 0);

        await world.Power.EvaluatePowerStateAsync(false);
        Assert.False(world.Power.LastAcStatus, "the automation did not record the machine as being on battery");

        // The fallback path the poll takes when AASSvc is gone, with the dGPU possibly in D3cold.
        var fallback = world.Hardware.CreateFallbackMonitorData(allowNvml: false);
        Assert.False(fallback.GpuMetricsAvailable);
        Assert.Equal(0.0, fallback.GpuPowerWatts);

        // The normal path with AASSvc reporting GPU1_TEMPERATURE 0, which is RTD3 / D3cold on Optimus.
        var sample = new MonitorData { CpuTemperature = 55, GpuTemperature = 0 };
        world.Hardware.EnrichMonitorData(sample, gpuAsleepPerService: true);
        Assert.False(sample.GpuMetricsAvailable);
        Assert.Equal(0.0, sample.GpuPowerWatts);
        Assert.Equal(0, sample.GpuTemperature);
    });

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

        Assert.Equal(FanCurve.MaxPercent, evaluator.Evaluate(95, t0));

        // The machine cools and the clock steps back an hour in the same poll.
        int? backwards = evaluator.Evaluate(40, t0.AddHours(-1));
        Assert.Equal(curve.PercentAt(40), backwards);

        // A day forward, then a normal dwell: the curve still slows down.
        var later = t0.AddDays(1);
        Assert.Equal(FanCurve.MaxPercent, evaluator.Evaluate(96, later));
        Assert.Equal(curve.PercentAt(40), evaluator.Evaluate(40, later.AddSeconds(30)));
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
        evaluator.Evaluate(70, now);
        int settled = evaluator.AppliedPercent!.Value;

        int downwardSteps = 0;
        for (int i = 0; i < 20; i++)
        {
            now = now.AddSeconds(1);
            double wobble = i % 2 == 0 ? 69.8 : 70.2;
            int? target = evaluator.Evaluate(wobble, now);
            if (target.HasValue && target.Value < settled) downwardSteps++;
        }

        Assert.Equal(0, downwardSteps);
    }

    #endregion
}
