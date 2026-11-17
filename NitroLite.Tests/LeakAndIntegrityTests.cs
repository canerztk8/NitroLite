using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using NitroLite.Linux;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The P3/P4 findings: what a tray return used to rebuild, what a save used to lose, and what a hand-edited file used
/// to break.
/// </summary>
/// <remarks>
/// Every test here is a regression for a defect that was found by reading the code rather than by a crash: a second
/// overlay window per tray return, a settings file that could come back zero bytes after a power cut, a corrupt
/// presets file that was deleted instead of kept, a restore that reported success while a part of it had failed.
/// </remarks>
public class LeakAndIntegrityTests : IDisposable
{
    private readonly string _dir;

    public LeakAndIntegrityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NitroLiteIntegrity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    // ---------------------------------------------------------------- LEAK-01..04 / DATA-09

    [Fact]
    public void OneShotGate_LetsTheFirstCallerInAndNobodyElse()
    {
        var gate = new OneShotGate();

        Assert.False(gate.HasRun);
        Assert.True(gate.TryEnter());
        Assert.True(gate.HasRun);
        Assert.False(gate.TryEnter());
        Assert.False(gate.TryEnter());
    }

    [Fact]
    public async Task OneShotGate_UnderConcurrentCallers_ExactlyOneEnters()
    {
        // The window's setup is reachable from the tray launch and from Opened, and the two can arrive together. A
        // plain bool let both read false before either wrote true, which is how a second overlay window was built.
        var gate = new OneShotGate();
        int entered = 0;
        using var start = new ManualResetEventSlim();

        var callers = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            start.Wait();
            if (gate.TryEnter()) Interlocked.Increment(ref entered);
        })).ToArray();

        start.Set();
        await Task.WhenAll(callers);

        Assert.Equal(1, entered);
    }

    [Fact]
    public async Task MainViewModel_SecondInitialize_DoesNotReplayAGameSessionRecord()
    {
        // The damage a tray return did: the window's Opened handler ran the first read again, and the first read
        // replays a crash record. A game that started after the window opened had written one, so the second pass put
        // the pre-game mode and fan back over a game that was still running.
        var settings = new AppSettings();
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = new MainViewModel(settings, hardware, null, Path_("settings.json"));

        await vm.InitializeAsync();
        hardware.Writes.Clear();

        settings.GameSessionRecovery = new GameSessionRecoveryState
        {
            IsActive = true,
            SavedMode = OperatingMode.Quiet,
            SavedFan = FanMode.Auto
        };

        await vm.InitializeAsync();

        Assert.Empty(hardware.Writes);
        Assert.NotNull(settings.GameSessionRecovery);
    }

    // ---------------------------------------------------------------- DATA-01 / DATA-02 / DATA-10

    [Fact]
    public void AtomicFile_RoundTripsAndLeavesNoTemporaryFile()
    {
        var path = Path_("settings.json");

        AtomicFile.WriteAllText(path, "{\"a\":1}");

        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp.*"));
    }

    [Fact]
    public void AtomicFile_OverwritesAnExistingFileWithoutLeavingTheOldContent()
    {
        var path = Path_("settings.json");
        File.WriteAllText(path, "old and much longer than the new content");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp.*"));
    }

    [Fact]
    public void AtomicFile_CreatesAMissingFolder()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "settings.json");

        AtomicFile.WriteAllText(path, "x");

        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void AtomicFile_WritesUtf8WithoutAByteOrderMark()
    {
        // The readers expect what File.WriteAllText produced: no BOM. A BOM here would make the first key of the
        // settings file unreadable to a parser that does not strip it.
        var path = Path_("settings.json");

        AtomicFile.WriteAllText(path, "{\"dil\":\"Türkçe\"}");

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains("Türkçe", File.ReadAllText(path), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- DATA-21

    [Theory]
    // A manual launch never starts hidden, whatever the stored preference says: an icon that appears to do nothing is
    // the one complaint this rule must not create.
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    // The sign-in entry carries --minimized only while the box is checked, so the flag is authoritative.
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    // A sign-in entry registered before the flag existed still honours the stored preference.
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void StartupOptions_ShouldStartHidden_OnlyHidesASignInLaunchThatAskedForIt(
        bool autoStart, bool minimized, bool preference, bool expected)
    {
        var options = new StartupOptions { IsAutoStart = autoStart, StartMinimized = minimized };

        Assert.Equal(expected, StartupOptions.ShouldStartHidden(options, preference));
    }

    [Fact]
    public void StartupOptions_ShouldStartHidden_WithNoOptionsAtAll_ShowsTheWindow()
    {
        Assert.False(StartupOptions.ShouldStartHidden(null, startInTrayPreference: true));
    }

    // ---------------------------------------------------------------- DATA-12 / DATA-13

    [Fact]
    public void RgbProfileService_WithACorruptFile_QuarantinesItAndKeepsTheFactoryPresets()
    {
        // The file is the only copy of the user's own presets. Deleting it threw away something that may be
        // repairable by hand; leaving it in place meant the next save overwrote it with the factory list.
        var path = Path_("rgb_presets.json");
        File.WriteAllText(path, "{ this is not json");

        var service = new RgbProfileService(path);

        Assert.True(File.Exists(path + ".bad"), "the unreadable file must be kept, not deleted");
        Assert.False(File.Exists(path));
        Assert.Equal("{ this is not json", File.ReadAllText(path + ".bad"));
        Assert.NotNull(service.LoadError);
        Assert.Contains(service.GetPresets(), p => p.IsBuiltIn);
    }

    [Fact]
    public void RgbProfileService_SaveCustomPreset_SanitizesWhatItWrites()
    {
        // The page's own controls cannot produce these values, but the page is not the only caller and the preset is
        // read back on the next launch.
        var path = Path_("rgb_presets.json");
        var service = new RgbProfileService(path);

        service.SaveCustomPreset("Mine", "NOT_AN_EFFECT", brightness: 99, speed: 0, direction: 7,
            color: "not a colour", leds: new List<string> { "nope" });

        var saved = service.GetPresets().Single(p => p.Name == "Mine");
        Assert.Equal("STATIC", saved.Effect);
        Assert.Equal(9, saved.Brightness);
        Assert.Equal(LightingLimits.MinSpeed, saved.Speed);
        Assert.Equal(LightingPolicy.DefaultColor, saved.Color);
        Assert.Equal(4, saved.LEDs.Count);

        // And the same values are what reached the file, not just the in-memory copy.
        var reloaded = new RgbProfileService(path).GetPresets().Single(p => p.Name == "Mine");
        Assert.Equal("STATIC", reloaded.Effect);
        Assert.Equal(LightingLimits.MinSpeed, reloaded.Speed);
    }

    // ---------------------------------------------------------------- DATA-14

    [Fact]
    public void LightingPolicy_Sanitize_ReplacesAnEffectTheKeyboardDoesNotHave()
    {
        // The firmware rejects the write and the keyboard keeps whatever it had, so the page would show an effect
        // that is not on the keys.
        var sanitized = LightingPolicy.Sanitize(new LightingConfig { Effect = "DISCO", Brightness = 5, Speed = 3 });

        Assert.NotNull(sanitized);
        Assert.Equal("STATIC", sanitized!.Effect);
    }

    [Fact]
    public void LightingPolicy_Sanitize_KeepsEveryEffectTheKeyboardDoesHave()
    {
        foreach (var effect in LightingPolicy.AllEffects)
        {
            var sanitized = LightingPolicy.Sanitize(new LightingConfig { Effect = effect, Brightness = 5, Speed = 3 });
            Assert.Equal(effect, sanitized!.Effect);
        }
    }

    // ---------------------------------------------------------------- DATA-15

    [Fact]
    public void BatteryWearHistory_Load_DropsRowsThatCannotBeDrawn()
    {
        // One unusable row turns the whole trend into a line that is not a number, and the chart then draws nothing
        // at all — which reads as "the history is empty" rather than "one row is broken".
        var path = Path_("battery_wear_history.json");
        File.WriteAllText(path, """
        [
          {"utc":"2026-01-01T00:00:00Z","health":92.5,"wear":7.5,"full_mwh":50000,"design_mwh":54000,"cycles":12},
          {"utc":"2026-01-02T00:00:00Z","health":150,"wear":7.5,"full_mwh":50000,"design_mwh":54000,"cycles":12},
          {"utc":"2026-01-03T00:00:00Z","health":92,"wear":-5,"full_mwh":50000,"design_mwh":54000,"cycles":12},
          {"utc":"2026-01-04T00:00:00Z","health":92,"wear":8,"full_mwh":-1,"design_mwh":54000,"cycles":12},
          {"utc":"2026-01-05T00:00:00Z","health":92,"wear":8,"full_mwh":50000,"design_mwh":54000,"cycles":-3},
          {"utc":"0001-01-01T00:00:00Z","health":92,"wear":8,"full_mwh":50000,"design_mwh":54000,"cycles":12}
        ]
        """);

        var samples = new BatteryWearHistory(path).GetSamples();

        var only = Assert.Single(samples);
        Assert.Equal(92.5, only.HealthPercent);
    }

    [Fact]
    public void BatteryWearHistory_Load_WithANaNLiteral_ReturnsAnEmptyHistoryInsteadOfThrowing()
    {
        // NaN is not valid JSON, so the parser rejects the file before the finiteness check sees it. What matters is
        // that the page comes up with an empty history rather than an exception.
        var path = Path_("battery_wear_history.json");
        File.WriteAllText(path,
            "[{\"utc\":\"2026-01-01T00:00:00Z\",\"health\":NaN,\"wear\":7.5,\"full_mwh\":50000,\"design_mwh\":54000}]");

        var samples = new BatteryWearHistory(path).GetSamples();

        Assert.Empty(samples);
    }

    [Fact]
    public void BatteryWearHistory_Load_CapsTheHistoryAtMaxSamples()
    {
        // A file written by an older build, or by hand, could hold thousands of rows, and the chart draws every one.
        var path = Path_("battery_wear_history.json");
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, BatteryWearHistory.MaxSamples + 100).Select(i =>
            "{\"utc\":\"" + start.AddDays(i).ToString("yyyy-MM-ddTHH:mm:ssZ") +
            "\",\"health\":90,\"wear\":10,\"full_mwh\":50000,\"design_mwh\":54000,\"cycles\":" + i + "}");
        File.WriteAllText(path, "[" + string.Join(",", rows) + "]");

        var samples = new BatteryWearHistory(path).GetSamples();

        Assert.Equal(BatteryWearHistory.MaxSamples, samples.Count);
        // The newest rows are the ones kept: the trend is read from the end.
        Assert.Equal(start.AddDays(BatteryWearHistory.MaxSamples + 99), samples[^1].TimestampUtc);
    }

    // ---------------------------------------------------------------- DATA-11

    [Fact]
    public void BackupService_Restore_WhenAPartCannotBeWritten_ReportsFailureAndKeepsThePreRestoreCopy()
    {
        // A part that was in the backup and could not be written is a partial restore. Reporting it as a success is
        // what let a user believe their game rules had come back when they had not.
        var settingsPath = Path_("settings.json");
        File.WriteAllText(settingsPath, "{\"Language\":\"de\"}");

        var sourceProfiles = Path_("game_profiles.json");
        File.WriteAllText(sourceProfiles, "[{\"exe_name\":\"game.exe\",\"display_name\":\"Game\"}]");

        var backupPath = Path_("backup.json");
        var created = BackupService.Create(backupPath, new AppSettings { Language = "es" },
            gameProfilesPath: sourceProfiles, rgbPresetsPath: null, appVersion: "test");
        Assert.True(created.Success, created.Error);
        Assert.Contains(created.Written, part => part.Contains("Oyun", StringComparison.Ordinal));

        // A file where the game-profiles folder should be: the write cannot create the directory, so that part fails
        // while the settings part succeeds.
        var blocker = Path_("blocker");
        File.WriteAllText(blocker, "not a folder");

        var result = BackupService.Restore(backupPath, settingsPath,
            gameProfilesPath: Path.Combine(blocker, "game_profiles.json"), rgbPresetsPath: null);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Failed);
        Assert.NotNull(result.Error);

        // The settings that were in use before the restore are still on disk next to the file.
        var preRestore = settingsPath + BackupService.PreRestoreSuffix;
        Assert.True(File.Exists(preRestore), "a restore must leave the settings it replaced");
        Assert.Contains("de", File.ReadAllText(preRestore), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- DATA-04 / DATA-17

    [Fact]
    public void AppSettings_Normalize_RepairsNullCapabilityMaps()
    {
        // Both maps come out of a file a user can edit and a crash can truncate. A null here reached a dictionary
        // lookup and a list add on the first capability read, so a hand-edited file took the whole page down.
        var settings = new AppSettings
        {
            CapabilitySessionFailures = null!,
            DisabledCapabilities = null!
        };

        settings.Normalize();

        Assert.NotNull(settings.CapabilitySessionFailures);
        Assert.NotNull(settings.DisabledCapabilities);
    }

    [Fact]
    public void AppSettings_Normalize_DropsCapabilityEntriesThatMeanNothing()
    {
        var settings = new AppSettings
        {
            CapabilitySessionFailures = new Dictionary<string, int>
            {
                ["FanControl"] = 2,
                ["Negative"] = -1,
                ["  "] = 1
            },
            DisabledCapabilities = new List<string> { "FanControl", "", "   " }
        };

        settings.Normalize();

        Assert.Equal(new[] { "FanControl" }, settings.CapabilitySessionFailures.Keys.ToArray());
        Assert.Equal(new[] { "FanControl" }, settings.DisabledCapabilities.ToArray());
    }

    [Fact]
    public void AppSettings_Normalize_RepairsAnOutOfRangeGameSessionRecord()
    {
        // The record is replayed straight to the firmware on the next launch, so an enum out of range would be the
        // one hardware write that is a guess.
        var settings = new AppSettings
        {
            GameSessionRecovery = new GameSessionRecoveryState
            {
                IsActive = true,
                SavedMode = (OperatingMode)99,
                SavedFan = (FanMode)42,
                SavedBoost = (CpuBoostMode)(-7),
                SavedAcBoost = (CpuBoostMode)77,
                SavedDcBoost = (CpuBoostMode)88,
                SavedMouseProfile = 9,
                Timestamp = default
            }
        };

        settings.Normalize();

        var recovery = settings.GameSessionRecovery!;
        Assert.Equal(OperatingMode.Default, recovery.SavedMode);
        Assert.Equal(FanMode.Auto, recovery.SavedFan);
        Assert.Equal(CpuBoostMode.Aggressive, recovery.SavedBoost);
        Assert.Null(recovery.SavedAcBoost);
        Assert.Null(recovery.SavedDcBoost);
        Assert.Equal(0, recovery.SavedMouseProfile);
        Assert.NotEqual(default, recovery.Timestamp);
    }

    // ---------------------------------------------------------------- DATA-18

    [Fact]
    public void LinuxKeyboardProtocol_BuildFourZone_ClampsASpeedNoSliderCanProduce()
    {
        // A speed of zero is a value no slider can produce, so one arriving here came from a hand-edited or
        // half-written settings file, and writing it would leave the keyboard on a level the app cannot restore.
        var line = LinuxKeyboardProtocol.BuildFourZone("WAVE", speed: 0, brightnessLevel: 5, direction: 1, color: "#FF0000");

        var fields = line.Split(',');
        Assert.Equal(LightingLimits.MinSpeed.ToString(), fields[1]);
    }

    [Fact]
    public void LinuxKeyboardProtocol_BuildFourZone_ClampsASpeedAboveTheRange()
    {
        var line = LinuxKeyboardProtocol.BuildFourZone("WAVE", speed: 99, brightnessLevel: 5, direction: 1, color: "#FF0000");

        Assert.Equal(LightingLimits.MaxSpeed.ToString(), line.Split(',')[1]);
    }

    // ---------------------------------------------------------------- DATA-06 / DATA-07 / DATA-08

    [Fact]
    public async Task UpdateNow_WithAPackagedBuildAndAVerifiedRelease_ReachesTheInstallPipeline()
    {
        // The button used to open the release page and stop there, whatever the check had found. A packaged build
        // with a published digest has a real pipeline behind it.
        var updates = new Phase3Fakes.FakeUpdates
        {
            IsPackaged = true,
            CheckResult = new UpdateCheckResult(
                "v9.9.9", "https://example.invalid/notes", "abc123",
                "https://example.invalid/NitroLite.zip", "NitroLite.zip", 1024),
            InstallResult = UpdateInstallResult.Installed("/tmp/NitroLite")
        };

        using var vm = new MainViewModel(new AppSettings(), new Phase3Fakes.StubHardware(), null,
            Path_("settings.json"), updates: updates);

        await vm.InitializeAsync();
        vm.CheckUpdatesCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => vm.HasPendingUpdate);

        vm.UpdateNowCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => updates.InstallAttempts > 0);

        Assert.Equal(1, updates.InstallAttempts);
    }

    [Fact]
    public async Task UpdateNow_WithNoPublishedDigest_DoesNotInstallAnUnverifiableFile()
    {
        var updates = new Phase3Fakes.FakeUpdates
        {
            IsPackaged = true,
            CheckResult = new UpdateCheckResult(
                "v9.9.9", "https://example.invalid/notes", Sha256: null,
                "https://example.invalid/NitroLite.zip", "NitroLite.zip", 1024)
        };

        using var vm = new MainViewModel(new AppSettings(), new Phase3Fakes.StubHardware(), null,
            Path_("settings.json"), updates: updates);

        await vm.InitializeAsync();
        vm.CheckUpdatesCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => vm.HasPendingUpdate);

        vm.UpdateNowCommand.Execute(null);
        await Task.Delay(200);

        Assert.Equal(0, updates.InstallAttempts);
    }
}
