using System.IO;
using System.Text.Json;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// School mode is the one switch that writes to the Windows power scheme, so every test here checks both halves:
/// what it changes, and that turning it off puts the machine back exactly as it was.
/// </summary>
public class SchoolModeServiceTests
{
    /// <summary>An in-memory power scheme: no powrprof call, no dependency on the host's real plan.</summary>
    private sealed class FakePowerScheme : IPowerSchemeAccess
    {
        public Dictionary<(Guid SubGroup, Guid Setting), uint> Values { get; } = new();
        public List<(Guid SubGroup, Guid Setting, uint Value)> Writes { get; } = new();
        public bool FailWrites { get; set; }
        public bool FailReads { get; set; }
        public (Guid SubGroup, Guid Setting)? FailOnly { get; set; }
        public Guid? ActiveScheme { get; set; }

        public bool TryReadDc(Guid subGroup, Guid setting, out uint value)
        {
            if (FailReads)
            {
                value = 0;
                return false;
            }
            return Values.TryGetValue((subGroup, setting), out value);
        }

        public bool TryWriteDc(Guid subGroup, Guid setting, uint value)
        {
            if (FailWrites || FailOnly == (subGroup, setting)) return false;
            Values[(subGroup, setting)] = value;
            Writes.Add((subGroup, setting, value));
            return true;
        }
    }

    /// <summary>The shared mock, plus the exact battery boost read school mode records before it changes it.</summary>
    private sealed class SchoolModeCpuPower : MockCpuPowerService
    {
        public CpuBoostMode? DcBoost { get; set; } = CpuBoostMode.Aggressive;
        public override CpuBoostMode? QueryBoostModeExact(bool isAc) => isAc ? CurrentMode : DcBoost;
    }

    /// <summary>The shared mock, plus the modes a given firmware refuses.</summary>
    private sealed class SchoolModeAgent : MockAcerAgentClient
    {
        public HashSet<OperatingMode> Rejected { get; } = new();
        public List<OperatingMode> Attempts { get; } = new();

        public override Task<bool> SetOperatingModeAsync(OperatingMode mode)
        {
            Attempts.Add(mode);
            if (Rejected.Contains(mode)) return Task.FromResult(false);
            return base.SetOperatingModeAsync(mode);
        }
    }

    private sealed class Harness
    {
        public FakePowerScheme Scheme { get; } = new();
        public SchoolModeAgent Agent { get; } = new();
        public SchoolModeCpuPower Cpu { get; } = new();
        public TestDisplayRefreshRateServiceStub Display { get; } = new();
        public TestScreenBrightnessService Brightness { get; } = new();
        public AppSettings Settings { get; } = new();
        public SchoolModeService Service { get; }

        public Harness()
        {
            Service = new SchoolModeService(new AgentHardwareController(Agent, new CareCenterClient()), Cpu, Display, Brightness, Settings, Scheme)
            {
                // Inside the isolated data folder, which TestDataIsolation sweeps: a bare %TEMP% path left one
                // file behind per harness and nothing ever removed them.
                BackupPath = AppPaths.GetDataFile("school_" + Guid.NewGuid().ToString("N") + ".json"),
                GetCurrentOperatingMode = () => OperatingMode.Extreme,
                GetCurrentFanMode = () => FanMode.Max
            };
        }

        public SchoolModeBackup? ReadBackup()
        {
            var path = Service.BackupPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SchoolModeBackup>(File.ReadAllText(path));
        }

        public bool BackupExists => !string.IsNullOrEmpty(Service.BackupPath) && File.Exists(Service.BackupPath);

        /// <summary>Every setting school mode touches, already at a value it will have to replace.</summary>
        public Harness WithPreviousValues(uint previous = 100)
        {
            foreach (var setting in SchoolModeService.PowerSettings)
                Scheme.Values[(setting.SubGroup, setting.Setting)] = previous;
            return this;
        }
    }

    [Fact]
    public async Task EnableAsync_WritesEveryTargetDcValue()
    {
        var h = new Harness().WithPreviousValues();

        var outcome = await h.Service.EnableAsync();

        Assert.True(outcome.Success, string.Join(", ", outcome.Failed));
        foreach (var setting in SchoolModeService.PowerSettings)
            Assert.Equal(setting.TargetDc, h.Scheme.Values[(setting.SubGroup, setting.Setting)]);
    }

    [Fact]
    public async Task EnableAsync_RecordsThePreviousDcValuesBeforeChangingThem()
    {
        var h = new Harness().WithPreviousValues(previous: 37);

        await h.Service.EnableAsync();

        var backup = h.ReadBackup();
        Assert.NotNull(backup);
        Assert.Equal(SchoolModeService.PowerSettings.Count, backup!.PowerSettings.Count);
        Assert.All(backup.PowerSettings, saved => Assert.Equal(37u, saved.PreviousDc));
    }

    [Fact]
    public async Task EnableAsync_SetsEcoModeAutoFanSixtyHertzAndFortyPercentBrightness()
    {
        var h = new Harness().WithPreviousValues();

        await h.Service.EnableAsync();

        // Eco, not Quiet: school mode is the most economical the machine can be, and Eco is the lowest mode Acer
        // offers. Quiet is only the fallback for a model that does not list Eco.
        Assert.Equal(OperatingMode.Eco, h.Agent.CurrentMode);
        Assert.Equal(FanMode.Auto, h.Agent.CurrentFanMode);
        Assert.Equal(60, h.Display.GetCurrentRefreshRate());
        Assert.Equal(SchoolModeService.TargetBrightnessPercent, h.Brightness.Current);
        Assert.Contains(CpuBoostMode.Disabled, h.Cpu.BoostHistory);
    }

    [Fact]
    public async Task EnableAsync_WhenTheMachineDoesNotListEco_FallsBackToQuiet()
    {
        var h = new Harness().WithPreviousValues();
        h.Service.IsModeSupported = mode => mode != OperatingMode.Eco;

        await h.Service.EnableAsync();

        Assert.Equal(OperatingMode.Quiet, h.Agent.CurrentMode);
        Assert.DoesNotContain(OperatingMode.Eco, h.Agent.Attempts);
    }

    [Fact]
    public async Task EnableAsync_WhenTheFirmwareRefusesEco_FallsBackToQuiet()
    {
        var h = new Harness().WithPreviousValues();
        h.Agent.Rejected.Add(OperatingMode.Eco);

        var outcome = await h.Service.EnableAsync();

        Assert.Equal(OperatingMode.Quiet, h.Agent.CurrentMode);
        Assert.Contains(Loc.T("Çalışma modu (Sessiz)"), outcome.Applied);
        Assert.True(outcome.Success, string.Join(", ", outcome.Failed));
    }

    [Fact]
    public async Task EnableAsync_WhenBothEcoAndQuietAreRefused_ReportsTheModeAsFailed()
    {
        var h = new Harness().WithPreviousValues();
        h.Agent.Rejected.Add(OperatingMode.Eco);
        h.Agent.Rejected.Add(OperatingMode.Quiet);

        var outcome = await h.Service.EnableAsync();

        Assert.Contains(Loc.T("Çalışma modu"), outcome.Failed);
    }

    [Fact]
    public async Task EnableAsync_RecordsWhatItReplacedSoTheUndoIsExact()
    {
        var h = new Harness().WithPreviousValues();

        await h.Service.EnableAsync();

        var backup = h.ReadBackup();
        Assert.NotNull(backup);
        Assert.Equal(OperatingMode.Extreme, backup!.Mode);
        Assert.Equal(FanMode.Max, backup.Fan);
        Assert.Equal(CpuBoostMode.Aggressive, backup.DcBoost);
        Assert.Equal(165, backup.RefreshRate);
        Assert.Equal(80, backup.Brightness);
    }

    [Fact]
    public async Task DisableAsync_PutsEveryDcValueBack()
    {
        var h = new Harness().WithPreviousValues(previous: 55);
        await h.Service.EnableAsync();

        var outcome = await h.Service.DisableAsync();

        Assert.True(outcome.Success, string.Join(", ", outcome.Failed));
        foreach (var setting in SchoolModeService.PowerSettings)
            Assert.Equal(55u, h.Scheme.Values[(setting.SubGroup, setting.Setting)]);
    }

    [Fact]
    public async Task DisableAsync_PutsModeFanRefreshRateBrightnessAndBoostBack()
    {
        var h = new Harness().WithPreviousValues();
        await h.Service.EnableAsync();

        await h.Service.DisableAsync();

        Assert.Equal(OperatingMode.Extreme, h.Agent.CurrentMode);
        Assert.Equal(FanMode.Max, h.Agent.CurrentFanMode);
        Assert.Equal(165, h.Display.GetCurrentRefreshRate());
        Assert.Equal(80, h.Brightness.Current);
        Assert.Equal(CpuBoostMode.Aggressive, h.Cpu.CurrentMode);
    }

    [Fact]
    public async Task DisableAsync_RemovesTheUndoRecord()
    {
        var h = new Harness().WithPreviousValues();
        await h.Service.EnableAsync();
        Assert.True(h.BackupExists);

        await h.Service.DisableAsync();

        Assert.False(h.BackupExists);
    }

    [Fact]
    public async Task DisableAsync_WhenARestoreStepFails_KeepsTheUndoRecordSoItCanBeRetried()
    {
        var h = new Harness().WithPreviousValues(previous: 55);
        await h.Service.EnableAsync();
        h.Scheme.FailWrites = true;

        var outcome = await h.Service.DisableAsync();

        Assert.False(outcome.Success);
        Assert.False(h.Settings.SchoolModeEnabled);
        Assert.True(h.BackupExists, "a failed restore must not throw the undo record away");

        // The next start finds the record while school mode is off and finishes the job.
        h.Scheme.FailWrites = false;
        Assert.True(await h.Service.RecoverOrphanedBackupAsync());
        Assert.All(SchoolModeService.PowerSettings, s => Assert.Equal(55u, h.Scheme.Values[(s.SubGroup, s.Setting)]));
        Assert.False(h.BackupExists);
    }

    [Fact]
    public async Task RecoverOrphanedBackupAsync_WhenARestoreStepFails_KeepsTheRecord()
    {
        var h = new Harness().WithPreviousValues(previous: 55);
        await h.Service.EnableAsync();
        h.Settings.SchoolModeEnabled = false; // a run that died before saving the setting
        h.Scheme.FailWrites = true;

        await h.Service.RecoverOrphanedBackupAsync();

        Assert.True(h.BackupExists);
    }

    [Fact]
    public async Task EnableAsync_AfterAFailedSwitchOff_KeepsTheOriginalValuesInTheRecord()
    {
        var h = new Harness().WithPreviousValues(previous: 55);
        await h.Service.EnableAsync();
        h.Scheme.FailWrites = true;
        await h.Service.DisableAsync();
        h.Scheme.FailWrites = false;

        // The machine still holds school-mode values; recording them as "previous" would lose the real ones.
        await h.Service.EnableAsync();

        Assert.All(h.ReadBackup()!.PowerSettings, saved => Assert.Equal(55u, saved.PreviousDc));
    }

    [Fact]
    public async Task EnableAsync_Twice_DoesNotOverwriteTheUndoRecordWithSchoolModeValues()
    {
        var h = new Harness().WithPreviousValues(previous: 90);
        await h.Service.EnableAsync();
        var first = h.ReadBackup();

        await h.Service.EnableAsync();
        var second = h.ReadBackup();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.All(second!.PowerSettings, saved => Assert.Equal(90u, saved.PreviousDc));
        Assert.Equal(first!.EnabledUtc, second.EnabledUtc);
    }

    [Fact]
    public async Task ReapplyAsync_LeavesTheUndoRecordAlone()
    {
        var h = new Harness().WithPreviousValues(previous: 90);
        await h.Service.EnableAsync();
        var before = h.ReadBackup();

        await h.Service.ReapplyAsync();
        var after = h.ReadBackup();

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before!.EnabledUtc, after!.EnabledUtc);
        Assert.All(after.PowerSettings, saved => Assert.Equal(90u, saved.PreviousDc));
    }

    [Fact]
    public async Task EnableAsync_KeepsTheUndoRecordEvenWhenEveryWriteFails()
    {
        // A machine that refuses the writes must still be undoable: the record is written before the first change.
        var h = new Harness().WithPreviousValues(previous: 42);
        h.Scheme.FailWrites = true;

        var outcome = await h.Service.EnableAsync();

        Assert.False(outcome.Success);
        var backup = h.ReadBackup();
        Assert.NotNull(backup);
        Assert.All(backup!.PowerSettings, saved => Assert.Equal(42u, saved.PreviousDc));
    }

    [Fact]
    public async Task EnableAsync_WhenTheSchemeCannotBeRead_StillAppliesAndStillTurnsOff()
    {
        var h = new Harness().WithPreviousValues();
        h.Scheme.FailReads = true;

        await h.Service.EnableAsync();
        var backup = h.ReadBackup();

        Assert.NotNull(backup);
        Assert.Empty(backup!.PowerSettings);
        Assert.True(h.Settings.SchoolModeEnabled);

        var outcome = await h.Service.DisableAsync();
        Assert.True(outcome.Success);
        Assert.False(h.Settings.SchoolModeEnabled);
    }

    [Fact]
    public async Task EnableAsync_WhenThePanelReportsNoBrightness_IsNotAFailure()
    {
        // An external-monitor-only setup has nothing to dim; that must not read as a broken school mode.
        var h = new Harness().WithPreviousValues();
        h.Brightness.Supported = false;

        var outcome = await h.Service.EnableAsync();

        Assert.True(outcome.Success, string.Join(", ", outcome.Failed));
        Assert.Equal(0, h.Brightness.SetCalls);
    }

    [Fact]
    public async Task EnableAsync_WhenTheScreenIsAlreadyAtSixtyHertz_DoesNotTouchIt()
    {
        var h = new Harness().WithPreviousValues();
        h.Display.SetRefreshRate(60);
        int callsBefore = h.Display.SetRefreshRateCalls;

        await h.Service.EnableAsync();

        Assert.Equal(callsBefore, h.Display.SetRefreshRateCalls);
    }

    [Fact]
    public async Task DisableAsync_WithoutAnUndoRecord_StillTurnsSchoolModeOff()
    {
        var h = new Harness().WithPreviousValues();

        var outcome = await h.Service.DisableAsync();

        Assert.True(outcome.Success);
        Assert.False(h.Settings.SchoolModeEnabled);
    }

    [Fact]
    public async Task EnableAsync_TurnsTheKeyboardBacklightOffAndRemembersItWasOn()
    {
        var h = new Harness().WithPreviousValues();
        bool lightingOn = true;
        h.Service.TurnLightingOffAsync = () => { lightingOn = false; return Task.FromResult(true); };

        await h.Service.EnableAsync();

        Assert.False(lightingOn);
        Assert.True(h.ReadBackup()!.LightingWasOn);
    }

    [Fact]
    public async Task DisableAsync_RestoresTheKeyboardBacklightOnlyWhenItWasOn()
    {
        var h = new Harness().WithPreviousValues();
        int restoreCalls = 0;
        h.Service.TurnLightingOffAsync = () => Task.FromResult(true);
        h.Service.RestoreLightingAsync = () => { restoreCalls++; return Task.CompletedTask; };

        await h.Service.EnableAsync();
        await h.Service.DisableAsync();

        Assert.Equal(1, restoreCalls);
    }

    [Fact]
    public async Task DisableAsync_DoesNotRestoreTheKeyboardBacklightWhenItWasAlreadyOff()
    {
        var h = new Harness().WithPreviousValues();
        int restoreCalls = 0;
        h.Service.TurnLightingOffAsync = () => Task.FromResult(false);
        h.Service.RestoreLightingAsync = () => { restoreCalls++; return Task.CompletedTask; };

        await h.Service.EnableAsync();
        await h.Service.DisableAsync();

        Assert.Equal(0, restoreCalls);
    }

    [Fact]
    public async Task EnableAsync_WhenTheBacklightCallThrows_StillAppliesTheRest()
    {
        var h = new Harness().WithPreviousValues();
        h.Service.TurnLightingOffAsync = () => throw new InvalidOperationException("device gone");

        var outcome = await h.Service.EnableAsync();

        // The power settings are already written and the undo record is on disk, so the next launch can still undo.
        Assert.Contains(Loc.T("Klavye ışığı"), outcome.Failed);
        Assert.True(h.BackupExists);
        Assert.Equal(SchoolModeService.TargetMaxProcessorPercent,
            h.Scheme.Values[(SchoolModeService.PowerSettings[0].SubGroup, SchoolModeService.PowerSettings[0].Setting)]);
    }

    [Fact]
    public async Task EnableAsync_ReportsEachFailedStepByItsTranslatedLabel()
    {
        var h = new Harness().WithPreviousValues();
        h.Scheme.FailWrites = true;

        var outcome = await h.Service.EnableAsync();

        Assert.Equal(SchoolModeService.PowerSettings.Count, outcome.Failed.Count);
        Assert.Contains(Loc.T(SchoolModeService.PowerSettings[0].Label), outcome.Failed);
    }

    [Fact]
    public async Task EnableAsync_WithACorruptUndoRecord_StillApplies()
    {
        var h = new Harness().WithPreviousValues();
        File.WriteAllText(h.Service.BackupPath!, "{ this is not json");

        var outcome = await h.Service.EnableAsync();

        Assert.True(outcome.Success, string.Join(", ", outcome.Failed));
        Assert.NotNull(h.ReadBackup());
    }

    [Fact]
    public async Task DisableAsync_WithACorruptUndoRecord_LeavesTheFileForHandRepair()
    {
        // The record is the only copy of the values the machine was in before school mode. An unreadable one is not
        // an empty one: deleting it throws away the one thing that could still be repaired by hand.
        var h = new Harness().WithPreviousValues();
        File.WriteAllText(h.Service.BackupPath!, "{ this is not json");

        var outcome = await h.Service.DisableAsync();

        // Nothing was restored, so the switch-off is not a success (clean-up round 3, P1): it used to report one while
        // the machine stayed on school mode's battery values.
        Assert.False(outcome.Success);
        Assert.False(h.Settings.SchoolModeEnabled);
        Assert.True(File.Exists(h.Service.BackupPath), "an unreadable undo record must survive the switch-off");
    }

    [Fact]
    public async Task EnableAsync_WithNoBackupPath_StillApplies()
    {
        // Persistence is optional: a read-only data folder must not stop school mode from working this session.
        var h = new Harness().WithPreviousValues();
        h.Service.BackupPath = null;

        var outcome = await h.Service.EnableAsync();

        Assert.True(outcome.Success, string.Join(", ", outcome.Failed));
        Assert.True(h.Settings.SchoolModeEnabled);
    }

    [Fact]
    public async Task EnableAsync_CreatesAMissingDataFolderForTheUndoRecord()
    {
        var h = new Harness().WithPreviousValues();
        h.Service.BackupPath = Path.Combine(Path.GetTempPath(), "nitrolite_missing_" + Guid.NewGuid().ToString("N"), "backup.json");

        var outcome = await h.Service.EnableAsync();

        Assert.True(outcome.Success, string.Join(", ", outcome.Failed));
        Assert.True(h.Settings.SchoolModeEnabled);
        Assert.True(File.Exists(h.Service.BackupPath));
    }

    /// <summary>
    /// The undo record is the whole promise of the switch. When it cannot be written, nothing is changed: a mode
    /// the user cannot turn off is worse than no mode, and the settings page cannot put back what was never saved.
    /// </summary>
    [Fact]
    public async Task EnableAsync_RefusesToApplyWhenTheUndoRecordCannotBeWritten()
    {
        var h = new Harness().WithPreviousValues();

        // A file where the folder should be: the record cannot be written, whatever the underlying reason.
        string blocker = Path.Combine(Path.GetTempPath(), "nitrolite_blocker_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            h.Service.BackupPath = Path.Combine(blocker, "backup.json");

            var outcome = await h.Service.EnableAsync();

            Assert.True(outcome.Refused);
            Assert.False(outcome.Success);
            Assert.False(h.Settings.SchoolModeEnabled);

            // Nothing reached the machine: no power-scheme write, no mode change, no fan change.
            Assert.Empty(h.Scheme.Writes);
            Assert.Empty(h.Agent.Attempts);
            Assert.Equal(FanMode.Auto, h.Agent.CurrentFanMode);
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
        }
    }

    [Fact]
    public void PowerSettings_CarryTheThreeProcessorLeversThatActuallySavePower()
    {
        // The GUIDs are the part that silently does nothing when it is wrong, so they are pinned here rather than
        // only in the service: a typo would otherwise look like a successful school mode.
        var processor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        var levers = SchoolModeService.PowerSettings.Where(s => s.SubGroup == processor).ToList();

        Assert.Equal(3, levers.Count);
        Assert.Contains(levers, s => s.Setting == new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec") && s.TargetDc == 60);
        Assert.Contains(levers, s => s.Setting == new Guid("94d3a615-a899-4ac5-ae2b-e4d8f634367f") && s.TargetDc == 1);
        Assert.Contains(levers, s => s.Setting == new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863") && s.TargetDc == 100);
    }

    [Fact]
    public void PowerSettings_AreDistinctAndHaveATranslatedLabel()
    {
        Assert.Equal(SchoolModeService.PowerSettings.Count,
            SchoolModeService.PowerSettings.Select(s => (s.SubGroup, s.Setting)).Distinct().Count());
        Assert.All(SchoolModeService.PowerSettings, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
    }

    /// <summary>
    /// A setting whose current value cannot be read has nothing to put back, so school mode must not write it:
    /// "turning it off restores everything" is only true for the values it recorded first.
    /// </summary>
    [Fact]
    public async Task EnableAsync_LeavesASettingAloneWhenItsCurrentValueCannotBeRead()
    {
        var h = new Harness().WithPreviousValues();
        var unreadable = SchoolModeService.PowerSettings[0];
        h.Scheme.Values.Remove((unreadable.SubGroup, unreadable.Setting));

        var outcome = await h.Service.EnableAsync();

        Assert.DoesNotContain(h.Scheme.Writes, w => w.SubGroup == unreadable.SubGroup && w.Setting == unreadable.Setting);
        Assert.False(h.Scheme.Values.ContainsKey((unreadable.SubGroup, unreadable.Setting)));
        Assert.NotEmpty(outcome.Warnings);

        // The rest of school mode still applies: one unreadable setting does not cost the user the whole switch.
        foreach (var setting in SchoolModeService.PowerSettings.Skip(1))
            Assert.Equal(setting.TargetDc, h.Scheme.Values[(setting.SubGroup, setting.Setting)]);
    }

    /// <summary>The other half of the same promise: switching off writes back only what was recorded.</summary>
    [Fact]
    public async Task DisableAsync_NeverWritesASettingItCouldNotRecord()
    {
        var h = new Harness().WithPreviousValues(previous: 55);
        var unreadable = SchoolModeService.PowerSettings[0];
        h.Scheme.Values.Remove((unreadable.SubGroup, unreadable.Setting));

        await h.Service.EnableAsync();
        h.Scheme.Writes.Clear();
        await h.Service.DisableAsync();

        Assert.DoesNotContain(h.Scheme.Writes, w => w.SubGroup == unreadable.SubGroup && w.Setting == unreadable.Setting);
        Assert.False(h.Scheme.Values.ContainsKey((unreadable.SubGroup, unreadable.Setting)));
        foreach (var setting in SchoolModeService.PowerSettings.Skip(1))
            Assert.Equal(55u, h.Scheme.Values[(setting.SubGroup, setting.Setting)]);
    }

    /// <summary>
    /// Re-applying at the start of a new session reads the undo record from disk, so it has to honour it too:
    /// a setting the record does not hold was never readable and must stay untouched.
    /// </summary>
    [Fact]
    public async Task ReapplyAsync_HonoursTheUndoRecordOnDisk()
    {
        var h = new Harness().WithPreviousValues();
        var unreadable = SchoolModeService.PowerSettings[0];
        h.Scheme.Values.Remove((unreadable.SubGroup, unreadable.Setting));
        await h.Service.EnableAsync();

        h.Scheme.Writes.Clear();
        await h.Service.ReapplyAsync();

        Assert.DoesNotContain(h.Scheme.Writes, w => w.SubGroup == unreadable.SubGroup && w.Setting == unreadable.Setting);
        foreach (var setting in SchoolModeService.PowerSettings.Skip(1))
            Assert.Equal(setting.TargetDc, h.Scheme.Values[(setting.SubGroup, setting.Setting)]);
    }

    /// <summary>
    /// The one part the fake scheme cannot prove: that every GUID school mode writes really exists in the active
    /// Windows power scheme and that powrprof accepts a write. Each value is written back exactly as it was read, so
    /// the machine is left untouched.
    /// </summary>
    [LiveSystemFact]
    public void EverySchoolModeSetting_ExistsInTheActiveScheme_AndAcceptsAWrite()
    {
        var scheme = new WindowsPowerSchemeAccess();

        foreach (var setting in SchoolModeService.PowerSettings)
        {
            Assert.True(scheme.TryReadDc(setting.SubGroup, setting.Setting, out uint before),
                $"could not read {setting.Label} ({setting.SubGroup}/{setting.Setting})");
            Assert.True(scheme.TryWriteDc(setting.SubGroup, setting.Setting, before),
                $"could not write {setting.Label} ({setting.SubGroup}/{setting.Setting})");
            Assert.True(scheme.TryReadDc(setting.SubGroup, setting.Setting, out uint after));
            Assert.Equal(before, after);
        }
    }

    // ---------------------------------------------------------------- recovery that can never fully succeed

    private static (Guid SubGroup, Guid Setting) FirstSetting() =>
        (SchoolModeService.PowerSettings[0].SubGroup, SchoolModeService.PowerSettings[0].Setting);

    [Fact]
    public async Task RecoverOrphanedBackupAsync_AStepThatAlwaysFails_IsGivenUpAfterThreeStartsAndDoneStepsAreNotRewritten()
    {
        var h = new Harness().WithPreviousValues(previous: 55);
        await h.Service.EnableAsync();
        h.Settings.SchoolModeEnabled = false; // a run that died before saving the setting
        h.Scheme.FailOnly = FirstSetting();
        int modeWrites = h.Agent.Attempts.Count;

        Assert.True(await h.Service.RecoverOrphanedBackupAsync()); // start 1
        int brightnessAfterFirst = h.Brightness.SetCalls;
        int powerWritesAfterFirst = h.Scheme.Writes.Count;
        var kept = h.ReadBackup();
        Assert.NotNull(kept);
        Assert.Single(kept!.PowerSettings);
        Assert.Equal(1, kept.RecoveryAttempts);
        Assert.NotNull(kept.FirstFailureUtc);
        Assert.Equal(modeWrites + 1, h.Agent.Attempts.Count);

        await h.Service.RecoverOrphanedBackupAsync(); // start 2
        Assert.True(h.BackupExists);
        Assert.Equal(2, h.ReadBackup()!.RecoveryAttempts);

        await h.Service.RecoverOrphanedBackupAsync(); // start 3: limit reached
        Assert.False(h.BackupExists);

        Assert.Equal(brightnessAfterFirst, h.Brightness.SetCalls);
        Assert.Equal(powerWritesAfterFirst, h.Scheme.Writes.Count);
        Assert.Equal(modeWrites + 1, h.Agent.Attempts.Count);
    }

    [Fact]
    public async Task DisableAsync_WhenThePowerPlanIsGone_DropsThatStepAtOnceAndDeletesTheRecord()
    {
        var planA = Guid.NewGuid();
        var h = new Harness().WithPreviousValues(previous: 55);
        h.Scheme.ActiveScheme = planA;
        await h.Service.EnableAsync();
        h.Scheme.ActiveScheme = Guid.NewGuid(); // plan A was deleted
        int writes = h.Scheme.Writes.Count;

        var outcome = await h.Service.DisableAsync();

        Assert.False(outcome.Success);
        Assert.Equal(writes, h.Scheme.Writes.Count);
        Assert.False(h.BackupExists, "a deleted plan can never be restored; the step must not be kept");
    }

    [Fact]
    public async Task RecoverOrphanedBackupAsync_WhenThePowerPlanIsGone_NeverRetriesIt()
    {
        var h = new Harness().WithPreviousValues(previous: 55);
        h.Scheme.ActiveScheme = Guid.NewGuid();
        await h.Service.EnableAsync();
        h.Settings.SchoolModeEnabled = false;
        h.Scheme.ActiveScheme = Guid.NewGuid();
        h.Brightness.Current = 20;

        Assert.True(await h.Service.RecoverOrphanedBackupAsync());

        Assert.False(h.BackupExists);
        Assert.False(await h.Service.RecoverOrphanedBackupAsync());
    }

    [Fact]
    public async Task RecoverOrphanedBackupAsync_SevenDaysAfterTheFirstFailure_DeletesTheRecordWithoutWriting()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var h = new Harness().WithPreviousValues(previous: 55);
        h.Service.UtcNow = () => now;
        await h.Service.EnableAsync();
        h.Settings.SchoolModeEnabled = false;
        h.Scheme.FailOnly = FirstSetting();
        await h.Service.RecoverOrphanedBackupAsync();
        Assert.True(h.BackupExists);
        int brightness = h.Brightness.SetCalls;
        int modes = h.Agent.Attempts.Count;

        now = now.AddDays(7).AddMinutes(1);
        bool recovered = await h.Service.RecoverOrphanedBackupAsync();

        Assert.False(recovered);
        Assert.False(h.BackupExists);
        Assert.Equal(brightness, h.Brightness.SetCalls);
        Assert.Equal(modes, h.Agent.Attempts.Count);
    }

    [Fact]
    public async Task RecoverOrphanedBackupAsync_AnOldRecordWithoutTheCounterFields_StillLoadsAndCountsFromZero()
    {
        var h = new Harness();
        var setting = SchoolModeService.PowerSettings[0];
        h.Scheme.FailOnly = FirstSetting();
        File.WriteAllText(h.Service.BackupPath!, "{\"PowerSettings\":[{\"SubGroup\":\"" + setting.SubGroup + "\",\"Setting\":\""
            + setting.Setting + "\",\"PreviousDc\":55}],\"Mode\":0,\"Fan\":0,\"LightingWasOn\":false,"
            + "\"EnabledUtc\":\"2020-01-01T00:00:00Z\"}");

        Assert.True(await h.Service.RecoverOrphanedBackupAsync());

        var kept = h.ReadBackup();
        Assert.NotNull(kept);
        Assert.Equal(1, kept!.RecoveryAttempts);
        Assert.Single(kept.PowerSettings);
    }

    [Fact]
    public async Task EnableAsync_WithAKeptRecord_TakesOnlyThePendingFieldsFromIt()
    {
        var h = new Harness().WithPreviousValues(previous: 55);
        await h.Service.EnableAsync();
        h.Scheme.FailOnly = FirstSetting();
        await h.Service.DisableAsync(); // restores everything but the first power setting
        h.Scheme.FailOnly = null;

        // The user has moved on since: new brightness, new mode. The one pending power value is still the old one.
        h.Brightness.Current = 33;
        h.Service.GetCurrentOperatingMode = () => OperatingMode.Quiet;

        await h.Service.EnableAsync();

        var record = h.ReadBackup()!;
        Assert.Equal(33, record.Brightness);
        Assert.Equal(OperatingMode.Quiet, record.Mode);
        Assert.False(record.ModeRestored);
        Assert.Equal(0, record.RecoveryAttempts);
        Assert.All(record.PowerSettings, saved => Assert.Equal(55u, saved.PreviousDc));
        Assert.Equal(SchoolModeService.PowerSettings.Count, record.PowerSettings.Count);
    }
}
