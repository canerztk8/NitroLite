using System.Text.Json.Nodes;

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
    private readonly WmiWorkerLauncher _launcher;
    private readonly ReadBackPolicy _readBack;
    private readonly Func<CapabilitySnapshot?> _capabilities;

    /// <summary>One gate for every write, so two callers cannot interleave a fan write and its read-back.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Separate gate for connecting: a second caller waits instead of starting a second helper.</summary>
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private WmiWorkerClient? _client;
    private bool _disposed;

    /// <summary>
    /// <paramref name="capabilities"/> supplies the probe result. It is a delegate rather than a value because the
    /// user can rescan from the settings page at any time, and a controller holding the snapshot from launch would
    /// keep using sensor offsets the rescan just replaced. It is optional: without it the controller still works,
    /// but the sensor offsets fall back to the default layout and the battery limit is always asked about.
    /// </summary>
    public WmiHardwareController(
        WmiWorkerLauncher launcher,
        ReadBackPolicy readBack,
        Func<CapabilitySnapshot?>? capabilities = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _readBack = readBack ?? throw new ArgumentNullException(nameof(readBack));
        _capabilities = capabilities ?? (() => null);
    }

    /// <summary>The probe result as it stands right now, or null when nothing has been probed yet.</summary>
    private CapabilitySnapshot? Snapshot => _capabilities();

    public string BackendName => "WMI";

    public bool IsAvailable => _client != null;

    /// <summary>True once the user has said no to the elevation prompt. The UI uses it to stop offering the path.</summary>
    public bool UserDeclinedElevation => _launcher.UserDeclined;

    /// <summary>Why the last connection attempt failed, for the settings page and the log.</summary>
    public string LastError => _launcher.LastError;

    /// <summary>The read-back policy, so the settings page can reset its counters.</summary>
    public ReadBackPolicy ReadBack => _readBack;

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
            OperatingModeControl = ReadBool(data, "operatingMode"),
            MuxSwitch = ReadBool(data, "muxSwitch"),
            BatteryChargeLimit = ReadBool(data, "batteryLimit"),
            KeyboardLighting = ReadBool(data, "keyboardLighting"),
            BacklightTimeout = false
        };
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

        var response = await SendAsync(WmiWorkerCommand.ReadTelemetry, new JsonObject
        {
            ["cpuTempSensor"] = layout.CpuTempSensor,
            ["cpuFanRpmSensor"] = layout.CpuFanRpmSensor,
            ["sys1TempSensor"] = layout.Sys1TempSensor,
            ["gpuFanRpmSensor"] = layout.GpuFanRpmSensor,
            ["gpuTempSensor"] = layout.GpuTempSensor
        }).ConfigureAwait(false);

        if (response is not { Ok: true } || response.Data is not JsonObject data) return null;

        return BuildMonitorData(data, layout, snapshot?.MaxCpuFanRpm ?? 0, snapshot?.MaxGpuFanRpm ?? 0);
    }

    /// <summary>
    /// Turns one <c>ReadTelemetry</c> answer into a sample. Pure, so the mapping from a firmware answer to a
    /// <see cref="MonitorData"/> can be pinned without a pipe or a worker.
    /// </summary>
    public static MonitorData BuildMonitorData(JsonObject data, SensorLayout layout, int maxCpuFanRpm, int maxGpuFanRpm)
    {
        var sensors = data["sensors"] as JsonObject;

        int? cpuRpm = layout.Supported ? ReadSensorValue(sensors, "cpuFanRpm") : null;
        int? gpuRpm = layout.Supported ? ReadSensorValue(sensors, "gpuFanRpm") : null;
        bool measured = cpuRpm.HasValue && gpuRpm.HasValue;

        return new MonitorData
        {
            CpuTemperature = layout.Supported ? ReadSensorValue(sensors, "cpuTemp") ?? 0 : 0,
            GpuTemperature = layout.Supported ? ReadSensorValue(sensors, "gpuTemp") ?? 0 : 0,
            SysTemperature = layout.Supported ? ReadSensorValue(sensors, "sys1Temp") ?? 0 : 0,

            CpuFanSpeed = measured ? cpuRpm!.Value : DeriveRpm(ReadInt(data, "cpuFanPercent") ?? 0, maxCpuFanRpm),
            GpuFanSpeed = measured ? gpuRpm!.Value : DeriveRpm(ReadInt(data, "gpuFanPercent") ?? 0, maxGpuFanRpm),

            IsFanRpmMeasured = measured
        };
    }

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
        var result = await ApplyAsync(
            ReadBackPolicy.OperatingMode,
            write: async () => (await SendAsync(WmiWorkerCommand.SetOperatingMode,
                new JsonObject { ["mode"] = (int)mode }).ConfigureAwait(false))?.Ok == true,
            readBack: GetOperatingModeAsync,
            matches: observed => observed == mode).ConfigureAwait(false);

        return result.Applied;
    }

    // ---------------------------------------------------------------- MUX

    public async Task<GpuOperatingMode?> GetGpuModeAsync()
    {
        var state = await ReadDeviceStateAsync().ConfigureAwait(false);
        return ParseEnum<GpuOperatingMode>(state, "muxMode");
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
            var state = await ReadDeviceStateAsync().ConfigureAwait(false);
            if (state == null) return false;

            if (!TryReadUlong(state, "muxRaw", out var raw) || !IsMuxValueInRange(raw))
            {
                AppLog.Warn("MUX yazımı reddedildi",
                    $"mevcut GetGamingMiscSetting(0x02) değeri {DescribeRaw(state)}; beklenen üst bayt 1..3");
                return false;
            }

            var result = await _readBack.ApplyAsync(
                ReadBackPolicy.MuxMode,
                write: async () => (await SendAsync(WmiWorkerCommand.SetMuxMode,
                    new JsonObject { ["mode"] = (int)mode }).ConfigureAwait(false))?.Ok == true,
                readBack: GetGpuModeAsync,
                matches: observed => observed == mode).ConfigureAwait(false);

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

    public async Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
    {
        if (mode == FanMode.Custom)
        {
            var cpu = NormalisePercent(cpuPercent);
            var gpu = NormalisePercent(gpuPercent);

            var speed = await ApplyAsync(
                ReadBackPolicy.FanSpeed,
                write: async () => (await SendAsync(WmiWorkerCommand.SetFanSpeed, new JsonObject
                {
                    ["cpuPercent"] = cpu,
                    ["gpuPercent"] = gpu
                }).ConfigureAwait(false))?.Ok == true,
                readBack: GetFanModeAsync,
                matches: observed => observed == FanMode.Custom).ConfigureAwait(false);

            return speed.Applied;
        }

        var behaviour = await ApplyAsync(
            ReadBackPolicy.FanBehavior,
            write: async () => (await SendAsync(WmiWorkerCommand.SetFanBehavior, new JsonObject
            {
                ["cpu"] = (int)mode,
                ["gpu"] = (int)mode
            }).ConfigureAwait(false))?.Ok == true,
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

        var color = AcerWmiProtocol.FormatHexColor(red, green, blue);
        var level = AcerWmiProtocol.BrightnessPercentToLevel(percent);
        var isOff = level == 0 || (red == 0 && green == 0 && blue == 0);

        return new LightingConfig
        {
            Effect = isOff ? "OFF" : "STATIC",
            Brightness = isOff ? 0 : Math.Max(level, 1),
            Speed = 3,
            Direction = 1,
            Color = color,
            LEDs = new List<string> { color, color, color, color }
        };
    }

    /// <summary>
    /// Writes the keyboard colour, one zone at a time.
    /// <para>
    /// The firmware takes one colour per call plus a zone mask, so four different zone colours are four writes. A
    /// mask of zero is rejected by the EC, which is why the four-zone picker never worked through the service path:
    /// the colour was sent with no zone to put it in.
    /// </para>
    /// <para>
    /// Effects (wave, breathing, ripple) are an animation the Acer service runs; the firmware only stores a static
    /// colour. A caller asking for one gets that colour held steady rather than a control that does nothing.
    /// Brightness is not writable on this path — see <see cref="LightingState"/> — so a level of zero means "black"
    /// and any other level means "on".
    /// </para>
    /// </summary>
    public async Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors)
    {
        var requested = (effect ?? "STATIC").Trim().ToUpperInvariant();
        var isOff = requested == "OFF" || brightness <= 0;

        var zones = new (byte Red, byte Green, byte Blue)[AcerWmiProtocol.LedZoneCount];
        if (!isOff)
        {
            for (int i = 0; i < zones.Length; i++)
            {
                var source = zoneColors != null && i < zoneColors.Length ? zoneColors[i] : primaryColor;
                if (!AcerWmiProtocol.TryParseHexColor(source, out var red, out var green, out var blue) &&
                    !AcerWmiProtocol.TryParseHexColor(primaryColor, out red, out green, out blue))
                {
                    red = green = blue = 0;
                }

                zones[i] = (red, green, blue);
            }
        }

        var target = new LightingState(PackRgb(zones[0]));

        var result = await ApplyAsync(
            ReadBackPolicy.KeyboardLighting,
            write: async () => await WriteZonesAsync(zones).ConfigureAwait(false),
            readBack: ReadLightingStateAsync,
            matches: observed => observed.Rgb == target.Rgb).ConfigureAwait(false);

        return result.Applied;
    }

    public Task<bool> TurnOffLightingAsync() =>
        SetLightingAsync("OFF", 0, 1, 1, "#000000", new[] { "#000000", "#000000", "#000000", "#000000" });

    /// <summary>
    /// Sends one write per zone. Zones that share a colour are still written separately: the mask is what selects
    /// them, and a combined mask would overwrite a neighbour that was set a moment ago.
    /// </summary>
    private async Task<bool> WriteZonesAsync((byte Red, byte Green, byte Blue)[] zones)
    {
        for (int i = 0; i < zones.Length; i++)
        {
            var mask = (byte)(AcerWmiProtocol.LedZone1 << i);
            var response = await SendAsync(WmiWorkerCommand.SetKeyboardBacklight, new JsonObject
            {
                ["zoneMask"] = mask,
                ["red"] = zones[i].Red,
                ["green"] = zones[i].Green,
                ["blue"] = zones[i].Blue
            }).ConfigureAwait(false);

            if (response is not { Ok: true }) return false;
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

    public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(null);

    public Task<bool> SetBacklightTimeoutAsync(bool enable30s) => Task.FromResult(false);

    // ---------------------------------------------------------------- battery charge limit

    /// <summary>
    /// True when the probe already established that this machine has no WMID_GUID5. Asking the worker anyway would
    /// cost a round trip to learn what is already known, and on an AN16-41 the answer is always "not registered".
    /// </summary>
    public bool BatteryLimitKnownUnsupported =>
        Snapshot?.Find(CapabilitySnapshot.WmiBatteryChargeLimit)?.Outcome == HardwareProbeOutcome.Unsupported;

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
            write: async () => (await SendAsync(WmiWorkerCommand.SetBatteryLimit,
                new JsonObject { ["mode"] = (int)mode }).ConfigureAwait(false))?.Ok == true,
            readBack: GetBatteryChargeModeAsync,
            matches: observed => observed == mode).ConfigureAwait(false);

        return result.Applied;
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>Runs one write and its read-back behind the write gate.</summary>
    private async Task<ReadBackResult> ApplyAsync<T>(
        string capability,
        Func<Task<bool>> write,
        Func<Task<T?>> readBack,
        Func<T, bool> matches) where T : struct
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _readBack.ApplyAsync(capability, write, readBack, matches).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<JsonObject?> ReadDeviceStateAsync()
    {
        var response = await SendAsync(WmiWorkerCommand.ReadDeviceState).ConfigureAwait(false);
        return response is { Ok: true } ? response.Data as JsonObject : null;
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
            AppLog.Warn("--wmi-worker bağlantısı koptu", $"{command}: {ex.Message}");
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
