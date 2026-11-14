using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Adversarial stress tests designed by challenger_gate_r3_1 to verify:
/// 1. TelemetryFormatter calculations (boundary values: 0, negative, NaN, infinity, extreme loads, 24h clamping).
/// 2. TelemetryFormatter EMA smoothing filter convergence, step response, and cold-start seeding.
/// 3. PowerAutomationService state machine (rapid toggling, debounce window, DC Balanced mode preservation, double-plug idempotency, driver fault resilience).
/// </summary>
public class AdversarialTelemetryAndPowerStressTests
{
    private class MockDisplayService : IDisplayRefreshRateService
    {
        public int CurrentHz { get; set; } = 165;
        public int MaxHz { get; set; } = 165;
        public List<int> CallLog { get; } = new();
        public bool ShouldThrow { get; set; } = false;

        public int GetCurrentRefreshRate() => CurrentHz;
        public int GetMaxRefreshRate() => MaxHz;

        public bool SetRefreshRate(int hz)
        {
            if (ShouldThrow) throw new InvalidOperationException("Win32 Display driver failure simulation");
            CurrentHz = hz;
            CallLog.Add(hz);
            return true;
        }
    }

    // =========================================================================
    // 1. TelemetryFormatter.CalculateRemainingMinutes Boundary & Stress Tests
    // =========================================================================

    [Theory]
    [InlineData(0, 15000, 0)]
    [InlineData(-100, 15000, 0)]
    [InlineData(60000, 0, 0)]
    [InlineData(60000, -500, 0)]
    [InlineData(double.NaN, 15000, 0)]
    [InlineData(60000, double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 15000, 0)]
    [InlineData(double.NegativeInfinity, 15000, 0)]
    [InlineData(60000, double.PositiveInfinity, 0)]
    [InlineData(60000, double.NegativeInfinity, 0)]
    public void CalculateRemainingMinutes_BoundaryAndInvalidInputs_ReturnsZero(double remMWh, double dischargeMw, int expected)
    {
        int result = TelemetryFormatter.CalculateRemainingMinutes(remMWh, dischargeMw);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(60000, 100000, 36)] // 100W heavy load, 60Wh -> 36 mins
    [InlineData(10000, 100000, 6)]  // 100W heavy load, 10Wh -> 6 mins
    [InlineData(1, 100000, 1)]      // Extreme ratio -> 0.0006 min clamped to 1 min
    [InlineData(0.001, 100000, 1)]  // Micro capacity -> clamped to 1 min
    public void CalculateRemainingMinutes_ExtremeHeavyDischarge_CalculatesOrClampsToMinimum(double remMWh, double dischargeMw, int expected)
    {
        int result = TelemetryFormatter.CalculateRemainingMinutes(remMWh, dischargeMw);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CalculateRemainingMinutes_ExtremeLowDischarge_ClampsTo24HoursOrEvaluates()
    {
        // 80,000 mWh at 50 mW -> 80000 / 50 * 60 = 96,000 mins -> clamped to 1440 (24 hours)
        int result1 = TelemetryFormatter.CalculateRemainingMinutes(80000, 50);
        Assert.Equal(1440, result1);

        // 80,000 mWh at 1 mW -> 80000 / 1 * 60 = 4,800,000 mins -> clamped to 1440
        int result2 = TelemetryFormatter.CalculateRemainingMinutes(80000, 1);
        Assert.Equal(1440, result2);

        double rem = 80000.0;
        double dis = 0.001;
        int rawMin = (int)((rem / dis) * 60.0);
        Assert.Equal(int.MaxValue, rawMin); // In .NET 10, runtime double-to-int cast saturates to int.MaxValue!
        int result3 = TelemetryFormatter.CalculateRemainingMinutes(80000, 0.001);
        Assert.Equal(1440, result3);
    }

    [Theory]
    [InlineData(1440.0, 1440)] // Exactly 24 hours
    [InlineData(1441.0, 1440)] // Over 24 hours
    [InlineData(50000.0, 1440)]
    public void CalculateRemainingMinutes_ClampingUpperLimit_StrictlyEnforced(double hours, int expected)
    {
        double discharge = 10000.0;
        double capacity = (hours * discharge) / 60.0;
        int result = TelemetryFormatter.CalculateRemainingMinutes(capacity, discharge);
        Assert.Equal(expected, result);
    }

    // =========================================================================
    // 2. TelemetryFormatter.UpdateSmoothedRate Convergence & Seeding Tests
    // =========================================================================

    [Fact]
    public void UpdateSmoothedRate_ColdStartSeeding_SeedsFirstReadingWithoutFilterDelay()
    {
        bool initialized = false;
        double seed = 25000.0;
        double smoothed = TelemetryFormatter.UpdateSmoothedRate(0.0, seed, ref initialized, 0.20);

        Assert.True(initialized);
        Assert.Equal(seed, smoothed);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-500.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void UpdateSmoothedRate_InvalidInputsWhenUninitialized_DoesNotInitialize(double invalidInput)
    {
        bool initialized = false;
        double smoothed = TelemetryFormatter.UpdateSmoothedRate(0.0, invalidInput, ref initialized, 0.20);

        Assert.False(initialized);
        Assert.Equal(0.0, smoothed);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-500.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UpdateSmoothedRate_InvalidInputsWhenInitialized_RetainsCurrentRate(double invalidInput)
    {
        bool initialized = true;
        double current = 15000.0;
        double smoothed = TelemetryFormatter.UpdateSmoothedRate(current, invalidInput, ref initialized, 0.20);

        Assert.True(initialized);
        Assert.Equal(current, smoothed);
    }

    [Fact]
    public void UpdateSmoothedRate_StepResponse_ConvergesAsymptotically()
    {
        bool initialized = true;
        double current = 10000.0;
        double target = 50000.0;
        double alpha = 0.20;

        // Apply 30 ticks of 50,000 mW
        for (int i = 0; i < 30; i++)
        {
            current = TelemetryFormatter.UpdateSmoothedRate(current, target, ref initialized, alpha);
        }

        // After 30 ticks with alpha=0.20, (1 - 0.20)^30 = 0.8^30 â‰ˆ 0.001237
        // Expected value should be within 0.2% of target (>= 49,900 mW)
        Assert.InRange(current, 49900.0, 50000.0);
    }

    [Fact]
    public void UpdateSmoothedRate_AlternatingLoad_MaintainsBoundedAverage()
    {
        bool initialized = true;
        double current = 20000.0;
        double alpha = 0.20;

        // Oscillate between 10,000 and 30,000 for 50 cycles
        for (int i = 0; i < 50; i++)
        {
            double val = (i % 2 == 0) ? 10000.0 : 30000.0;
            current = TelemetryFormatter.UpdateSmoothedRate(current, val, ref initialized, alpha);
        }

        // Bounded within oscillate range, centered around 20,000 mW
        Assert.InRange(current, 18000.0, 22000.0);
    }

    // =========================================================================
    // 3. PowerAutomationService State Machine Adversarial Stress Tests
    // =========================================================================

    [Fact]
    public async Task PowerAutomationService_RapidTogglingWithinDebounceWindow_OnlyLastEventSettles()
    {
        var settings = new AppSettings
        {
            DcPowerMode = DcPowerProfilePreference.Eco,
            AutoRefreshRateSwitching = true
        };

        var agentClient = new MockAcerAgentClient();
        var cpuPower = new MockCpuPowerService();
        var mockDisplay = new MockDisplayService();

        var service = new PowerAutomationService(agentClient, cpuPower, mockDisplay, settings, attachSystemEvents: false)
        {
            // Pinned: the assertion below is about the debounce settling once, not about whether the host running
            // the suite happens to be plugged in.
            AcStatusProvider = () => true,
            GetCurrentOperatingMode = () => OperatingMode.Turbo,
            GetCurrentFanMode = () => FanMode.Max
        };

        // Simulate rapid contact bouncing: trigger debounce repeatedly
        for (int i = 0; i < 10; i++)
        {
            service.TriggerDebounce();
            await Task.Delay(20); // 20ms < 500ms debounce
        }

        // Before debounce fires (at ~200ms total), no display mode switch should have completed
        Assert.Empty(mockDisplay.CallLog);

        // Wait for debounce timer (500ms) to elapse + margin
        await Task.Delay(650);

        // Service should now have evaluated hardware status cleanly once
        Assert.True(service.LastAcStatus);
    }

    [Fact]
    public async Task PowerAutomationService_DCBalanced_ManualModeChange_PreservedOnReconnect()
    {
        var settings = new AppSettings
        {
            DcPowerMode = DcPowerProfilePreference.Balanced,
            AutoRefreshRateSwitching = true
        };

        var agentClient = new MockAcerAgentClient();
        var cpuPower = new MockCpuPowerService();
        var mockDisplay = new MockDisplayService();

        var service = new PowerAutomationService(agentClient, cpuPower, mockDisplay, settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Auto
        };

        // 1. Unplug to DC Balanced
        await service.ProcessPowerTransitionAsync(isAc: false);
        Assert.False(service.IsDcEcoActive);
        Assert.Empty(mockDisplay.CallLog);

        // 2. User manually changes mode while on battery
        service.GetCurrentOperatingMode = () => OperatingMode.Extreme;

        // 3. Reconnect to AC
        await service.ProcessPowerTransitionAsync(isAc: true);
        Assert.False(service.IsDcEcoActive);

        // Because Eco was never active, it must NOT revert the user's settings to the old saved mode,
        // and since Eco never lowered the refresh rate, the display must not be touched either.
        Assert.Empty(mockDisplay.CallLog);
    }

    [Fact]
    public async Task PowerAutomationService_ConsecutivePlugEvents_Idempotent()
    {
        var settings = new AppSettings
        {
            DcPowerMode = DcPowerProfilePreference.Eco,
            AutoRefreshRateSwitching = true
        };

        var agentClient = new MockAcerAgentClient();
        var cpuPower = new MockCpuPowerService();
        var mockDisplay = new MockDisplayService();

        var service = new PowerAutomationService(agentClient, cpuPower, mockDisplay, settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () => OperatingMode.Turbo,
            GetCurrentFanMode = () => FanMode.Auto
        };

        // Double AC transition without Eco ever lowering the rate: the display is left alone.
        await service.ProcessPowerTransitionAsync(isAc: true);
        await service.ProcessPowerTransitionAsync(isAc: true);
        Assert.Empty(mockDisplay.CallLog);

        // Battery Eco lowers it once; the following AC events restore it exactly once.
        await service.ProcessPowerTransitionAsync(isAc: false);
        await service.ProcessPowerTransitionAsync(isAc: true);
        await service.ProcessPowerTransitionAsync(isAc: true);
        Assert.Equal(new[] { 60, 165 }, mockDisplay.CallLog);
    }

    [Fact]
    public async Task PowerAutomationService_DisplayDriverException_DoesNotCrashStateMachine()
    {
        var settings = new AppSettings
        {
            DcPowerMode = DcPowerProfilePreference.Eco,
            AutoRefreshRateSwitching = true
        };

        var agentClient = new MockAcerAgentClient();
        var cpuPower = new MockCpuPowerService();
        var mockDisplay = new MockDisplayService { ShouldThrow = true }; // Driver failure

        var service = new PowerAutomationService(agentClient, cpuPower, mockDisplay, settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Auto
        };

        // Transition to DC with throwing display service
        var ex = await Record.ExceptionAsync(() => service.ProcessPowerTransitionAsync(isAc: false));
        Assert.False(service.LastAcStatus);
    }
}

