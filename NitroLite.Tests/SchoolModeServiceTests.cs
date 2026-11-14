using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NitroLite.Core;
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
            if (FailWrites) return false;
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

    private sealed class Harness
    {
        public FakePowerScheme Scheme { get; } = new();
        public MockAcerAgentClient Agent { get; } = new();
        public SchoolModeCpuPower Cpu { get; } = new();
        public TestDisplayRefreshRateServiceStub Display { get; } = new();
        public TestScreenBrightnessService Brightness { get; } = new();
        public AppSettings Settings { get; } = new();
        public SchoolModeService Service { get; }

        public Harness()
        {
            Service = new SchoolModeService(Agent, Cpu, Display, Brightness, Settings, Scheme)
            {
                BackupPath = Path.Combine(Path.GetTempPath(), "nitrolite_school_" + Guid.NewGuid().ToString("N") + ".json"),
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
    public async Task EnableAsync_SetsQuietModeAutoFanSixtyHertzAndFortyPercentBrightness()
    {
        var h = new Harness().WithPreviousValues();

        await h.Service.EnableAsync();

        Assert.Equal(OperatingMode.Quiet, h.Agent.CurrentMode);
        Assert.Equal(FanMode.Auto, h.Agent.CurrentFanMode);
        Assert.Equal(60, h.Display.GetCurrentRefreshRate());
        Assert.Equal(SchoolModeService.TargetBrightnessPercent, h.Brightness.Current);
        Assert.Contains(CpuBoostMode.Disabled, h.Cpu.BoostHistory);
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
    public async Task EnableAsync_WhenTheDataFolderIsReadOnly_StillApplies()
    {
        var h = new Harness().WithPreviousValues();
        h.Service.BackupPath = Path.Combine(Path.GetTempPath(), "nitrolite_missing_" + Guid.NewGuid().ToString("N"), "backup.json");

        var outcome = await h.Service.EnableAsync();

        Assert.True(outcome.Success, string.Join(", ", outcome.Failed));
        Assert.True(h.Settings.SchoolModeEnabled);
    }

    [Fact]
    public async Task PowerAutomation_WhileSchoolModeHoldsTheMachine_ChangesNothing()
    {
        var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco, AutoRefreshRateSwitching = true };
        var agent = new MockAcerAgentClient();
        var cpu = new MockCpuPowerService();
        var display = new TestDisplayRefreshRateServiceStub();
        var automation = new PowerAutomationService(agent, cpu, display, settings, attachSystemEvents: false)
        {
            AutomationSuspended = true
        };

        await automation.ProcessPowerTransitionAsync(isAc: false);

        Assert.False(automation.IsDcEcoActive);
        Assert.Equal(0, display.SetRefreshRateCalls);
        Assert.Empty(cpu.BoostHistory);
        Assert.Empty(agent.ModeChangeHistory);
        Assert.False(automation.LastAcStatus);
    }

    [Fact]
    public async Task PowerAutomation_AfterSchoolModeEnds_AppliesEcoAgain()
    {
        var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco, AutoRefreshRateSwitching = true };
        var agent = new MockAcerAgentClient();
        var cpu = new MockCpuPowerService();
        var display = new TestDisplayRefreshRateServiceStub();
        var automation = new PowerAutomationService(agent, cpu, display, settings, attachSystemEvents: false)
        {
            AutomationSuspended = true
        };

        await automation.ProcessPowerTransitionAsync(isAc: false);
        automation.AutomationSuspended = false;
        await automation.EvaluatePowerStateAsync(forceAcStatus: false);

        Assert.True(automation.IsDcEcoActive);
        Assert.Equal(60, display.LastSetRefreshRate);
    }

    [Fact]
    public void PowerSettings_AreDistinctAndHaveATranslatedLabel()
    {
        Assert.Equal(SchoolModeService.PowerSettings.Count,
            SchoolModeService.PowerSettings.Select(s => (s.SubGroup, s.Setting)).Distinct().Count());
        Assert.All(SchoolModeService.PowerSettings, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
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
}
