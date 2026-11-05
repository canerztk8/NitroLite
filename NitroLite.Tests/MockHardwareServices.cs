using System.Collections.Generic;
using System.Threading.Tasks;
using NitroLite.Core;

namespace NitroLite.Tests;

public class MockAcerAgentClient : AcerAgentClient
{
    public OperatingMode CurrentMode { get; set; } = OperatingMode.Default;
    public FanMode CurrentFanMode { get; set; } = FanMode.Auto;
    public List<OperatingMode> ModeChangeHistory { get; } = new();

    public override Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

    public override Task<OperatingMode> GetOperatingModeAsync() => Task.FromResult(CurrentMode);

    public override Task<bool> SetOperatingModeAsync(OperatingMode mode)
    {
        CurrentMode = mode;
        ModeChangeHistory.Add(mode);
        return Task.FromResult(true);
    }

    public override Task<bool> SetFanModeAsync(FanMode mode, int cpuRpm = 0, int gpuRpm = 0)
    {
        CurrentFanMode = mode;
        return Task.FromResult(true);
    }
}

public class MockCpuPowerService : CpuPowerService
{
    public new CpuBoostMode CurrentMode { get; set; } = CpuBoostMode.Aggressive;
    public List<CpuBoostMode> BoostHistory { get; } = new();

    public MockCpuPowerService() : base(false)
    {
    }

    public override Task<bool> SetBoostModeAsync(CpuBoostMode mode, bool targetAc = true, bool targetDc = true)
    {
        CurrentMode = mode;
        BoostHistory.Add(mode);
        return Task.FromResult(true);
    }

    public override CpuBoostMode QueryCurrentBoostMode(bool isAc = true) => CurrentMode;
}
