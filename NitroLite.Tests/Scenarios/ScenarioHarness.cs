using NitroLite.Core;
using NitroLite.Core.Hardware;

namespace NitroLite.Tests.Scenarios;

/// <summary>
/// The hardware boundary every scenario test fakes. Nothing here talks to WMI, the Acer service, NVML or the
/// registry: the scenario tests drive the real service classes and only replace the four things that would
/// otherwise need a laptop.
/// </summary>
internal sealed class ScenarioAgentClient : AcerAgentClient
{
    public OperatingMode CurrentMode { get; set; } = OperatingMode.Default;
    public FanMode CurrentFanMode { get; set; } = FanMode.Auto;
    public int CustomCpuPercent { get; private set; }
    public int CustomGpuPercent { get; private set; }

    /// <summary>Every fan write in order, so a scenario can assert the fans never went quiet.</summary>
    public List<(FanMode Mode, int Cpu, int Gpu)> FanWrites { get; } = new();
    public List<OperatingMode> ModeWrites { get; } = new();

    /// <summary>When false, every write reports failure, like a service that stopped answering.</summary>
    public bool WritesSucceed { get; set; } = true;

    /// <summary>When set, a write blocks on this until the scenario releases it. Used to build a real race.</summary>
    public SemaphoreSlim? WriteGate { get; set; }

    /// <summary>When set, the next monitor read returns this instead of the default sample.</summary>
    public Func<MonitorData?>? MonitorDataFactory { get; set; }

    /// <summary>
    /// What the capability query answers. Null means the device never said which modes it has, which the policy
    /// reads as "show everything" — the same answer a service that is not running gives.
    /// </summary>
    public IReadOnlyList<OperatingMode>? SupportedModes { get; set; }

    public override Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

    public override Task<IReadOnlyList<OperatingMode>?> GetOperatingModesAsync() =>
        Task.FromResult(SupportedModes);

    public override async Task<bool> SetOperatingModeAsync(OperatingMode mode)
    {
        if (WriteGate != null) await WriteGate.WaitAsync().ConfigureAwait(false);
        if (!WritesSucceed) return false;
        CurrentMode = mode;
        ModeWrites.Add(mode);
        return true;
    }

    public override async Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
    {
        if (WriteGate != null) await WriteGate.WaitAsync().ConfigureAwait(false);
        if (!WritesSucceed) return false;
        CurrentFanMode = mode;
        // The real client falls back to the properties the caller set before asking for Custom, so the mock has
        // to do the same or every recorded write reads as 0 %.
        if (cpuPercent < 0) cpuPercent = CustomFanCpuPercent;
        if (gpuPercent < 0) gpuPercent = CustomFanGpuPercent;
        CustomCpuPercent = cpuPercent;
        CustomGpuPercent = gpuPercent;
        FanWrites.Add((mode, CustomCpuPercent, CustomGpuPercent));
        return true;
    }

    public override Task<MonitorData?> GetMonitorDataAsync() =>
        Task.FromResult<MonitorData?>(MonitorDataFactory?.Invoke() ?? new MonitorData
        {
            CpuTemperature = 45,
            GpuTemperature = 45,
            CpuFanSpeed = 2400,
            GpuFanSpeed = 2400,
            CpuUsage = 10,
            CpuFrequencyMHz = 2000
        });
}

internal sealed class ScenarioCpuPower : CpuPowerService
{
    public CpuBoostMode Mode { get; set; } = CpuBoostMode.Aggressive;
    public List<CpuBoostMode> Writes { get; } = new();

    public ScenarioCpuPower() : base(autoInitialize: false, bypassNativePowerCalls: true) { }

    public override Task<bool> SetBoostModeAsync(CpuBoostMode mode, bool targetAc = true, bool targetDc = true)
    {
        Mode = mode;
        Writes.Add(mode);
        return Task.FromResult(true);
    }

    public override CpuBoostMode QueryCurrentBoostMode(bool isAc = true) => Mode;

    public override CpuBoostMode? QueryBoostModeExact(bool isAc = true) => Mode;
}

internal sealed class ScenarioDisplay : IDisplayRefreshRateService
{
    public int CurrentHz { get; set; } = 165;
    public int MaxHz { get; set; } = 165;
    public List<int> Writes { get; } = new();

    public int GetCurrentRefreshRate() => CurrentHz;
    public int GetMaxRefreshRate() => MaxHz;

    public bool SetRefreshRate(int hz)
    {
        CurrentHz = hz;
        Writes.Add(hz);
        return true;
    }
}

internal sealed class ScenarioBrightness : IScreenBrightnessService
{
    public int? Level { get; set; } = 80;
    public List<int> Writes { get; } = new();

    public int? GetBrightness() => Level;

    public bool SetBrightness(int percent)
    {
        Level = percent;
        Writes.Add(percent);
        return true;
    }
}

/// <summary>
/// The active power scheme, in memory. School mode reads and writes DC values through this instead of powrprof,
/// so a scenario can assert exactly which settings it lowered and that every one of them came back.
/// </summary>
internal sealed class InMemoryPowerScheme : IPowerSchemeAccess
{
    public Dictionary<(Guid SubGroup, Guid Setting), uint> Dc { get; } = new();
    public List<(Guid SubGroup, Guid Setting, uint Value)> Writes { get; } = new();

    /// <summary>When false, a write is refused, like a setting the active scheme does not carry.</summary>
    public bool WritesSucceed { get; set; } = true;

    public bool TryReadDc(Guid subGroup, Guid setting, out uint value) =>
        Dc.TryGetValue((subGroup, setting), out value);

    public bool TryWriteDc(Guid subGroup, Guid setting, uint value)
    {
        if (!WritesSucceed) return false;
        Dc[(subGroup, setting)] = value;
        Writes.Add((subGroup, setting, value));
        return true;
    }
}

/// <summary>
/// Runs a scenario body on an STA thread with a dispatcher, which is what WPF types require. Every scenario that
/// builds a <see cref="MainWindow"/> goes through this.
/// </summary>
internal static class Sta
{
    public static Task RunAsync(Func<Task> body)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                body().GetAwaiter().GetResult();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}

/// <summary>Builds the four fakes a scenario needs, so each test only states what it cares about.</summary>
internal sealed class ScenarioRig
{
    public ScenarioAgentClient Agent { get; } = new();
    public ScenarioCpuPower Cpu { get; } = new();
    public ScenarioDisplay Display { get; } = new();
    public ScenarioBrightness Brightness { get; } = new();
    public InMemoryPowerScheme Scheme { get; } = new();
    public AppSettings Settings { get; } = new();

    /// <summary>
    /// A school mode service wired to the fakes. The undo record goes to a throw-away file by default: with a null
    /// path the service writes nothing and has nothing to restore, which would make every round-trip test pass for
    /// the wrong reason.
    /// </summary>
    public SchoolModeService BuildSchoolMode(string? backupPath = null) =>
        new(new AgentHardwareController(Agent, new CareCenterClient()), Cpu, Display, Brightness, Settings, Scheme)
        {
            BackupPath = backupPath ?? AppPaths.GetDataFile($"scenario_school_{Guid.NewGuid():N}.json"),
            GetCurrentOperatingMode = () => Agent.CurrentMode,
            GetCurrentFanMode = () => Agent.CurrentFanMode
        };

    public PowerAutomationService BuildPowerAutomation() =>
        new(Agent, Cpu, Display, Settings, attachSystemEvents: false)
        {
            GetCurrentOperatingMode = () => Agent.CurrentMode,
            GetCurrentFanMode = () => Agent.CurrentFanMode
        };
}
