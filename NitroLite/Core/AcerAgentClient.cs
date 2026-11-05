using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
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

public class AcerAgentClient : IDisposable
{
    private const string Host = "127.0.0.1";
    private const int Port = 46933;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ACER");

    // Packet IDs
    private const uint PacketInitialization = 0;
    private const uint PacketGetMonitorData = 10;
    private const uint PacketGetUpdatedData = 20;
    private const uint PacketGetConnectedDevice = 30;
    private const uint PacketGetLightingCapability = 40;
    private const uint PacketSetDeviceData = 100;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;

    public virtual bool IsConnected => _client?.Connected ?? false;

    private void Disconnect()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
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
        if (_client != null && _client.Connected)
            return true;

        Disconnect();

        try
        {
            _client = new TcpClient();
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _client.ConnectAsync(Host, Port, connectCts.Token);
            _stream = _client.GetStream();
            _stream.ReadTimeout = 3000;
            _stream.WriteTimeout = 3000;

            // Send handshake initialization
            var initResp = await SendRawAsync(PacketInitialization, new { Function = "VERSION" });
            return initResp != null;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    public const int MaxResponseSizeBytes = 1024 * 1024; // 1 MB upper bound to prevent unbounded memory allocation

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

    private async Task<string?> SendRawAsync(uint packetId, object payload)
    {
        if (_stream == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(payload);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var packetIdBytes = BitConverter.GetBytes(packetId);

            using var ms = new MemoryStream();
            await ms.WriteAsync(Magic);
            await ms.WriteAsync(packetIdBytes);
            await ms.WriteAsync(jsonBytes);

            var packet = ms.ToArray();
            using var ioCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            await _stream.WriteAsync(packet, ioCts.Token);
            await _stream.FlushAsync(ioCts.Token);

            // Read response with streaming chunk reassembly, balanced brace framing, and size limits
            using var responseMs = new MemoryStream();
            var buffer = new byte[8192];
            int openBraces = 0;
            bool inString = false;
            bool isEscaped = false;
            bool hasStarted = false;

            while (true)
            {
                int read = await _stream.ReadAsync(buffer, 0, buffer.Length, ioCts.Token);
                if (read <= 0)
                {
                    // Server closed or EOF before complete JSON was received
                    Disconnect();
                    return null;
                }

                // Check size bounding before copying
                if (responseMs.Length + read > MaxResponseSizeBytes)
                {
                    // Response exceeded maximum safe limit (1 MB). Disconnect to drain socket.
                    Disconnect();
                    return null;
                }

                responseMs.Write(buffer, 0, read);

                // Scan byte stream for balanced JSON braces without decoding to string every chunk
                bool isComplete = TryScanJsonCompleteness(buffer, 0, read, ref openBraces, ref inString, ref isEscaped, ref hasStarted);

                if (isComplete)
                {
                    var fullBytes = responseMs.ToArray();
                    var candidate = Encoding.UTF8.GetString(fullBytes).Trim();
                    if (IsValidJson(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch
        {
            // On timeout or I/O error, disconnect immediately so late bytes do not corrupt next command
            Disconnect();
            return null;
        }
    }

    private static bool IsValidJson(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return false;
        str = str.Trim();
        if (!str.StartsWith("{") || !str.EndsWith("}")) return false;
        try
        {
            using var doc = JsonDocument.Parse(str);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public virtual async Task<MonitorData?> GetMonitorDataAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return null;

            var resp = await SendRawAsync(PacketGetMonitorData, new { Function = "GET_MONITOR_DATA" });
            if (string.IsNullOrWhiteSpace(resp)) return null;

            var node = JsonNode.Parse(resp);
            var dataNode = node?["data"];
            if (dataNode == null) return null;

            return JsonSerializer.Deserialize<MonitorData>(dataNode.ToJsonString());
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
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return OperatingMode.Default;

            var resp = await SendRawAsync(PacketGetUpdatedData, new { Function = "OPERATING_MODE" });
            if (string.IsNullOrWhiteSpace(resp)) return OperatingMode.Default;

            var node = JsonNode.Parse(resp);
            var modeVal = node?["data"]?["mode"]?.GetValue<int>() ?? 1;
            return Enum.IsDefined(typeof(OperatingMode), modeVal) ? (OperatingMode)modeVal : OperatingMode.Default;
        }
        catch
        {
            return OperatingMode.Default;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Validates Acer Agent Service response JSON.
    /// In AASSvc on Acer Nitro, result "0" or "3" indicates command acknowledgement.
    /// Non-zero/non-three results (e.g. "99"), null, empty, or malformed JSON are treated as failures.
    /// </summary>
    public static bool IsSuccessResult(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return false;
        try
        {
            var node = JsonNode.Parse(responseJson);
            var resStr = node?["result"]?.ToString();
            return resStr == "0" || resStr == "3";
        }
        catch
        {
            return false;
        }
    }

    public virtual async Task<bool> SetOperatingModeAsync(OperatingMode mode)
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return false;

            var payload = new
            {
                Function = "OPERATING_MODE",
                Parameter = new { mode = (int)mode }
            };

            var resp = await SendRawAsync(PacketSetDeviceData, payload);
            if (string.IsNullOrWhiteSpace(resp)) return false;

            var node = JsonNode.Parse(resp);
            var resStr = node?["result"]?.ToString();
            // In AASSvc on Acer Nitro, result "0" or "3" indicates command acknowledgement
            return resStr == "0" || resStr == "3";
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

            var resp = await SendRawAsync(PacketGetUpdatedData, new { Function = "GPU_MODE" });
            if (string.IsNullOrWhiteSpace(resp)) return null;

            var node = JsonNode.Parse(resp);
            var resStr = node?["result"]?.ToString();
            if (resStr != null && resStr != "0" && resStr != "3") return null;

            var modeVal = node?["data"]?["mode"]?.GetValue<int>();
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

            var payload = new
            {
                Function = "GPU_MODE",
                Parameter = new { mode = (int)mode }
            };

            var resp = await SendRawAsync(PacketSetDeviceData, payload);
            if (string.IsNullOrWhiteSpace(resp)) return false;

            var node = JsonNode.Parse(resp);
            var resStr = node?["result"]?.ToString();
            return resStr == "0" || resStr == "3";
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

            var resp = await SendRawAsync(PacketInitialization, new { Function = "SUPPORT_GPU_MODE_CAPABILITY" });
            if (string.IsNullOrWhiteSpace(resp)) return null;

            var node = JsonNode.Parse(resp);
            var resStr = node?["result"]?.ToString();
            if (resStr != null && resStr != "0" && resStr != "3") return null;

            var capArr = node?["data"] as JsonArray;
            if (capArr != null && capArr.Count > 0)
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

    public virtual async Task<bool> SetFanModeAsync(FanMode mode, int cpuRpm = 0, int gpuRpm = 0)
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return false;

            int safeCpu = Math.Clamp(cpuRpm, 0, 6000);
            int safeGpu = Math.Clamp(gpuRpm, 0, 6000);

            object param = mode switch
            {
                FanMode.Max => new { mode = 1 },
                FanMode.Custom => new { mode = 2, cpu_fan = safeCpu, gpu_fan = safeGpu },
                _ => new { mode = 0 } // Auto
            };

            var payload = new
            {
                Function = "FAN_CONTROL",
                Parameter = param
            };

            var resp = await SendRawAsync(PacketSetDeviceData, payload);
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

    public async Task<LightingConfig?> GetLightingConfigAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return null;

            var resp = await SendRawAsync(PacketGetUpdatedData, new { Function = "LIGHTING" });
            if (string.IsNullOrWhiteSpace(resp)) return null;

            var node = JsonNode.Parse(resp);
            var dataNode = node?["data"];
            if (dataNode == null) return null;

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

    public async Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors)
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return false;

            var effUpper = effect.ToUpperInvariant();
            var isOff = effUpper == "OFF" || brightness <= 0;
            var isStatic = effUpper == "STATIC" || isOff;
            var leds = isOff 
                ? new[] { "#000000", "#000000", "#000000", "#000000" } 
                : (zoneColors ?? new[] { primaryColor, primaryColor, primaryColor, primaryColor });

            var actualBrightness = isOff ? 0 : Math.Clamp(brightness, 0, 9);
            var actualColor = isOff ? "#000000" : primaryColor;
            var actualEffect = isOff ? "OFF" : effUpper;

            // Global device (device = 0) is the master lighting target
            object paramGlobal = (isStatic || isOff)
                ? new
                {
                    device = 0,
                    effect = actualEffect,
                    brightness = actualBrightness,
                    speed = Math.Clamp(speed, 1, 9),
                    duration = 5,
                    direction = Math.Clamp(direction, 0, 1),
                    color = actualColor,
                    LEDs = leds
                }
                : new
                {
                    device = 0,
                    effect = actualEffect,
                    dyEffect = actualEffect,
                    brightness = actualBrightness,
                    speed = Math.Clamp(speed, 1, 9),
                    duration = 5,
                    direction = Math.Clamp(direction, 0, 1),
                    color = actualColor,
                    LEDs = leds
                };

            var respGlobal = await SendRawAsync(PacketSetDeviceData, new { Function = "LIGHTING", Parameter = paramGlobal });
            bool globalSuccess = !string.IsNullOrWhiteSpace(respGlobal) && IsSuccessResult(respGlobal);

            // Also synchronize with Keyboard device (device = 1) for dynamic / multi-device models
            object paramKb = (isStatic || isOff)
                ? new
                {
                    device = 1,
                    effect = actualEffect,
                    brightness = actualBrightness,
                    speed = Math.Clamp(speed, 1, 9),
                    duration = 5,
                    direction = Math.Clamp(direction, 0, 1),
                    color = actualColor,
                    LEDs = leds
                }
                : new
                {
                    device = 1,
                    effect = actualEffect,
                    dyEffect = actualEffect,
                    brightness = actualBrightness,
                    speed = Math.Clamp(speed, 1, 9),
                    duration = 5,
                    direction = Math.Clamp(direction, 0, 1),
                    color = actualColor,
                    LEDs = leds
                };

            var respKb = await SendRawAsync(PacketSetDeviceData, new { Function = "LIGHTING", Parameter = paramKb });
            bool kbSuccess = !string.IsNullOrWhiteSpace(respKb) && IsSuccessResult(respKb);

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

    public async Task<bool> SetBacklightTimeoutAsync(bool enable30s)
    {
        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedInternalAsync()) return false;

            var payload = new
            {
                Function = "BACKLIGHT_30S",
                Parameter = new { status = enable30s ? 1 : 0 }
            };

            var resp = await SendRawAsync(PacketSetDeviceData, payload);
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

    public void Dispose()
    {
        Disconnect();
        _lock.Dispose();
    }
}
