using System.IO;
using System.Net.Sockets;

namespace NitroLite.Core.Hardware.OpenRgb;

/// <summary>How a colour write ended.</summary>
public enum OpenRgbWriteOutcome
{
    /// <summary>Written and read back: the device reports the colours that were sent.</summary>
    Applied = 0,

    /// <summary>Written, but the device description could not be read back, so nothing confirms it.</summary>
    Unverified = 1,

    /// <summary>No OpenRGB server is listening. Not an error: the user simply is not running one.</summary>
    NotSupported = 2,

    /// <summary>The server answered and the write did not take.</summary>
    Failed = 3
}

/// <summary>The result of one colour write, with the reason when it did not apply.</summary>
public sealed record OpenRgbWriteResult(OpenRgbWriteOutcome Outcome, string? Detail)
{
    public bool Applied => Outcome == OpenRgbWriteOutcome.Applied;

    public static OpenRgbWriteResult NotSupported(string? detail = null) => new(OpenRgbWriteOutcome.NotSupported, detail);
}

/// <summary>
/// A zero-dependency client for the OpenRGB SDK server on <c>127.0.0.1:6742</c>.
/// <para>
/// This is the third way to light an Acer keyboard, and the only one that does not need either Acer's service or a
/// per-chassis USB HID packet layout. OpenRGB already speaks to the embedded controller on the models it supports;
/// talking to OpenRGB means supporting exactly the machines OpenRGB supports, with no packet reverse engineering here
/// and no risk of writing arbitrary bytes into a keyboard controller.
/// </para>
/// <para>
/// It is a client, not a server: nothing is installed, nothing listens, and a machine without OpenRGB running simply
/// gets <see cref="OpenRgbWriteOutcome.NotSupported"/>. That is why every entry point returns a result instead of
/// throwing — a missing optional integration must never take the application down.
/// </para>
/// <para>
/// Writes are read back. OpenRGB is a shared bus: the user's own OpenRGB window, a game's lighting plugin and this
/// client can all write the same device, and the last writer wins silently. Reading the device description after a
/// write is the only way to tell "the colour is on the keyboard" from "the colour was sent".
/// </para>
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 6742;

    /// <summary>How many times a write is read back before it is called unverified.</summary>
    public const int ReadBackAttempts = 3;

    /// <summary>Gap between read-back attempts. OpenRGB applies colours on its own thread.</summary>
    public static readonly TimeSpan ReadBackDelay = TimeSpan.FromMilliseconds(60);

    private readonly string _host;
    private readonly int _port;
    private readonly string _clientName;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;

    /// <summary>
    /// <paramref name="delay"/> exists so the read-back retries do not make a unit test wait: the default is a real
    /// delay, and the tests pass a no-op.
    /// </summary>
    public OpenRgbClient(
        string host = DefaultHost,
        int port = DefaultPort,
        string clientName = "NitroLite",
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _host = host;
        _port = port;
        _clientName = clientName;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    /// <summary>True while a server is connected.</summary>
    public bool IsConnected => _stream != null && _client?.Connected == true;

    /// <summary>Why the last operation failed, for the status line and the log.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Connects and registers the client name. False when nothing is listening, which is the normal state on a machine
    /// that does not run OpenRGB.
    /// </summary>
    public async Task<bool> ConnectAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_disposed) return false;
        if (IsConnected) return true;

        Disconnect();

        var limit = timeout ?? TimeSpan.FromSeconds(2);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);

        try
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();

            // The name is what the OpenRGB window shows in its client list. Registering it is also the handshake:
            // a server that answers this is a server that speaks the protocol. The answer is read here rather than
            // left in the stream, because the next request would otherwise read it as its own reply.
            await SendAsync(0, OpenRgbProtocol.PacketType.SetClientName,
                OpenRgbProtocol.BuildClientNamePayload(_clientName), cts.Token).ConfigureAwait(false);
            await ReadPayloadAsync(cts.Token).ConfigureAwait(false);

            LastError = null;
            return true;
        }
        catch (SocketException ex)
        {
            LastError = ex.Message;
            Disconnect();
            return false;
        }
        catch (OperationCanceledException)
        {
            LastError = "OpenRGB bağlantısı zaman aşımına uğradı.";
            Disconnect();
            return false;
        }
        catch (IOException ex)
        {
            LastError = ex.Message;
            Disconnect();
            return false;
        }
    }

    /// <summary>How many devices the server exposes. Null when it did not answer.</summary>
    public async Task<int?> GetControllerCountAsync(CancellationToken ct = default)
    {
        var response = await RequestAsync(0, OpenRgbProtocol.PacketType.RequestControllerCount, [], ct)
            .ConfigureAwait(false);
        return response == null ? null : OpenRgbProtocol.ParseControllerCount(response);
    }

    /// <summary>One device description, or null when the server did not answer or the index is out of range.</summary>
    public async Task<OpenRgbController?> GetControllerAsync(int index, CancellationToken ct = default)
    {
        if (index < 0) return null;
        var response = await RequestAsync((uint)index, OpenRgbProtocol.PacketType.RequestControllerData, [], ct)
            .ConfigureAwait(false);
        return response == null ? null : OpenRgbProtocol.ParseController(index, response);
    }

    /// <summary>Puts the device in direct mode, which is what a per-LED write needs.</summary>
    public async Task<bool> SetCustomModeAsync(int deviceIndex, CancellationToken ct = default)
    {
        var response = await RequestAsync((uint)deviceIndex, OpenRgbProtocol.PacketType.SetCustomMode, [], ct)
            .ConfigureAwait(false);
        return response != null;
    }

    /// <summary>Writes every LED on the device and reads the result back.</summary>
    public async Task<OpenRgbWriteResult> UpdateLedsAsync(
        int deviceIndex,
        IReadOnlyList<RgbColor> colors,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Count == 0) return new OpenRgbWriteResult(OpenRgbWriteOutcome.Failed, "Renk listesi boş.");

        var payload = OpenRgbProtocol.BuildUpdateLedsPayload(colors);
        return await WriteAndVerifyAsync(deviceIndex, OpenRgbProtocol.PacketType.UpdateLeds, payload, colors, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Writes one zone and reads the result back.</summary>
    public async Task<OpenRgbWriteResult> UpdateZoneLedsAsync(
        int deviceIndex,
        int zoneIndex,
        IReadOnlyList<RgbColor> colors,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Count == 0) return new OpenRgbWriteResult(OpenRgbWriteOutcome.Failed, "Renk listesi boş.");
        if (zoneIndex < 0) return new OpenRgbWriteResult(OpenRgbWriteOutcome.Failed, "Bölge numarası negatif.");

        var payload = OpenRgbProtocol.BuildUpdateZoneLedsPayload(zoneIndex, colors);
        return await WriteAndVerifyAsync(deviceIndex, OpenRgbProtocol.PacketType.UpdateZoneLeds, payload, colors, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one write and confirms it. A device whose description does not parse is reported as
    /// <see cref="OpenRgbWriteOutcome.Unverified"/> rather than failed: the write may well have landed, and claiming
    /// otherwise would make the app turn a working keyboard into a broken one.
    /// </summary>
    private async Task<OpenRgbWriteResult> WriteAndVerifyAsync(
        int deviceIndex,
        OpenRgbProtocol.PacketType type,
        byte[] payload,
        IReadOnlyList<RgbColor> expected,
        CancellationToken ct)
    {
        if (!await ConnectAsync(ct: ct).ConfigureAwait(false))
        {
            return OpenRgbWriteResult.NotSupported(LastError);
        }

        var response = await RequestAsync((uint)deviceIndex, type, payload, ct).ConfigureAwait(false);
        if (response == null)
        {
            return new OpenRgbWriteResult(OpenRgbWriteOutcome.Failed, LastError ?? "OpenRGB yanıt vermedi.");
        }

        for (int attempt = 0; attempt < ReadBackAttempts; attempt++)
        {
            if (attempt > 0) await _delay(ReadBackDelay, ct).ConfigureAwait(false);

            var controller = await GetControllerAsync(deviceIndex, ct).ConfigureAwait(false);
            if (controller == null) continue;
            if (!controller.Parsed) return new OpenRgbWriteResult(OpenRgbWriteOutcome.Unverified, null);

            if (Matches(controller.Colors, expected))
            {
                return new OpenRgbWriteResult(OpenRgbWriteOutcome.Applied, null);
            }
        }

        return new OpenRgbWriteResult(OpenRgbWriteOutcome.Failed,
            "Yazılan renkler cihazdan geri okunamadı; başka bir OpenRGB istemcisi üzerine yazıyor olabilir.");
    }

    /// <summary>
    /// True when the device reports the colours that were sent. Only the LEDs that were written are compared: a zone
    /// write leaves the rest of the device alone, and demanding a whole-device match would fail every zone write.
    /// </summary>
    public static bool Matches(IReadOnlyList<RgbColor> observed, IReadOnlyList<RgbColor> expected)
    {
        if (observed.Count < expected.Count) return false;
        for (int i = 0; i < expected.Count; i++)
        {
            if (observed[i] != expected[i]) return false;
        }
        return true;
    }

    /// <summary>Sends a packet and reads the answer. Null when the connection is gone or the answer never came.</summary>
    private async Task<byte[]?> RequestAsync(
        uint deviceIndex,
        OpenRgbProtocol.PacketType type,
        byte[] payload,
        CancellationToken ct)
    {
        if (!await ConnectAsync(ct: ct).ConfigureAwait(false)) return null;

        try
        {
            await SendAsync(deviceIndex, type, payload, ct).ConfigureAwait(false);
            return await ReadPayloadAsync(ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            LastError = ex.Message;
            Disconnect();
            return null;
        }
        catch (IOException ex)
        {
            LastError = ex.Message;
            Disconnect();
            return null;
        }
        catch (OperationCanceledException)
        {
            LastError = "OpenRGB isteği zaman aşımına uğradı.";
            Disconnect();
            return null;
        }
    }

    private async Task SendAsync(
        uint deviceIndex,
        OpenRgbProtocol.PacketType type,
        byte[] payload,
        CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("OpenRGB bağlantısı yok.");
        var packet = OpenRgbProtocol.BuildPacket(deviceIndex, type, payload);
        await stream.WriteAsync(packet, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one packet and returns its payload. A header this client cannot follow ends the connection.</summary>
    private async Task<byte[]?> ReadPayloadAsync(CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("OpenRGB bağlantısı yok.");

        var header = new byte[OpenRgbProtocol.HeaderBytes];
        if (!await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false)) return null;

        if (!OpenRgbProtocol.TryParseHeader(header, out _, out _, out int payloadBytes))
        {
            LastError = "OpenRGB sunucusu tanınmayan bir paket gönderdi.";
            Disconnect();
            return null;
        }

        if (payloadBytes == 0) return [];

        var payload = new byte[payloadBytes];
        if (!await ReadExactlyAsync(stream, payload, ct).ConfigureAwait(false)) return null;
        return payload;
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    private void Disconnect()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
