using System.Globalization;
using System.Text.Json.Nodes;
using NitroLite.Core.Hardware.Diagnostics;
using NitroLite.Core.Hardware.Verification;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// The direct-WMI backend: everything goes through the elevated <c>--wmi-worker</c> over its pipe.
/// <para>
/// This is the path that works on a machine with no Acer software installed. It is also the path that can ask for
/// elevation, so it is built to be quiet: nothing is started until a caller actually needs the hardware, the prompt
/// is raised at most once per session, and a refusal is remembered rather than repeated.
/// </para>
/// <para>
/// Every write goes through <see cref="ReadBackPolicy"/>. Acer's firmware answers success to writes it did not apply,
/// so the status byte alone is not evidence that anything happened; the value has to be read back before the caller
/// is told the write worked.
/// </para>
/// <para>
/// Keyboard lighting is implemented for colour only. <c>SetGamingRgbKb</c> takes a packed colour plus a zone mask and
/// was verified on an AN16-41: mask <c>0x00</c> is rejected with <c>gmOutput = 0x01</c>, masks <c>0x01</c> and
/// <c>0x0F</c> apply the colour. Brightness and the 30-second backlight timeout are not writable on this path —
/// <c>SetGamingKBBacklight</c>, the only method that carries brightness, rejects every payload tried — so those two
/// stay on the Acer service path.
/// </para>
/// </summary>
public sealed class WmiHardwareController : IHardwareController
{
    /// <summary>The four zone colours that mean "off".</summary>
    private static readonly string[] AllZonesOff = { "#000000", "#000000", "#000000", "#000000" };

    /// <summary>
    /// How far the percentage the EC reports may sit from the one that was written and still count as applied.
    /// </summary>
    /// <remarks>
    /// Two points, not zero. The firmware quantises a percentage into its own PWM steps, so a 60 % request can read
    /// back as 59 or 61 on a machine that accepted it. A tolerance of zero would report those as failures, and three
    /// failures disable the capability for the session — which is the opposite of what the read-back exists for.
    /// </remarks>
    private const int FanPercentReadBackTolerance = 2;

    private readonly WmiWorkerLauncher _launcher;
    private readonly ReadBackPolicy _readBack;
    private readonly Func<CapabilitySnapshot?> _capabilities;

    /// <summary>One gate for every write, so two callers cannot interleave a fan write and its read-back.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Separate gate for connecting: a second caller waits instead of starting a second helper.</summary>
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private WmiWorkerClient? _client;
    private bool _disposed;

    /// <summary>Set once the live sensor probe has run, so an unverified layout is only tested once per session.</summary>
    private bool _sensorProbeDone;

    /// <summary>The layout the live probe confirmed, or null when the machine did not answer plausibly.</summary>
    private SensorLayout? _discoveredSensors;

    /// <summary>
    /// Told about a mismatch the MUX pre-check finds before any write, or null when nothing is listening. The policy
    /// sink covers every write that goes through the retry loop; this one covers the read that fails before it, which
    /// is where a firmware answering an out-of-range selector value is caught.
    /// </summary>
    private readonly IHardwareDiagnosticSink? _diagnostics;

    /// <summary>
    /// <paramref name="capabilities"/> supplies the probe result. It is a delegate rather than a value because the
    /// user can rescan from the settings page at any time, and a controller holding the snapshot from launch would
    /// keep using sensor offsets the rescan just replaced. It is optional: without it the controller still works,
    /// but the sensor offsets fall back to the default layout and the battery limit is always asked about.
    /// <paramref name="diagnostics"/> is optional and null by default, which is what every test gets.
    /// </summary>
    public WmiHardwareController(
        WmiWorkerLauncher launcher,
        ReadBackPolicy readBack,
        Func<CapabilitySnapshot?>? capabilities = null,
        IHardwareDiagnosticSink? diagnostics = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _readBack = readBack ?? throw new ArgumentNullException(nameof(readBack));
        _capabilities = capabilities ?? (() => null);
        _diagnostics = diagnostics;
    }

    /// <summary>The probe result as it stands right now, or null when nothing has been probed yet.</summary>
    private CapabilitySnapshot? Snapshot => _capabilities();

    // This firmware pulses Balanced on every direct mode write. Acer service writes use a separate path.
    private bool DirectModeWriteAvailable
    {
        get
        {
            var snapshot = Snapshot;
            return snapshot?.EcMmioProfile != true
                && !string.Equals(snapshot?.CacheKey, "AN16-61|1.53", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string BackendName => "WMI";

    public bool IsAvailable => _client != null;

    /// <summary>True once the user has said no to the elevation prompt. The UI uses it to stop offering the path.</summary>
    public bool UserDeclinedElevation => _launcher.UserDeclined;

    /// <summary>Why the last connection attempt failed, for the settings page and the log.</summary>
    public string LastError => _launcher.LastError;

    /// <summary>Which kind of failure that is, so the window can say it in the user's language.</summary>
    public WmiWorkerRefusal Refusal => _launcher.Refusal;

    /// <summary>The read-back policy, so the settings page can reset its counters.</summary>
    public ReadBackPolicy ReadBack => _readBack;

    /// <summary>True once the live sensor probe has run in this session, whatever it decided.</summary>
    public bool SensorProbeRan => _sensorProbeDone;

    /// <summary>The layout the live probe confirmed, or null. Reported so a bug report says what was discovered.</summary>
    public SensorLayout? DiscoveredSensors => _discoveredSensors;

    /// <summary>
    /// Connects on first use. A worker that died is dropped by <see cref="SendAsync"/>, so the next request after a
    /// crash starts a fresh one instead of failing forever.
    /// </summary>
    public async Task<bool> EnsureConnectedAsync()
    {
        if (_disposed) return false;
        if (_client != null) return true;

        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client != null) return true;
            _client = await _launcher.StartAsync().ConfigureAwait(false);
            return _client != null;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<HardwareCapabilities> GetCapabilitiesAsync()
    {
        var response = await SendAsync(WmiWorkerCommand.GetWorkerCapabilities).ConfigureAwait(false);
        if (response is not { Ok: true } || response.Data is not JsonObject data)
        {
            return new HardwareCapabilities { Backend = BackendName, ServiceAvailable = false };
        }

        return new HardwareCapabilities
        {
            Backend = BackendName,
            ServiceAvailable = true,
            FanControl = ReadBool(data, "fanControl"),
            OperatingModeControl = DirectModeWriteAvailable && ReadBool(data, "operatingMode"),
            MuxSwitch = ReadBool(data, "muxSwitch"),
            MuxSwitchOverWmi = ReadNullableBool(data, "wmiMuxSwitch"),
            BatteryChargeLimit = ReadBool(data, "batteryLimit"),
            KeyboardLighting = ReadBool(data, "keyboardLighting"),
            BacklightTimeout = ReadBool(data, "backlightTimeout"),
            CoolBoost = ReadBool(data, "coolBoost"),
            LcdOverdrive = ReadBool(data, "lcdOverdrive"),
            WinKeyLock = ReadBool(data, "winKeyLock"),
            SupportedOperatingModes = ReadModes(data),
            SupportedModeMask = ReadMask(data)
        };
    }

    /// <summary>The raw <c>0x0A</c> answer, or null when the worker did not read one.</summary>
    private static ulong? ReadMask(JsonObject data)
    {
        if (data["supportedModeMask"] is not JsonValue node) return null;
        if (node.TryGetValue<ulong>(out var wide)) return wide;
        if (node.TryGetValue<long>(out var signed) && signed >= 0) return (ulong)signed;
        return null;
    }

    /// <summary>
    /// The operating modes the worker reported, in the order the firmware listed them. An empty list means the
    /// firmware did not answer, which the UI reads as "keep showing every mode" rather than "hide them all".
    /// </summary>
    private static IReadOnlyList<OperatingMode> ReadModes(JsonObject data)
    {
        if (data["supportedModes"] is not JsonArray array) return Array.Empty<OperatingMode>();

        var modes = new List<OperatingMode>(array.Count);
        foreach (var node in array)
        {
            if (node is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                Enum.TryParse<OperatingMode>(text, ignoreCase: true, out var mode))
            {
                modes.Add(mode);
            }
        }

        return modes;
    }

    // ---------------------------------------------------------------- telemetry

    /// <summary>
    /// One sample: five <c>GetGamingSysInfo</c> calls, one per sensor id the matched profile names, plus both fan
    /// percentages and the active mode.
    /// <para>
    /// The readings are real. Measured on an AN16-41 against the Acer service at the same moment: sensor 1 answered
    /// 63 °C (service: 63), sensor 2 answered 2436 RPM (service: 2444), sensor 3 answered 55 °C (service: 55),
    /// sensor 6 answered 2409 RPM (service: 2422) and sensor 10 answered 57 °C (service: 57). The small RPM gap is
    /// the two reads landing a few milliseconds apart, not a decode error.
    /// </para>
    /// <para>
    /// <see cref="MonitorData.IsFanRpmMeasured"/> is true only when <em>both</em> tachometers answered with status
    /// zero. The stall watchdog judges the two fans together, so a sample with one real reading and one derived one
    /// would compare a measurement against a guess; a half-measured sample therefore falls back to the derived
    /// speeds for both and leaves the watchdog in standby.
    /// </para>
    /// </summary>
    public async Task<MonitorData?> GetMonitorDataAsync()
    {
        var snapshot = Snapshot;
        var layout = snapshot?.Sensors ?? SensorLayout.Default;

        // An unverified layout is a guess: the profile table does not name this machine, so nobody has measured which
        // sensor ids its firmware answers. The first sample doubles as the probe — the ids are queried, the answers
        // are put through the plausibility window, and only a machine that answered like a machine is trusted with
        // the tachometer. A failed probe costs nothing: the readings still show, and the stall watchdog stays in
        // standby instead of judging a real die against a number that was never a speed.
        if (!layout.Verified && !_sensorProbeDone)
        {
            _sensorProbeDone = true;
            _discoveredSensors = await ProbeSensorsAsync(layout).ConfigureAwait(false);
        }

        var tachometerConfirmed = layout.Verified || _discoveredSensors != null;

        var response = await SendAsync(WmiWorkerCommand.ReadTelemetry, new JsonObject
        {
            ["cpuTempSensor"] = layout.CpuTempSensor,
            ["cpuFanRpmSensor"] = layout.CpuFanRpmSensor,
            ["sys1TempSensor"] = layout.Sys1TempSensor,
            ["gpuFanRpmSensor"] = layout.GpuFanRpmSensor,
            ["gpuTempSensor"] = layout.GpuTempSensor,

            // Told to the worker so it can skip the percentage fallback it would otherwise read on every sample.
            ["tachometerConfirmed"] = tachometerConfirmed
        }).ConfigureAwait(false);

        if (response is not { Ok: true } || response.Data is not JsonObject data) return null;

        return BuildMonitorData(
            data, layout, snapshot?.MaxCpuFanRpm ?? 0, snapshot?.MaxGpuFanRpm ?? 0,
            tachometerConfirmed: tachometerConfirmed);
    }

    /// <summary>
    /// Asks the firmware for every sensor id in <paramref name="layout"/> and decides whether the answers describe a
    /// real machine. Returns the confirmed layout, or null when the machine did not answer plausibly.
    /// </summary>
    private async Task<SensorLayout?> ProbeSensorsAsync(SensorLayout layout)
    {
        var response = await SendAsync(WmiWorkerCommand.ReadTelemetry, new JsonObject
        {
            ["cpuTempSensor"] = layout.CpuTempSensor,
            ["cpuFanRpmSensor"] = layout.CpuFanRpmSensor,
            ["sys1TempSensor"] = layout.Sys1TempSensor,
            ["gpuFanRpmSensor"] = layout.GpuFanRpmSensor,
            ["gpuTempSensor"] = layout.GpuTempSensor
        }).ConfigureAwait(false);

        if (response is not { Ok: true } || response.Data is not JsonObject data) return null;
        if (data["sensors"] is not JsonObject sensors) return null;

        var answered = new Dictionary<string, int?>(StringComparer.Ordinal)
        {
            [SensorDiscovery.CpuTemp] = ReadSensorValue(sensors, SensorDiscovery.CpuTemp),
            [SensorDiscovery.CpuFanRpm] = ReadSensorValue(sensors, SensorDiscovery.CpuFanRpm),
            [SensorDiscovery.Sys1Temp] = ReadSensorValue(sensors, SensorDiscovery.Sys1Temp),
            [SensorDiscovery.GpuFanRpm] = ReadSensorValue(sensors, SensorDiscovery.GpuFanRpm),
            [SensorDiscovery.GpuTemp] = ReadSensorValue(sensors, SensorDiscovery.GpuTemp)
        };

        return SensorDiscovery.Resolve(answered, layout);
    }

    /// <summary>
    /// Turns one <c>ReadTelemetry</c> answer into a sample. Pure, so the mapping from a firmware answer to a
    /// <see cref="MonitorData"/> can be pinned without a pipe or a worker.
    /// <para>
    /// Every reading passes <see cref="SensorPlausibility"/> first. The status byte is the primary gate, but a
    /// firmware that does not know an id sometimes answers status zero with a sentinel in the value field, and
    /// publishing that would show a 6553.5 °C die or let the stall watchdog compare a real temperature against a
    /// number that was never a speed.
    /// </para>
    /// <para>
    /// <paramref name="tachometerConfirmed"/> is what separates "the fan is spinning" from "the firmware answered
    /// something". It defaults to <see cref="SensorLayout.Verified"/>: a profile nobody measured, and a machine whose
    /// live sensor probe did not pass, leave the watchdog in standby rather than judging a guess.
    /// </para>
    /// </summary>
    public static MonitorData BuildMonitorData(
        JsonObject data, SensorLayout layout, int maxCpuFanRpm, int maxGpuFanRpm, bool? tachometerConfirmed = null)
    {
        var sensors = data["sensors"] as JsonObject;

        int? cpuRpm = layout.Supported ? PlausibleRpm(ReadSensorValue(sensors, SensorDiscovery.CpuFanRpm)) : null;
        int? gpuRpm = layout.Supported ? PlausibleRpm(ReadSensorValue(sensors, SensorDiscovery.GpuFanRpm)) : null;
        bool measured = (tachometerConfirmed ?? layout.Verified) && cpuRpm.HasValue && gpuRpm.HasValue;

        // The temperatures are still published on an unverified layout — an unlisted model's readings are useful, and
        // hiding them would leave the page blank on every machine the table does not name. What the flag carries is
        // the fact that nobody has confirmed which sensor id answers what, so the fan curve must not act on the
        // number: a machine whose id 1 answers the 55 °C system sensor instead of the die would ask for a third of
        // full speed at 95 °C, and the 95 °C floor would never fire because the curve never sees 95 °C.
        bool temperaturesConfirmed = tachometerConfirmed ?? layout.Verified;

        return new MonitorData
        {
            CpuTemperature = layout.Supported ? PlausibleTemperature(ReadSensorValue(sensors, SensorDiscovery.CpuTemp)) : 0,
            GpuTemperature = layout.Supported ? PlausibleTemperature(ReadSensorValue(sensors, SensorDiscovery.GpuTemp)) : 0,
            SysTemperature = layout.Supported ? PlausibleTemperature(ReadSensorValue(sensors, SensorDiscovery.Sys1Temp)) : 0,

            IsTemperatureMeasured = temperaturesConfirmed,

            CpuFanSpeed = measured ? cpuRpm!.Value : DeriveRpm(ReadInt(data, "cpuFanPercent") ?? 0, maxCpuFanRpm),
            GpuFanSpeed = measured ? gpuRpm!.Value : DeriveRpm(ReadInt(data, "gpuFanPercent") ?? 0, maxGpuFanRpm),

            IsFanRpmMeasured = measured
        };
    }

    /// <summary>A temperature that survived the window, or 0 so the UI renders "-- °C" instead of a wrong number.</summary>
    private static int PlausibleTemperature(int? value) =>
        value is int reading && SensorPlausibility.IsPlausibleTemperature(reading) ? reading : 0;

    /// <summary>A fan speed that survived the window, or null so the sample falls back to the derived value.</summary>
    private static int? PlausibleRpm(int? value) =>
        value is int reading && SensorPlausibility.IsPlausibleFanRpm(reading) ? reading : null;

    /// <summary>
    /// One sensor's reading, or null when the firmware answered a non-zero status or no value at all. The status
    /// byte is the gate: a firmware that does not know the id answers a code and a zero, and publishing that zero
    /// would show a fan at 0 RPM that is physically spinning.
    /// </summary>
    private static int? ReadSensorValue(JsonObject? sensors, string key)
    {
        if (sensors?[key] is not JsonObject node) return null;
        if (ReadInt(node, "status") != AcerWmiProtocol.StatusOk) return null;
        return ReadInt(node, "value");
    }

    /// <summary>Turns a fan percentage into an approximate RPM using the model's ceiling. 0 when the ceiling is unknown.</summary>
    public static int DeriveRpm(int percent, int ceilingRpm) =>
        ceilingRpm <= 0 ? 0 : (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * ceilingRpm);

    // ---------------------------------------------------------------- operating mode

    public async Task<OperatingMode?> GetOperatingModeAsync()
    {
        var state = await ReadDeviceStateAsync().ConfigureAwait(false);
        return ParseEnum<OperatingMode>(state, "operatingMode");
    }

    public async Task<bool> SetOperatingModeAsync(OperatingMode mode)
    {
        // A model whose firmware drives the mode through EC MMIO accepts the WMI write and ignores it. Reporting
        // not-supported is honest; the caller falls back to the Windows power plan, which does work there.
        if (!DirectModeWriteAvailable) return false;

        var result = await ApplyAsync(
            ReadBackPolicy.OperatingMode,
            write: () => WriteOrThrowAsync(WmiWorkerCommand.SetOperatingMode,
                new JsonObject { ["mode"] = (int)mode }),
            readBack: GetOperatingModeAsync,
            matches: observed => observed == mode).ConfigureAwait(false);

        return result.Applied;
    }

    // ---------------------------------------------------------------- MUX

    /// <summary>The last MUX word logged as unrecognised, so the same answer is said once, not on every read.</summary>
    private ulong? _unrecognisedMuxWord;

    /// <summary>
    /// The last <c>GetGamingMiscSetting(0x02)</c> word the worker answered, or null when nothing has read it this
    /// session. The hardware report prints it: it is the one number that says what a board's MUX selector holds, and
    /// the AN16-61 MUX report could not be acted on without it.
    /// </summary>
    public ulong? LastMuxWord { get; private set; }

    public async Task<GpuOperatingMode?> GetGpuModeAsync()
    {
        var (state, refusal) = await ReadDeviceStateWithReasonAsync().ConfigureAwait(false);

        if (state == null)
        {
            // A MUX card with no mode and no explanation is what an AN16-61 report showed: the panel said the
            // switch was supported and offered no current mode. The worker knows why — it names the class or the
            // selector that failed — and that sentence is the only thing a maintainer can act on.
            AppLog.Warn("The MUX mode could not be read", refusal);
            return null;
        }

        if (!TryReadUlong(state, "muxRaw", out var raw))
        {
            AppLog.Warn("The MUX mode could not be read",
                $"the device state carries no GetGamingMiscSetting(0x02) answer ({DescribeRaw(state)})");
            return null;
        }

        LastMuxWord = raw;

        var decoded = ParseEnum<GpuOperatingMode>(state, "muxMode");
        if (decoded == null && _unrecognisedMuxWord != raw)
        {
            // The firmware answered something this build does not map. Say what it was rather than showing nothing:
            // the raw word is how a new numbering gets recognised. Once per distinct word: the same answer on every
            // refresh is one fact, not a stream of them.
            _unrecognisedMuxWord = raw;
            AppLog.Warn("The MUX mode was not recognised",
                $"GetGamingMiscSetting(0x02) answered 0x{raw:X4}; expected the high byte to be 1..3");
        }

        return decoded;
    }

    /// <summary>
    /// The worker's own words for a refused command, or a note that there was no worker to ask, so a capability that
    /// silently did nothing at least leaves a line in the log.
    /// </summary>
    private string DescribeWorkerRefusal(WmiWorkerResponse? response)
    {
        if (response == null)
        {
            var detail = _launcher.LastError;
            return string.IsNullOrWhiteSpace(detail)
                ? "the worker did not answer"
                : $"the worker did not answer: {detail}";
        }

        var code = string.IsNullOrWhiteSpace(response.ErrorCode) ? "" : $"[{response.ErrorCode}] ";
        var error = string.IsNullOrWhiteSpace(response.Error) ? "no detail" : response.Error;
        return $"{code}{error}";
    }

    /// <summary>
    /// True when a raw <c>GetGamingMiscSetting(0x02)</c> answer carries a mode number the firmware can be expected to
    /// understand: the high byte must be 1, 2 or 3.
    /// <para>
    /// This is the guard against the off-by-one that the migration brief itself contained. The encoder writes
    /// <c>((mode + 1) &lt;&lt; 8) | 0x02</c>, so Optimus goes out as <c>0x0102</c>. If a machine answers with something
    /// outside 1..3 the numbering is not what the encoder assumes, and writing anyway would set a mode nobody asked
    /// for on a switch that needs a reboot to come back from.
    /// </para>
    /// </summary>
    public static bool IsMuxValueInRange(ulong raw)
    {
        var value = (int)((raw >> 8) & 0xFF);
        return value is >= 1 and <= 3;
    }

    public async Task<bool> SetGpuModeAsync(GpuOperatingMode mode)
    {
        // Automatic is a firmware decision (Advanced Optimus); it is not a value that can be written.
        if (mode is not (GpuOperatingMode.Optimus or GpuOperatingMode.DiscreteGpu)) return false;

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var (state, refusal) = await ReadDeviceStateWithReasonAsync().ConfigureAwait(false);
            if (state == null)
            {
                // Returning false here without a word was the whole of the AN16-61 "switching to discrete GPU does
                // not apply the setting" report: a click, no reboot prompt, no message, and nothing in the log.
                AppLog.Warn("The MUX write was not attempted",
                    $"the worker could not read the current mode: {refusal}");

                // The AN16-61 report was exactly this: a click, no message, and nothing in the log. The note is what
                // puts it in front of the user and into the hardware report.
                SessionVerification.Shared.Mark(new VerificationNote
                {
                    Control = ReadBackPolicy.MuxMode,
                    Verdict = ControlVerdict.Unverifiable,
                    Requested = mode.ToString(),
                    Detail = $"the current mode could not be read: {refusal}"
                });

                return false;
            }

            if (!TryReadUlong(state, "muxRaw", out var raw) || !IsMuxValueInRange(raw))
            {
                AppLog.Warn("The MUX write was refused",
                    $"current GetGamingMiscSetting(0x02) value {DescribeRaw(state)}; expected high byte 1..3");

                SessionVerification.Shared.Mark(new VerificationNote
                {
                    Control = ReadBackPolicy.MuxMode,
                    Verdict = ControlVerdict.Mismatch,
                    Requested = mode.ToString(),
                    Observed = DescribeRaw(state),
                    Detail = "GetGamingMiscSetting(0x02) answered outside the range the encoder writes"
                });

                // A mismatch that never reached the retry loop: the read itself was the refusal. Reported here because
                // this is the branch a board with an undocumented MUX selector lands in, and the policy never sees it.
                _diagnostics?.ReadBackMismatch(ReadBackPolicy.MuxMode);

                return false;
            }

            var result = await _readBack.ApplyAsync(
                ReadBackPolicy.MuxMode,
                write: () => WriteOrThrowAsync(WmiWorkerCommand.SetMuxMode,
                    new JsonObject { ["mode"] = (int)mode }),
                readBack: GetGpuModeAsync,
                matches: observed => observed == mode,
                requested: mode.ToString()).ConfigureAwait(false);

            return result.Applied;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<int?> GetGpuModeCapabilityAsync() =>
        Task.FromResult(Snapshot?.Registry.DgpuModeCapability);

    // ---------------------------------------------------------------- fans

    public async Task<FanMode?> GetFanModeAsync()
    {
        var state = await ReadDeviceStateAsync().ConfigureAwait(false);
        return ParseEnum<FanMode>(state, "cpuFanMode");
    }

    /// <summary>
    /// The percentage the EC is actually holding for the CPU fan, or null when the firmware did not answer.
    /// </summary>
    /// <remarks>
    /// This is the value a custom-speed write has to be verified against. The mode byte answers a different question:
    /// <c>cpuFanMode</c> comes from <c>GetGamingFanBehavior</c> and says "the fans are in manual mode", not "the fans
    /// are at the speed I asked for". A write the EC clamped or silently dropped still leaves the mode byte saying
    /// Custom, so verifying against it reports a refused write as applied — and the caller then records the speed as
    /// committed and never proposes it again, which is how the 95 °C full-speed floor stops being re-sent.
    /// </remarks>
    public async Task<int?> GetFanPercentAsync()
    {
        var state = await ReadDeviceStateAsync().ConfigureAwait(false);
        return ReadNullableInt(state, "cpuFanPercent");
    }

    public async Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
    {
        if (mode == FanMode.Custom)
        {
            var cpu = NormalisePercent(cpuPercent);
            var gpu = NormalisePercent(gpuPercent);

            var speed = await ApplyAsync(
                ReadBackPolicy.FanSpeed,
                write: () => WriteOrThrowAsync(WmiWorkerCommand.SetFanSpeed, new JsonObject
                {
                    ["cpuPercent"] = cpu,
                    ["gpuPercent"] = gpu
                }),

                // The percentage, not the mode byte. See GetFanPercentAsync for why the mode byte is the wrong
                // question, and why answering it wrongly is a thermal problem rather than a cosmetic one.
                readBack: GetFanPercentAsync,
                matches: observed => Math.Abs(observed - cpu) <= FanPercentReadBackTolerance).ConfigureAwait(false);

            // A capability that has just been switched off will refuse every later write, so the fans stay at
            // whatever the last accepted write left them at — for the rest of the session, and after three sessions
            // permanently. The curve still computes the 85/90/95 °C floor, but the only function that can put it in
            // the EC is now a no-op. Handing the fans back to the firmware is the one action that is still available
            // and the only one that leaves the machine safe.
            if (speed.DisabledForSession || speed.PermanentlyDisabled)
            {
                AppLog.Warn("The fan speed control was disabled; handing the fans back to the firmware",
                    $"{speed.Error} (session={speed.DisabledForSession}, permanent={speed.PermanentlyDisabled})");

                await ApplyAsync(
                    ReadBackPolicy.FanBehavior,
                    write: () => WriteOrThrowAsync(WmiWorkerCommand.SetFanBehavior, new JsonObject
                    {
                        ["cpu"] = (int)FanMode.Auto,
                        ["gpu"] = (int)FanMode.Auto
                    }),
                    readBack: GetFanModeAsync,
                    matches: observed => observed == FanMode.Auto).ConfigureAwait(false);
            }

            return speed.Applied;
        }

        var behaviour = await ApplyAsync(
            ReadBackPolicy.FanBehavior,
            write: () => WriteOrThrowAsync(WmiWorkerCommand.SetFanBehavior, new JsonObject
            {
                ["cpu"] = (int)mode,
                ["gpu"] = (int)mode
            }),
            readBack: GetFanModeAsync,
            matches: observed => observed == mode).ConfigureAwait(false);

        return behaviour.Applied;
    }

    /// <summary>
    /// Keeps a custom speed inside the range the firmware accepts. A negative value means "the caller did not care",
    /// which for this backend becomes the application's own default rather than an error.
    /// </summary>
    public static int NormalisePercent(int percent) => percent < 0
        ? 50
        : Math.Clamp(percent, AcerWmiProtocol.MinFanPercent, AcerWmiProtocol.MaxFanPercent);

    // ---------------------------------------------------------------- keyboard lighting

    /// <summary>
    /// The part of the keyboard state the firmware reports back: the colour of zone 1, packed as <c>0xRRGGBB</c>.
    /// <para>
    /// Brightness is deliberately not part of it. <c>SetGamingRgbKb</c> carries a colour and a zone mask but no
    /// brightness, and the only method that does carry brightness — <c>SetGamingKBBacklight</c> — answers
    /// <c>gmOutput = 0x01</c> and changes nothing on an AN16-41. Comparing a brightness the backend cannot write
    /// would make every read-back fail and disable the capability for a fault that is not there.
    /// </para>
    /// </summary>
    private readonly record struct LightingState(int Rgb);

    public async Task<LightingConfig?> GetLightingConfigAsync()
    {
        var response = await SendAsync(WmiWorkerCommand.GetKeyboardBacklight).ConfigureAwait(false);
        if (response is not { Ok: true } || response.Data is not JsonObject data) return null;

        var red = (byte)(ReadInt(data, "red") ?? 0);
        var green = (byte)(ReadInt(data, "green") ?? 0);
        var blue = (byte)(ReadInt(data, "blue") ?? 0);
        var percent = ReadInt(data, "brightness") ?? 0;
        var mode = ReadInt(data, "mode") ?? AcerWmiProtocol.KbModeStatic;

        var color = AcerWmiProtocol.FormatHexColor(red, green, blue);
        var level = AcerWmiProtocol.BrightnessPercentToLevel(percent);
        var isOff = level == 0 || (red == 0 && green == 0 && blue == 0);

        return new LightingConfig
        {
            Effect = isOff ? "OFF" : EffectNameFor(mode),
            Brightness = isOff ? 0 : Math.Max(level, 1),
            Speed = Math.Clamp(ReadInt(data, "speed") ?? 3, 1, AcerWmiProtocol.MaxEffectSpeed),
            Direction = Math.Clamp(ReadInt(data, "direction") ?? 1, 1, AcerWmiProtocol.MaxEffectDirection),
            Color = color,
            LEDs = new List<string> { color, color, color, color }
        };
    }

    /// <summary>The app's effect name for a firmware mode byte.</summary>
    public static string EffectNameFor(int mode) => mode switch
    {
        AcerWmiProtocol.KbModeBreathing => "BREATHING",
        AcerWmiProtocol.KbModeNeon => "NEON",
        AcerWmiProtocol.KbModeWave => "WAVE",
        AcerWmiProtocol.KbModeShifting => "SHIFTING",
        AcerWmiProtocol.KbModeZoom => "ZOOM",
        _ => "STATIC"
    };

    /// <summary>
    /// Writes the keyboard lighting through the full four-zone sequence, with the effect, its speed, brightness and
    /// direction carried in the same command.
    /// <para>
    /// The firmware takes one colour per call plus a zone mask, so four different zone colours are four writes, and
    /// the executor runs them behind the arm and the commit. A mask of zero is rejected by the EC, which is why the
    /// four-zone picker never worked through the service path: the colour was sent with no zone to put it in.
    /// </para>
    /// <para>
    /// Brightness is applied by scaling the colour, not by the brightness byte: the firmware does not apply that byte
    /// to a per-zone colour. The read-back therefore compares the scaled colour, which is what the EC stores.
    /// </para>
    /// </summary>
    public async Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors)
    {
        // A model whose WMI RGB path is silently ignored has to fall through to the service or OpenRGB rather than
        // report a write that changed nothing.
        if (Snapshot?.NitroHidKeyboard == true) return false;

        var requested = (effect ?? "STATIC").Trim().ToUpperInvariant();
        var isOff = requested == "OFF" || brightness <= 0;
        var percent = isOff ? 0 : AcerWmiProtocol.BrightnessLevelToPercent(brightness);

        var zones = new int[AcerWmiProtocol.LedZoneCount];
        for (int i = 0; i < zones.Length; i++)
        {
            if (isOff) continue;

            var source = zoneColors != null && i < zoneColors.Length ? zoneColors[i] : primaryColor;
            if (!AcerWmiProtocol.TryParseHexColor(source, out var red, out var green, out var blue) &&
                !AcerWmiProtocol.TryParseHexColor(primaryColor, out red, out green, out blue))
            {
                red = green = blue = 0;
            }

            zones[i] = (red << 16) | (green << 8) | blue;
        }

        var request = new KeyboardLightingRequest
        {
            Effect = isOff ? "STATIC" : requested,
            Brightness = percent,
            Speed = Math.Clamp(speed, 1, AcerWmiProtocol.MaxEffectSpeed),
            Direction = Math.Clamp(direction, 1, AcerWmiProtocol.MaxEffectDirection),
            Zones = zones
        };

        if (isOff)
        {
            // Turning the lights off is not verified by colour: a firmware that keeps the last colour in its getter
            // would fail the read-back and disable the capability for a write that worked. There is no policy under
            // this path, so a missing transport is answered here instead of being counted against anything.
            try
            {
                var written = await WriteLightingAsync(request).ConfigureAwait(false);
                if (written) NoteRejectedCommit();
                return written;
            }
            catch (WmiTransportUnavailableException)
            {
                return false;
            }
        }

        var scaled = AcerWmiProtocol.ScaleColor(
            (byte)((zones[0] >> 16) & 0xFF), (byte)((zones[0] >> 8) & 0xFF), (byte)(zones[0] & 0xFF), percent);
        var target = new LightingState(PackRgb(scaled));

        var result = await ApplyAsync(
            ReadBackPolicy.KeyboardLighting,
            write: async () => await WriteLightingAsync(request).ConfigureAwait(false),
            readBack: ReadLightingStateAsync,
            matches: observed => observed.Rgb == target.Rgb).ConfigureAwait(false);

        // After the policy's own verdict, which would clear a note made earlier: a colour that reads back is still not
        // proof the keys show it when the firmware refused the static lock.
        if (result.Applied) NoteRejectedCommit();
        return result.Applied;
    }

    /// <summary>The status of the last <c>SetGamingKBBacklight</c> commit the firmware refused, or null.</summary>
    private int? _rejectedCommitStatus;

    /// <summary>
    /// Records a refused commit as an unverified lighting write, so the keyboard page offers a compatibility report
    /// instead of claiming a colour the keys may not be showing. Not a failure: on the firmware that answers non-zero
    /// and applies the colours anyway, calling it one would disable a keyboard that works.
    /// </summary>
    private void NoteRejectedCommit()
    {
        if (_rejectedCommitStatus is not { } status) return;

        SessionVerification.Shared.Mark(new VerificationNote
        {
            Control = ReadBackPolicy.KeyboardLighting,
            Verdict = ControlVerdict.Unverifiable,
            Requested = "static lock",
            Detail = string.Create(CultureInfo.InvariantCulture,
                $"SetGamingKBBacklight refused the commit (status {status}); the zone colours were written, but the keys may not show them")
        });
    }

    public Task<bool> TurnOffLightingAsync() =>
        SetLightingAsync("OFF", 0, 1, 1, "#000000", AllZonesOff);

    /// <summary>
    /// Sends one lighting command. The executor runs the whole sequence — poll, arm, four colours, commit — so this
    /// is a single round trip rather than five.
    /// <para>
    /// A non-zero commit status is logged but not treated as a failure: some firmware revisions apply the zone
    /// colours and still answer non-zero to <c>SetGamingKBBacklight</c>, and the keyboard is already showing them.
    /// </para>
    /// </summary>
    private async Task<bool> WriteLightingAsync(KeyboardLightingRequest request)
    {
        var zones = new JsonArray();
        foreach (var packed in request.Zones) zones.Add(packed);

        var response = await SendAsync(WmiWorkerCommand.SetKeyboardLighting, new JsonObject
        {
            ["effect"] = request.Effect,
            ["brightness"] = request.Brightness,
            ["speed"] = request.Speed,
            ["direction"] = request.Direction,
            ["zones"] = zones
        }).ConfigureAwait(false);

        _rejectedCommitStatus = null;
        if (response is null) throw new WmiTransportUnavailableException(_launcher.LastError);
        if (!response.Ok) return false;

        if (response.Data is JsonObject data && ReadNullableBool(data, "commitApplied") == false)
        {
            _rejectedCommitStatus = ReadInt(data, "commitStatus") ?? -1;
            AppLog.Warn("The keyboard static lock was not applied",
                $"SetGamingKBBacklight status {_rejectedCommitStatus}; the zone colours were written anyway.");
        }

        return true;
    }

    /// <summary>Reads the zone-1 colour the firmware reports, for the read-back comparison.</summary>
    private async Task<LightingState?> ReadLightingStateAsync()
    {
        var response = await SendAsync(WmiWorkerCommand.GetKeyboardBacklight).ConfigureAwait(false);
        if (response is not { Ok: true } || response.Data is not JsonObject data) return null;

        // The packed getter is preferred: it is the same field the write sets, with no byte-offset guesswork.
        if (TryReadUlong(data, "rgbRaw", out var packed))
        {
            var (red, green, blue, _) = AcerWmiProtocol.DecodeRgbKb(packed);
            return new LightingState(PackRgb((red, green, blue)));
        }

        var r = (byte)(ReadInt(data, "red") ?? 0);
        var g = (byte)(ReadInt(data, "green") ?? 0);
        var b = (byte)(ReadInt(data, "blue") ?? 0);
        return new LightingState(PackRgb((r, g, b)));
    }

    private static int PackRgb((byte Red, byte Green, byte Blue) color) =>
        (color.Red << 16) | (color.Green << 8) | color.Blue;

    public async Task<bool?> GetBacklightTimeoutAsync()
    {
        var response = await SendAsync(WmiWorkerCommand.GetBacklightTimeout).ConfigureAwait(false);
        if (response is not { Ok: true } || response.Data is not JsonObject data) return null;
        return ReadNullableBool(data, "enabled");
    }

    /// <summary>
    /// Writes the 30-second backlight timeout and reads it back, as every control with a getter is. A firmware that
    /// answers status 0 still needs to report the requested timeout state.
    /// </summary>
    public async Task<bool> SetBacklightTimeoutAsync(bool enable30s)
    {
        var response = await SendAsync(WmiWorkerCommand.SetBacklightTimeout, new JsonObject
        {
            ["enabled"] = enable30s
        }).ConfigureAwait(false);
        if (response is not { Ok: true }) return false;

        var observed = await GetBacklightTimeoutAsync().ConfigureAwait(false);
        if (observed == enable30s) return true;
        if (!observed.HasValue)
        {
            AppLog.Warn("The backlight timeout could not be verified after writing");
            return false;
        }

        AppLog.Warn("The backlight timeout did not change",
            $"requested {(enable30s ? "on" : "off")}, the firmware still reports {(observed.Value ? "on" : "off")}");
        return false;
    }

    /// <summary>
    /// The full selector sweep, plus the worker's explanation when it could not run. No setter is called.
    /// </summary>
    /// <remarks>
    /// Only the explicitly confirmed sweep action calls this — never a snapshot and never a poll. It is a
    /// hundred-odd firmware round trips over selectors whose behaviour nobody has confirmed, and on one reported
    /// Acer model the undocumented <c>APGeAction</c> pokes in it hard-reset the machine, which is why it was moved
    /// out of "Capture state A" and put behind a yes/no dialog. "Read-only by construction" held (only getters are
    /// called); it was not enough: a Get on an action class is not proven side-effect-free on every firmware.
    /// </remarks>
    public async Task<(JsonObject? Sweep, string Refusal)> ProbeModeSelectorsAsync()
    {
        var response = await SendAsync(WmiWorkerCommand.ProbeModeSelectors).ConfigureAwait(false);
        if (response is { Ok: true } && response.Data is JsonObject sweep) return (sweep, "");
        return (null, DescribeWorkerRefusal(response));
    }

    // ---------------------------------------------------------------- CoolBoost, overdrive, WinKey lock

    public async Task<bool?> GetCoolBoostAsync()
    {
        var response = await SendAsync(WmiWorkerCommand.GetCoolBoost).ConfigureAwait(false);
        if (response is not { Ok: true } || response.Data is not JsonObject data) return null;
        return ReadNullableBool(data, "coolBoost");
    }

    public async Task<bool> SetCoolBoostAsync(bool on)
    {
        var response = await SendAsync(WmiWorkerCommand.SetCoolBoost, new JsonObject { ["on"] = on })
            .ConfigureAwait(false);
        if (response is not { Ok: true }) return false;

        // CoolBoost has a getter, so the write is confirmed rather than assumed.
        var observed = await GetCoolBoostAsync().ConfigureAwait(false);
        return observed == on;
    }

    public async Task<bool?> GetLcdOverdriveAsync()
    {
        var response = await SendAsync(WmiWorkerCommand.GetLcdOverdrive).ConfigureAwait(false);
        if (response is not { Ok: true } || response.Data is not JsonObject data) return null;
        return ReadNullableBool(data, "lcdOverdrive");
    }

    public async Task<bool> SetLcdOverdriveAsync(bool on)
    {
        var response = await SendAsync(WmiWorkerCommand.SetLcdOverdrive, new JsonObject { ["on"] = on })
            .ConfigureAwait(false);
        if (response is not { Ok: true }) return false;

        var observed = await GetLcdOverdriveAsync().ConfigureAwait(false);
        return observed == on;
    }

    /// <summary>
    /// Writes the Windows-key lock. The firmware has no getter for this setting, so the answer is the status byte and
    /// nothing more.
    /// <para>
    /// The call therefore records an <see cref="ControlVerdict.Unverifiable"/> note instead of confirming anything: the write is reported
    /// as accepted, the control is recorded as unverified for the session, and the UI offers a compatibility report
    /// instead of claiming a state the machine never confirmed. It is deliberately not a failure — a setting with no
    /// getter is not a broken setting, and disabling it for the session would take away the one thing this control can
    /// do. A refusal from the worker still comes back as false, because that is evidence the write never happened.
    /// </para>
    /// </summary>
    public async Task<bool> SetWinKeyLockAsync(bool enabled)
    {
        var response = await SendAsync(WmiWorkerCommand.SetWinKeyLock, new JsonObject { ["enabled"] = enabled })
            .ConfigureAwait(false);

        if (response is not { Ok: true })
        {
            // The worker answered and refused, or it was not there at all. Both are a write that did not happen, and
            // the caller's status line has to say so.
            SessionVerification.Shared.Mark(new VerificationNote
            {
                Control = ReadBackPolicy.WinKeyLock,
                Verdict = ControlVerdict.TransportMissing,
                Requested = enabled ? "enabled" : "disabled",
                Detail = DescribeWorkerRefusal(response)
            });

            return false;
        }

        SessionVerification.Shared.Mark(new VerificationNote
        {
            Control = ReadBackPolicy.WinKeyLock,
            Verdict = ControlVerdict.Unverifiable,
            Requested = enabled ? "enabled" : "disabled",
            Detail = "the firmware has no getter for the Windows-key lock (SetGamingProfile has no matching get)"
        });

        return true;
    }

    // ---------------------------------------------------------------- battery charge limit

    /// <summary>
    /// True when the probe already established that this machine has no WMID_GUID5. Asking the worker anyway would
    /// cost a round trip to learn what is already known, and on an AN16-41 the answer is always "not registered".
    /// <para>
    /// Deliberately reads <see cref="CapabilitySnapshot.WmiBatteryChargeLimitPresent"/> rather than the combined
    /// battery capability: on an AN16-41 the limit is supported through Acer Care Center, so the capability is a yes
    /// while the class this backend writes through does not exist. A null answer means nobody asked, which must not
    /// stop a write on a machine that does have the class.
    /// </para>
    /// </summary>
    public bool BatteryLimitKnownUnsupported => Snapshot?.WmiBatteryChargeLimitPresent == false;

    public async Task<BatteryChargeMode?> GetBatteryChargeModeAsync()
    {
        if (BatteryLimitKnownUnsupported) return null;

        var state = await ReadDeviceStateAsync().ConfigureAwait(false);
        return ParseEnum<BatteryChargeMode>(state, "batteryLimit");
    }

    public async Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode)
    {
        if (BatteryLimitKnownUnsupported) return false;

        var result = await ApplyAsync(
            ReadBackPolicy.BatteryLimit,
            write: () => WriteOrThrowAsync(WmiWorkerCommand.SetBatteryLimit,
                new JsonObject { ["mode"] = (int)mode }),
            readBack: GetBatteryChargeModeAsync,
            matches: observed => observed == mode,
            requested: mode.ToString()).ConfigureAwait(false);

        return result.Applied;
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>Runs one write and its read-back behind the write gate.</summary>
    private async Task<ReadBackResult> ApplyAsync<T>(
        string capability,
        Func<Task<bool>> write,
        Func<Task<T?>> readBack,
        Func<T, bool> matches,
        string requested = "") where T : struct
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // No transport is not a capability failure. The worker may be unstarted because the user declined the
            // prompt or the install folder is untrusted; counting that against the firmware would disable the
            // capability for three sessions and then permanently, on a machine where nothing was ever written.
            if (!await EnsureConnectedAsync().ConfigureAwait(false))
                return ReadBackResult.NoTransport();

            return await _readBack.ApplyAsync(capability, write, readBack, matches, requested).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// The raw device state the worker answers, for the read-only NitroSense snapshot diff.
    /// </summary>
    /// <remarks>
    /// Deliberately the same <see cref="WmiWorkerCommand.ReadDeviceState"/> call the window already makes: a diagnostic
    /// that introduced a new selector would be a diagnostic that can itself change what the firmware is doing, which is
    /// the one thing a snapshot must not do. The decoded model is not enough for a diff either — two firmwares can
    /// answer <c>GetGamingMiscSetting(0x02)</c> with different raw words and decode to the same mode name.
    /// </remarks>
    public Task<JsonObject?> ReadRawDeviceStateAsync() => ReadDeviceStateAsync();

    private async Task<JsonObject?> ReadDeviceStateAsync()
    {
        var (state, _) = await ReadDeviceStateWithReasonAsync().ConfigureAwait(false);
        return state;
    }

    /// <summary>
    /// The same read, plus the worker's own explanation when it refused. Kept beside
    /// <see cref="ReadDeviceStateAsync"/> rather than replacing it because most callers have nothing useful to add to
    /// a log line — but the MUX does, since its failure is otherwise a control that does nothing.
    /// </summary>
    private async Task<(JsonObject? State, string Refusal)> ReadDeviceStateWithReasonAsync()
    {
        var response = await SendAsync(WmiWorkerCommand.ReadDeviceState).ConfigureAwait(false);
        if (response is { Ok: true } && response.Data is JsonObject data) return (data, "");
        return (null, DescribeWorkerRefusal(response));
    }

    /// <summary>
    /// One write for the read-back policy: true when the worker accepted it, false when the worker was there and
    /// refused it, and <see cref="WmiTransportUnavailableException"/> when there was no worker to ask.
    /// <para>
    /// The distinction is the whole reason this method exists instead of a <c>?.Ok == true</c> at each call site. A
    /// missing transport used to read as a refused write, which the policy counts as a failed session — three of
    /// those switch the capability off for good, for a firmware fault that never happened.
    /// </para>
    /// </summary>
    private async Task<bool> WriteOrThrowAsync(WmiWorkerCommand command, JsonObject? args = null)
    {
        var response = await SendAsync(command, args).ConfigureAwait(false);
        if (response is null) throw new WmiTransportUnavailableException(_launcher.LastError);
        return response.Ok;
    }

    /// <summary>
    /// Sends one command, connecting first if needed. A transport failure drops the client so the next request
    /// reconnects instead of talking to a dead pipe for the rest of the session.
    /// </summary>
    private async Task<WmiWorkerResponse?> SendAsync(WmiWorkerCommand command, JsonObject? args = null)
    {
        if (_disposed) return null;
        if (!await EnsureConnectedAsync().ConfigureAwait(false)) return null;

        var client = _client;
        if (client == null) return null;

        try
        {
            return await client.SendAsync(command, args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("--wmi-worker connection lost", $"{command}: {ex.Message}");
            DropClient();
            return null;
        }
    }

    private void DropClient()
    {
        var client = Interlocked.Exchange(ref _client, null);
        try { client?.Dispose(); } catch { }
    }

    private static T? ParseEnum<T>(JsonObject? data, string key) where T : struct, Enum
    {
        try
        {
            var text = data?[key]?.GetValue<string>();
            return !string.IsNullOrEmpty(text) && Enum.TryParse<T>(text, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadBool(JsonObject data, string key)
    {
        try { return data[key]?.GetValue<bool>() == true; }
        catch { return false; }
    }

    /// <summary>A boolean that is absent when the worker did not report it, so "missing" is not read as "false".</summary>
    private static bool? ReadNullableBool(JsonObject data, string key)
    {
        try
        {
            var node = data[key];
            return node == null ? null : node.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// An integer that is absent when the worker did not report it, so "missing" is not read as zero.
    /// </summary>
    /// <remarks>
    /// The distinction matters for the fan percentage: zero is a real answer the firmware can give, and a read-back
    /// that turned "the worker did not say" into 0 would compare a 60 % write against 0 and report a failure that
    /// never happened.
    /// </remarks>
    private static int? ReadNullableInt(JsonObject? data, string key)
    {
        try
        {
            var node = data?[key];
            return node == null ? null : node.GetValue<int>();
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadInt(JsonObject data, string key)
    {
        try { return data[key]?.GetValue<int>(); }
        catch { return null; }
    }

    private static bool TryReadUlong(JsonObject data, string key, out ulong value)
    {
        value = 0;
        try
        {
            var node = data[key];
            if (node == null) return false;
            value = node.GetValue<ulong>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeRaw(JsonObject data) =>
        TryReadUlong(data, "muxRaw", out var raw) ? $"0x{raw:X4}" : "(okunamadı)";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DropClient();
        _writeGate.Dispose();
        _connectGate.Dispose();
        _launcher.Dispose();
    }
}
