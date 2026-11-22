using NitroLite.Core.Hardware.Diagnostics;
using NitroLite.Core.Hardware.Lighting;
using NitroLite.Core.Hardware.Verification;

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
public sealed class HybridHardwareController : IHardwareController, ILightingElevationControl
{
    private readonly IHardwareController _agent;
    private readonly Func<IHardwareController?> _wmiFactory;
    private readonly Func<bool> _useDirectWmi;
    private readonly Func<Task<bool>> _serviceHealthy;
    private readonly Func<Task<MonitorData?>>? _legacyTelemetry;
    private long _nextLegacyPoll;

    /// <summary>
    /// The vendor HID lighting probe, or null when this build or this session must not touch HID. The test harness
    /// passes null so no test can open a real keyboard controller, exactly as it does for the WMI factory.
    /// </summary>
    private readonly Func<IHidLightingWriter?>? _hidLighting;

    /// <summary>
    /// Told when the WMI-to-HID lighting chain runs out, or null when nothing is listening. Null in every test and in
    /// every host that has no error endpoint, so the lighting behaviour is unchanged by the diagnostics existing.
    /// </summary>
    private readonly IHardwareDiagnosticSink? _diagnostics;

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
    /// <paramref name="diagnostics"/> is told when the WMI-to-HID lighting chain runs out of transports; it is
    /// optional and null by default, which is what every test gets.
    /// </summary>
    public HybridHardwareController(
        IHardwareController agent,
        Func<IHardwareController?>? wmiFactory = null,
        Func<bool>? useDirectWmi = null,
        Func<Task<bool>>? serviceHealthy = null,
        Func<IHidLightingWriter?>? hidLighting = null,
        IHardwareDiagnosticSink? diagnostics = null,
        Func<Task<MonitorData?>>? legacyTelemetry = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _wmiFactory = wmiFactory ?? (() => null);
        _useDirectWmi = useDirectWmi ?? (() => true);
        _serviceHealthy = serviceHealthy ?? agent.EnsureConnectedAsync;
        _hidLighting = hidLighting;
        _diagnostics = diagnostics;
        _legacyTelemetry = legacyTelemetry;
    }

    public string BackendName => ActiveBackendName;

    /// <summary>Which backend the last resolution chose. Shown in the hardware report and the settings page.</summary>
    public string ActiveBackendName => _activeIsWmi ? "WMI" : _agent.BackendName;

    /// <summary>
    /// One English line for the hardware report: which path is answering, and what the other one is doing about it.
    /// <para>
    /// The report is the thing a user pastes into an issue, and the AN16-61 report had to be reconstructed from a log
    /// because it did not say this. "Nothing detected" is answered by these three facts — is the service connected, is
    /// the helper running, and if not, why not — so they travel with the report instead of being inferred from it.
    /// </para>
    /// </summary>
    public string DescribeBackend()
    {
        var service = _agent.IsAvailable ? "AASSvc connected" : "AASSvc not connected";

        var direct = DirectWmiRefusal switch
        {
            WmiWorkerRefusal.UserDeclined => "refused: elevation declined",
            WmiWorkerRefusal.UntrustedLocation => "refused: install folder is writable by other accounts",
            WmiWorkerRefusal.StartFailed => "failed to start",
            WmiWorkerRefusal.HandshakeFailed => "handshake failed",
            _ => DirectWmi?.IsAvailable == true ? "running" : "not started"
        };

        return $"active {ActiveBackendName}; {service}; direct WMI {direct}";
    }

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

    /// <summary>
    /// Why the direct-WMI backend is not running, when it is not. <see cref="WmiWorkerRefusal.None"/> means nobody has
    /// asked for it yet, which is not a failure and must not be reported as one.
    /// </summary>
    public WmiWorkerRefusal DirectWmiRefusal => (_wmi as WmiHardwareController)?.Refusal ?? WmiWorkerRefusal.None;

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
            AppLog.Info("The hardware path changed", "AASSvc is reusable; there is no target to reconcile.");
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
                else AppLog.Warn("Reconciliation: the operating mode could not be written", $"{current} -> {mode}");
            }
        }

        if (target.Fan is { } fan)
        {
            var current = await _agent.GetFanModeAsync().ConfigureAwait(false);
            if (current != fan)
            {
                bool ok = await _agent.SetFanModeAsync(fan, target.CpuPercent, target.GpuPercent).ConfigureAwait(false);
                if (ok) applied.Add($"fan={fan}");
                else AppLog.Warn("Reconciliation: the fan mode could not be written", $"{current} -> {fan}");
            }
        }

        string detail = applied.Count > 0
            ? $"{target.Reason}: {string.Join(", ", applied)}"
            : $"{target.Reason}: zaten uyumlu";

        AppLog.Info("The hardware path changed", $"WMI -> AASSvc, {detail}");
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
            AppLog.Warn("The direct WMI backend could not be created", ex.Message);
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
        if (started) AppLog.Info("Direct WMI was enabled at startup", "The process is already elevated; UAC was not asked.");
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

    /// <summary>
    /// The active backend's answer, with the one capability another provider can serve filled in.
    /// </summary>
    /// <remarks>
    /// A capability is a promise that a write will be attempted, so this only upgrades the charge limit when
    /// <see cref="GetBatteryChargeModeAsync"/> can actually read it — the same call the dashboard's card makes. The
    /// backlight timeout is not upgraded: AASSvc is the only provider that reads it, and on the machine that reported
    /// the problem that service is not installed, so the honest answer there stays "could not ask".
    /// </remarks>
    public async Task<HardwareCapabilities> GetCapabilitiesAsync()
    {
        var capabilities = await (await ResolveAsync().ConfigureAwait(false))
            .GetCapabilitiesAsync().ConfigureAwait(false);

        if (capabilities.BatteryChargeLimit || !_activeIsWmi) return capabilities;

        var mode = await _agent.GetBatteryChargeModeAsync().ConfigureAwait(false);
        return mode.HasValue ? capabilities with { BatteryChargeLimit = true } : capabilities;
    }

    public async Task<MonitorData?> GetMonitorDataAsync() => await (await ResolveAsync().ConfigureAwait(false))
        .GetMonitorDataAsync().ConfigureAwait(false);

    /// <summary>
    /// The telemetry poll's own entry point. It differs from <see cref="GetMonitorDataAsync"/> in one way: it never
    /// starts the direct-WMI backend, so opening the window on a machine with no Acer service does not raise an
    /// elevation prompt. The WMI path is used only once it is already connected — the user started it from the
    /// settings page, or a write needed it — and the service is still not answering.
    /// </summary>
    /// <remarks>
    /// This has to be the method the polling loop calls, and for a while it was not: the loop called
    /// <see cref="GetMonitorDataAsync"/>, which resolves through <see cref="ResolveAsync"/> and therefore created the
    /// WMI backend and started the elevated worker on the first tick. On a machine whose Acer service is missing —
    /// the exact machine this path exists for — that was a UAC prompt one second after the window opened, and a new
    /// elevated process every thirty seconds after that if the handshake kept failing. The interface now declares it,
    /// so a caller cannot reach the wrong one by accident.
    /// </remarks>
    public async Task<MonitorData?> GetTelemetryAsync()
    {
        var primary = _wmi is { IsAvailable: true } && !await _serviceHealthy().ConfigureAwait(false) ? _wmi : _agent;
        var data = await primary.GetMonitorDataAsync().ConfigureAwait(false);
        if (data != null || _legacyTelemetry == null) return data;
        var now = Environment.TickCount64;
        var next = Interlocked.Read(ref _nextLegacyPoll);
        if (now < next || Interlocked.CompareExchange(ref _nextLegacyPoll, now + 30000, next) != next) return null;
        var legacy = await _legacyTelemetry().ConfigureAwait(false);
        if (legacy != null) Interlocked.Exchange(ref _nextLegacyPoll, 0);
        return legacy;
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
    /// True when a lighting write may start the elevated helper. <see cref="ILightingElevationControl"/>: the view
    /// model clears it around the automatic re-assertions — start-up, resume, the window coming back — so opening the
    /// app never raises a prompt, and leaves it set for the writes the user asked for.
    /// </summary>
    public bool AllowElevationForLighting { get; set; } = true;

    public bool PreferDetectedHidKeyboard { get; init; }

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

        // The request in transport-neutral form. The WMI executor and the HID writer both start from this, so a
        // fallback cannot quietly send a different colour than the one the user picked.
        var frame = KeyboardLightingFrame.Create(effect, AcerWmiProtocol.BrightnessLevelToPercent(brightness), speed, direction, primaryColor, zoneColors);
        bool wantsZones = WantsZoneColors(zoneColors);

        // On EV9_SKF the WMI colour can read back unchanged while the actual keyboard runs on KB5130.
        if (PreferDetectedHidKeyboard && _hidLighting?.Invoke() is { CanWrite: true, Protocol: "ene-kb5130" } keyboard)
        {
            var identity = HardwareDetector.Current;
            var result = KeyboardLightingRuntime.Router.ApplyDetectedHid(identity.ModelCode, identity.BiosVersion, frame, keyboard);
            LastZoneColorsApplied = wantsZones ? result.Applied : null;
            if (!result.Applied) AppLog.Warn("Keyboard lighting", result.Detail);
            return result.Applied;
        }

        if (!_useDirectWmi())
        {
            if (await _agent.SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors)
                    .ConfigureAwait(false))
                return true;

            // The service path can fail on these boards too: it is the vendor's own client, and the 2025 hardware
            // moved the backlight away from the interface it writes to. One HID attempt beats a dead lighting tab.
            return await ApplyThroughHidAsync(frame, wantsZones, "the Acer service did not apply it")
                .ConfigureAwait(false);
        }

        bool serviceHealthy = await _serviceHealthy().ConfigureAwait(false);

        if (serviceHealthy && !wantsZones)
        {
            if (await _agent.SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors)
                    .ConfigureAwait(false))
                return true;

            return await ApplyThroughHidAsync(frame, wantsZones, "the Acer service did not apply it")
                .ConfigureAwait(false);
        }

        // A helper that is already running is used without asking; starting one is only allowed for a write the user
        // asked for, so the boot re-assertion cannot raise a prompt.
        var wmi = _wmi is { IsAvailable: true } ? _wmi : (AllowElevationForLighting ? EnsureWmi() : null);
        bool hidTried = false;

        if (wmi != null && await wmi.EnsureConnectedAsync().ConfigureAwait(false))
        {
            // On a healthy machine the service still owns the effect parameters, so they are written there first and
            // the zone colours are the last thing the keyboard is told — except when the request IS the zone colours.
            // The service takes one colour and paints all four zones with it, so that pre-write is a solid keyboard
            // underneath a split one: whichever of the two the firmware finished second is what the user sees, and a
            // service that queues its work lands after the split and flattens it. Nothing is lost by leaving it out:
            // the effect parameters travel with the zone write, and the fallback below still asks the service for the
            // full request if the firmware refuses it.
            if (serviceHealthy && !wantsZones)
            {
                await _agent.SetLightingAsync(effect, brightness, speed, direction, primaryColor, null)
                    .ConfigureAwait(false);
            }

            var outcome = await ApplyToWmiAsync(
                    wmi, frame, effect, brightness, speed, direction, primaryColor, zoneColors)
                .ConfigureAwait(false);

            hidTried = outcome.ProbeRan;
            LastZoneColorsApplied = wantsZones ? outcome.Applied : null;

            if (outcome.Applied)
            {
                AppLog.Info("Keyboard lighting", outcome.Transport == KeyboardLightingTransport.Hid
                    ? $"Direct WMI answered success without changing the colour; it was written through the HID controller instead ({outcome.Detail})."
                    : serviceHealthy
                        ? "All four zones were written through direct WMI."
                        : "No Acer service; the lighting was written through direct WMI.");
                return true;
            }

            AppLog.Warn("Keyboard lighting",
                $"The WMI write was not applied ({outcome.Detail}); falling back to the service path.");
        }
        else if (wantsZones)
        {
            LastZoneColorsApplied = false;
            AppLog.Warn("Keyboard zone colours", "No direct WMI path; the service will apply a single colour.");
        }

        if (await _agent.SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors)
                .ConfigureAwait(false))
            return true;

        // Only worth another probe when the pass above did not already look for a controller.
        if (hidTried) return false;

        return await ApplyThroughHidAsync(frame, wantsZones, "neither the service nor direct WMI applied it")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one frame through direct WMI, and through the vendor HID controller when WMI does not take it.
    /// <para>
    /// When this session has no HID probe configured — every test, and any run that must not touch a keyboard
    /// controller — the WMI answer is the whole answer, exactly as it was before the HID path existed.
    /// </para>
    /// </summary>
    private async Task<KeyboardLightingOutcome> ApplyToWmiAsync(
        IHardwareController wmi,
        KeyboardLightingFrame frame,
        string effect,
        int brightness,
        int speed,
        int direction,
        string primaryColor,
        string[]? zoneColors)
    {
        async Task<bool> ThroughWmi() => await wmi
            .SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors).ConfigureAwait(false);

        if (_hidLighting == null)
        {
            bool applied = await ThroughWmi().ConfigureAwait(false);
            return new KeyboardLightingOutcome(
                applied,
                applied ? KeyboardLightingTransport.Wmi : KeyboardLightingTransport.Unknown,
                applied ? "direct WMI" : "the WMI write was not applied",
                false);
        }

        var identity = HardwareDetector.Current;
        var outcome = await KeyboardLightingRuntime.Router.ApplyAsync(
                identity.ModelCode,
                identity.BiosVersion,
                frame,
                ThroughWmi,
                _hidLighting,
                writer => AppLog.Info("Keyboard lighting",
                    $"The colour was written through the HID controller {writer.Description}."))
            .ConfigureAwait(false);

        // WMI did not take it and the HID controller did not either. Two transports tried, none applied: this is the
        // exhaustion the diagnostics want to see, and it is said once per machine per run by the sink itself.
        if (!outcome.Applied) _diagnostics?.LightingFallbackExhausted(identity.ModelCode, identity.BiosVersion);

        return outcome;
    }

    /// <summary>
    /// The last resort, and the first path on a machine with no direct WMI at all: one HID write, no WMI round trip.
    /// </summary>
    private async Task<bool> ApplyThroughHidAsync(KeyboardLightingFrame frame, bool wantsZones, string reason)
    {
        if (_hidLighting == null) return false;

        var identity = HardwareDetector.Current;
        var outcome = await KeyboardLightingRuntime.Router.ApplyAsync(
                identity.ModelCode,
                identity.BiosVersion,
                frame,
                static () => Task.FromResult(false),
                _hidLighting,
                writer => AppLog.Info("Keyboard lighting",
                    $"The colour was written through the HID controller {writer.Description}."))
            .ConfigureAwait(false);

        LastZoneColorsApplied = wantsZones ? outcome.Applied : null;
        if (outcome.Applied) return true;

        // No WMI path and no HID answer: the chain is empty. The same sink call as the WMI-first route, so a machine
        // that fails both ways produces one report rather than two.
        _diagnostics?.LightingFallbackExhausted(identity.ModelCode, identity.BiosVersion);

        AppLog.Warn("Keyboard lighting", $"The hardware write was not applied ({reason}; {outcome.Detail}).");
        return false;
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

    /// <summary>
    /// The backlight timeout, from whichever backend actually answers.
    /// </summary>
    /// <remarks>
    /// The selector lives on <c>APGeAction</c>, which the WMI backend reaches directly, so that is tried first. But
    /// "the worker was reachable" is not the same as "the selector answered": on the reference AN16-41 the class is
    /// registered and <c>GetFunction(0x80001)</c> returns a status this build does not recognise, so the WMI call
    /// succeeds and yields nothing. Treating that as the answer discarded the Acer service's own reading — which is
    /// the one that works — and left the switch showing a default the keyboard did not share. A backend that returns
    /// no value has not answered, so the other one is asked.
    /// </remarks>
    public async Task<bool?> GetBacklightTimeoutAsync()
    {
        var (reached, value) = await TryWmiAsync(w => w.GetBacklightTimeoutAsync()).ConfigureAwait(false);
        if (reached && value.HasValue) return value;

        return await _agent.GetBacklightTimeoutAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The same rule for the write: a refusal from the selector is not the last word while the service is still there
    /// to be asked.
    /// </summary>
    public async Task<bool> SetBacklightTimeoutAsync(bool enable30s)
    {
        var (reached, applied) = await TryWmiAsync(w => w.SetBacklightTimeoutAsync(enable30s)).ConfigureAwait(false);
        if (reached && applied) return true;

        return await _agent.SetBacklightTimeoutAsync(enable30s).ConfigureAwait(false);
    }

    public async Task<bool?> GetCoolBoostAsync()
    {
        var (reached, value) = await TryWmiAsync(w => w.GetCoolBoostAsync()).ConfigureAwait(false);
        return reached ? value : await _agent.GetCoolBoostAsync().ConfigureAwait(false);
    }

    public async Task<bool> SetCoolBoostAsync(bool on)
    {
        var (reached, applied) = await TryWmiAsync(w => w.SetCoolBoostAsync(on)).ConfigureAwait(false);
        return reached ? applied : await _agent.SetCoolBoostAsync(on).ConfigureAwait(false);
    }

    public async Task<bool?> GetLcdOverdriveAsync()
    {
        var (reached, value) = await TryWmiAsync(w => w.GetLcdOverdriveAsync()).ConfigureAwait(false);
        return reached ? value : await _agent.GetLcdOverdriveAsync().ConfigureAwait(false);
    }

    public async Task<bool> SetLcdOverdriveAsync(bool on)
    {
        var (reached, applied) = await TryWmiAsync(w => w.SetLcdOverdriveAsync(on)).ConfigureAwait(false);
        return reached ? applied : await _agent.SetLcdOverdriveAsync(on).ConfigureAwait(false);
    }

    public async Task<bool> SetWinKeyLockAsync(bool enabled)
    {
        var (reached, applied) = await TryWmiAsync(w => w.SetWinKeyLockAsync(enabled)).ConfigureAwait(false);
        return reached ? applied : await _agent.SetWinKeyLockAsync(enabled).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one call against the WMI backend and reports whether that backend was reachable at all, so the caller can
    /// tell "the hardware said no" from "there was nothing to ask". A helper that is already running is used without
    /// asking; starting one is only allowed for a write the user asked for, so a boot-time read cannot raise a prompt.
    /// </summary>
    private async Task<(bool Reached, T Value)> TryWmiAsync<T>(Func<IHardwareController, Task<T>> call)
    {
        if (!_useDirectWmi()) return (false, default!);

        var wmi = _wmi is { IsAvailable: true } ? _wmi : (AllowElevationForLighting ? EnsureWmi() : null);
        if (wmi == null) return (false, default!);
        if (!await wmi.EnsureConnectedAsync().ConfigureAwait(false)) return (false, default!);

        try
        {
            return (true, await call(wmi).ConfigureAwait(false));
        }
        catch
        {
            return (false, default!);
        }
    }

    /// <summary>
    /// The charge limit, from whichever provider actually holds it.
    /// </summary>
    /// <remarks>
    /// The direct-WMI backend can only reach <c>WMID_GUID5</c>, and plenty of Nitros never register that class: the
    /// AN16-61 report shows a machine whose limit is provided and controlled by Acer Care Center, so the WMI answer is
    /// "not registered" and the dashboard hid a battery card that NitroSense was happily driving. Care Center is a
    /// separate loopback service from AASSvc, so it answers here even with the Acer service silent — asking it before
    /// reporting "no limit" is what makes the capability line and the card agree with the hardware.
    /// </remarks>
    public async Task<BatteryChargeMode?> GetBatteryChargeModeAsync()
    {
        var backend = await ResolveAsync().ConfigureAwait(false);
        var mode = await backend.GetBatteryChargeModeAsync().ConfigureAwait(false);
        if (mode.HasValue || !_activeIsWmi) return mode;

        return await _agent.GetBatteryChargeModeAsync().ConfigureAwait(false);
    }

    public async Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode)
    {
        var backend = await ResolveAsync().ConfigureAwait(false);

        // The direct-WMI backend already reads the limit back through ReadBackPolicy, so a true answer from it is a
        // confirmed answer and needs no second look.
        if (_activeIsWmi && await backend.SetBatteryChargeModeAsync(mode).ConfigureAwait(false))
        {
            SessionVerification.Shared.Confirm(ReadBackPolicy.BatteryLimit);
            return true;
        }

        // Only the direct-WMI path has a second provider to ask. On the service path the answer already came from
        // Care Center, and asking it again would just repeat the same refusal.
        if (!_activeIsWmi) return false;
        if (!await _agent.SetBatteryChargeModeAsync(mode).ConfigureAwait(false)) return false;

        return await ConfirmServiceBatteryAsync(mode).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the charge limit back from Care Center after it accepted a write.
    /// <para>
    /// The service answers <c>ok</c> before it has told the EC, so the read is repeated after
    /// <see cref="ReadBackPolicy.SettleDelay"/>. A service that cannot answer at all — an older Care Center, or a
    /// machine where the vendor software is only half installed — leaves the control unverified rather than failed: the
    /// toggle keeps what the user asked for, and the hint offers a compatibility report instead of a false "Applied".
    /// </para>
    /// </summary>
    private async Task<bool> ConfirmServiceBatteryAsync(BatteryChargeMode mode)
    {
        await Task.Delay(ReadBackPolicy.SettleDelay).ConfigureAwait(false);

        BatteryChargeMode? observed;
        try
        {
            observed = await _agent.GetBatteryChargeModeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Info("The charge-limit read-back failed", ex.Message);
            observed = null;
        }

        if (observed is not { } actual)
        {
            SessionVerification.Shared.Mark(new VerificationNote
            {
                Control = ReadBackPolicy.BatteryLimit,
                Verdict = ControlVerdict.Unverifiable,
                Requested = mode.ToString(),
                Detail = "Acer Care Center accepted the write and does not report the current limit"
            });

            return true;
        }

        if (actual == mode)
        {
            SessionVerification.Shared.Confirm(ReadBackPolicy.BatteryLimit);
            return true;
        }

        SessionVerification.Shared.Mark(new VerificationNote
        {
            Control = ReadBackPolicy.BatteryLimit,
            Verdict = ControlVerdict.Mismatch,
            Requested = mode.ToString(),
            Observed = actual.ToString(),
            Detail = "Acer Care Center accepted the write and still reports the previous limit"
        });

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wmi?.Dispose();
        _agent.Dispose();
    }
}
