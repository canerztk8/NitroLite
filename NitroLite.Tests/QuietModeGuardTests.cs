using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Tests.Scenarios;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// A quiet mode that is not quiet is not a quiet mode. These tests pin the two ways the machine used to get loud on
/// its own: a single bad sensor poll taking the fans to full speed, and the boot/resume reassertion (or a game
/// profile) writing the saved Turbo mode over the school mode the user turned on.
/// </summary>
public class QuietModeGuardTests
{
    private static (MainWindow Window, ScenarioAgentClient Agent) CreateWindow(AppSettings? settings = null)
    {
        var agent = new ScenarioAgentClient();
        var window = new MainWindow(
            agent,
            new MockCpuPowerService(),
            new GameDetectionService(),
            new AtkMouseService(),
            settings: settings ?? new AppSettings(),
            isTestHarness: true);
        window.PowerStateProvider = () => (true, 80);
        return (window, agent);
    }

    [Fact]
    public async Task ApplyFanCurveAsync_HoldsTheLastSpeedThroughASingleDeadPoll()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var (window, agent) = CreateWindow();
            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));

            await window.ApplyFanCurveAsync(40, 40);
            int quiet = agent.CustomCpuPercent;
            Assert.True(quiet < FanCurve.MaxPercent);

            // One poll with no reading at all: AASSvc timed out and the fallback sample carries 0.
            await window.ApplyFanCurveAsync(0, 0);

            Assert.Equal(quiet, agent.CustomCpuPercent);
        });
    }

    [Fact]
    public async Task ApplyFanCurveAsync_HoldsFullSpeedOnceTheOutageIsReal()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var (window, agent) = CreateWindow();
            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));
            await window.ApplyFanCurveAsync(40, 40);

            for (int i = 0; i < MainWindow.FanCurveDeadSensorPollThreshold; i++)
            {
                await window.ApplyFanCurveAsync(0, 0);
            }

            Assert.Equal(FanCurve.MaxPercent, agent.CustomCpuPercent);
            Assert.Equal(FanCurve.MaxPercent, agent.CustomGpuPercent);
        });
    }

    [Fact]
    public async Task ApplyFanCurveAsync_TreatsAnImpossibleReadingAsADeadSensor()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var (window, agent) = CreateWindow();
            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));
            await window.ApplyFanCurveAsync(40, 40);
            int quiet = agent.CustomCpuPercent;

            // 0xFF is what a sensor that did not answer leaves behind. One frame of it must not spin the fans up.
            await window.ApplyFanCurveAsync(255, 255);
            Assert.Equal(quiet, agent.CustomCpuPercent);

            // Three frames of it is a sensor that is really gone, and then full speed is the safe answer.
            await window.ApplyFanCurveAsync(255, 255);
            await window.ApplyFanCurveAsync(255, 255);
            Assert.Equal(FanCurve.MaxPercent, agent.CustomCpuPercent);
        });
    }

    [Fact]
    public async Task ApplyFanCurveAsync_ForgetsTheOutageWhenTheCurveIsRearmed()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var (window, agent) = CreateWindow();
            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));
            await window.ApplyFanCurveAsync(40, 40);

            // Two bad polls, one short of the threshold, then the user turns the curve off and on again.
            await window.ApplyFanCurveAsync(0, 0);
            await window.ApplyFanCurveAsync(0, 0);
            Assert.True(await window.SetFanCurveEnabledAsync(false, quiet: true));
            Assert.True(await window.SetFanCurveEnabledAsync(true, quiet: true));

            await window.ApplyFanCurveAsync(40, 40);
            int quiet = agent.CustomCpuPercent;

            // A fresh arming starts from zero: this single bad poll must not be the third one.
            await window.ApplyFanCurveAsync(0, 0);
            Assert.Equal(quiet, agent.CustomCpuPercent);
        });
    }

    [Fact]
    public async Task ReassertOperatingModeAsync_DoesNothingWhileSchoolModeIsOn()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var settings = new AppSettings { LastOperatingMode = OperatingMode.Turbo, SchoolModeEnabled = true };
            var (window, agent) = CreateWindow(settings);

            await window.ReassertOperatingModeAsync(isBoot: false);

            Assert.Empty(agent.ModeWrites);
        });
    }

    [Fact]
    public async Task ReassertOperatingModeAsync_StopsWhenSchoolModeComesOnMidLoop()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var settings = new AppSettings { LastOperatingMode = OperatingMode.Turbo };
            var (window, agent) = CreateWindow(settings);

            var reassert = window.ReassertOperatingModeAsync(isBoot: false);
            await Task.Delay(100);
            settings.SchoolModeEnabled = true;
            await reassert;

            Assert.Empty(agent.ModeWrites);
        });
    }

    [Fact]
    public async Task HandleGameStartedAsync_IsSkippedWhileSchoolModeIsOn()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var settings = new AppSettings { SchoolModeEnabled = true };
            var (window, agent) = CreateWindow(settings);

            var game = new GameProfile
            {
                ExeName = "testgame",
                DisplayName = "Test Game",
                TargetMode = OperatingMode.Turbo,
                TargetFanMode = FanMode.Max
            };

            await window.HandleGameStartedAsync(game);

            Assert.False(window.IsGameSessionActive);
            Assert.Empty(agent.ModeWrites);
            Assert.DoesNotContain(agent.FanWrites, w => w.Mode == FanMode.Max);
        });
    }

    [Fact]
    public async Task SyncDeviceStateAsync_DoesNotOverwriteTheSavedModeWhileSchoolModeIsOn()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var settings = new AppSettings { LastOperatingMode = OperatingMode.Turbo, SchoolModeEnabled = true };
            var (window, agent) = CreateWindow(settings);

            // School mode wrote Eco straight to the device; the read-back must not become the user's preference.
            agent.CurrentMode = OperatingMode.Eco;
            await window.SyncDeviceStateAsync();

            Assert.Equal(OperatingMode.Turbo, settings.LastOperatingMode);
        });
    }

    [Fact]
    public async Task ApplySchoolModeAutomationState_SilencesTheBatteryWatchdog()
    {
        await StaTestRunner.RunInStaAsync(async () =>
        {
            var settings = new AppSettings();
            var (window, _) = CreateWindow(settings);

            window.ApplySchoolModeAutomationState();
            Assert.False(window.BatteryServiceForTesting.SuppressSounds);

            settings.SchoolModeEnabled = true;
            window.ApplySchoolModeAutomationState();
            Assert.True(window.BatteryServiceForTesting.SuppressSounds);
        });
    }
}
