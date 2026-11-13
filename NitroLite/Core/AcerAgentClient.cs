using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NitroLite.Core;

public class LightingConfig
{
    public string Effect { get; set; } = "WAVE";
    public int Brightness { get; set; } = 5;
    public int Speed { get; set; } = 3;
    public int Direction { get; set; } = 1;
    public string Color { get; set; } = "#00E5FF";
    public List<string> LEDs { get; set; } = new() { "#00E5FF", "#00E5FF", "#00E5FF", "#00E5FF" };
}

/// <summary>
/// Client for the Acer Agent Service (AASSvc / AcerAgentService.exe) loopback protocol on 127.0.0.1:46933.
///
/// Protocol (verified against NitroSense 5.0.1327 PSSdkAdapter and live AASSvc 1.2.2108.1):
///   Request  = "ACER" + uint32 little-endian PacketID + UTF-8 JSON { "Function": ..., "Parameter": ... }
///   Response = JSON { "request": &lt;Function or PacketID name&gt;, "result": "&lt;code&gt;", "data": ... }
///   Result codes: "0" SUCCESS, "1" FAIL_TO_GET_DATA, "2" TIMEOUT, "3" WRONG_PARAMETERS;
///   invalid parameter values have also been observed to return "4". Only "0" is success.
///   Messages without a "request" field are unsolicited service events
///   ({ "Function": ..., "Parameter": ... }, e.g. Turbo button or adapter changes).
///   Responses are matched to requests by the "request" field, never by arrival order.
/// </summary>
public class AcerAgentClient : IDisposable
{
    private const string Host = "127.0.0.1";
    private const int Port = 46933;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ACER");

    // Packet IDs (NitroSense PacketID enum)
    private const uint PacketInitialization = 0;
    private const uint PacketGetMonitorData = 10;
    private const uint PacketGetUpdatedData = 20;
    private const uint PacketGetConnectedDevice = 30;
    private const uint PacketGetLightingCapability = 40;
    private const uint PacketSetDeviceData = 100;

    public const string ResultSuccess = "0";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;

    // Bytes received but not yet consumed (may hold events or the start of the next message).
    private readonly MemoryStream _pending = new();

    /// <summary>
    /// Raised (on a thread-pool thread) for unsolicited AASSvc messages that carry no "request" field.
    /// Arguments: event function name (may be empty) and its Parameter node (may be null).
    /// </summary>
    public event Action<string, JsonNode?>? ServiceEventReceived;

    public virtual bool IsConnected => _client?.Connected ?? false;

    private void Disconnect()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
        // Dispose() may already have released the buffer; clearing it then would throw.
        try { _pending.SetLength(0); } catch (ObjectDisposedException) { }
    }

    public virtual async Task<bool> EnsureConnectedAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await EnsureConnectedInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> EnsureConnectedInternalAsync()
    {
        if (IsDisposed) return false;

        if (_client != null && _client.Connected && _stream != null)
            return true;

        // AASSvc is often absent (not installed, stopped, or still starting). Retrying on every telemetry tick
        // stalled each request for the full connect timeout, so back off exponentially between attempts.
        if (DateTime.UtcNow < _nextConnectAttemptUtc) return false;

        Disconnect();

        try
        {
            _client = new TcpClient();
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _client.ConnectAsync(Host, Port, connectCts.Token);
            _stream = _client.GetStream();

            // Handshake: the service answers VERSION with result "0".
            var initResp = await SendRawAsync(PacketInitialization, new { Function = "VERSION" }, "VERSION");
            if (!IsSuccessResult(initResp))
            {
                AppLog.Warn("AcerAgentService el sıkışması reddedildi", $"VERSION yanıtı: {initResp ?? "(boş)"}");
                Disconnect();
                RegisterConnectFailure();
                return false;
            }

            ResetConnectBackoff();
            return true;
        }
        catch
        {
            Disconnect();
            RegisterConnectFailure();
            return false;
        }
    }

    /// <summary>Clears the reconnect backoff after a successful connect or a connection that was alive.</summary>
    private void ResetConnectBackoff()
    {
        _consecutiveConnectFailures = 0;
        _nextConnectAttemptUtc = DateTime.MinValue;
    }

    /// <summary>Grows the delay between connect attempts: 1, 2, 4, 8, 16, then 30 seconds.</summary>
    private void RegisterConnectFailure()
    {
        _consecutiveConnectFailures = Math.Min(_consecutiveConnectFailures + 1, 10);
        double seconds = Math.Min(MaxConnectBackoffSeconds, Math.Pow(2, _consecutiveConnectFailures - 1));
        _nextConnectAttemptUtc = DateTime.UtcNow.AddSeconds(seconds);

        // Only the first failure of a streak: AASSvc being absent is normal and must not fill the log.
        if (_consecutiveConnectFailures == 1)
            AppLog.Warn("AcerAgentService bağlantısı kurulamadı", $"{Host}:{Port} - yeniden deneme geri çekilmesiyle devam ediliyor.");
    }

    public const int MaxResponseSizeBytes = 1024 * 1024; // 1 MB upper bound to prevent unbounded memory allocation

    /// <summary>
    /// Incremental brace scanner kept for compatibility. Returns true once a complete top-level object has been seen.
    /// </summary>
    public static bool TryScanJsonCompleteness(byte[] buffer, int offset, int length, ref int openBraceCount, ref bool inString, ref bool isEscaped, ref bool hasStartedJson)
    {
        for (int i = offset; i < offset + length; i++)
        {
            byte b = buffer[i];

            if (isEscaped)
            {
                isEscaped = false;
                continue;
            }

            if (b == '\\')
            {
                if (inString) isEscaped = true;
                continue;
            }

            if (b == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString)
            {
                if (b == '{')
                {
                    openBraceCount++;
                    hasStartedJson = true;
                }
                else if (b == '}')
                {
                    if (openBraceCount > 0)
                    {
                        openBraceCount--;
                        if (openBraceCount == 0 && hasStartedJson)
                        {
                            return true; // Complete JSON object reached!
                        }
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Finds the first complete top-level JSON object in <paramref name="data"/>.
    /// Bytes before the first '{' are treated as noise. Returns false if no complete object is present yet.
    /// <paramref name="start"/> is the index of the opening brace, <paramref name="endExclusive"/> the index after the closing brace.
    /// </summary>
    public static bool TryFindJsonObject(ReadOnlySpan<byte> data, out int start, out int endExclusive)
    {
        start = -1;
        endExclusive = -1;
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (start < 0)
            {
                if (b == '{')
                {
                    start = i;
                    depth = 1;
                }
                continue;
            }

            if (escaped) { escaped = false; continue; }
            if (inString)
            {
                if (b == '\\') escaped = true;
                else if (b == '"') inString = false;
                continue;
            }

            if (b == '"') inString = true;
            else if (b == '{') depth++;
            else if (b == '}')
            {
                depth--;
                if (depth == 0)
                {
                    endExclusive = i + 1;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Removes and returns the next complete JSON object from the pending buffer, or null if none is complete yet.
    /// </summary>
    private string? TakeNextObjectFromPending()
    {
        if (_pending.Length == 0) return null;
        var bytes = _pending.GetBuffer();
        var span = new ReadOnlySpan<byte>(bytes, 0, (int)_pending.Length);
        if (!TryFindJsonObject(span, out int start, out int end))
        {
            // Drop leading noise before any '{' so it cannot accumulate.
            int brace = span.IndexOf((byte)'{');
            if (brace < 0) _pending.SetLength(0);
            else if (brace > 0) Compact(brace);
            return null;
        }

        string json = Encoding.UTF8.GetString(bytes, start, end - start);
        Compact(end);
        return json;
    }

    private void Compact(int consumed)
    {
        int remaining = (int)_pending.Length - consumed;
        if (remaining > 0)
        {
            var bytes = _pending.GetBuffer();
            Buffer.BlockCopy(bytes, consumed, bytes, 0, remaining);
        }
        _pending.SetLength(Math.Max(0, remaining));
        _pending.Position = _pending.Length;
    }

    /// <summary>
    /// Classifies one decoded message. Returns true if it is the response to <paramref name="expectedRequest"/>.
    /// Unsolicited events are published through <see cref="ServiceEventReceived"/>; stale responses are dropped.
    /// </summary>
    internal bool IsResponseFor(string json, string expectedRequest)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch { return false; }
        if (node is not JsonObject obj) return false;

        var request = obj["request"]?.ToString();

        // Some AASSvc replies (e.g. packet 40) carry "request": null. They are still responses: they have a
        // "request" or "result" key, while service events carry "Function"/"Parameter" instead. Only one
        // request is ever in flight (the client lock) and a timeout disconnects, so a null-request response
        // belongs to the request being awaited. During the idle drain (expectedRequest empty) it is dropped.
        bool isResponseShaped = obj.ContainsKey("request") || obj.ContainsKey("result");
        if (request == null && isResponseShaped)
        {
            return expectedRequest.Length > 0;
        }

        if (request == null)
        {
            var fn = obj["Function"]?.ToString() ?? string.Empty;
            var parameter = obj["Parameter"]?.DeepClone();
            var handler = ServiceEventReceived;
            if (handler != null)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { handler(fn, parameter); } catch { }
                });
            }
            return false;
        }

        return string.Equals(request, expectedRequest, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> SendRawAsync(uint packetId, object payload, string expectedRequest)
    {
        if (IsDisposed || _stream == null) return null;

        try
        {
            var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            var packet = new byte[Magic.Length + 4 + jsonBytes.Length];
            Buffer.BlockCopy(Magic, 0, packet, 0, Magic.Length);
            BitConverter.GetBytes(packetId).CopyTo(packet, Magic.Length);
            Buffer.BlockCopy(jsonBytes, 0, packet, Magic.Length + 4, jsonBytes.Length);

            using var ioCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            // Events that arrived while idle are still in the buffer; publish them before the new request.
            string? queued;
            while ((queued = TakeNextObjectFromPending()) != null)
            {
                IsResponseFor(queued, string.Empty);
            }

            await _stream.WriteAsync(packet, ioCts.Token);
            await _stream.FlushAsync(ioCts.Token);

            var buffer = new byte[8192];
            while (true)
            {
                string? message;
                while ((message = TakeNextObjectFromPending()) != null)
                {
                    if (IsResponseFor(message, expectedRequest))
                    {
                        return message;
                    }
                }

                int read = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ioCts.Token);
                if (read <= 0)
                {
                    // The connection was alive, so the next request should try to reconnect immediately.
                    Disconnect();
                    ResetConnectBackoff();
                    return null;
                }

                if (_pending.Length + read > MaxResponseSizeBytes)
                {
                    Disconnect();
                    ResetConnectBackoff();
                    return null;
                }

                _pending.Position = _pending.Length;
                _pending.Write(buffer, 0, read);
            }
        }
        catch
        {
            // On timeout or I/O error, disconnect so late bytes cannot be mistaken for the next response.
            Disconnect();
            ResetConnectBackoff();
            return null;
        }
    }

    private Task<string?> SendFunctionAsync(uint packetId, string function, object? parameter = null)
    {
        object payload = parameter == null
            ? new { Function = function }
            : new { Function = function, Parameter = parameter };
        return SendRawAsync(packetId, payload, function);
    }

    /// <summary>
    /// Returns true only for result "0" (SUCCESS). "1"/"2"/"3"/"4" and malformed JSON are failures.
    /// </summary>
    public static bool IsSuccessResult(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return false;
        try
        {
            var node = JsonNode.Parse(responseJson);
            var resStr = node?["result"]?.ToString();
            return resStr == ResultSuccess;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Parses a successful response and returns its "data" node, or null on any failure.</summary>
    private static JsonNode? GetSuccessData(string? responseJson)
    {
        if (!IsSuccessResult(responseJson)) return null;
        try
        {
            return JsonNode.Parse(responseJson!)?["data"];
        }
        catch
        {
            return null;
        }
    }

    public virtual async Task<MonitorData?> GetMonitorDataAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return null;

            var resp = await SendRawAsync(PacketGetMonitorData, new { }, "GET_MONITOR_DATA");
            return ParseMonitorData(GetSuccessData(resp));
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Reads the monitor payload field by field. A single malformed or missing field used to make the whole
    /// JsonSerializer.Deserialize call throw, which blanked every value on the monitoring page for that tick.
    /// </summary>
    public static MonitorData? ParseMonitorData(JsonNode? dataNode)
    {
        if (dataNode is not JsonObject obj) return null;

        return new MonitorData
        {
            CpuTemperature = ReadInt(obj, "CPU_TEMPERATURE"),
            GpuTemperature = ReadInt(obj, "GPU1_TEMPERATURE"),
            SysTemperature = ReadInt(obj, "SYS1_TEMPERATURE"),
            CpuFanSpeed = ReadInt(obj, "CPU_FANSPEED"),
            GpuFanSpeed = ReadInt(obj, "GPU1_FANSPEED"),
            SysFanSpeed = ReadInt(obj, "SYS1_FANSPEED"),
            CpuUsage = ReadDouble(obj, "CPU_USAGE"),
            GpuUsage = ReadDouble(obj, "GPU1_USAGE"),
            RamUsage = ReadDouble(obj, "RAM_USAGE"),
            RamTotalMB = ReadDouble(obj, "RAM_TOTAL"),
            CpuFrequencyMHz = ReadDouble(obj, "CPU_FREQUENCY"),
            GpuFrequencyMHz = ReadDouble(obj, "GPU1_FREQUENCY"),
            CpuCores = ReadInt(obj, "CPU_CORE_AMOUNT")
        };
    }

    private static int ReadInt(JsonObject obj, string name)
    {
        double value = ReadDouble(obj, name);
        if (value <= 0) return 0;
        return value >= int.MaxValue ? int.MaxValue : (int)Math.Round(value);
    }

    /// <summary>Reads one numeric field, accepting numbers and numeric strings; anything else counts as 0.</summary>
    private static double ReadDouble(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value) return 0;
        try
        {
            if (value.TryGetValue<double>(out var number)) return double.IsFinite(number) ? number : 0;
            if (value.TryGetValue<long>(out var integer)) return integer;
            if (value.TryGetValue<string>(out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return double.IsFinite(parsed) ? parsed : 0;
        }
        catch { }
        return 0;
    }

    /// <summary>Reads the operating mode. Returns null when the service is unreachable or rejects the query.</summary>
    public virtual async Task<OperatingMode?> TryGetOperatingModeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return null;

            var resp = await SendFunctionAsync(PacketGetUpdatedData, "OPERATING_MODE");
            var modeVal = GetSuccessData(resp)?["mode"]?.GetValue<int>();
            if (modeVal.HasValue && Enum.IsDefined(typeof(OperatingMode), modeVal.Value))
            {
                return (OperatingMode)modeVal.Value;
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public virtual async Task<OperatingMode> GetOperatingModeAsync()
    {
        return await TryGetOperatingModeAsync() ?? OperatingMode.Default;
    }

    public virtual async Task<bool> SetOperatingModeAsync(OperatingMode mode)
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return false;
            var resp = await SendFunctionAsync(PacketSetDeviceData, "OPERATING_MODE", new { mode = (int)mode });
            return IsSuccessResult(resp);
        }
        catch
        {
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public virtual async Task<GpuOperatingMode?> GetGpuModeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return null;

            var resp = await SendFunctionAsync(PacketGetUpdatedData, "GPU_MODE");
            var modeVal = GetSuccessData(resp)?["mode"]?.GetValue<int>();
            if (modeVal.HasValue && Enum.IsDefined(typeof(GpuOperatingMode), modeVal.Value))
            {
                return (GpuOperatingMode)modeVal.Value;
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public virtual async Task<bool> SetGpuModeAsync(GpuOperatingMode mode)
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return false;
            var resp = await SendFunctionAsync(PacketSetDeviceData, "GPU_MODE", new { mode = (int)mode });
            return IsSuccessResult(resp);
        }
        catch
        {
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public virtual async Task<int?> GetGpuModeCapabilityAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return null;

            var resp = await SendFunctionAsync(PacketInitialization, "SUPPORT_GPU_MODE_CAPABILITY");
            if (GetSuccessData(resp) is JsonArray capArr && capArr.Count > 0)
            {
                return capArr[0]?.GetValue<int>();
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Builds the FAN_CONTROL parameter in the format used by NitroSense and accepted by AASSvc:
    /// { mode, custom_fan_data: [ { fan_custom_auto, fan_custom_speed (0-100 %), fan_index, fan_name } ] }.
    /// </summary>
    public static object BuildFanControlParameter(FanMode mode, int cpuPercent = 50, int gpuPercent = 50)
    {
        int cpu = Math.Clamp(cpuPercent, 0, 100);
        int gpu = Math.Clamp(gpuPercent, 0, 100);
        int customAuto = mode == FanMode.Custom ? 0 : 1;
        return new
        {
            mode = (int)mode,
            custom_fan_data = new object[]
            {
                new { fan_custom_auto = customAuto, fan_custom_speed = cpu, fan_index = 0, fan_name = "CPU" },
                new { fan_custom_auto = customAuto, fan_custom_speed = gpu, fan_index = 0, fan_name = "GPU" }
            }
        };
    }

    /// <summary>Reads the current fan mode (GET_UPDATED_DATA FAN_CONTROL → data.mode). Null if unknown.</summary>
    public virtual async Task<FanMode?> GetFanModeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return null;
            var resp = await SendFunctionAsync(PacketGetUpdatedData, "FAN_CONTROL");
            var mode = GetSuccessData(resp)?["mode"]?.GetValue<int>();
            return mode.HasValue && Enum.IsDefined(typeof(FanMode), mode.Value) ? (FanMode)mode.Value : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// CPU/GPU fan speeds (%) sent whenever <see cref="FanMode.Custom"/> is applied without explicit values, so every
    /// path that restores a saved fan mode (game end, adapter change, crash recovery) restores the user's speeds too.
    /// </summary>
    public int CustomFanCpuPercent { get; set; } = 50;
    public int CustomFanGpuPercent { get; set; } = 50;

    /// <summary>
    /// Sets fan mode. For <see cref="FanMode.Custom"/> the speeds are percentages (0-100); a negative value means
    /// "use <see cref="CustomFanCpuPercent"/> / <see cref="CustomFanGpuPercent"/>". AASSvc ignores the speeds in
    /// Auto and Max (verified on the AN16-41: 60 % custom ≈ 4300 RPM on both fans).
    /// </summary>
    public virtual async Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
    {
        if (cpuPercent < 0) cpuPercent = CustomFanCpuPercent;
        if (gpuPercent < 0) gpuPercent = CustomFanGpuPercent;
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return false;
            var resp = await SendFunctionAsync(PacketSetDeviceData, "FAN_CONTROL", BuildFanControlParameter(mode, cpuPercent, gpuPercent));
            return IsSuccessResult(resp);
        }
        catch
        {
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Asks the agent whether this machine has controllable keyboard lighting (packet 40). Returns null when the
    /// agent does not answer or answers in an unexpected shape: an unknown capability must never disable the tab.
    /// </summary>
    public virtual async Task<bool?> GetLightingCapabilityAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return null;

            var resp = await SendFunctionAsync(PacketGetLightingCapability, "LIGHTING_CAPABILITY");
            var data = GetSuccessData(resp);
            if (data == null) return null;

            // Depending on the model the agent answers with a bare flag or a one-element array.
            if (data is JsonArray arr) return arr.Count > 0 && arr[0] != null && ReadCapabilityFlag(arr[0]!);
            return ReadCapabilityFlag(data);
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Reads a capability answer that may be a bool, 0/1, "true"/"1" or an object wrapping one of those.</summary>
    private static bool ReadCapabilityFlag(JsonNode node)
    {
        try
        {
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "support", "supported", "capability", "value", "lighting", "result" })
                {
                    if (obj[key] is JsonNode inner) return ReadCapabilityFlag(inner);
                }
                return false;
            }

            if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out var flag)) return flag;
                if (value.TryGetValue<int>(out var number)) return number != 0;
                if (value.TryGetValue<string>(out var text))
                    return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
    }

    public async Task<LightingConfig?> GetLightingConfigAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return null;

            var resp = await SendFunctionAsync(PacketGetUpdatedData, "LIGHTING");
            var dataNode = GetSuccessData(resp);
            // Without the effect and brightness the reply describes nothing usable; do not invent a WAVE/cyan default.
            if (dataNode?["effect"] == null || dataNode["brightness"] == null) return null;

            var cfg = new LightingConfig
            {
                Effect = dataNode["effect"]?.GetValue<string>() ?? "WAVE",
                Brightness = dataNode["brightness"]?.GetValue<int>() ?? 5,
                Speed = dataNode["speed"]?.GetValue<int>() ?? 3,
                Direction = dataNode["direction"]?.GetValue<int>() ?? 1,
                Color = dataNode["color"]?.GetValue<string>() ?? "#00E5FF"
            };

            if (dataNode["LEDs"] is JsonArray ledsArr && ledsArr.Count >= 4)
            {
                cfg.LEDs = ledsArr.Select(x => x?.GetValue<string>() ?? "#00E5FF").ToList();
            }

            return cfg;
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string NormalizeHexColor(string? color, string fallback)
    {
        if (string.IsNullOrWhiteSpace(color)) return fallback;
        var hex = color.Trim().TrimStart('#');
        if (hex.Length != 6) return fallback;
        foreach (var ch in hex)
        {
            if (!Uri.IsHexDigit(ch)) return fallback;
        }
        return "#" + hex.ToUpperInvariant();
    }

    /// <summary>True when the last lighting command was accepted by only one of the two targets (global / keyboard).</summary>
    public bool LastLightingPartialFailure { get; private set; }

    /// <summary>
    /// NitroSense EFFECT_DIRECTION: 1 = right, 2 = left, 3 = up, 4 = down (5-8 rotate/diagonal, unused here).
    /// 0 is not a valid direction (the old 0..1 clamp sent it for "left").
    /// </summary>
    public static int NormalizeDirection(int direction) => direction is >= 1 and <= 4 ? direction : 1;

    public async Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors)
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return false;

            var effUpper = (effect ?? "STATIC").ToUpperInvariant();
            var isOff = effUpper == "OFF" || brightness <= 0;
            var isStatic = effUpper == "STATIC" || isOff;

            var safePrimary = NormalizeHexColor(primaryColor, "#00E5FF");
            string[] leds;
            if (isOff)
            {
                leds = new[] { "#000000", "#000000", "#000000", "#000000" };
            }
            else
            {
                leds = new string[4];
                for (int i = 0; i < 4; i++)
                {
                    var source = zoneColors != null && i < zoneColors.Length ? zoneColors[i] : safePrimary;
                    leds[i] = NormalizeHexColor(source, safePrimary);
                }
            }

            var actualBrightness = isOff ? 0 : Math.Clamp(brightness, 0, 9);
            var actualColor = isOff ? "#000000" : safePrimary;
            var actualEffect = isOff ? "OFF" : effUpper;

            object BuildParam(int device) => isStatic
                ? new
                {
                    device,
                    effect = actualEffect,
                    brightness = actualBrightness,
                    speed = Math.Clamp(speed, 1, 9),
                    duration = 5,
                    direction = NormalizeDirection(direction),
                    color = actualColor,
                    LEDs = leds
                }
                : new
                {
                    device,
                    effect = actualEffect,
                    dyEffect = actualEffect,
                    brightness = actualBrightness,
                    speed = Math.Clamp(speed, 1, 9),
                    duration = 5,
                    direction = NormalizeDirection(direction),
                    color = actualColor,
                    LEDs = leds
                };

            // Global device (device = 0) is the master lighting target; device = 1 is the keyboard sub-device.
            var respGlobal = await SendFunctionAsync(PacketSetDeviceData, "LIGHTING", BuildParam(0));
            bool globalSuccess = IsSuccessResult(respGlobal);

            var respKb = await SendFunctionAsync(PacketSetDeviceData, "LIGHTING", BuildParam(1));
            bool kbSuccess = IsSuccessResult(respKb);

            // Both the global (0) and keyboard (1) targets are expected to accept the command; report a
            // half-applied change instead of hiding it.
            LastLightingPartialFailure = globalSuccess != kbSuccess;
            return globalSuccess || kbSuccess;
        }
        catch
        {
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> TurnOffLightingAsync()
    {
        return await SetLightingAsync("OFF", 0, 1, 1, "#000000", new[] { "#000000", "#000000", "#000000", "#000000" });
    }

    /// <summary>Reads the keyboard 30 s idle-off setting (GET_UPDATED_DATA BACKLIGHT_30S → data.status). Null if unknown.</summary>
    public virtual async Task<bool?> GetBacklightTimeoutAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return null;
            var resp = await SendFunctionAsync(PacketGetUpdatedData, "BACKLIGHT_30S");
            var status = GetSuccessData(resp)?["status"]?.GetValue<int>();
            return status.HasValue ? status.Value == 1 : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SetBacklightTimeoutAsync(bool enable30s)
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return false;
            var resp = await SendFunctionAsync(PacketSetDeviceData, "BACKLIGHT_30S", new { status = enable30s ? 1 : 0 });
            return IsSuccessResult(resp);
        }
        catch
        {
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    private int _disposed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private int _consecutiveConnectFailures;
    private DateTime _nextConnectAttemptUtc = DateTime.MinValue;
    private const double MaxConnectBackoffSeconds = 30;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Dispose is called on the UI thread during exit, so wait only briefly for an in-flight request.
        // If it is still running, Disconnect() closes the socket, which makes its pending read fail fast
        // (SendRawAsync catches that); the buffer is then left to the GC instead of being disposed under it.
        bool acquired = false;
        try { acquired = _lock.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        try
        {
            Disconnect();
            if (acquired) _pending.Dispose();
        }
        finally
        {
            if (acquired)
            {
                try { _lock.Release(); } catch { }
            }
        }
    }
}
