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
/// Keyboard lighting and the backlight timeout are not implemented here. The gaming class does expose
/// <c>SetGamingRgbKb</c>, but its packet layout differs per chassis and getting it wrong writes arbitrary bytes into
/// the keyboard controller. That work belongs with the OpenRGB client, and until then those two capabilities stay on
/// the Acer service path.
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
            KeyboardLighting = false,
            BacklightTimeout = false
        };
    }

    // ---------------------------------------------------------------- telemetry

    /// <summary>
    /// One sample from <c>GetGamingSysInfo</c>. Temperatures come straight out of the raw bytes at the offsets the
    /// matched profile declares. Fan speeds do not: the firmware reports a percentage, not an RPM, so the RPM shown
    /// is derived from the profile's ceiling and is an estimate rather than a measurement.
    /// </summary>
    public async Task<MonitorData?> GetMonitorDataAsync()
    {
        var response = await SendAsync(WmiWorkerCommand.ReadTelemetry).ConfigureAwait(false);
        if (response is not { Ok: true } || response.Data is not JsonObject data) return null;

        var snapshot = Snapshot;
        var layout = snapshot?.Sensors ?? SensorLayout.Default;
        var bytes = ReadBytes(data["sysInfoBytes"]);

        return new MonitorData
        {
            CpuTemperature = AcerWmiProtocol.ByteAt(bytes, layout.CpuTempByte) ?? 0,
            GpuTemperature = AcerWmiProtocol.ByteAt(bytes, layout.GpuTempByte) ?? 0,
            SysTemperature = AcerWmiProtocol.ByteAt(bytes, layout.Sys1TempByte) ?? 0,
            CpuFanSpeed = DeriveRpm(ReadInt(data, "cpuFanPercent") ?? 0, snapshot?.MaxCpuFanRpm ?? 0),
            GpuFanSpeed = DeriveRpm(ReadInt(data, "gpuFanPercent") ?? 0, snapshot?.MaxGpuFanRpm ?? 0),

            // The fan speeds above are the requested percentage restated as RPM, not a tachometer reading. The
            // watchdog must not judge them: a fan that has stopped still reports the percentage the EC was given.
            IsFanRpmMeasured = false
        };
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

    // ---------------------------------------------------------------- keyboard lighting (not on this backend)

    public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult<LightingConfig?>(null);

    public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors) =>
        Task.FromResult(false);

    public Task<bool> TurnOffLightingAsync() => Task.FromResult(false);

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

    private static IReadOnlyList<byte>? ReadBytes(JsonNode? node)
    {
        if (node is not JsonArray array) return null;

        var bytes = new List<byte>(array.Count);
        foreach (var item in array)
        {
            try { bytes.Add((byte)item!.GetValue<int>()); }
            catch { return null; }
        }

        return bytes;
    }

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
