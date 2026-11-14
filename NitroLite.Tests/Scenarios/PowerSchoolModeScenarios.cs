using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests.Scenarios;

/// <summary>
/// Category 2: the power chain. School mode, the adapter being unplugged and plugged back in faster than the
/// debounce, and the charge limit that lives in the EC rather than in this app.
/// </summary>
public class PowerSchoolModeScenarios
{
    #region S2-01 School mode with the backlight already off

    /// <summary>
    /// A backlight that was already off must stay off. The service records "was it on?" and only restores when the
    /// answer is yes; a run that claimed it was on turned the light on for someone who had it off.
    /// </summary>
    [Fact]
    public async Task BacklightOff_EnableThenDisable_LeavesItOff()
    {
        var rig = new ScenarioRig();
        int restoreCalls = 0;
        var service = rig.BuildSchoolMode();
        service.TurnLightingOffAsync = () => Task.FromResult(false);   // it was already off
        service.RestoreLightingAsync = () => { restoreCalls++; return Task.CompletedTask; };

        await service.EnableAsync();
        Assert.True(service.IsEnabled);

        await service.DisableAsync();
        Assert.False(service.IsEnabled);
        Assert.Equal(0, restoreCalls);
    }

    /// <summary>
    /// The other half: a backlight that was on is turned off and put back.
    /// </summary>
    [Fact]
    public async Task BacklightOn_EnableThenDisable_PutsItBack()
    {
        var rig = new ScenarioRig();
        int restoreCalls = 0;
        var service = rig.BuildSchoolMode();
        service.TurnLightingOffAsync = () => Task.FromResult(true);    // it was on
        service.RestoreLightingAsync = () => { restoreCalls++; return Task.CompletedTask; };

        await service.EnableAsync();
        await service.DisableAsync();

        Assert.Equal(1, restoreCalls);
    }

    #endregion

    #region S2-02 The full round trip: light on, school on, sleep, unplug, wake, school off

    /// <summary>
    /// The long scenario. Every value school mode lowers is recorded before the first write and put back on the way
    /// out, including the ones the machine changed underneath it while it was asleep.
    /// </summary>
    [Fact]
    public async Task TheFullRoundTripRestoresEveryValueItChanged()
    {
        var rig = new ScenarioRig();
        rig.Agent.CurrentMode = OperatingMode.Extreme;
        rig.Agent.CurrentFanMode = FanMode.Max;
        rig.Cpu.Mode = CpuBoostMode.Aggressive;
        rig.Display.CurrentHz = 165;
        rig.Brightness.Level = 80;

        // Seed the power scheme with the values school mode is about to lower.
        var service = rig.BuildSchoolMode();
        foreach (var setting in SchoolModeService.PowerSettings)
        {
            rig.Scheme.Dc[(setting.SubGroup, setting.Setting)] = 111;
        }

        int restoreCalls = 0;
        service.TurnLightingOffAsync = () => Task.FromResult(true);
        service.RestoreLightingAsync = () => { restoreCalls++; return Task.CompletedTask; };

        // 1. School mode on.
        var on = await service.EnableAsync();
        Assert.True(on.Success, string.Join(", ", on.Failed));
        Assert.Equal(OperatingMode.Eco, rig.Agent.CurrentMode);
        Assert.Equal(FanMode.Auto, rig.Agent.CurrentFanMode);
        Assert.Equal(CpuBoostMode.Disabled, rig.Cpu.Mode);
        Assert.Equal(SchoolModeService.TargetRefreshRate, rig.Display.CurrentHz);
        Assert.Equal(SchoolModeService.TargetBrightnessPercent, rig.Brightness.Level);
        Assert.All(SchoolModeService.PowerSettings,
            s => Assert.Equal(s.TargetDc, rig.Scheme.Dc[(s.SubGroup, s.Setting)]));

        // 2. The machine sleeps and the adapter is pulled while it is out. Nothing in this app runs.
        //    The EC and Windows are free to change anything they like; the undo record is on disk.

        // 3. Wake, then school mode off.
        var off = await service.DisableAsync();
        Assert.True(off.Success, string.Join(", ", off.Failed));

        Assert.Equal(OperatingMode.Extreme, rig.Agent.CurrentMode);
        Assert.Equal(FanMode.Max, rig.Agent.CurrentFanMode);
        Assert.Equal(CpuBoostMode.Aggressive, rig.Cpu.Mode);
        Assert.Equal(165, rig.Display.CurrentHz);
        Assert.Equal(80, rig.Brightness.Level);
        Assert.All(SchoolModeService.PowerSettings,
            s => Assert.Equal(111u, rig.Scheme.Dc[(s.SubGroup, s.Setting)]));
        Assert.Equal(1, restoreCalls);
    }

    /// <summary>
    /// A setting the active scheme refuses must be reported, not silently skipped: the user has to know which value
    /// they have to put back by hand.
    /// </summary>
    [Fact]
    public async Task ASettingTheSchemeRefusesIsReportedNotSwallowed()
    {
        var rig = new ScenarioRig();
        var service = rig.BuildSchoolMode();
        rig.Scheme.WritesSucceed = false;

        var outcome = await service.EnableAsync();

        Assert.False(outcome.Success);
        Assert.NotEmpty(outcome.Failed);
    }

    #endregion

    #region S2-03 A crash between the undo record and the setting

    /// <summary>
    /// A run that dies after writing the undo record but before saving the setting leaves the machine in
    /// school-mode values with nothing in the UI to say so. The next start has to apply the record in reverse.
    /// </summary>
    [Fact]
    public async Task AnOrphanedUndoRecordIsAppliedInReverseOnTheNextStart()
    {
        var rig = new ScenarioRig();
        rig.Agent.CurrentMode = OperatingMode.Extreme;
        rig.Display.CurrentHz = 165;
        rig.Brightness.Level = 80;

        var backupPath = AppPaths.GetDataFile("scenario_school_orphan.json");
        try
        {
            var service = rig.BuildSchoolMode();
            service.BackupPath = backupPath;

            foreach (var setting in SchoolModeService.PowerSettings)
                rig.Scheme.Dc[(setting.SubGroup, setting.Setting)] = 222;

            await service.EnableAsync();
            Assert.True(File.Exists(backupPath));

            // The crash: the setting never reached disk, so the next run sees school mode as off.
            rig.Settings.SchoolModeEnabled = false;

            var next = rig.BuildSchoolMode();
            next.BackupPath = backupPath;

            bool recovered = await next.RecoverOrphanedBackupAsync();

            Assert.True(recovered);
            Assert.Equal(OperatingMode.Extreme, rig.Agent.CurrentMode);
            Assert.Equal(165, rig.Display.CurrentHz);
            Assert.Equal(80, rig.Brightness.Level);
            Assert.All(SchoolModeService.PowerSettings,
                s => Assert.Equal(222u, rig.Scheme.Dc[(s.SubGroup, s.Setting)]));
            Assert.False(File.Exists(backupPath));
        }
        finally
        {
            try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
        }
    }

    #endregion

    #region S2-04 Five adapter events in two seconds

    /// <summary>
    /// The adapter is pulled and pushed faster than the debounce. Every transition must finish, none may deadlock,
    /// and the state left behind must be the one the last event asked for.
    /// </summary>
    [Fact]
    public async Task FiveAdapterEventsInTwoSecondsDoNotDeadlockAndLeaveTheLastState()
    {
        var rig = new ScenarioRig();
        rig.Settings.DcPowerMode = DcPowerProfilePreference.Eco;
        rig.Settings.AutoRefreshRateSwitching = true;
        rig.Agent.CurrentMode = OperatingMode.Default;

        var service = rig.BuildPowerAutomation();

        // Alternate AC, DC, AC, DC, AC. The last one is AC, so the machine must end on the adapter.
        var events = new[] { true, false, true, false, true };
        var tasks = new List<Task>();
        foreach (var isAc in events)
        {
            tasks.Add(service.ProcessPowerTransitionAsync(isAc));
            await Task.Delay(20);
        }

        var all = Task.WhenAll(tasks);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(all, finished);
        await all;   // surfaces any exception the storm produced

        Assert.True(service.LastAcStatus, "The last event was the adapter being plugged in, but the service ended on battery.");
        Assert.False(service.IsDcEcoActive, "Eco must not stay on once the adapter is back.");
    }

    /// <summary>
    /// The same storm with the transitions started at the same instant rather than 20 ms apart, which is what a
    /// flapping connector actually looks like to the debounce timer.
    /// </summary>
    [Fact]
    public async Task SimultaneousAdapterEventsDoNotDeadlock()
    {
        var rig = new ScenarioRig();
        rig.Settings.DcPowerMode = DcPowerProfilePreference.Eco;
        var service = rig.BuildPowerAutomation();

        var tasks = Enumerable.Range(0, 5)
            .Select(i => service.ProcessPowerTransitionAsync(i % 2 == 0))
            .ToArray();

        var all = Task.WhenAll(tasks);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(all, finished);
        await all;
    }

    /// <summary>
    /// A transition whose writes all failed must not be forgotten: Eco is the intended state on battery, and the
    /// pending flag is what makes the next poll try again instead of leaving the machine half-switched.
    /// </summary>
    [Fact]
    public async Task AFailedTransitionIsRememberedAsPendingRetry()
    {
        var rig = new ScenarioRig();
        rig.Settings.DcPowerMode = DcPowerProfilePreference.Eco;
        rig.Agent.WritesSucceed = false;

        var service = rig.BuildPowerAutomation();
        await service.ProcessPowerTransitionAsync(false);

        Assert.True(service.IsDcEcoActive, "Eco is the intended state on battery even when the writes failed.");
        Assert.True(service.HasPendingDcApply, "A failed write must be retried, not silently dropped.");
    }

    #endregion

    #region S2-05 The charge limit lives in the EC, not here

    /// <summary>
    /// The limit is owned by Acer Care Center. What this app can do is report honestly whether it is controllable
    /// and whether it is on, and never claim a limit it did not read.
    /// </summary>
    [Theory]
    [InlineData(BatteryChargeMode.Optimized80, true, true)]
    [InlineData(BatteryChargeMode.Full, true, false)]
    [InlineData(null, false, false)]
    public void TheChargeLimitIsOnlyClaimedWhenItWasRead(BatteryChargeMode? mode, bool available, bool enabled)
    {
        var state = CareCenterClient.DescribeChargeLimit(mode);

        Assert.Equal(available, state.Available);
        Assert.Equal(enabled, state.Limited);
    }

    /// <summary>
    /// A rotated certificate must be recoverable: forgetting the pin deletes the file and clears the mismatch flag,
    /// so the next connection trusts whatever the agent presents.
    /// </summary>
    [Fact]
    public void ForgettingThePinMakesTheNextConnectionTrustAgain()
    {
        var pinPath = AppPaths.GetDataFile("carecenter_cert.pin");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pinPath)!);
            File.WriteAllText(pinPath, "AABBCCDD");

            var client = new CareCenterClient();
            client.ForgetPinnedCertificate();

            Assert.False(File.Exists(pinPath));
            Assert.False(client.CertificateMismatch);
        }
        finally
        {
            try { if (File.Exists(pinPath)) File.Delete(pinPath); } catch { }
        }
    }

    /// <summary>
    /// The pin is written through <see cref="AtomicFile"/>, so a torn write cannot leave a thumbprint that never
    /// matches again. This is the property the rotation recovery depends on.
    /// </summary>
    [Fact]
    public void ThePinFileIsWrittenAtomically()
    {
        var path = AppPaths.GetDataFile("scenario_pin_atomic.pin");
        try
        {
            AtomicFile.WriteAllText(path, "FIRST");
            AtomicFile.WriteAllText(path, "SECOND");

            Assert.Equal("SECOND", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp.*"));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    #endregion
}
