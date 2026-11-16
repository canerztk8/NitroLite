using System.IO;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// The closed set of things the elevated worker can be asked to do.
/// <para>
/// There is deliberately no <c>InvokeWmi(method, payload)</c> member. The worker runs with an administrator token, so
/// its attack surface is exactly this enum: if a command is not listed here, no amount of crafted JSON can make the
/// worker call an arbitrary WMI method. Adding a capability means editing this list, which is a change a reviewer can
/// see.
/// </para>
/// </summary>
public enum WmiWorkerCommand
{
    /// <summary>Liveness check. Touches nothing.</summary>
    Ping = 0,

    /// <summary>Which interfaces this machine exposes.</summary>
    GetWorkerCapabilities = 1,

    /// <summary>Sensors: system info bytes, fan speeds, active mode, fan behaviour.</summary>
    ReadTelemetry = 2,

    /// <summary>Everything the window draws: mode, fan modes and speeds, MUX, battery limit.</summary>
    ReadDeviceState = 3,

    /// <summary>CPU and GPU fan behaviour (Auto / Max / Custom).</summary>
    SetFanBehavior = 4,

    /// <summary>CPU and GPU fan percentage.</summary>
    SetFanSpeed = 5,

    /// <summary>Platform power profile.</summary>
    SetOperatingMode = 6,

    /// <summary>MUX switch.</summary>
    SetMuxMode = 7,

    /// <summary>Battery charge limit, when WMID_GUID5 exists.</summary>
    SetBatteryLimit = 8,

    /// <summary>Keyboard backlight state: brightness, colour and the raw structure.</summary>
    GetKeyboardBacklight = 9,

    /// <summary>
    /// The full keyboard lighting write: four zone colours plus an effect, its speed, brightness and direction.
    /// Replaces the single-colour write that used to live at this value; the number is kept so the wire format does
    /// not change for a rename.
    /// </summary>
    SetKeyboardLighting = 10
}

/// <summary>
/// One message from the window to the worker.
/// <para>
/// The first message on a connection is always a <see cref="WmiWorkerProtocol.TypeHello"/> carrying the nonce; only
/// after that is accepted does the worker read a <see cref="WmiWorkerProtocol.TypeCommand"/>. A connection that sends
/// a command first is closed without an answer.
/// </para>
/// </summary>
public sealed record WmiWorkerRequest
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = WmiWorkerProtocol.TypeCommand;

    /// <summary>The single-use handshake value. Only read on the hello message.</summary>
    [JsonPropertyName("nonce")]
    public string Nonce { get; init; } = "";

    /// <summary>Serialised as a number: renaming an enum member must not change the wire format.</summary>
    [JsonPropertyName("command")]
    public WmiWorkerCommand Command { get; init; } = WmiWorkerCommand.Ping;

    /// <summary>Command arguments. Absent for the read-only commands.</summary>
    [JsonPropertyName("args")]
    public JsonObject? Args { get; init; }
}

/// <summary>One message from the worker back to the window.</summary>
public sealed record WmiWorkerResponse
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = WmiWorkerProtocol.TypeResult;

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>Stable token from <see cref="WmiOperationResult"/>; empty on success.</summary>
    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; init; } = "";

    [JsonPropertyName("error")]
    public string Error { get; init; } = "";

    [JsonPropertyName("data")]
    public JsonObject? Data { get; init; }

    public static WmiWorkerResponse FromResult(WmiOperationResult result) => new()
    {
        Type = WmiWorkerProtocol.TypeResult,
        Ok = result.Ok,
        ErrorCode = result.ErrorCode,
        Error = result.Error,
        Data = result.Data
    };

    public static WmiWorkerResponse Failure(string errorCode, string error) => new()
    {
        Type = WmiWorkerProtocol.TypeResult,
        Ok = false,
        ErrorCode = errorCode,
        Error = error
    };

    /// <summary>Turns the wire message back into the result type the executor speaks.</summary>
    public WmiOperationResult ToResult() => Ok
        ? WmiOperationResult.Success(Data)
        : WmiOperationResult.Failure(string.IsNullOrEmpty(ErrorCode) ? WmiOperationResult.CodeWmiFailed : ErrorCode, Error);
}

/// <summary>
/// The wire format: a four-byte little-endian length followed by that many bytes of UTF-8 JSON.
/// <para>
/// Length-prefixed rather than newline-delimited because the payload is JSON, and JSON written by a serializer may
/// contain newlines. The cap exists so a buggy or hostile peer cannot make the worker allocate a gigabyte by sending
/// a length header that promises one.
/// </para>
/// </summary>
public static class WmiWorkerProtocol
{
    /// <summary>Largest frame either side will read. Every real message is a few hundred bytes.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>First message on a connection: proves the caller knows the nonce.</summary>
    public const string TypeHello = "hello";

    /// <summary>Worker's answer to an accepted hello.</summary>
    public const string TypeWelcome = "welcome";

    /// <summary>A command from the window.</summary>
    public const string TypeCommand = "command";

    /// <summary>The worker's answer to a command.</summary>
    public const string TypeResult = "result";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize(WmiWorkerRequest request) => JsonSerializer.Serialize(request, Options);

    public static string Serialize(WmiWorkerResponse response) => JsonSerializer.Serialize(response, Options);

    public static WmiWorkerRequest? DeserializeRequest(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WmiWorkerRequest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static WmiWorkerResponse? DeserializeResponse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WmiWorkerResponse>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Wraps a payload in the length prefix. Exposed so the framing can be tested without a pipe.</summary>
    public static byte[] Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Writes one framed message and flushes, so the peer is not left waiting on a buffer.</summary>
    public static async Task WriteAsync(Stream stream, string json, CancellationToken cancellationToken = default)
    {
        var frame = Frame(json);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one framed message. Returns null when the peer closed the connection or sent a frame that cannot be
    /// trusted (a length outside 1..<see cref="MaxFrameBytes"/>), which the caller treats as a disconnect.
    /// </summary>
    public static async Task<string?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes) return null;

        var payload = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>
    /// Constant-time comparison of the handshake value. A byte-by-byte early exit would leak the nonce's prefix to a
    /// caller that can time the rejection, which is the one thing the nonce exists to prevent.
    /// </summary>
    public static bool NonceMatches(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        if (expected.Length != actual.Length) return false;

        var diff = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            diff |= char.ToLowerInvariant(expected[i]) ^ char.ToLowerInvariant(actual[i]);
        }

        return diff == 0;
    }
}
