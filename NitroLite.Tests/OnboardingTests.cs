using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Phase 3 of the WPF parity pass: the three-step first-run walkthrough.
/// </summary>
/// <remarks>
/// The walkthrough is the one screen a user sees before they have asked for anything, so the tests are about when it
/// appears, that the three steps are reachable in both directions, that skipping is remembered, and that finishing
/// actually writes the profile it promised.
/// </remarks>
public class OnboardingTests
{
    private static MainViewModel Create(
        AppSettings? settings = null,
        Phase3Fakes.StubHardware? hardware = null,
        string? path = null) =>
        new(settings ?? new AppSettings(),
            hardware,
            null,
            path ?? Phase3Fakes.TempSettingsPath());

    [Fact]
    public async Task FirstRun_Shows()
    {
        using var vm = Create();

        // Before the probe there is nothing to list, so the walkthrough waits for it.
        Assert.False(vm.ShowOnboarding);

        await vm.InitializeAsync();

        Assert.True(vm.ShowOnboarding);
        Assert.Equal(0, vm.OnboardingStep);
        Assert.True(vm.IsOnboardingFirstStep);
        Assert.False(vm.IsOnboardingSecondStep);
        Assert.False(vm.IsOnboardingLastStep);

        // Step one is the hardware summary, so the title is the hardware title and the rows are the probe's.
        Assert.Equal(vm.Labels.OnboardingHardwareTitle, vm.OnboardingTitle);
        Assert.NotEmpty(vm.OnboardingBody);
        Assert.NotEmpty(vm.OnboardingStepLabel);
    }

    [Fact]
    public async Task ThreeSteps_Navigate()
    {
        using var vm = Create();
        await vm.InitializeAsync();

        Assert.True(vm.ShowOnboarding);

        vm.OnboardingNextCommand.Execute(null);
        Assert.Equal(1, vm.OnboardingStep);
        Assert.True(vm.IsOnboardingSecondStep);
        Assert.Equal(vm.Labels.OnboardingProfileTitle, vm.OnboardingTitle);
        Assert.Equal(vm.Labels.OnboardingRecommended, vm.OnboardingRecommendedText);

        vm.OnboardingNextCommand.Execute(null);
        Assert.Equal(2, vm.OnboardingStep);
        Assert.True(vm.IsOnboardingLastStep);
        Assert.Equal(vm.Labels.OnboardingStartupTitle, vm.OnboardingTitle);

        // The last step does not advance past itself.
        vm.OnboardingNextCommand.Execute(null);
        Assert.Equal(2, vm.OnboardingStep);

        vm.OnboardingBackCommand.Execute(null);
        Assert.Equal(1, vm.OnboardingStep);

        vm.OnboardingBackCommand.Execute(null);
        Assert.Equal(0, vm.OnboardingStep);

        // Nor does the first step go back past itself.
        vm.OnboardingBackCommand.Execute(null);
        Assert.Equal(0, vm.OnboardingStep);
    }

    [Fact]
    public async Task Skip_Persists()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            using var vm = Create(path: path);
            await vm.InitializeAsync();
            Assert.True(vm.ShowOnboarding);

            vm.OnboardingSkipCommand.Execute(null);
            await Phase3Fakes.WaitUntilAsync(() => !vm.ShowOnboarding);

            Assert.False(vm.ShowOnboarding);
            Assert.True(SettingsManager.LoadSettingsWithFallback(path).OnboardingCompleted);

            // A second launch reads the flag from the file and stays closed.
            using var again = Create(settings: SettingsManager.LoadSettingsWithFallback(path), path: path);
            await again.InitializeAsync();
            Assert.False(again.ShowOnboarding);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task RecommendedProfile_Applies()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            // A backend that supports modes, fans and the charge limit, so all three parts of the profile have a target.
            var hardware = new Phase3Fakes.StubHardware();
            var settings = new AppSettings { Theme = AppTheme.WarmCream };

            using var vm = Create(settings: settings, hardware: hardware, path: path);
            await vm.InitializeAsync();

            Assert.True(vm.ModeControlAvailable);
            Assert.True(vm.FanControlAvailable);
            Assert.True(vm.BatteryLimitAvailable);

            // Start somewhere that is not the recommendation, so the assertion is about the finish, not the default.
            vm.SelectedMode = vm.Modes.First(m => m.Mode == OperatingMode.Turbo);
            vm.SelectedFanMode = vm.FanModes.First(m => m.Mode == FanMode.Max);
            Assert.False(vm.BatteryLimitOn);

            vm.OnboardingFinishCommand.Execute(null);
            await Phase3Fakes.WaitUntilAsync(() => !vm.ShowOnboarding);

            // Balanced, automatic fans, and the 80 % limit — the three things step two promised.
            Assert.Equal(OperatingMode.Default, vm.SelectedMode!.Mode);
            Assert.Equal(FanMode.Auto, vm.SelectedFanMode!.Mode);
            Assert.True(vm.BatteryLimitOn);

            // And the writes reached the backend rather than only the view model.
            Assert.Contains("mode:" + OperatingMode.Default, hardware.Writes);
            Assert.Contains("fan:" + FanMode.Auto, hardware.Writes);
            Assert.Contains("charge:" + BatteryChargeMode.Optimized80, hardware.Writes);

            Assert.True(SettingsManager.LoadSettingsWithFallback(path).OnboardingCompleted);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
