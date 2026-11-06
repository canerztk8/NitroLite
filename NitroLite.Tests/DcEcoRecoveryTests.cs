using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Battery Eco must never leave the adapter (AC) power-plan value changed, and must survive a crash on battery.
/// </summary>
public class DcEcoRecoveryTests
{
    private sealed class RecordingCpuPowerService : CpuPowerService
    {
        public List<(CpuBoostMode Mode, bool Ac, bool Dc)> Writes { get; } = new();
        public CpuBoostMode DcValue { get; set; } = CpuBoostMode.Efficient;

        public RecordingCpuPowerService() : base(autoInitialize: false, bypassNativePowerCalls: true) { }

        public override Task<bool> SetBoostModeAsync(CpuBoostMode mode, bool targetAc = true, bool targetDc = true)
        {
            Writes.Add((mode, targetAc, targetDc));
            if (targetDc) DcValue = mode;
            return Task.FromResult(true);
        }

        public override CpuBoostMode? QueryBoostModeExact(bool isAc) => isAc ? CurrentMode : DcValue;
    }

    private sealed class NullDisplay : IDisplayRefreshRateService
    {
        public List<int> Calls { get; } = new();
        public int GetCurrentRefreshRate() => 165;
        public int GetMaxRefreshRate() => 165;
        public bool SetRefreshRate(int hz) { Calls.Add(hz); return true; }
    }

    private static string TempStatePath() => Path.Combine(Path.GetTempPath(), $"nitrolite_eco_test_{Guid.NewGuid():N}.json");

    private static PowerAutomationService CreateService(RecordingCpuPowerService cpu, string? statePath, NullDisplay? display = null)
    {
        var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco, AutoRefreshRateSwitching = true };
        return new PowerAutomationService(new MockAcerAgentClient(), cpu, display ?? new NullDisplay(), settings, attachSystemEvents: false)
        {
            EcoStateFilePath = statePath,
            GetCurrentOperatingMode = () => OperatingMode.Turbo,
            GetCurrentFanMode = () => FanMode.Auto
        };
    }

    [Fact]
    public async Task BatteryEco_WritesOnlyTheDcPowerPlanValue()
    {
        var cpu = new RecordingCpuPowerService();
        var service = CreateService(cpu, null);

        await service.ProcessPowerTransitionAsync(isAc: false);

        var ecoWrite = Assert.Single(cpu.Writes);
        Assert.Equal(CpuBoostMode.Disabled, ecoWrite.Mode);
        Assert.False(ecoWrite.Ac);
        Assert.True(ecoWrite.Dc);
    }

    [Fact]
    public async Task AdapterReconnect_RestoresPreviousDcValueWithoutTouchingAc()
    {
        var cpu = new RecordingCpuPowerService { DcValue = CpuBoostMode.Efficient };
        var service = CreateService(cpu, null);

        await service.ProcessPowerTransitionAsync(isAc: false);
        await service.ProcessPowerTransitionAsync(isAc: true);

        Assert.All(cpu.Writes, w => Assert.False(w.Ac));
        Assert.Equal(CpuBoostMode.Efficient, cpu.Writes.Last().Mode);
        Assert.False(service.IsDcEcoActive);
    }

    [Fact]
    public async Task EcoState_IsPersistedOnBattery_AndRecoveredByANewInstance()
    {
        var path = TempStatePath();
        try
        {
            var cpu = new RecordingCpuPowerService { DcValue = CpuBoostMode.Aggressive };
            var first = CreateService(cpu, path);
            await first.ProcessPowerTransitionAsync(isAc: false);
            Assert.True(File.Exists(path));

            // Simulated crash: a fresh instance starts on the adapter and must undo Eco.
            var cpu2 = new RecordingCpuPowerService { DcValue = CpuBoostMode.Disabled };
            var second = CreateService(cpu2, path);
            Assert.True(second.RestorePersistedEcoState());
            Assert.Equal(OperatingMode.Turbo, second.SavedOperatingMode);

            await second.ProcessPowerTransitionAsync(isAc: true);

            Assert.Equal((CpuBoostMode.Aggressive, false, true), cpu2.Writes.Last());
            Assert.False(second.IsDcEcoActive);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RevertEcoForExit_RestoresBaselineAndClearsPersistedState()
    {
        var path = TempStatePath();
        try
        {
            var cpu = new RecordingCpuPowerService { DcValue = CpuBoostMode.Enabled };
            var display = new NullDisplay();
            var service = CreateService(cpu, path, display);
            await service.ProcessPowerTransitionAsync(isAc: false);

            Assert.True(await service.RevertEcoForExitAsync());

            Assert.Equal((CpuBoostMode.Enabled, false, true), cpu.Writes.Last());
            Assert.Equal(165, display.Calls.Last());
            Assert.False(service.IsDcEcoActive);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
