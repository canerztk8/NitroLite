using System.Collections.Generic;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// School mode asks the firmware for automatic fans. An armed curve used to take them back to Custom on the very
/// next poll, so the switch said "Fan (Otomatik)" while the fans ran the user's shape. These tests pin the
/// handover: the curve stops writing while school mode is on, and takes the fans back when it goes off, without
/// the user having to arm it again.
/// </summary>
public class SchoolModeFanCurveTests
{
    private sealed class RecordingAgent : AcerAgentClient
    {
        public FanMode CurrentFanMode { get; set; } = FanMode.Auto;
        public List<FanMode> FanHistory { get; } = new();

        public override Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

        public override Task<bool> SetFanModeAsync(FanMode mode, int cpuRpm = 0, int gpuRpm = 0)
        {
            FanHistory.Add(mode);
            CurrentFanMode = mode;
            return Task.FromResult(true);
        }
    }

    private static (MainWindow Window, RecordingAgent Agent) CreateWindow()
    {
        var agent = new RecordingAgent();
        var window = new MainWindow(
            agent,
            new MockCpuPowerService(),
            new GameDetectionService(),
            new AtkMouseService(),
            settings: new AppSettings(),
            isTestHarness: true);
        return (window, agent);
    }

    [Fact]
    public async Task SuspendFanCurveForSchoolMode_StopsTheCurveWriting()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var (window, agent) = CreateWindow();
            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));
            Assert.True(window.IsFanCurveArmed);

            // One poll so the curve has a temperature to work from, then a clean slate for the assertion.
            await window.ApplyFanCurveAsync(55);
            agent.FanHistory.Clear();

            window.SuspendFanCurveForSchoolMode();
            Assert.False(window.IsFanCurveArmed);

            // A hot reading while school mode owns the fans must not reach the firmware.
            await window.ApplyFanCurveAsync(75);
            Assert.Empty(agent.FanHistory);
        });
    }

    [Fact]
    public async Task ResumeFanCurveAfterSchoolModeAsync_TakesTheFansBack()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var (window, agent) = CreateWindow();
            await window.SetFanCurveEnabledAsync(true, quiet: true);
            await window.ApplyFanCurveAsync(55);

            window.SuspendFanCurveForSchoolMode();

            // What school mode does to the machine: the firmware goes back to automatic fans.
            window.SetCurrentFanModeForTesting(FanMode.Auto);
            agent.FanHistory.Clear();

            await window.ResumeFanCurveAfterSchoolModeAsync();

            Assert.True(window.IsFanCurveArmed);
            Assert.Contains(FanMode.Custom, agent.FanHistory);
        });
    }

    [Fact]
    public async Task ResumeFanCurveAfterSchoolModeAsync_LeavesTheFansAloneWhenNoCurveWasArmed()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var (window, agent) = CreateWindow();
            window.SuspendFanCurveForSchoolMode();
            agent.FanHistory.Clear();

            await window.ResumeFanCurveAfterSchoolModeAsync();

            Assert.False(window.IsFanCurveArmed);
            Assert.Empty(agent.FanHistory);
        });
    }

    [Fact]
    public async Task SetFanCurveEnabledAsync_RefusesToArmWhileSchoolModeIsOn()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var (window, agent) = CreateWindow();
            window.SuspendFanCurveForSchoolMode();
            agent.FanHistory.Clear();

            Assert.False(await window.SetFanCurveEnabledAsync(true, quiet: true));
            Assert.False(window.IsFanCurveArmed);
            Assert.Empty(agent.FanHistory);
        });
    }

    [Fact]
    public async Task ArmGameFanCurveAsync_IsRefusedWhileSchoolModeIsOn()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var (window, agent) = CreateWindow();
            window.SuspendFanCurveForSchoolMode();
            agent.FanHistory.Clear();

            var profile = new GameProfile { DisplayName = "Test", TargetFanCurveEnabled = true };
            Assert.False(await window.ArmGameFanCurveAsync(profile));
            Assert.False(window.IsGameFanCurveArmed);
            Assert.Empty(agent.FanHistory);
        });
    }

    [Fact]
    public async Task SuspendingAndResuming_KeepsTheSavedPreference()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var (window, _) = CreateWindow();
            await window.SetFanCurveEnabledAsync(true, quiet: true);
            Assert.True(window.CurrentAppSettings.FanCurveEnabled);

            window.SuspendFanCurveForSchoolMode();
            Assert.True(window.CurrentAppSettings.FanCurveEnabled);

            await window.ResumeFanCurveAfterSchoolModeAsync();
            Assert.True(window.CurrentAppSettings.FanCurveEnabled);
            Assert.True(window.IsFanCurveArmed);
        });
    }
}
