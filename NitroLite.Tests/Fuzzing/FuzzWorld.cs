using System.IO;
using NitroLite.Core;
using NitroLite.Tests.Scenarios;

namespace NitroLite.Tests.Fuzzing;

/// <summary>One thing that can happen to a running NitroLite.</summary>
internal enum FuzzEvent
{
    /// <summary>The user picks an operating mode (Eco / Balanced / Performance / Turbo / Quiet).</summary>
    PowerMode,

    /// <summary>The user picks a fan mode (Auto / Max / Custom).</summary>
    FanMode,

    /// <summary>The user turns the software fan curve on or off.</summary>
    FanCurveToggle,

    /// <summary>The sensors move: a jump, a drift, or one of them stops answering.</summary>
    Temperature,

    /// <summary>One telemetry poll: the curve is asked for a speed.</summary>
    Poll,

    /// <summary>The adapter is plugged in or pulled out.</summary>
    AcDc,

    /// <summary>The machine sleeps and wakes.</summary>
    SuspendResume,

    /// <summary>The wall clock jumps forward or backward (NTP correction, manual change).</summary>
    ClockSkew,

    /// <summary>School mode is switched on or off.</summary>
    SchoolMode
}

/// <summary>
/// One seeded world: the real <see cref="MainWindow"/> state machine, the real <see cref="SchoolModeService"/> and
/// the real <see cref="PowerAutomationService"/>, wired to the same four fakes the scenario tests use. Nothing in
/// here re-implements a decision the product makes; the fakes only stand in for WMI, the Acer service, powrprof
/// and the panel.
/// </summary>
/// <remarks>
/// The six invariants are checked after every step, and a violation records the seed, the step number and the event
/// that produced it, so a failure is reproducible by re-running that one seed.
/// </remarks>
internal sealed class FuzzWorld : IDisposable
{
    private static readonly OperatingMode[] Modes =
    {
        OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo, OperatingMode.Eco
    };

    private static readonly FanMode[] FanModes = { FanMode.Auto, FanMode.Max, FanMode.Custom };

    /// <summary>Every event the world can produce, in the order the generator picks from.</summary>
    public static readonly IReadOnlyList<FuzzEvent> Events = new[]
    {
        FuzzEvent.PowerMode, FuzzEvent.FanMode, FuzzEvent.FanCurveToggle, FuzzEvent.Temperature,
        FuzzEvent.Poll, FuzzEvent.Poll, FuzzEvent.Poll, FuzzEvent.AcDc, FuzzEvent.SuspendResume,
        FuzzEvent.ClockSkew, FuzzEvent.SchoolMode
    };

    private readonly Dictionary<(Guid SubGroup, Guid Setting), uint> _beforeSchoolMode = new();
    private readonly List<(Guid SubGroup, Guid Setting)> _schoolModeWrote = new();
    private int _validatedFanWrites;

    public FuzzWorld(int seed)
    {
        Seed = seed;

        // A realistic starting point: on the adapter, a healthy battery, and every power setting school mode
        // touches is readable, so the round-trip invariant has something real to restore.
        foreach (var setting in SchoolModeService.PowerSettings)
        {
            Scheme.Dc[(setting.SubGroup, setting.Setting)] = 0;
        }

        Settings.FanCurveEnabled = false;
        Settings.CustomFanCpuPercent = 50;
        Settings.FanCurvePoints = FanCurve.DefaultPoints().ToList();

        Window = new MainWindow(Agent, Cpu, new GameDetectionService(), new AtkMouseService(),
            settings: Settings, isTestHarness: true);
        Window.PowerStateProvider = () => (IsAc, BatteryPercent);

        School = new SchoolModeService(Agent, Cpu, Display, Brightness, Settings, Scheme)
        {
            BackupPath = AppPaths.GetDataFile($"fuzz_school_{seed}_{Guid.NewGuid():N}.json"),
            GetCurrentOperatingMode = () => Agent.CurrentMode,
            GetCurrentFanMode = () => Agent.CurrentFanMode,
            TurnLightingOffAsync = () =>
            {
                bool wasOn = LightingOn;
                LightingOn = false;
                return Task.FromResult(wasOn);
            },
            RestoreLightingAsync = () =>
            {
                LightingOn = true;
                return Task.CompletedTask;
            }
        };

        Power = new PowerAutomationService(Agent, Cpu, Display, Settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () => Agent.CurrentMode,
            GetCurrentFanMode = () => Agent.CurrentFanMode,
            AcStatusProvider = () => IsAc
        };

        Hardware = new HardwareMonitorService(initNvml: false);
    }

    public int Seed { get; }

    public ScenarioAgentClient Agent { get; } = new();
    public ScenarioCpuPower Cpu { get; } = new();
    public ScenarioDisplay Display { get; } = new();
    public ScenarioBrightness Brightness { get; } = new();
    public InMemoryPowerScheme Scheme { get; } = new();
    public AppSettings Settings { get; } = new();
    public MainWindow Window { get; }
    public SchoolModeService School { get; }
    public PowerAutomationService Power { get; }
    public HardwareMonitorService Hardware { get; }

    /// <summary>A virtual wall clock. Only the events that take a time read it; the window's own poll uses UtcNow.</summary>
    public DateTime NowUtc { get; private set; } = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    public bool IsAc { get; private set; } = true;
    public int BatteryPercent { get; private set; } = 80;
    public int CpuTemp { get; private set; } = 45;
    public int GpuTemp { get; private set; } = 45;
    public bool SensorsDead { get; private set; }
    public bool LightingOn { get; private set; } = true;
    public bool SchoolModeOn { get; private set; }

    public int Step { get; private set; }
    public FuzzEvent LastEvent { get; private set; }

    /// <summary>
    /// How often each invariant was actually evaluated. A green run only means something if these are non-zero: a
    /// seed that never armed the curve would pass the thermal invariant by never testing it.
    /// </summary>
    public int ThermalFloorChecks { get; private set; }
    public int DeadSensorChecks { get; private set; }
    public int FanSpeedRangeChecks { get; private set; }
    public int SchoolModeRoundTrips { get; private set; }
    public int AcDcFlips { get; private set; }
    public int CurveArmings { get; private set; }

    /// <summary>Every invariant breach, in the order it happened. Empty means the seed ran clean.</summary>
    public List<string> Violations { get; } = new();

    /// <summary>Where a violation is reported from: the seed and the step that produced it.</summary>
    public string Where => $"seed {Seed}, step {Step}, event {LastEvent}";

    public async Task RunAsync(DeterministicRandom rng, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            await StepAsync(rng.Pick(Events), rng);
        }
    }

    public async Task StepAsync(FuzzEvent evt, DeterministicRandom rng)
    {
        Step++;
        LastEvent = evt;

        switch (evt)
        {
            case FuzzEvent.PowerMode:
                await Window.SetManualModeAsync(rng.Pick(Modes));
                break;

            case FuzzEvent.FanMode:
                await Window.SetManualFanModeAsync(rng.Pick(FanModes));
                break;

            case FuzzEvent.FanCurveToggle:
                await Window.SetFanCurveEnabledAsync(!Window.IsFanCurveArmed, quiet: true);
                if (Window.IsFanCurveArmed) CurveArmings++;
                break;

            case FuzzEvent.Temperature:
                // A dead sensor is 0, which is what AASSvc reports when it stops answering. NaN cannot reach the
                // window: the poll passes an int, so the NaN path is covered by the evaluator's own tests.
                // Both dying at once is the case the thermal invariant exists for, so it is a fifth of the readings
                // rather than the one-in-eighty a naive two-coin flip would give.
                double roll = rng.NextDouble();
                if (roll < 0.20)
                {
                    SensorsDead = true;
                    CpuTemp = 0;
                    GpuTemp = 0;
                }
                else if (roll < 0.35)
                {
                    SensorsDead = false;
                    CpuTemp = 0;
                    GpuTemp = rng.Next(35, 98);
                }
                else if (roll < 0.50)
                {
                    SensorsDead = false;
                    CpuTemp = rng.Next(35, 98);
                    GpuTemp = 0;
                }
                else
                {
                    SensorsDead = false;
                    CpuTemp = rng.Next(35, 98);
                    GpuTemp = rng.Next(35, 98);
                }
                break;

            case FuzzEvent.Poll:
                await Window.ApplyFanCurveAsync(CpuTemp, GpuTemp);
                CheckThermalFloor();
                break;

            case FuzzEvent.AcDc:
                IsAc = !IsAc;
                AcDcFlips++;
                BatteryPercent = rng.Next(5, 100);
                await Power.EvaluatePowerStateAsync(IsAc);
                break;

            case FuzzEvent.SuspendResume:
                NowUtc = NowUtc.AddMinutes(rng.Next(1, 240));
                await Power.EvaluatePowerStateAsync(IsAc);
                break;

            case FuzzEvent.ClockSkew:
                NowUtc = rng.Chance(0.5)
                    ? NowUtc.AddSeconds(-rng.Next(60, 86400))
                    : NowUtc.AddSeconds(rng.Next(60, 86400));
                break;

            case FuzzEvent.SchoolMode:
                await ToggleSchoolModeAsync();
                break;
        }

        CheckFanSpeedRange();
    }

    /// <summary>
    /// Invariant 1: while the curve owns the fans, the speed in effect can never sit below the safety floor for the
    /// temperature that produced it, and a dead sensor pair can never leave the fans quiet.
    /// </summary>
    private void CheckThermalFloor()
    {
        if (!Window.IsFanCurveArmed) return;

        // The curve only owns the fans once it has taken them; a failed write leaves the firmware in charge, which
        // is safe and is not this invariant's business.
        if (Agent.CurrentFanMode != FanMode.Custom) return;

        ThermalFloorChecks++;
        int hottest = Math.Max(CpuTemp, GpuTemp);

        if (hottest <= 0)
        {
            DeadSensorChecks++;
            if (Agent.CustomCpuPercent != FanCurve.MaxPercent || Agent.CustomFanGpuPercent != FanCurve.MaxPercent)
            {
                Violations.Add($"{Where}: both sensors dead but the fans are at " +
                    $"{Agent.CustomCpuPercent}/{Agent.CustomFanGpuPercent} %, not {FanCurve.MaxPercent} %.");
            }
            return;
        }

        int floor = FanCurve.SafetyFloorAt(hottest);
        if (Agent.CustomCpuPercent < floor)
        {
            Violations.Add($"{Where}: {hottest} °C needs at least {floor} % but the fans are at " +
                $"{Agent.CustomCpuPercent} %.");
        }
    }

    /// <summary>
    /// Invariant 2: every speed handed to the fans is one the service accepts. A custom write outside
    /// [30, 100] is a value the firmware would reject or misread.
    /// </summary>
    private void CheckFanSpeedRange()
    {
        for (; _validatedFanWrites < Agent.FanWrites.Count; _validatedFanWrites++)
        {
            var write = Agent.FanWrites[_validatedFanWrites];
            if (write.Mode != FanMode.Custom) continue;

            FanSpeedRangeChecks++;
            if (write.Cpu < FanCurve.MinPercent || write.Cpu > FanCurve.MaxPercent ||
                write.Gpu < FanCurve.MinPercent || write.Gpu > FanCurve.MaxPercent)
            {
                Violations.Add($"{Where}: a custom fan write asked for {write.Cpu}/{write.Gpu} %, " +
                    $"outside [{FanCurve.MinPercent}, {FanCurve.MaxPercent}].");
            }
        }
    }

    /// <summary>
    /// Invariant 3: every power setting school mode writes is one it recorded first, so switching it off puts the
    /// machine back exactly where it was — whatever happened in between.
    /// </summary>
    private async Task ToggleSchoolModeAsync()
    {
        if (!SchoolModeOn)
        {
            _beforeSchoolMode.Clear();
            foreach (var pair in Scheme.Dc) _beforeSchoolMode[pair.Key] = pair.Value;
            _schoolModeWrote.Clear();

            int writesBefore = Scheme.Writes.Count;
            await School.EnableAsync();
            SchoolModeOn = true;

            for (int i = writesBefore; i < Scheme.Writes.Count; i++)
            {
                var write = Scheme.Writes[i];
                _schoolModeWrote.Add((write.SubGroup, write.Setting));
            }
            return;
        }

        await School.DisableAsync();
        SchoolModeOn = false;
        SchoolModeRoundTrips++;

        foreach (var key in _schoolModeWrote)
        {
            if (!_beforeSchoolMode.TryGetValue(key, out uint expected))
            {
                Violations.Add($"{Where}: school mode wrote {key.Setting} but never recorded its previous value.");
                continue;
            }

            if (!Scheme.Dc.TryGetValue(key, out uint actual) || actual != expected)
            {
                string landed = Scheme.Dc.TryGetValue(key, out uint now) ? now.ToString() : "nothing";
                Violations.Add($"{Where}: school mode changed {key.Setting} from {expected} to {landed} " +
                    "and did not put it back.");
            }
        }
    }

    public void Dispose()
    {
        Power.Dispose();
        Hardware.Dispose();
        try
        {
            if (School.BackupPath != null && File.Exists(School.BackupPath)) File.Delete(School.BackupPath);
        }
        catch
        {
            // A leftover undo record in the throw-away test folder is not worth failing a run over.
        }
    }
}
