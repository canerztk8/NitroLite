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

    /// <summary>1 once the "empty fan list, fans not on Auto" disagreement has been logged, so a poll does not repeat it.</summary>
    private int _loggedEmptyFanList;

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

    // What the service said the machine has. These answers describe the hardware, which does not change while the
    // service runs, so they are read once per session. Only an answer is stored: a read that came back empty-handed
    // (null) is retried on the next call, because "the service did not answer" is not "the machine has nothing".
    private IReadOnlyList<AgentFan>? _cachedFans;
    private IReadOnlyList<OperatingMode>? _cachedModes;
    // Boxed so one reference carries the whole answer: an int? is two fields and could be read half-written by a
    // concurrent poll while another thread stores it.
    private volatile object? _cachedMux;

    /// <summary>1 once the "service maximum is below the safety floor" disagreement has been logged.</summary>
    private int _loggedMaxBelowFloor;

    /// <summary>
    /// Forgets the cached service answers so the next <see cref="GetCapabilitiesAsync"/> asks again. The client has no
    /// reconnect event, so the controller calls this itself when it finds the service unreachable (a restart passes
    /// through that state); callers that know better (a driver or firmware change) can call it directly.
    /// </summary>
    public void InvalidateCapabilityCache()
    {
        _cachedFans = null;
        _cachedModes = null;
        _cachedMux = null;
    }

    private async Task<IReadOnlyList<AgentFan>?> GetFansCachedAsync()
    {
        var cached = _cachedFans;
        if (cached != null) return cached;

        var fans = await ProbeReferenceAsync(() => _agent.GetFanListAsync()).ConfigureAwait(false);
        if (fans != null) _cachedFans = fans;
        return fans;
    }

    private async Task<IReadOnlyList<OperatingMode>?> GetModesCachedAsync()
    {
        var cached = _cachedModes;
        if (cached != null) return cached;

        var modes = await ProbeReferenceAsync(() => _agent.GetOperatingModesAsync()).ConfigureAwait(false);
        if (modes != null) _cachedModes = modes;
        return modes;
    }

    private async Task<int?> GetMuxCachedAsync()
    {
        if (_cachedMux is int cached) return cached;

        var mux = await ProbeAsync(() => _agent.GetGpuModeCapabilityAsync()).ConfigureAwait(false);
        if (mux.HasValue) _cachedMux = mux.Value;
        return mux;
    }

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
            // The service is gone (stopped, restarting, updating): whatever it said before may not hold when it returns.
            InvalidateCapabilityCache();
            return new HardwareCapabilities { Backend = BackendName, ServiceAvailable = false };
        }

        var fanMode = await ProbeAsync(() => _agent.GetFanModeAsync()).ConfigureAwait(false);
        var mode = await ProbeAsync(() => _agent.TryGetOperatingModeAsync()).ConfigureAwait(false);
        var mux = await GetMuxCachedAsync().ConfigureAwait(false);
        var chargeMode = await ProbeAsync(() => _careCenter.GetBatteryChargeModeAsync()).ConfigureAwait(false);
        var backlight = await ProbeAsync(() => _agent.GetBacklightTimeoutAsync()).ConfigureAwait(false);
        var keyboard = await ProbeKeyboardAsync().ConfigureAwait(false);

        // What the service itself says the machine has (the SUPPORT_* answers NitroSense relies on). Null is "no
        // answer", and every consumer below keeps its old behaviour for that.
        var fans = await GetFansCachedAsync().ConfigureAwait(false);
        var modes = await GetModesCachedAsync().ConfigureAwait(false);

        // The service says there are no fans to control yet the fan mode reads back as something other than Auto: the
        // two answers disagree. The empty list is followed (no control is shown), but the disagreement is worth one
        // line in the log, because it is the first thing to look at if a model reports a missing fan page.
        if (fans is { Count: 0 } && fanMode.HasValue && fanMode.Value != FanMode.Auto &&
            Interlocked.Exchange(ref _loggedEmptyFanList, 1) == 0)
        {
            AppLog.Warn("The service reported no fans", $"SUPPORT_FAN_CONTROL is empty but the fan mode reads {fanMode.Value}");
        }

        var capabilities = new HardwareCapabilities
        {
            Backend = BackendName,
            ServiceAvailable = true,
            // A list the service sent is the answer; only without one does a readable fan mode stand in for it.
            FanControl = fans != null ? fans.Count > 0 : fanMode.HasValue,
            OperatingModeControl = mode.HasValue,
            // The capability is a bitmask of the GPU modes the firmware has; an answer of 0 means there is no switch
            // even though the service replied.
            MuxSwitch = mux is int bits && bits > 0,
            BatteryChargeLimit = chargeMode.HasValue,
            KeyboardLighting = keyboard?.ZoneCount > 0,
            WinKeyLock = (await ProbeAsync(() => _agent.GetWinKeyEnabledAsync()).ConfigureAwait(false)).HasValue,
            BacklightTimeout = backlight.HasValue
        };

        // An empty or missing list means "unknown", which the mode policy reads as "show every mode"; only a real
        // list narrows the cards.
        return modes is { Count: > 0 }
            ? capabilities with { SupportedOperatingModes = modes }
            : capabilities;
    }

    /// <summary>The reference-type twin of <see cref="ProbeAsync{T}"/>: any failure is "no answer".</summary>
    private static async Task<T?> ProbeReferenceAsync<T>(Func<Task<T?>> read) where T : class
    {
        try { return await read().ConfigureAwait(false); }
        catch { return null; }
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
            AppLog.Warn("The MUX write was refused", $"mode {mode} is not in the capability bitmask ({bits})");
            return false;
        }

        return await WriteAsync(() => _agent.SetGpuModeAsync(mode)).ConfigureAwait(false);
    }

    public Task<int?> GetGpuModeCapabilityAsync() => _agent.GetGpuModeCapabilityAsync();

    public Task<FanMode?> GetFanModeAsync() => _agent.GetFanModeAsync();

    /// <summary>
    /// Writes the fan mode. A custom speed is first pulled into the range the service reports for that fan
    /// (fan_custom_min_speed / fan_custom_max_speed), so the service is never asked for a speed it does not offer.
    /// Without a usable range, or in any mode other than Custom, the values go through exactly as given.
    /// </summary>
    public async Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
    {
        if (mode == FanMode.Custom && !_disposed)
        {
            var fans = await GetFansCachedAsync().ConfigureAwait(false);
            if (fans is { Count: > 0 })
            {
                // A negative value means "the client's stored speed"; resolve it here so the stored speed is clamped too.
                if (cpuPercent < 0) cpuPercent = _agent.CustomFanCpuPercent;
                if (gpuPercent < 0) gpuPercent = _agent.CustomFanGpuPercent;
                cpuPercent = ClampToFan(fans, "CPU", 0, cpuPercent);
                gpuPercent = ClampToFan(fans, "GPU", 1, gpuPercent);
            }
        }

        return await WriteAsync(() => _agent.SetFanModeAsync(mode, cpuPercent, gpuPercent)).ConfigureAwait(false);
    }

    /// <summary>
    /// The fan is found by name. Only the unnamed fans in the list are matched by position, and only when its
    /// indices are distinct: two nameless fans sharing an index cannot be told apart, so the speed is left alone
    /// rather than clamped into the range of whichever fan happens to come first. A fan with no usable range also
    /// leaves the speed alone.
    /// </summary>
    private int ClampToFan(IReadOnlyList<AgentFan> fans, string name, int position, int percent)
    {
        var fan = fans.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (fan == null)
        {
            var unnamed = fans.Where(f => string.IsNullOrEmpty(f.Name)).ToList();
            if (unnamed.Select(f => f.Index).Distinct().Count() == unnamed.Count)
                fan = unnamed.FirstOrDefault(f => f.Index == position);
        }

        if (fan is not { HasRange: true }) return percent;

        // The safety floor (AppSettings.MinCustomFanPercent) still wins downstream, so a service maximum below it is
        // not honoured; say so once, because the user sees a fan that runs faster than the service's own limit.
        if (fan.MaxSpeed < AppSettings.MinCustomFanPercent && Interlocked.Exchange(ref _loggedMaxBelowFloor, 1) == 0)
        {
            AppLog.Warn("The service's fan maximum is below the safety floor",
                $"{fan.Name} max {fan.MaxSpeed}% < {AppSettings.MinCustomFanPercent}%; the floor is kept");
        }

        return Math.Clamp(percent, fan.MinSpeed, fan.MaxSpeed);
    }

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

    /// <summary>The Acer service stores whether the Windows key is enabled.</summary>
    public Task<bool> SetWinKeyLockAsync(bool enabled) => WriteAsync(() => _agent.SetWinKeyEnabledAsync(!enabled));

    /// <summary>
    /// Runs one write behind the gate. The gate is released in a <c>finally</c>, so a write that throws does not
    /// wedge every later write for the rest of the session.
    /// </summary>
    private async Task<bool> WriteAsync(Func<Task<bool>> write)
    {
        // A disposed backend writes nothing; see Dispose for why the gate itself is left alone.
        if (_disposed) return false;

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

        // The write gate is not disposed: a write already inside it still releases it in its finally, and releasing a
        // disposed SemaphoreSlim throws ObjectDisposedException on that caller's thread. It holds no unmanaged
        // resource unless AvailableWaitHandle is read, which this class never does; _disposed stops new writes.
        _agent.Dispose();
    }
}
