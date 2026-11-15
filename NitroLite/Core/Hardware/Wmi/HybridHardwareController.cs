namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// Picks a backend per call: the Acer service when it is healthy, direct WMI when it is not.
/// <para>
/// The order is not arbitrary. The service is the vendor's own path — it knows the model, it owns the EC registers,
/// and it needs no elevation — so whenever it answers, it wins. Direct WMI is the fallback for the machine that has
/// no Acer software at all, and it is only reached when the service is genuinely absent or frozen, which is decided
/// by the <c>VERSION</c> handshake rather than by a port being open. A service that is running but wedged still holds
/// the socket, so "the port accepts a connection" is not evidence that anything is listening.
/// </para>
/// <para>
/// Keyboard lighting and the backlight timeout always go to the service. The direct-WMI backend does not implement
/// them, and routing them to a backend that would answer "no" would turn a working control into a dead one.
/// </para>
/// </summary>
public sealed class HybridHardwareController : IHardwareController
{
    private readonly IHardwareController _agent;
    private readonly Func<IHardwareController?> _wmiFactory;
    private readonly Func<bool> _useDirectWmi;
    private readonly Func<Task<bool>> _serviceHealthy;

    private IHardwareController? _wmi;
    private bool _wmiCreated;
    private bool _disposed;

    /// <summary>
    /// <paramref name="wmiFactory"/> returns the direct-WMI backend, or null when this build or this session must not
    /// use one (the test harness passes null so no test can ever raise a UAC prompt).
    /// <paramref name="serviceHealthy"/> is the <c>VERSION</c> handshake; it defaults to the agent's own
    /// <see cref="IHardwareController.EnsureConnectedAsync"/>.
    /// </summary>
    public HybridHardwareController(
        IHardwareController agent,
        Func<IHardwareController?>? wmiFactory = null,
        Func<bool>? useDirectWmi = null,
        Func<Task<bool>>? serviceHealthy = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _wmiFactory = wmiFactory ?? (() => null);
        _useDirectWmi = useDirectWmi ?? (() => true);
        _serviceHealthy = serviceHealthy ?? agent.EnsureConnectedAsync;
    }

    public string BackendName => ActiveBackendName;

    /// <summary>Which backend the last resolution chose. Shown in the hardware report and the settings page.</summary>
    public string ActiveBackendName => _activeIsWmi ? "WMI" : _agent.BackendName;

    private bool _activeIsWmi;

    /// <summary>The service backend, always present.</summary>
    public IHardwareController Agent => _agent;

    /// <summary>The direct-WMI backend, or null when it has not been created or is not configured.</summary>
    public IHardwareController? DirectWmi => _wmi;

    /// <summary>True when the direct-WMI backend exists and the user has declined its elevation prompt.</summary>
    public bool UserDeclinedElevation => _wmi is WmiHardwareController wmi && wmi.UserDeclinedElevation;

    /// <summary>The read-back policy of the direct-WMI backend, when it exists. Null otherwise.</summary>
    public ReadBackPolicy? ReadBack => (_wmi as WmiHardwareController)?.ReadBack;

    public bool IsAvailable => _agent.IsAvailable || (_wmi?.IsAvailable ?? false);

    /// <summary>
    /// Chooses the backend for this call. The service is asked first and only a failed handshake moves the call to
    /// WMI, so a healthy machine never pays for the fallback.
    /// </summary>
    public async Task<IHardwareController> ResolveAsync()
    {
        if (!_useDirectWmi())
        {
            _activeIsWmi = false;
            return _agent;
        }

        if (await _serviceHealthy().ConfigureAwait(false))
        {
            _activeIsWmi = false;
            return _agent;
        }

        var wmi = EnsureWmi();
        if (wmi == null)
        {
            _activeIsWmi = false;
            return _agent;
        }

        _activeIsWmi = true;
        return wmi;
    }

    /// <summary>
    /// Creates the direct-WMI backend once. A factory that throws or returns null leaves the hybrid on the service
    /// path rather than failing the call.
    /// </summary>
    private IHardwareController? EnsureWmi()
    {
        if (_wmiCreated) return _wmi;
        _wmiCreated = true;

        try
        {
            _wmi = _wmiFactory();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Doğrudan WMI arka ucu oluşturulamadı", ex.Message);
            _wmi = null;
        }

        return _wmi;
    }

    /// <summary>
    /// Starts the direct-WMI backend on purpose, for the settings page's button. Returns false when the path is not
    /// configured, the user declined, or the helper could not be reached.
    /// </summary>
    public async Task<bool> StartDirectWmiAsync()
    {
        var wmi = EnsureWmi();
        if (wmi == null) return false;

        var connected = await wmi.EnsureConnectedAsync().ConfigureAwait(false);
        _activeIsWmi = connected;
        return connected;
    }

    public async Task<bool> EnsureConnectedAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .EnsureConnectedAsync().ConfigureAwait(false);

    public async Task<HardwareCapabilities> GetCapabilitiesAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .GetCapabilitiesAsync().ConfigureAwait(false);

    public async Task<MonitorData?> GetMonitorDataAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .GetMonitorDataAsync().ConfigureAwait(false);

    public async Task<OperatingMode?> GetOperatingModeAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .GetOperatingModeAsync().ConfigureAwait(false);

    public async Task<bool> SetOperatingModeAsync(OperatingMode mode) => await (await ResolveAsync().ConfigureAwait(false))
        .SetOperatingModeAsync(mode).ConfigureAwait(false);

    public async Task<GpuOperatingMode?> GetGpuModeAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .GetGpuModeAsync().ConfigureAwait(false);

    public async Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => await (await ResolveAsync().ConfigureAwait(false))
        .SetGpuModeAsync(mode).ConfigureAwait(false);

    public async Task<int?> GetGpuModeCapabilityAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .GetGpuModeCapabilityAsync().ConfigureAwait(false);

    public async Task<FanMode?> GetFanModeAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .GetFanModeAsync().ConfigureAwait(false);

    public async Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1) =>
        await (await ResolveAsync().ConfigureAwait(false))
            .SetFanModeAsync(mode, cpuPercent, gpuPercent).ConfigureAwait(false);

    // Keyboard lighting and the backlight timeout stay on the service path: the direct-WMI backend does not
    // implement them, and a control that silently stops working is worse than one that reports the service is down.

    public Task<LightingConfig?> GetLightingConfigAsync() => _agent.GetLightingConfigAsync();

    public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors) =>
        _agent.SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors);

    public Task<bool> TurnOffLightingAsync() => _agent.TurnOffLightingAsync();

    public Task<bool?> GetBacklightTimeoutAsync() => _agent.GetBacklightTimeoutAsync();

    public Task<bool> SetBacklightTimeoutAsync(bool enable30s) => _agent.SetBacklightTimeoutAsync(enable30s);

    public async Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .GetBatteryChargeModeAsync().ConfigureAwait(false);

    public async Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => await (await ResolveAsync().ConfigureAwait(false))
        .SetBatteryChargeModeAsync(mode).ConfigureAwait(false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wmi?.Dispose();
        _agent.Dispose();
    }
}
