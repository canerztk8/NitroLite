namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// What NitroLite currently wants the hardware to be doing, and why. Supplied by the window, which is the only place
/// that knows whether school mode holds the fans or a thermal floor is in force.
/// </summary>
public sealed record HardwareTarget(
    OperatingMode? Mode,
    FanMode? Fan,
    string Reason,
    int CpuPercent = -1,
    int GpuPercent = -1);

/// <summary>The outcome of one WMI-to-service handover.</summary>
public sealed record ReconciliationResult(bool Switched, bool Reapplied, string? Detail)
{
    /// <summary>No handover happened, so there was nothing to reconcile.</summary>
    public static readonly ReconciliationResult None = new(false, false, null);
}

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
/// Keyboard lighting is the one exception, and only for four different zone colours. The service path supports
/// effects, speed, direction and brightness; the direct-WMI path writes a static colour per zone and nothing else.
/// A single colour therefore stays on the service, which has the richer feature set, while four different colours
/// go to WMI, which is the only path that can hold them — see <see cref="SetLightingAsync"/>.
/// </para>
/// <para>
/// A handover in the other direction needs care. While the app was on WMI it wrote the EC directly; if the user then
/// installs or starts Acer's software, the service comes up with its own idea of the fan mode and the power mode, and
/// the two writers drift apart silently. <see cref="ReconcileAsync"/> closes that gap: on the first call that finds
/// the service healthy again, it reads what the service sees and re-applies NitroLite's target over the socket when
/// the two disagree.
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

    /// <summary>Set when a call moves the app off WMI, so the next <see cref="ReconcileAsync"/> knows to act.</summary>
    private bool _pendingReconciliation;

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

    /// <summary>
    /// Supplies NitroLite's active target for <see cref="ReconcileAsync"/>. Null means "no opinion", and then the
    /// handover only logs that it happened.
    /// </summary>
    public Func<HardwareTarget?>? TargetProvider { get; set; }

    /// <summary>
    /// True when this process already holds an elevated token. Defaults to the real check; the tests replace it so no
    /// test can depend on how the test runner was started.
    /// </summary>
    public Func<bool> IsElevated { get; set; } = MemoryCleanerService.IsRunningAsAdministrator;

    /// <summary>True when a handover from WMI to the service is waiting to be reconciled.</summary>
    public bool ReconciliationPending => _pendingReconciliation;

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
    /// Records which backend is active and notices the one transition that needs work: leaving WMI for the service.
    /// </summary>
    private void SetActive(bool isWmi)
    {
        if (_activeIsWmi && !isWmi) _pendingReconciliation = true;
        _activeIsWmi = isWmi;
    }

    /// <summary>
    /// Chooses the backend for this call. The service is asked first and only a failed handshake moves the call to
    /// WMI, so a healthy machine never pays for the fallback.
    /// </summary>
    public async Task<IHardwareController> ResolveAsync()
    {
        if (!_useDirectWmi())
        {
            SetActive(false);
            return _agent;
        }

        if (await _serviceHealthy().ConfigureAwait(false))
        {
            SetActive(false);
            return _agent;
        }

        var wmi = EnsureWmi();
        if (wmi == null)
        {
            SetActive(false);
            return _agent;
        }

        SetActive(true);
        return wmi;
    }

    /// <summary>
    /// Re-applies NitroLite's target over the socket after a handover from WMI. Returns
    /// <see cref="ReconciliationResult.None"/> when no handover is pending, so it is safe to call on every poll.
    /// <para>
    /// The target is read, not guessed: school mode's lock and the thermal floors live in the window, and a
    /// reconciliation that invented its own idea of the right fan mode would be a third writer rather than a fix.
    /// A target that already matches what the service reports is left alone — writing it again would be a no-op that
    /// still costs an EC round trip.
    /// </para>
    /// </summary>
    public async Task<ReconciliationResult> ReconcileAsync()
    {
        // Resolving first is what notices the handover: the service may have come up since the last call.
        await ResolveAsync().ConfigureAwait(false);
        if (!_pendingReconciliation) return ReconciliationResult.None;
        _pendingReconciliation = false;

        var target = TargetProvider?.Invoke();
        if (target == null)
        {
            AppLog.Info("Donanım yolu değişti", "AASSvc yeniden kullanılabilir; uzlaştırılacak hedef yok.");
            return new ReconciliationResult(true, false, null);
        }

        var applied = new List<string>();

        if (target.Mode is { } mode)
        {
            var current = await _agent.GetOperatingModeAsync().ConfigureAwait(false);
            if (current != mode)
            {
                bool ok = await _agent.SetOperatingModeAsync(mode).ConfigureAwait(false);
                if (ok) applied.Add($"mod={mode}");
                else AppLog.Warn("Uzlaştırma: çalışma modu yazılamadı", $"{current} -> {mode}");
            }
        }

        if (target.Fan is { } fan)
        {
            var current = await _agent.GetFanModeAsync().ConfigureAwait(false);
            if (current != fan)
            {
                bool ok = await _agent.SetFanModeAsync(fan, target.CpuPercent, target.GpuPercent).ConfigureAwait(false);
                if (ok) applied.Add($"fan={fan}");
                else AppLog.Warn("Uzlaştırma: fan modu yazılamadı", $"{current} -> {fan}");
            }
        }

        string detail = applied.Count > 0
            ? $"{target.Reason}: {string.Join(", ", applied)}"
            : $"{target.Reason}: zaten uyumlu";

        AppLog.Info("Donanım yolu değişti", $"WMI -> AASSvc, {detail}");
        return new ReconciliationResult(true, applied.Count > 0, detail);
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
    /// Starts the direct-WMI backend at launch, without waiting for the settings button, when the process is already
    /// elevated and the Acer service is not answering.
    /// <para>
    /// This is the payoff of the optional Task Scheduler entry: a task launched with <c>HighestAvailable</c> gives the
    /// process an elevated token, the helper inherits it, and no prompt appears. Asking the user to press a button
    /// first would be ceremony for a decision they already made when they turned the option on.
    /// </para>
    /// <para>
    /// It stays silent in the two cases where it must: the user pinned the app to the service path, or the service is
    /// healthy and there is nothing to fall back from.
    /// </para>
    /// </summary>
    public async Task<bool> AutoStartDirectWmiAsync()
    {
        if (!_useDirectWmi()) return false;
        if (!IsElevated()) return false;
        if (await _serviceHealthy().ConfigureAwait(false)) return false;

        bool started = await StartDirectWmiAsync().ConfigureAwait(false);
        if (started) AppLog.Info("Doğrudan WMI açılışta etkinleştirildi", "Süreç zaten yönetici; UAC sorulmadı.");
        return started;
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
        SetActive(connected);
        return connected;
    }

    public async Task<bool> EnsureConnectedAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .EnsureConnectedAsync().ConfigureAwait(false);

    public async Task<HardwareCapabilities> GetCapabilitiesAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .GetCapabilitiesAsync().ConfigureAwait(false);

    public async Task<MonitorData?> GetMonitorDataAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .GetMonitorDataAsync().ConfigureAwait(false);

    /// <summary>
    /// The telemetry poll's own entry point. It differs from <see cref="GetMonitorDataAsync"/> in one way: it never
    /// starts the direct-WMI backend, so opening the window on a machine with no Acer service does not raise an
    /// elevation prompt. The WMI path is used only once it is already connected — the user started it from the
    /// settings page, or a write needed it — and the service is still not answering.
    /// </summary>
    public async Task<MonitorData?> GetTelemetryAsync()
    {
        if (_wmi is { IsAvailable: true } && !await _serviceHealthy().ConfigureAwait(false))
        {
            return await _wmi.GetMonitorDataAsync().ConfigureAwait(false);
        }

        return await _agent.GetMonitorDataAsync().ConfigureAwait(false);
    }

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

    // Keyboard lighting: the service when it answers, direct WMI when it does not — except for four different zone
    // colours, which only the firmware can hold. The WMI path writes colour only (no effects, no brightness), so a
    // single colour keeps the full feature set on the service. The 30-second backlight timeout has no WMI
    // equivalent and stays on the service path.

    public async Task<LightingConfig?> GetLightingConfigAsync() =>
        await (await ResolveAsync().ConfigureAwait(false)).GetLightingConfigAsync().ConfigureAwait(false);

    /// <summary>
    /// True when the last lighting write carried more than one zone colour and the firmware applied them; false when
    /// such a write was asked for but the direct-WMI path was not available; null when no zone colours were involved.
    /// The window uses it to explain a keyboard that came out in one colour.
    /// </summary>
    public bool? LastZoneColorsApplied { get; private set; }

    /// <summary>
    /// True when a lighting write may start the elevated helper. The window clears it around the automatic
    /// re-assertions — boot, resume, an adapter change — so opening the app never raises a prompt, and leaves it set
    /// for the writes the user asked for.
    /// </summary>
    public bool AllowElevationForLighting { get; set; } = true;

    /// <summary>
    /// Writes the keyboard lighting, choosing between the service and the firmware.
    /// <para>
    /// The service is the richer path — it carries effects, speed, direction and brightness — so it is used whenever
    /// it answers, with one exception: it cannot hold four different zone colours. Its own capability report for this
    /// keyboard lists <c>DIRECT</c> as the only four-colour mode and <c>STATIC</c> as one colour, and a measured run
    /// on an AN16-41 showed what that means in practice: <c>STATIC</c> with four colours lights one of them (the
    /// keyboard sub-device answers <c>4</c>, a rejection) and <c>DIRECT</c> with four colours animates them instead
    /// of holding them. The firmware's <c>SetGamingRgbKb</c> takes one colour plus a zone mask and holds it.
    /// </para>
    /// <para>
    /// When the service is absent or frozen, every lighting request goes to the firmware instead — not just the
    /// four-colour ones. That is what makes the lighting tab work on a machine with no Acer software: the executor
    /// runs the full sequence, so effects, speed, direction and brightness all reach the keyboard over WMI.
    /// </para>
    /// </summary>
    public async Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors)
    {
        LastZoneColorsApplied = null;

        if (!_useDirectWmi())
            return await _agent.SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors)
                .ConfigureAwait(false);

        bool serviceHealthy = await _serviceHealthy().ConfigureAwait(false);
        bool wantsZones = WantsZoneColors(zoneColors);

        if (serviceHealthy && !wantsZones)
            return await _agent.SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors)
                .ConfigureAwait(false);

        // A helper that is already running is used without asking; starting one is only allowed for a write the user
        // asked for, so the boot re-assertion cannot raise a prompt.
        var wmi = _wmi is { IsAvailable: true } ? _wmi : (AllowElevationForLighting ? EnsureWmi() : null);
        if (wmi != null && await wmi.EnsureConnectedAsync().ConfigureAwait(false))
        {
            // On a healthy machine the service still owns the effect parameters, so they are written there first and
            // the zone colours are the last thing the keyboard is told.
            if (serviceHealthy)
            {
                await _agent.SetLightingAsync(effect, brightness, speed, direction, primaryColor, null)
                    .ConfigureAwait(false);
            }

            bool applied = await wmi
                .SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors)
                .ConfigureAwait(false);

            LastZoneColorsApplied = wantsZones ? applied : null;
            if (applied)
            {
                AppLog.Info("Klavye aydınlatması",
                    serviceHealthy
                        ? "Dört bölge doğrudan WMI üzerinden yazıldı."
                        : "Acer servisi yok; aydınlatma doğrudan WMI üzerinden yazıldı.");
            }
            else
            {
                AppLog.Warn("Klavye aydınlatması", "WMI yazımı uygulanmadı; servis yoluna dönülüyor.");
            }

            if (applied) return true;
        }
        else if (wantsZones)
        {
            LastZoneColorsApplied = false;
            AppLog.Warn("Klavye bölge renkleri", "Doğrudan WMI yolu yok; servis tek renk uygulayacak.");
        }

        return await _agent.SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors)
            .ConfigureAwait(false);
    }

    /// <summary>True when the request carries at least two different zone colours, which is what the service cannot do.</summary>
    private static bool WantsZoneColors(string[]? zoneColors)
    {
        if (zoneColors == null) return false;

        string? first = null;
        foreach (var color in zoneColors)
        {
            if (string.IsNullOrWhiteSpace(color)) continue;
            var trimmed = color.Trim();
            if (first == null)
            {
                first = trimmed;
                continue;
            }

            if (!string.Equals(first, trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public async Task<bool> TurnOffLightingAsync() =>
        await (await ResolveAsync().ConfigureAwait(false)).TurnOffLightingAsync().ConfigureAwait(false);

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
