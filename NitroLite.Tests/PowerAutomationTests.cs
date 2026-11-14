using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class PowerAutomationTests
{
    private class MockDisplayRefreshRateService : IDisplayRefreshRateService
    {
        public int CurrentHz { get; set; } = 165;
        public int MaxHz { get; set; } = 165;
        public List<int> SetRefreshRateCalls { get; } = new();

        public int GetCurrentRefreshRate() => CurrentHz;
        public int GetMaxRefreshRate() => MaxHz;

        public bool SetRefreshRate(int hz)
        {
            CurrentHz = hz;
            SetRefreshRateCalls.Add(hz);
            return true;
        }
    }

    [Fact]
    public async Task TransitionToDc_EcoMode_SavesStateAndAppliesQuietAndDisabledBoostAnd60Hz()
    {
        var settings = new AppSettings
        {
            DcPowerMode = DcPowerProfilePreference.Eco,
            AutoRefreshRateSwitching = true
        };

        var agentClient = new MockAcerAgentClient();
        var cpuPower = new MockCpuPowerService();
        var mockDisplay = new MockDisplayRefreshRateService();

        var service = new PowerAutomationService(agentClient, cpuPower, mockDisplay, settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () => OperatingMode.Extreme,
            GetCurrentFanMode = () => FanMode.Max
        };

        // Transition from AC to DC (on battery)
        await service.ProcessPowerTransitionAsync(isAc: false);

        Assert.True(service.IsDcEcoActive);
        Assert.Equal(OperatingMode.Extreme, service.SavedOperatingMode);
        Assert.Equal(FanMode.Max, service.SavedFanMode);
        Assert.False(service.LastAcStatus);

        // Display refresh rate must be switched to 60Hz
        Assert.Single(mockDisplay.SetRefreshRateCalls);
        Assert.Equal(60, mockDisplay.SetRefreshRateCalls[0]);
    }

    [Fact]
    public async Task TransitionToAc_EcoModeActive_RestoresPreviousStateAnd165Hz()
    {
        var settings = new AppSettings
        {
            DcPowerMode = DcPowerProfilePreference.Eco,
            AutoRefreshRateSwitching = true
        };

        var agentClient = new MockAcerAgentClient();
        var cpuPower = new MockCpuPowerService();
        var mockDisplay = new MockDisplayRefreshRateService();

        var service = new PowerAutomationService(agentClient, cpuPower, mockDisplay, settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () => OperatingMode.Turbo,
            GetCurrentFanMode = () => FanMode.Custom
        };

        // 1. Transition to DC
        await service.ProcessPowerTransitionAsync(isAc: false);
        Assert.True(service.IsDcEcoActive);

        // 2. Transition back to AC
        await service.ProcessPowerTransitionAsync(isAc: true);
        Assert.False(service.IsDcEcoActive);
        Assert.True(service.LastAcStatus);

        // Display refresh rate must be switched to 60Hz then restored to 165Hz
        Assert.Equal(2, mockDisplay.SetRefreshRateCalls.Count);
        Assert.Equal(60, mockDisplay.SetRefreshRateCalls[0]);
        Assert.Equal(165, mockDisplay.SetRefreshRateCalls[1]);
    }

    [Fact]
    public async Task TransitionToDc_BalancedMode_RetainsSettingsAndDoesNotSwitchRefreshRate()
    {
        var settings = new AppSettings
        {
            DcPowerMode = DcPowerProfilePreference.Balanced,
            AutoRefreshRateSwitching = true
        };

        var agentClient = new MockAcerAgentClient();
        var cpuPower = new MockCpuPowerService();
        var mockDisplay = new MockDisplayRefreshRateService();

        var service = new PowerAutomationService(agentClient, cpuPower, mockDisplay, settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Auto
        };

        await service.ProcessPowerTransitionAsync(isAc: false);

        Assert.False(service.IsDcEcoActive);
        Assert.Empty(mockDisplay.SetRefreshRateCalls);
    }

    [Fact]
    public async Task TransitionToDc_AutoRefreshRateDisabled_DoesNotSwitchRefreshRate()
    {
        var settings = new AppSettings
        {
            DcPowerMode = DcPowerProfilePreference.Eco,
            AutoRefreshRateSwitching = false // Disabled by user
        };

        var agentClient = new MockAcerAgentClient();
        var cpuPower = new MockCpuPowerService();
        var mockDisplay = new MockDisplayRefreshRateService();

        var service = new PowerAutomationService(agentClient, cpuPower, mockDisplay, settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Auto
        };

        await service.ProcessPowerTransitionAsync(isAc: false);

        Assert.True(service.IsDcEcoActive);
        Assert.Empty(mockDisplay.SetRefreshRateCalls);

        await service.ProcessPowerTransitionAsync(isAc: true);
        Assert.Empty(mockDisplay.SetRefreshRateCalls);
    }

    [Fact]
    public async Task ConsecutiveDuplicatePowerEvents_DoesNotReapplyOrOverrideSavedState()
    {
        var settings = new AppSettings
        {
            DcPowerMode = DcPowerProfilePreference.Eco,
            AutoRefreshRateSwitching = true
        };

        var agentClient = new MockAcerAgentClient();
        var cpuPower = new MockCpuPowerService();
        var mockDisplay = new MockDisplayRefreshRateService();

        int modeCallCount = 0;
        var service = new PowerAutomationService(agentClient, cpuPower, mockDisplay, settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () =>
            {
                modeCallCount++;
                return OperatingMode.Extreme;
            },
            GetCurrentFanMode = () => FanMode.Auto
        };

        await service.ProcessPowerTransitionAsync(isAc: false);
        Assert.Equal(1, modeCallCount);
        Assert.Single(mockDisplay.SetRefreshRateCalls);

        // Consecutive identical DC event
        await service.ProcessPowerTransitionAsync(isAc: false);
        Assert.Equal(1, modeCallCount); // Saved mode getter was not reinvoked
        Assert.Single(mockDisplay.SetRefreshRateCalls); // No redundant display switch
    }

    [Fact]
    public void PowerAutomationService_DebounceAndDisposal_ExecutesCleanly()
    {
        var settings = new AppSettings();
        var agentClient = new MockAcerAgentClient();
        var cpuPower = new MockCpuPowerService();
        var mockDisplay = new MockDisplayRefreshRateService();

        var service = new PowerAutomationService(agentClient, cpuPower, mockDisplay, settings, attachSystemEvents: false);

        service.TriggerDebounce();
        service.TriggerDebounce(); // Multiple triggers within debounce window

        service.Dispose();
        service.TriggerDebounce(); // Should be a safe no-op after disposal
    }

    [Fact]
    public async Task TurningTheSixtyHertzOptionOffOnBattery_PutsThePanelBack()
    {
        // The bug: Eco lowered the panel to 60 Hz, the user then cleared the option, and the display stayed at
        // 60 Hz for the rest of the battery session because only the AC branch ever raised it again.
        var settings = new AppSettings
        {
            DcPowerMode = DcPowerProfilePreference.Eco,
            AutoRefreshRateSwitching = true
        };

        var mockDisplay = new MockDisplayRefreshRateService { CurrentHz = 165 };
        var service = new PowerAutomationService(
            new MockAcerAgentClient(), new MockCpuPowerService(), mockDisplay, settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Auto
        };

        await service.ProcessPowerTransitionAsync(isAc: false);
        Assert.Equal(60, mockDisplay.CurrentHz);

        settings.AutoRefreshRateSwitching = false;
        await service.ProcessPowerTransitionAsync(isAc: false);

        Assert.Equal(165, mockDisplay.CurrentHz);
        Assert.Equal(new[] { 60, 165 }, mockDisplay.SetRefreshRateCalls);
    }

    [Fact]
    public async Task SwitchingFromEcoToBalancedOnBattery_PutsThePanelBack()
    {
        // Balanced promises to keep the current settings, but a rate Eco lowered is Eco's doing, not the user's.
        var settings = new AppSettings
        {
            DcPowerMode = DcPowerProfilePreference.Eco,
            AutoRefreshRateSwitching = true
        };

        var mockDisplay = new MockDisplayRefreshRateService { CurrentHz = 165 };
        var service = new PowerAutomationService(
            new MockAcerAgentClient(), new MockCpuPowerService(), mockDisplay, settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Auto
        };

        await service.ProcessPowerTransitionAsync(isAc: false);
        Assert.Equal(60, mockDisplay.CurrentHz);

        settings.DcPowerMode = DcPowerProfilePreference.Balanced;
        await service.ProcessPowerTransitionAsync(isAc: false);

        Assert.Equal(165, mockDisplay.CurrentHz);
        Assert.False(service.IsDcEcoActive);
    }
}

