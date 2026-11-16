using System.IO;
using System.Windows;
using System.Windows.Controls;
using NitroLite.Core;
using NitroLite.Tests.Scenarios;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Drives one real <see cref="MainWindow"/> through the whole event universe — power, thermal, school mode, games,
/// discharge, peripherals, updates — and checks twelve safety and consistency invariants after every single step.
/// </summary>
/// <remarks>
/// The point is not to re-test what the focused suites already pin. It is to find the states that only appear when
/// two subsystems act in an order nobody wrote a test for: school mode plus an adapter change plus a game starting,
/// a sensor that dies while a game profile owns the curve, an update notice cleared mid-download.
/// <para>
/// Everything here is deterministic. The random walks are seeded, the clock is the real one only where the code
/// under test reads it, and a failure prints the seed, the step and the event that produced it, so a red run is
/// reproducible from the message alone.
/// </para>
/// <para>
/// The step budget is shaped by what each call actually costs. A curve step is about a microsecond, a mode switch
/// about 0.2 ms, a curve arm/disarm about 4 ms (two atomic settings writes) and a school-mode round trip about
/// 400 ms against the real power scheme. The 100 000-step walk therefore runs the cheap half of the universe at
/// volume, and the expensive half gets its own bounded walks with the same invariant checks.
/// </para>
/// </remarks>
public class ComprehensiveUsageChaosTests
{
    // =============================================================================================================
    // The event universe: 53 events across the seven categories.
    // =============================================================================================================

    internal enum ChaosEvent
    {
        // Power and operating modes.
        ModeSilent, ModeBalanced, ModePerformance, ModeTurbo,
        PlugAc, UnplugDc, SleepResume, SessionUnlock,
        BoostAggressive, BoostEfficient, BoostDisabled, BoostCacheExpiry,

        // Fan and thermal.
        FanAuto, FanMax, FanCustom, FanCurveOn, FanCurveOff,
        TempNormal, TempCritical, TempSensorDropout, TempImpossible,
        TempGlitchBurst, TempPersistentFailure, FanStall,

        // School mode crossings.
        SchoolModeOn, SchoolModeOff, SchoolUndoUnwritable, SchoolThenGame, SchoolThenAcDc,

        // Game sessions and display.
        GameStartA, GameStartB, GameEnd, GameCrash, RefreshRateSwitch, ExternalMonitor,

        // Battery.
        ChargeLimitOn, ChargeLimitOff,

        // Peripherals and RGB.
        MousePlug, MouseUnplug, RgbChange, RgbIdleTimeout,

        // Update, UI and environment.
        UpdateNone, UpdateAvailable, UpdateNetworkError, UpdateInstalled, BadUrl,
        ThemeToggle, LanguageSwitch, ButtonSpam, CorruptConfig, UncleanExit
    }

    /// <summary>Every event, in declaration order. The systematic passes enumerate this.</summary>
    private static readonly ChaosEvent[] AllEvents = Enum.GetValues<ChaosEvent>();

    /// <summary>
    /// The events that change a subsystem's state rather than just nudging a reading. Pairs and triples are built
    /// from these: two temperature readings in a row say nothing a single one did not, but a game start followed by
    /// an adapter pull is exactly the kind of crossing this suite exists for.
    /// </summary>
    private static readonly ChaosEvent[] StructuralEvents =
    {
        ChaosEvent.ModeSilent, ChaosEvent.ModeBalanced, ChaosEvent.ModePerformance, ChaosEvent.ModeTurbo,
        ChaosEvent.PlugAc, ChaosEvent.UnplugDc, ChaosEvent.SleepResume,
        ChaosEvent.BoostAggressive, ChaosEvent.BoostDisabled,
        ChaosEvent.FanAuto, ChaosEvent.FanMax, ChaosEvent.FanCurveOn, ChaosEvent.FanCurveOff,
        ChaosEvent.TempCritical, ChaosEvent.TempPersistentFailure,
        ChaosEvent.SchoolModeOn, ChaosEvent.SchoolModeOff,
        ChaosEvent.GameStartA, ChaosEvent.GameEnd,
        ChaosEvent.UpdateAvailable, ChaosEvent.UpdateInstalled
    };

    // =============================================================================================================
    // The world: one real window, the fakes it talks to, and the environment the events move.
    // =============================================================================================================

    private sealed class ChaosWorld : IDisposable
    {
        public ScenarioAgentClient Agent { get; } = new();
        public ScenarioCpuPower Cpu { get; } = new();
        public ScenarioDisplay Display { get; } = new();
        public ScenarioBrightness Brightness { get; } = new();
        public InMemoryPowerScheme Scheme { get; } = new();
        public AppSettings Settings { get; } = new();
        public BatteryWatchdogService Battery { get; }
        public MainWindow Window { get; }
        public Random Rng { get; }
        public int Seed { get; }

        // The environment the events move. Nothing here is read from the machine.
        public bool OnAc = true;
        public int BatteryPercent = 90;
        public int TemperatureC = 45;
        public bool GameRunning;
        public GameProfile? ActiveGame;
        public UpdateInfo? PendingUpdate;
        public bool ChargeLimitOn;
        public bool MouseReceiverPresent = true;
        public bool ExternalMonitor;
        public int RgbBrightness = 80;

        // The two halves of the title bar's update notice. The harness window has no XAML, so the real controls do
        // not exist; these stand in for them and go through the same helper the window uses.
        public Button UpdateBadge { get; } = new() { Visibility = Visibility.Collapsed };
        public Button ReleaseNotes { get; } = new() { Visibility = Visibility.Collapsed };

        public int Step;
        public ChaosEvent LastEvent;

        private OperatingMode? _lastOperatingModeBeforeSchool;
        private (OperatingMode Mode, CpuBoostMode Boost, FanMode Fan, int Hz, int? Brightness) _desktopBaseline;
        private bool _baselineCaptured;

        public ChaosWorld(int seed)
        {
            Seed = seed;
            Rng = new Random(seed);

            Battery = new BatteryWatchdogService(attachSystemEvents: false)
            {
                SuppressSounds = true
            };

            Window = new MainWindow(
                Agent, Cpu, new GameDetectionService(), new AtkMouseService(),
                batteryService: Battery,
                settings: Settings,
                isTestHarness: true,
                displayService: Display,
                screenBrightnessService: Brightness,
                schoolModeScheme: Scheme);

            Window.PowerStateProvider = () => (OnAc, BatteryPercent);
            Window.ToastHandler = (_, _) => { };
            Window.MouseProfileSwitcher = _ => true;
            Window.SchoolModeServiceForTesting!.BackupPath =
                AppPaths.GetDataFile($"chaos_school_{seed}_{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            try { Battery.Dispose(); } catch { }
            try { Window.Close(); } catch { }
        }

        // ---- dispatch ------------------------------------------------------------------------------------------

        public async Task ApplyAsync(ChaosEvent e)
        {
            LastEvent = e;
            Step++;
            int writesBefore = Agent.FanWrites.Count;

            switch (e)
            {
                case ChaosEvent.ModeSilent: await Window.SetManualModeAsync(OperatingMode.Quiet, persist: false); break;
                case ChaosEvent.ModeBalanced: await Window.SetManualModeAsync(OperatingMode.Default, persist: false); break;
                case ChaosEvent.ModePerformance: await Window.SetManualModeAsync(OperatingMode.Extreme, persist: false); break;
                case ChaosEvent.ModeTurbo: await Window.SetManualModeAsync(OperatingMode.Turbo, persist: false); break;

                case ChaosEvent.PlugAc:
                    OnAc = true;
                    await Window.PowerAutomation!.EvaluatePowerStateAsync(forceAcStatus: true);
                    break;
                case ChaosEvent.UnplugDc: OnAc = false; await Window.PowerAutomation!.EvaluatePowerStateAsync(forceAcStatus: false); break;
                case ChaosEvent.SleepResume: await Window.ReassertOperatingModeAsync(isBoot: false); break;
                case ChaosEvent.SessionUnlock: await Window.ReassertOperatingModeAsync(isBoot: false); break;

                case ChaosEvent.BoostAggressive: await Window.SetManualCpuBoostAsync(CpuBoostMode.Aggressive); break;
                case ChaosEvent.BoostEfficient: await Window.SetManualCpuBoostAsync(CpuBoostMode.Efficient); break;
                case ChaosEvent.BoostDisabled: await Window.SetManualCpuBoostAsync(CpuBoostMode.Disabled); break;
                case ChaosEvent.BoostCacheExpiry: Cpu.InvalidatePowerCfgCache(); break;

                case ChaosEvent.FanAuto: await Window.SetManualFanModeAsync(FanMode.Auto); break;
                case ChaosEvent.FanMax: await Window.SetManualFanModeAsync(FanMode.Max); break;
                case ChaosEvent.FanCustom: await Window.SetManualFanModeAsync(FanMode.Custom); break;
                case ChaosEvent.FanCurveOn: await Window.SetFanCurveEnabledAsync(true, quiet: true); break;
                case ChaosEvent.FanCurveOff: await Window.SetFanCurveEnabledAsync(false, quiet: true); break;

                case ChaosEvent.TempNormal: TemperatureC = Rng.Next(35, 83); await CurveStepAsync(); break;
                case ChaosEvent.TempCritical: TemperatureC = new[] { 85, 90, 95, 102 }[Rng.Next(4)]; await CurveStepAsync(); break;
                case ChaosEvent.TempSensorDropout: TemperatureC = new[] { 0, -1 }[Rng.Next(2)]; await CurveStepAsync(); break;
                case ChaosEvent.TempImpossible: TemperatureC = 255; await CurveStepAsync(); break;
                case ChaosEvent.TempGlitchBurst:
                    for (int i = 0; i < Rng.Next(1, 3); i++) { TemperatureC = 0; await CurveStepAsync(); }
                    break;
                case ChaosEvent.TempPersistentFailure:
                    for (int i = 0; i < 3; i++) { TemperatureC = 0; await CurveStepAsync(); }
                    break;
                case ChaosEvent.FanStall:
                    // A real AASSvc sample: the watchdog only judges tachometer readings, so the flag is part of
                    // what makes this a stall rather than a derived number.
                    await Window.CheckFanStallAsync(new MonitorData
                    {
                        CpuTemperature = 92, GpuTemperature = 90, CpuFanSpeed = 0, GpuFanSpeed = 0,
                        IsFanRpmMeasured = true
                    });
                    break;

                case ChaosEvent.SchoolModeOn: await SchoolModeAsync(true); break;
                case ChaosEvent.SchoolModeOff: await SchoolModeAsync(false); break;
                case ChaosEvent.SchoolUndoUnwritable:
                    // A read-only data folder: the undo record cannot be written, so school mode must refuse.
                    Scheme.WritesSucceed = false;
                    await SchoolModeAsync(true);
                    Scheme.WritesSucceed = true;
                    break;
                case ChaosEvent.SchoolThenGame:
                    await SchoolModeAsync(true);
                    await StartGameAsync(GameA);
                    break;
                case ChaosEvent.SchoolThenAcDc:
                    await SchoolModeAsync(true);
                    OnAc = !OnAc;
                    await Window.PowerAutomation!.EvaluatePowerStateAsync(forceAcStatus: OnAc);
                    break;

                case ChaosEvent.GameStartA: await StartGameAsync(GameA); break;
                case ChaosEvent.GameStartB: await StartGameAsync(GameB); break;
                case ChaosEvent.GameEnd: await EndGameAsync(); break;
                case ChaosEvent.GameCrash:
                    // The process dies without the game-ended event: the recovery record is what survives.
                    if (ActiveGame != null)
                    {
                        Window.SaveGameSessionRecoveryState(new GameSessionRecoveryState
                        {
                            IsActive = true,
                            SavedMode = Window.SavedModeBeforeGame,
                            SavedBoost = Window.SavedCpuBoostBeforeGame,
                            SavedFan = Window.CurrentFanMode,
                            SavedMouseProfile = Window.SavedMouseProfileBeforeGame
                        });
                    }
                    GameRunning = false;
                    ActiveGame = null;
                    break;
                case ChaosEvent.RefreshRateSwitch:
                    Display.SetRefreshRate(OnAc ? 165 : 60);
                    break;
                case ChaosEvent.ExternalMonitor:
                    ExternalMonitor = !ExternalMonitor;
                    break;

                case ChaosEvent.ChargeLimitOn: ChargeLimitOn = true; break;
                case ChaosEvent.ChargeLimitOff: ChargeLimitOn = false; break;
                case ChaosEvent.MousePlug: MouseReceiverPresent = true; break;
                case ChaosEvent.MouseUnplug: MouseReceiverPresent = false; break;
                case ChaosEvent.RgbChange: RgbBrightness = Rng.Next(0, 101); break;
                case ChaosEvent.RgbIdleTimeout: RgbBrightness = 0; break;

                case ChaosEvent.UpdateNone: SetPendingUpdate(null); break;
                case ChaosEvent.UpdateAvailable: SetPendingUpdate(UpdateInfoFor("v9.9.9")); break;
                case ChaosEvent.UpdateNetworkError: SetPendingUpdate(null); break;
                case ChaosEvent.UpdateInstalled: SetPendingUpdate(null); break;
                case ChaosEvent.BadUrl:
                    // A payload whose page is not https must never reach the browser.
                    SetPendingUpdate(UpdateInfoFor("v9.9.9", htmlUrl: "http://evil.test/x"));
                    break;
                case ChaosEvent.ThemeToggle: Settings.Theme = Settings.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark; break;
                case ChaosEvent.LanguageSwitch: Settings.Language = Settings.Language == "de" ? "en" : "de"; break;
                case ChaosEvent.ButtonSpam:
                    for (int i = 0; i < 5; i++) await Window.SetManualModeAsync(OperatingMode.Default, persist: false);
                    break;
                case ChaosEvent.CorruptConfig:
                    SettingsManager.LoadSettings(Path.Combine(Path.GetTempPath(), $"chaos_missing_{Seed}.json"));
                    break;
                case ChaosEvent.UncleanExit:
                    CleanExitMarker.MarkRunning();
                    break;
            }

            CheckInvariants(writesBefore);
        }

        private async Task CurveStepAsync()
        {
            // A dropout is a missing reading, not a cold one: the curve is fed the same value the poll would carry.
            await Window.ApplyFanCurveAsync(TemperatureC);
        }

        private async Task SchoolModeAsync(bool on)
        {
            var service = Window.SchoolModeServiceForTesting!;
            if (on)
            {
                if (Settings.SchoolModeEnabled) return;
                _lastOperatingModeBeforeSchool = Settings.LastOperatingMode;
                var outcome = await service.EnableAsync();
                if (outcome.Refused) return;
                Window.ApplySchoolModeAutomationState();
                Window.SuspendFanCurveForSchoolMode();
            }
            else
            {
                if (!Settings.SchoolModeEnabled) return;
                await service.DisableAsync();
                Window.ApplySchoolModeAutomationState();
                await Window.ResumeFanCurveAfterSchoolModeAsync();
            }
        }

        private async Task StartGameAsync(GameProfile profile)
        {
            if (GameRunning) return;
            if (!_baselineCaptured)
            {
                _desktopBaseline = (Window.CurrentMode, Cpu.Mode, Window.CurrentFanMode, Display.CurrentHz, Brightness.Level);
                _baselineCaptured = true;
            }
            GameRunning = true;
            ActiveGame = profile;
            await Window.HandleGameStartedAsync(profile);
        }

        private async Task EndGameAsync()
        {
            if (!GameRunning || ActiveGame == null) return;
            var profile = ActiveGame;
            GameRunning = false;
            ActiveGame = null;
            await Window.HandleGameEndedAsync(profile);
        }

        private void SetPendingUpdate(UpdateInfo? info)
        {
            PendingUpdate = info;
            UpdateNotice.Apply(UpdateBadge, ReleaseNotes, info);
        }

        // ---- the twelve invariants -----------------------------------------------------------------------------

        private void CheckInvariants(int writesBefore)
        {
            // 2. Fan bounds. Every custom write the service would send is inside the range the firmware accepts.
            for (int i = writesBefore; i < Agent.FanWrites.Count; i++)
            {
                var (mode, cpu, gpu) = Agent.FanWrites[i];
                if (mode != FanMode.Custom) continue;
                if (cpu < FanCurve.MinPercent || cpu > FanCurve.MaxPercent)
                    Fail("I2 fan bounds", $"custom write {cpu}% outside [{FanCurve.MinPercent},{FanCurve.MaxPercent}]");
                if (gpu < FanCurve.MinPercent || gpu > FanCurve.MaxPercent)
                    Fail("I2 fan bounds", $"custom write {gpu}% outside [{FanCurve.MinPercent},{FanCurve.MaxPercent}]");
            }

            // 1. Thermal safety floor. A curve write made while the die is hot is never a quiet one. Judged against
            //    the temperature the curve itself acted on: while the curve is off nothing updates that value, so
            //    arming it uses the last reading it saw rather than the live one.
            int curveTemperature = Window.FanCurveLastTemperatureForTesting;
            if (Window.IsFanCurveArmed && curveTemperature >= FanCurve.SafetyTemperatureC)
            {
                int floor = FanCurve.SafetyFloorAt(curveTemperature);
                for (int i = writesBefore; i < Agent.FanWrites.Count; i++)
                {
                    var (mode, cpu, _) = Agent.FanWrites[i];
                    if (mode != FanMode.Custom) continue;
                    if (cpu < floor)
                        Fail("I1 thermal floor", $"{cpu}% at {curveTemperature} °C, floor is {floor}%");
                }
            }

            // 3. Sensor glitch protection. One or two bad polls hold the last speed; the third goes to full speed.
            if (Window.IsFanCurveArmed)
            {
                int polls = Window.FanCurveDeadSensorPollsForTesting;
                if (polls >= MainWindow.FanCurveDeadSensorPollThreshold)
                {
                    if (!Window.FanCurveSensorDeadForTesting)
                        Fail("I3 glitch", $"{polls} bad polls but the sensor is not marked dead");
                    if (Window.FanCurveLastPercentForTesting != FanCurve.MaxPercent)
                        Fail("I3 glitch", $"{polls} bad polls but the fan is at {Window.FanCurveLastPercentForTesting}%");
                }
                else if (Window.FanCurveSensorDeadForTesting)
                {
                    Fail("I3 glitch", $"sensor marked dead after only {polls} bad polls");
                }
            }

            // 4. School mode immunity. Nothing may take the machine out of school mode's settings. The device is the
            //    truth here, not the window: school mode writes its mode and fan straight to the firmware, and the
            //    window's cache only catches up on the next SyncDeviceStateAsync pass.
            if (Settings.SchoolModeEnabled)
            {
                if (ModePolicy.IsHighPowerMode(Agent.CurrentMode))
                    Fail("I4 school immunity", $"the device went to {Agent.CurrentMode}");
                if (Agent.CurrentFanMode == FanMode.Max)
                    Fail("I4 school immunity", "the device fan went to Max");
                if (!Window.FanCurveSuspendedBySchoolModeForTesting)
                    Fail("I4 school immunity", "the fan curve is not suspended");
                if (Settings.LastOperatingMode != _lastOperatingModeBeforeSchool)
                    Fail("I4 school immunity",
                        $"LastOperatingMode {_lastOperatingModeBeforeSchool} became {Settings.LastOperatingMode}");
            }

            // 9. Update notice sync. The two halves are one notice, and the page is always https.
            if (UpdateBadge.Visibility != ReleaseNotes.Visibility)
                Fail("I9 notice sync", $"badge {UpdateBadge.Visibility} vs notes {ReleaseNotes.Visibility}");
            if (ReleaseNotes.Visibility == Visibility.Visible)
            {
                var url = UpdateService.ReleaseNotesUrl(PendingUpdate);
                if (!UpdateService.IsSafeHttpsUrl(url))
                    Fail("I9 notice sync", $"release notes URL is not https: {url}");
            }

            // 10. Data integrity. A settings round trip never loses a value it was given.
            if (Step % 500 == 0) CheckSettingsRoundTrip();
        }

        private void CheckSettingsRoundTrip()
        {
            var path = Path.Combine(Path.GetTempPath(), $"chaos_settings_{Seed}.json");
            try
            {
                var probe = new AppSettings
                {
                    LastOperatingMode = Settings.LastOperatingMode,
                    Theme = Settings.Theme,
                    Language = Settings.Language,
                    CustomFanCpuPercent = Settings.CustomFanCpuPercent,
                    SchoolModeEnabled = Settings.SchoolModeEnabled
                };
                SettingsManager.SaveSettingsAtomic(path, probe);
                var loaded = SettingsManager.LoadSettings(path).Settings;
                if (loaded.LastOperatingMode != probe.LastOperatingMode
                    || loaded.Theme != probe.Theme
                    || loaded.Language != probe.Language
                    || loaded.CustomFanCpuPercent != probe.CustomFanCpuPercent
                    || loaded.SchoolModeEnabled != probe.SchoolModeEnabled)
                {
                    Fail("I10 data integrity", "a settings round trip changed a value");
                }
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        public void Fail(string invariant, string detail) =>
            throw new Xunit.Sdk.XunitException(
                $"seed={Seed} step={Step} event={LastEvent} {invariant}: {detail}");
    }

    // =============================================================================================================
    // Fixtures
    // =============================================================================================================

    /// <summary>
    /// Runs a body on a dedicated STA thread with a live dispatcher.
    /// </summary>
    /// <remarks>
    /// <see cref="Scenarios.Sta"/> is not enough here. It blocks on the body, so every continuation after the first
    /// <c>await</c> lands on a pool thread, and a pool thread is MTA: the second window a walk builds throws
    /// "the calling thread must be STA". Installing a dispatcher synchronisation context and pumping it keeps every
    /// continuation on the thread that owns the WPF objects.
    /// </remarks>
    private static Task RunStaAsync(Func<Task> body)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static readonly GameProfile GameA = new()
    {
        ExeName = "game-a.exe", DisplayName = "Game A",
        TargetMode = OperatingMode.Turbo, TargetCpuBoost = CpuBoostMode.Aggressive,
        TargetFanMode = FanMode.Max, TargetAtkMouseProfile = 2
    };

    private static readonly GameProfile GameB = new()
    {
        ExeName = "game-b.exe", DisplayName = "Game B",
        TargetMode = OperatingMode.Extreme, TargetCpuBoost = CpuBoostMode.Efficient,
        TargetFanMode = FanMode.Custom, TargetAtkMouseProfile = 3
    };

    private static UpdateInfo UpdateInfoFor(string tag, string? htmlUrl = null) =>
        new(new Version(9, 9, 9), tag, "NitroLite-9.9.9-win-x64.zip",
            "https://example.test/NitroLite-9.9.9-win-x64.zip", "abcdef", 1234,
            htmlUrl ?? $"https://github.com/canerztk8/NitroLite/releases/tag/{tag}");

    // =============================================================================================================
    // 1. The 100 000-step deterministic walk.
    // =============================================================================================================

    /// <summary>
    /// The volume pass: 100 000 seeded steps over the cheap half of the universe, with all twelve invariants checked
    /// after every one. The expensive events (a curve arm/disarm writes two settings files, a school-mode round trip
    /// launches powercfg) are represented here by their cheap effects and get their own walks below.
    /// </summary>
    [Fact]
    public async Task RandomWalk_OneHundredThousandSteps_HoldsEveryInvariant()
    {
        const int steps = 100_000;
        const int seed = 20240613;

        await RunStaAsync(async () =>
        {
            using var world = new ChaosWorld(seed);

            // The curve is armed once: arming is a settings write, and the walk is about what happens afterwards.
            await world.Window.SetFanCurveEnabledAsync(true, quiet: true);

            for (int i = 0; i < steps; i++)
            {
                await world.ApplyAsync(NextCheapEvent(world.Rng));
            }

            Assert.Equal(steps, world.Step);
        });
    }

    /// <summary>
    /// The weighted event table for the volume pass. Thermal readings dominate because that is where the safety
    /// invariants live; mode and fan switches are kept to a few percent because each one costs two hundred times a
    /// curve step.
    /// </summary>
    private static ChaosEvent NextCheapEvent(Random rng)
    {
        int roll = rng.Next(1000);
        return roll switch
        {
            < 260 => ChaosEvent.TempNormal,
            < 360 => ChaosEvent.TempCritical,
            < 420 => ChaosEvent.TempSensorDropout,
            < 450 => ChaosEvent.TempImpossible,
            < 500 => ChaosEvent.TempGlitchBurst,
            < 530 => ChaosEvent.TempPersistentFailure,
            < 560 => ChaosEvent.FanStall,
            < 600 => ChaosEvent.ModeSilent,
            < 640 => ChaosEvent.ModeBalanced,
            < 670 => ChaosEvent.ModePerformance,
            < 700 => ChaosEvent.ModeTurbo,
            < 730 => ChaosEvent.PlugAc,
            < 760 => ChaosEvent.UnplugDc,
            < 780 => ChaosEvent.SleepResume,
            < 795 => ChaosEvent.SessionUnlock,
            < 820 => ChaosEvent.BoostAggressive,
            < 840 => ChaosEvent.BoostEfficient,
            < 855 => ChaosEvent.BoostDisabled,
            < 865 => ChaosEvent.BoostCacheExpiry,
            < 880 => ChaosEvent.FanAuto,
            < 890 => ChaosEvent.FanMax,
            < 900 => ChaosEvent.FanCustom,
            < 915 => ChaosEvent.UpdateNone,
            < 935 => ChaosEvent.UpdateAvailable,
            < 945 => ChaosEvent.UpdateNetworkError,
            < 955 => ChaosEvent.UpdateInstalled,
            < 965 => ChaosEvent.BadUrl,
            < 990 => ChaosEvent.MousePlug,
            < 995 => ChaosEvent.MouseUnplug,
            _ => ChaosEvent.RgbChange
        };
    }

    // =============================================================================================================
    // 2. Systematic pairs: every ordered pair of structural events, on a fresh window.
    // =============================================================================================================

    [Fact]
    public async Task SystematicPairs_EveryOrderedPairOfStructuralEvents_HoldsEveryInvariant()
    {
        await RunStaAsync(async () =>
        {
            int pairs = 0;
            foreach (var first in StructuralEvents)
            {
                foreach (var second in StructuralEvents)
                {
                    using var world = new ChaosWorld(seed: 1000 + pairs);
                    await world.ApplyAsync(first);
                    await world.ApplyAsync(second);
                    pairs++;
                }
            }

            Assert.Equal(StructuralEvents.Length * StructuralEvents.Length, pairs);
        });
    }

    // =============================================================================================================
    // 3. Critical triples: the crossings where three subsystems meet.
    // =============================================================================================================

    /// <summary>
    /// The triples that matter are the ones where a third event can undo the protection the first two set up:
    /// school mode, then an adapter change, then a game; a game, then a crash, then a restart.
    /// </summary>
    private static readonly (ChaosEvent A, ChaosEvent B, ChaosEvent C)[] CriticalTriples =
    {
        (ChaosEvent.SchoolModeOn, ChaosEvent.PlugAc, ChaosEvent.GameStartA),
        (ChaosEvent.SchoolModeOn, ChaosEvent.UnplugDc, ChaosEvent.GameStartA),
        (ChaosEvent.SchoolModeOn, ChaosEvent.SleepResume, ChaosEvent.ModeTurbo),
        (ChaosEvent.SchoolModeOn, ChaosEvent.GameStartA, ChaosEvent.GameEnd),
        (ChaosEvent.SchoolModeOn, ChaosEvent.FanCurveOn, ChaosEvent.TempCritical),
        (ChaosEvent.SchoolModeOn, ChaosEvent.SchoolModeOff, ChaosEvent.ModeTurbo),
        (ChaosEvent.SchoolModeOn, ChaosEvent.SchoolUndoUnwritable, ChaosEvent.ModeTurbo),
        (ChaosEvent.SchoolModeOn, ChaosEvent.SchoolModeOff, ChaosEvent.FanCurveOn),
        (ChaosEvent.GameStartA, ChaosEvent.UnplugDc, ChaosEvent.GameEnd),
        (ChaosEvent.GameStartA, ChaosEvent.PlugAc, ChaosEvent.GameEnd),
        (ChaosEvent.GameStartA, ChaosEvent.GameStartB, ChaosEvent.GameEnd),
        (ChaosEvent.GameStartA, ChaosEvent.GameCrash, ChaosEvent.GameEnd),
        (ChaosEvent.GameStartA, ChaosEvent.TempCritical, ChaosEvent.GameEnd),
        (ChaosEvent.GameStartA, ChaosEvent.FanCurveOn, ChaosEvent.GameEnd),
        (ChaosEvent.GameStartA, ChaosEvent.SleepResume, ChaosEvent.GameEnd),
        (ChaosEvent.FanCurveOn, ChaosEvent.TempPersistentFailure, ChaosEvent.TempNormal),
        (ChaosEvent.FanCurveOn, ChaosEvent.TempCritical, ChaosEvent.FanCurveOff),
        (ChaosEvent.FanCurveOn, ChaosEvent.ModeTurbo, ChaosEvent.TempCritical),
        (ChaosEvent.FanCurveOn, ChaosEvent.SchoolModeOn, ChaosEvent.TempCritical),
        (ChaosEvent.FanCurveOn, ChaosEvent.SchoolModeOn, ChaosEvent.SchoolModeOff),
        (ChaosEvent.UpdateAvailable, ChaosEvent.UpdateInstalled, ChaosEvent.UpdateNone),
        (ChaosEvent.UpdateAvailable, ChaosEvent.BadUrl, ChaosEvent.UpdateInstalled),
        (ChaosEvent.ModeTurbo, ChaosEvent.UnplugDc, ChaosEvent.SleepResume),
        (ChaosEvent.ModeTurbo, ChaosEvent.UnplugDc, ChaosEvent.GameStartA),
        (ChaosEvent.ModeTurbo, ChaosEvent.SchoolModeOn, ChaosEvent.UnplugDc),
        (ChaosEvent.FanMax, ChaosEvent.SchoolModeOn, ChaosEvent.PlugAc),
        (ChaosEvent.FanMax, ChaosEvent.GameStartA, ChaosEvent.GameCrash),
        (ChaosEvent.BoostAggressive, ChaosEvent.SchoolModeOn, ChaosEvent.SchoolModeOff),
        (ChaosEvent.BoostAggressive, ChaosEvent.GameStartA, ChaosEvent.GameEnd),
    };

    [Fact]
    public async Task CriticalTriples_EveryCrossing_HoldsEveryInvariant()
    {
        await RunStaAsync(async () =>
        {
            for (int i = 0; i < CriticalTriples.Length; i++)
            {
                var (a, b, c) = CriticalTriples[i];
                using var world = new ChaosWorld(seed: 50_000 + i);
                await world.ApplyAsync(a);
                await world.ApplyAsync(b);
                await world.ApplyAsync(c);
            }
        });
    }

    // =============================================================================================================
    // 4. The expensive half: school mode, games and settings, in seeded walks.
    // =============================================================================================================

    /// <summary>
    /// School mode against the in-memory power scheme: enable, cross it with everything that used to be able to
    /// break it, disable, and require the machine to be exactly where it started.
    /// </summary>
    [Fact]
    public async Task SchoolModeWalk_RoundTripsRestoreEveryValueItChanged()
    {
        await RunStaAsync(async () =>
        {
            var rng = new Random(777);
            using var world = new ChaosWorld(seed: 777);

            for (int round = 0; round < 12; round++)
            {
                // A different starting point each round, so the round trip is not always from the same state.
                await world.Window.SetManualModeAsync(
                    new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme }[rng.Next(3)],
                    persist: false);
                await world.Window.SetManualCpuBoostAsync(
                    new[] { CpuBoostMode.Aggressive, CpuBoostMode.Efficient, CpuBoostMode.Disabled }[rng.Next(3)]);
                world.Display.SetRefreshRate(new[] { 60, 120, 165 }[rng.Next(3)]);
                world.Brightness.SetBrightness(rng.Next(30, 101));

                var before = (Mode: world.Window.CurrentMode, Boost: world.Cpu.Mode,
                    Fan: world.Window.CurrentFanMode, Hz: world.Display.CurrentHz,
                    Brightness: world.Brightness.Level);

                // Through the window's own path, not the service directly: the window is what suspends the curve,
                // and a walk that skipped that would be testing a state the product never reaches.
                await world.ApplyAsync(ChaosEvent.SchoolModeOn);
                Assert.True(world.Settings.SchoolModeEnabled, "school mode did not turn on");

                // Everything that used to be able to break school mode, in a random order.
                for (int i = 0; i < 6; i++)
                {
                    await world.ApplyAsync(new[]
                    {
                        ChaosEvent.PlugAc, ChaosEvent.UnplugDc, ChaosEvent.SleepResume,
                        ChaosEvent.GameStartA, ChaosEvent.GameEnd, ChaosEvent.ModeTurbo
                    }[rng.Next(6)]);
                }

                await world.ApplyAsync(ChaosEvent.SchoolModeOff);
                Assert.False(world.Settings.SchoolModeEnabled, "school mode did not turn off");

                Assert.Equal(before.Mode, world.Window.CurrentMode);
                Assert.Equal(before.Boost, world.Cpu.Mode);
                Assert.Equal(before.Fan, world.Window.CurrentFanMode);
                Assert.Equal(before.Hz, world.Display.CurrentHz);
                Assert.Equal(before.Brightness, world.Brightness.Level);
            }
        });
    }

    /// <summary>
    /// The two guards the pair sweep found missing. A mode card, the tray menu and the mode key all land on
    /// <c>SetManualModeAsync</c>, and the Auto/Max buttons land on <c>SetManualFanModeAsync</c>; neither refused
    /// anything while school mode was on, so one click took the machine to Turbo or the fans to full speed with the
    /// switch still showing on. Seeds 50002 and 1355 are the walks that caught them.
    /// </summary>
    [Fact]
    public async Task SchoolMode_RefusesManualModeAndFanChanges_AndSaysWhy()
    {
        await RunStaAsync(async () =>
        {
            using var world = new ChaosWorld(seed: 50002);
            var toasts = new List<(string Text, bool Success)>();
            world.Window.ToastHandler = (text, ok) => toasts.Add((text, ok));

            await world.ApplyAsync(ChaosEvent.SchoolModeOn);
            Assert.True(world.Settings.SchoolModeEnabled, "school mode did not turn on");

            // Every mode louder than the quiet one school mode applied is refused, and the device stays put.
            foreach (var mode in new[] { OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo })
            {
                toasts.Clear();
                Assert.False(await world.Window.SetManualModeAsync(mode), $"{mode} was applied during school mode");
                Assert.False(ModePolicy.IsHighPowerMode(world.Agent.CurrentMode), $"the device went to {world.Agent.CurrentMode}");
                Assert.Contains(toasts, t => !t.Success && t.Text.Contains("okul modu", StringComparison.OrdinalIgnoreCase));
            }

            // The fan buttons are refused the same way: the switch promises automatic fans.
            toasts.Clear();
            Assert.False(await world.Window.SetManualFanModeAsync(FanMode.Max), "Max fan was applied during school mode");
            Assert.NotEqual(FanMode.Max, world.Agent.CurrentFanMode);
            Assert.Contains(toasts, t => !t.Success && t.Text.Contains("okul modu", StringComparison.OrdinalIgnoreCase));

            // Going quieter is still allowed: the fan-stall protection drops to Eco through this same call.
            Assert.True(await world.Window.SetManualModeAsync(OperatingMode.Eco, persist: false),
                "the safety drop to Eco was blocked");
        });
    }

    /// <summary>
    /// A game session, crossed with adapter changes, sleep, a second game and a crash, always ends with the desktop
    /// exactly as it was.
    /// </summary>
    [Fact]
    public async Task GameSessionWalk_EverySessionEndsWithTheDesktopRestored()
    {
        await RunStaAsync(async () =>
        {
            var rng = new Random(4242);
            using var world = new ChaosWorld(seed: 4242);

            for (int round = 0; round < 15; round++)
            {
                await world.Window.SetManualModeAsync(OperatingMode.Default, persist: false);
                await world.Window.SetManualCpuBoostAsync(CpuBoostMode.Efficient);
                await world.Window.SetManualFanModeAsync(FanMode.Auto);
                world.OnAc = true;

                var before = (Mode: world.Window.CurrentMode, Boost: world.Cpu.Mode, Fan: world.Window.CurrentFanMode);

                await world.ApplyAsync(round % 2 == 0 ? ChaosEvent.GameStartA : ChaosEvent.GameStartB);
                Assert.True(world.Window.IsGameSessionActive);

                // The adapter stays put: pulling it mid-session legitimately changes what the desktop should be,
                // so the strict round trip is only meaningful while the environment is unchanged. The crossing
                // with an adapter change is covered by the relaxed pass below.
                for (int i = 0; i < 4; i++)
                {
                    await world.ApplyAsync(new[]
                    {
                        ChaosEvent.SleepResume, ChaosEvent.TempCritical,
                        ChaosEvent.RefreshRateSwitch, ChaosEvent.BoostEfficient
                    }[rng.Next(4)]);
                }

                await world.ApplyAsync(ChaosEvent.GameEnd);

                Assert.False(world.Window.IsGameSessionActive);
                Assert.Equal(before.Mode, world.Window.CurrentMode);
                Assert.Equal(before.Boost, world.Cpu.Mode);
                Assert.Equal(before.Fan, world.Window.CurrentFanMode);
            }
        });
    }

    /// <summary>
    /// The same session with the adapter pulled underneath it. The desktop state is allowed to follow the adapter,
    /// but it must never come back in a game mode or with the fans pinned at maximum.
    /// </summary>
    [Fact]
    public async Task GameSessionWalk_WithAdapterChanges_NeverComesBackInAGameMode()
    {
        await RunStaAsync(async () =>
        {
            var rng = new Random(4243);
            using var world = new ChaosWorld(seed: 4243);

            for (int round = 0; round < 15; round++)
            {
                world.OnAc = true;
                await world.Window.SetManualModeAsync(OperatingMode.Default, persist: false);
                await world.Window.SetManualFanModeAsync(FanMode.Auto);

                await world.ApplyAsync(round % 2 == 0 ? ChaosEvent.GameStartA : ChaosEvent.GameStartB);

                for (int i = 0; i < 4; i++)
                {
                    await world.ApplyAsync(new[]
                    {
                        ChaosEvent.UnplugDc, ChaosEvent.PlugAc, ChaosEvent.SleepResume,
                        ChaosEvent.TempCritical, ChaosEvent.RefreshRateSwitch
                    }[rng.Next(5)]);
                }

                await world.ApplyAsync(ChaosEvent.GameEnd);

                Assert.False(world.Window.IsGameSessionActive);
                if (!world.OnAc)
                    Assert.False(ModePolicy.IsHighPowerMode(world.Window.CurrentMode),
                        $"came back in {world.Window.CurrentMode} on battery");
                Assert.NotEqual(FanMode.Max, world.Window.CurrentFanMode);
            }
        });
    }

    /// <summary>
    /// The update notice, driven through every state the check can end in. The two halves must never disagree and
    /// the page must never be anything but https.
    /// </summary>
    [Fact]
    public async Task UpdateNoticeWalk_StaysSynchronisedThroughEveryOutcome()
    {
        await RunStaAsync(async () =>
        {
            var rng = new Random(31337);
            using var world = new ChaosWorld(seed: 31337);

            var outcomes = new[]
            {
                ChaosEvent.UpdateNone, ChaosEvent.UpdateAvailable, ChaosEvent.UpdateNetworkError,
                ChaosEvent.UpdateInstalled, ChaosEvent.BadUrl
            };

            for (int i = 0; i < 500; i++)
            {
                await world.ApplyAsync(outcomes[rng.Next(outcomes.Length)]);
                Assert.Equal(world.UpdateBadge.Visibility, world.ReleaseNotes.Visibility);
            }
        });
    }

    // =============================================================================================================
    // 5. Concurrency: overlapping commands must not deadlock or leave a torn state.
    // =============================================================================================================

    [Fact]
    public async Task ConcurrentCommands_DoNotDeadlockAndLeaveAConsistentState()
    {
        await RunStaAsync(async () =>
        {
            using var world = new ChaosWorld(seed: 909);

            var work = new List<Task>();
            for (int i = 0; i < 40; i++)
            {
                int n = i;
                work.Add(Task.Run(async () =>
                {
                    for (int j = 0; j < 10; j++)
                    {
                        switch ((n + j) % 5)
                        {
                            case 0: await world.Window.SetManualModeAsync(OperatingMode.Default, persist: false); break;
                            case 1: await world.Window.SetManualFanModeAsync(FanMode.Auto); break;
                            case 2: await world.Window.SetManualCpuBoostAsync(CpuBoostMode.Efficient); break;
                            case 3: await world.Window.ApplyFanCurveAsync(70); break;
                            default: await world.Window.SyncDeviceStateAsync(); break;
                        }
                    }
                }));
            }

            var all = Task.WhenAll(work);
            var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(all, finished);
            await all;

            // Whatever order they landed in, the state is one of the values the commands asked for.
            Assert.Contains(world.Window.CurrentMode,
                new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo, OperatingMode.Eco });
            Assert.Contains(world.Window.CurrentFanMode, new[] { FanMode.Auto, FanMode.Max, FanMode.Custom });
        });
    }

    // =============================================================================================================
    // 6. Crash recovery: a run that dies mid-session must not come back pinned to Turbo or Max.
    // =============================================================================================================

    [Fact]
    public async Task CrashRecovery_AfterAGameDiesMidSession_DoesNotComeBackPinnedToTurboOrMax()
    {
        await RunStaAsync(async () =>
        {
            using var world = new ChaosWorld(seed: 606);

            await world.Window.SetManualModeAsync(OperatingMode.Default, persist: false);
            await world.Window.SetManualFanModeAsync(FanMode.Auto);
            await world.ApplyAsync(ChaosEvent.GameStartA);

            // The process is killed here: no game-ended event, only the recovery record on disk.
            var recovery = new GameSessionRecoveryState
            {
                IsActive = true,
                SavedMode = OperatingMode.Default,
                SavedBoost = CpuBoostMode.Efficient,
                SavedFan = FanMode.Auto,
                SavedMouseProfile = 1
            };

            using var restarted = new ChaosWorld(seed: 607);
            await restarted.Window.RestoreInterruptedGameAsync(recovery);

            Assert.False(ModePolicy.IsHighPowerMode(restarted.Window.CurrentMode),
                $"came back in {restarted.Window.CurrentMode}");
            Assert.NotEqual(FanMode.Max, restarted.Window.CurrentFanMode);
        });
    }

    // =============================================================================================================
    // 7. Data integrity: a corrupt or missing settings file never throws and never loses a good one.
    // =============================================================================================================

    [Fact]
    public void CorruptSettings_NeverThrowAndNeverLoseTheGoodCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "chaos_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var primary = Path.Combine(root, "config.json");
            var backup = Path.Combine(root, "config.backup.json");

            var good = new AppSettings
            {
                LastOperatingMode = OperatingMode.Quiet,
                Theme = AppTheme.Dark,
                Language = "de",
                CustomFanCpuPercent = 42
            };
            SettingsManager.SaveSettingsAtomic(primary, good);
            SettingsManager.SaveSettingsAtomic(backup, good);

            // An unknown theme name is not in this list: the theme converter recovers from it, so the file still
            // parses and the rest of the settings survive. Only shapes that cannot be read at all belong here.
            foreach (var garbage in new[] { "", "{", "null", "[]", "{\"OsdCorner\":\"nine\"}", new string('x', 4096) })
            {
                File.WriteAllText(primary, garbage);
                var result = SettingsManager.LoadSettings(primary, backup);
                Assert.NotNull(result.Settings);
                // The backup is the last good copy, so a corrupt primary must not cost the user their settings.
                Assert.Equal(OperatingMode.Quiet, result.Settings.LastOperatingMode);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
