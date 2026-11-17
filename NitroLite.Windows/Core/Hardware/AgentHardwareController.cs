namespace NitroLite.Core.Hardware;

/// <summary>
/// The Acer-service backend: <see cref="AcerAgentClient"/> for telemetry, fans, modes, MUX and lighting, and
/// <see cref="CareCenterClient"/> for the battery charge limit.
/// <para>
/// This is a wrapper, not a rewrite. Every method forwards to the same client call the pages used to make directly,
/// with the same arguments and the same return value, so the behaviour of the application does not change when it
/// starts going through the interface. What it adds is one thing the scattered call sites could not give: a single
/// gate in front of every write.
/// </para>
/// <para>
/// The gate matters because the fan curve, the thermal safety floor, school mode, game detection and the user's own
/// clicks all write the same registers from different tasks. Two of those writes interleaving leaves the EC in a
/// state neither of them asked for, and the read-back that follows then reports a value nobody set. Reads are left
/// ungated: they are idempotent, and serialising them would make the telemetry timer wait behind a fan write.
/// </para>
/// </summary>
public sealed class AgentHardwareController : IHardwareController, ICareCenterTrust
{
    private readonly AcerAgentClient _agent;
    private readonly CareCenterClient _careCenter;

    /// <summary>One gate for every write, whichever client it goes to.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private bool _disposed;

    /// <summary>
    /// Wraps the two clients. The controller takes ownership of <paramref name="agent"/> and disposes it; the
    /// Care Center client holds no connection of its own and is left alone.
    /// </summary>
    public AgentHardwareController(AcerAgentClient agent, CareCenterClient careCenter)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _careCenter = careCenter ?? throw new ArgumentNullException(nameof(careCenter));
    }

    /// <summary>The client behind this backend, for the call sites that still need its events.</summary>
    public AcerAgentClient Agent => _agent;

    public string BackendName => "AASSvc";

    /// <summary>
    /// True when the Care Center agent answered with a certificate other than the pinned one. The battery page turns
    /// this into an explanation and a "trust the new certificate" button, because the alternative is a charge limit
    /// that silently stopped working after an Acer update.
    /// </summary>
    public bool CertificateMismatch => _careCenter.CertificateMismatch;

    /// <inheritdoc />
    public bool ForgetPinnedCertificate() => _careCenter.ForgetPinnedCertificate();

    public bool IsAvailable => _agent.IsConnected;

    public Task<bool> EnsureConnectedAsync() => _agent.EnsureConnectedAsync();

    /// <summary>
    /// Asks each subsystem whether it answers. A capability is only reported as supported when the hardware returned
    /// a value for it: a machine with the service running but no MUX switch must not get a MUX control.
    /// </summary>
    public async Task<HardwareCapabilities> GetCapabilitiesAsync()
    {
        bool connected = await EnsureConnectedAsync().ConfigureAwait(false);
        if (!connected)
        {
            return new HardwareCapabilities { Backend = BackendName, ServiceAvailable = false };
        }

        var fanMode = await ProbeAsync(() => _agent.GetFanModeAsync()).ConfigureAwait(false);
        var mode = await ProbeAsync(() => _agent.TryGetOperatingModeAsync()).ConfigureAwait(false);
        var mux = await ProbeAsync(() => _agent.GetGpuModeCapabilityAsync()).ConfigureAwait(false);
        var chargeMode = await ProbeAsync(() => _careCenter.GetBatteryChargeModeAsync()).ConfigureAwait(false);
        var backlight = await ProbeAsync(() => _agent.GetBacklightTimeoutAsync()).ConfigureAwait(false);
        var keyboard = await ProbeKeyboardAsync().ConfigureAwait(false);

        return new HardwareCapabilities
        {
            Backend = BackendName,
            ServiceAvailable = true,
            FanControl = fanMode.HasValue,
            OperatingModeControl = mode.HasValue,
            MuxSwitch = mux.HasValue,
            BatteryChargeLimit = chargeMode.HasValue,
            KeyboardLighting = keyboard?.ZoneCount > 0,
            BacklightTimeout = backlight.HasValue
        };
    }

    /// <summary>
    /// Runs one capability read and turns any failure into "no answer". A probe that throws would take the whole
    /// snapshot down and hide the capabilities that did answer.
    /// </summary>
    private static async Task<T?> ProbeAsync<T>(Func<Task<T?>> read) where T : struct
    {
        try { return await read().ConfigureAwait(false); }
        catch { return null; }
    }

    /// <summary>
    /// The keyboard capability is a reference type, so it gets its own probe: two overloads that differ only in a
    /// generic constraint cannot coexist, and a nullable reference type cannot satisfy a <c>class</c> constraint.
    /// </summary>
    private async Task<KeyboardCapability?> ProbeKeyboardAsync()
    {
        try { return await _agent.GetKeyboardCapabilityAsync().ConfigureAwait(false); }
        catch { return null; }
    }

    /// <summary>
    /// The service's own sample. Its fan speeds are tachometer readings, so the flag is set here rather than left to
    /// the caller: the fan-stall watchdog is allowed to judge this sample and nothing else.
    /// </summary>
    public async Task<MonitorData?> GetMonitorDataAsync()
    {
        var data = await _agent.GetMonitorDataAsync().ConfigureAwait(false);
        if (data != null) data.IsFanRpmMeasured = true;
        return data;
    }

    public Task<OperatingMode?> GetOperatingModeAsync() => _agent.TryGetOperatingModeAsync();

    public Task<bool> SetOperatingModeAsync(OperatingMode mode) =>
        WriteAsync(() => _agent.SetOperatingModeAsync(mode));

    public Task<GpuOperatingMode?> GetGpuModeAsync() => _agent.GetGpuModeAsync();

    /// <summary>
    /// Writes the MUX mode, but only one the firmware advertises. The service answers "success" to a mode it does
    /// not have, so the capability bitmask is the only thing that separates a staged switch from a no-op that still
    /// reads as applied. The direct-WMI backend has always checked it; this one did not, which is how a third mode
    /// could report "applied" on a machine whose firmware never took it.
    /// </summary>
    public async Task<bool> SetGpuModeAsync(GpuOperatingMode mode)
    {
        var mask = await _agent.GetGpuModeCapabilityAsync().ConfigureAwait(false);
        if (mask is int bits && bits > 0 && !AcerRegistrySnapshot.IsGpuModeSupported(bits, mode))
        {
            AppLog.Warn("MUX yazımı reddedildi", $"mod {mode} yetenek bitmaskesinde yok ({bits})");
            return false;
        }

        return await WriteAsync(() => _agent.SetGpuModeAsync(mode)).ConfigureAwait(false);
    }

    public Task<int?> GetGpuModeCapabilityAsync() => _agent.GetGpuModeCapabilityAsync();

    public Task<FanMode?> GetFanModeAsync() => _agent.GetFanModeAsync();

    public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1) =>
        WriteAsync(() => _agent.SetFanModeAsync(mode, cpuPercent, gpuPercent));

    public Task<LightingConfig?> GetLightingConfigAsync() => _agent.GetLightingConfigAsync();

    public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors) =>
        WriteAsync(() => _agent.SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors));

    public Task<bool> TurnOffLightingAsync() => WriteAsync(() => _agent.TurnOffLightingAsync());

    public Task<bool?> GetBacklightTimeoutAsync() => _agent.GetBacklightTimeoutAsync();

    public Task<bool> SetBacklightTimeoutAsync(bool enable30s) =>
        WriteAsync(() => _agent.SetBacklightTimeoutAsync(enable30s));

    public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => _careCenter.GetBatteryChargeModeAsync();

    public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) =>
        WriteAsync(() => _careCenter.SetBatteryChargeModeAsync(mode));

    /// <summary>
    /// The service socket has no CoolBoost command, so this backend cannot answer. Null rather than false: the
    /// hybrid controller reads null as "ask the other backend" and false as "the hardware said no".
    /// </summary>
    public Task<bool?> GetCoolBoostAsync() => Task.FromResult<bool?>(null);

    /// <summary>No CoolBoost command exists on the service socket.</summary>
    public Task<bool> SetCoolBoostAsync(bool on) => Task.FromResult(false);

    /// <summary>No LCD overdrive command exists on the service socket.</summary>
    public Task<bool?> GetLcdOverdriveAsync() => Task.FromResult<bool?>(null);

    /// <summary>No LCD overdrive command exists on the service socket.</summary>
    public Task<bool> SetLcdOverdriveAsync(bool on) => Task.FromResult(false);

    /// <summary>No Windows-key lock command exists on the service socket.</summary>
    public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(false);

    /// <summary>
    /// Runs one write behind the gate. The gate is released in a <c>finally</c>, so a write that throws does not
    /// wedge every later write for the rest of the session.
    /// </summary>
    private async Task<bool> WriteAsync(Func<Task<bool>> write)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await write().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeGate.Dispose();
        _agent.Dispose();
    }
}
