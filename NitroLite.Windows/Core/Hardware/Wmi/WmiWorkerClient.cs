using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// The window's end of the pipe. Connects to the elevated worker, proves it knows the nonce, then sends typed
/// commands.
/// <para>
/// This class never decides whether a value is safe — the worker does that. What it does own is the handshake: the
/// nonce is generated here, sent as the first message, and required back on the welcome. A worker that answers
/// anything other than a welcome carrying that value is not the worker this process started, and the connection is
/// dropped rather than used.
/// </para>
/// <para>
/// The nonce never touches the command line. It used to travel as <c>--nonce</c>, which put it in
/// <c>Win32_Process.CommandLine</c> where any process running as the same user could read it; the worker now
/// authenticates its caller by process id, and the nonce only has to prove the reply came from the helper that
/// received it.
/// </para>
/// </summary>
public sealed class WmiWorkerClient : IDisposable
{
    /// <summary>How long to wait for the worker to answer the handshake.</summary>
    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly NamedPipeClientStream _pipe;
    private bool _disposed;

    /// <summary>
    /// Serialises one request/response pair on the pipe.
    /// </summary>
    /// <remarks>
    /// The wire format is a length prefix followed by a payload, and a pipe is a byte stream, not a message queue. Two
    /// callers that write and read concurrently therefore do not merely race for an answer — they corrupt the framing:
    /// reader A consumes the four-byte header, reader B consumes the first four bytes of A's payload as its own
    /// header, and the length it decodes is whatever those bytes happen to be.
    /// <para>
    /// This is reachable in the shipped app. The fan curve steps on its own thread while the telemetry timer ticks on
    /// the UI thread, and the controller gates only its writes — the read path calls <c>SendAsync</c> directly. The
    /// consequence is either a bogus length that fails the frame check and tears the connection down, or two callers
    /// each receiving the other's reply: a fan-speed write confirmed by a telemetry frame, and a telemetry read that
    /// reports zero RPM because it parsed a write acknowledgement.
    /// </para>
    /// <para>
    /// The gate belongs here rather than in the controller because this is the only place that knows a request and
    /// its answer are one indivisible exchange.
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _exchange = new(1, 1);

    private WmiWorkerClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>
    /// Generates the single-use handshake value: 32 cryptographically random bytes as 64 hex characters. 256 bits is
    /// far more than the handshake needs; the point is that guessing it is not a strategy.
    /// </summary>
    public static string GenerateNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// Connects and completes the handshake.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The worker refused the nonce, answered with the wrong message, or did not answer in time. The caller treats
    /// all three the same way: the elevated path is unavailable and the window keeps working without it.
    /// </exception>
    public static async Task<WmiWorkerClient> ConnectAsync(
        string pipeName,
        string nonce,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("pipe name is required", nameof(pipeName));

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? DefaultHandshakeTimeout);

        try
        {
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);

            await WmiWorkerProtocol.WriteAsync(pipe, new WmiWorkerRequest
            {
                Type = WmiWorkerProtocol.TypeHello,
                Nonce = nonce
            }, cts.Token).ConfigureAwait(false);

            var response = await WmiWorkerProtocol.ReadResponseAsync(pipe, cts.Token).ConfigureAwait(false);
            if (response == null) throw new InvalidOperationException("the worker closed the pipe during the handshake");

            // The type is checked as well as the flag: a reply that is not a welcome did not come from a worker that
            // accepted this hello, whatever its "ok" field says.
            if (!string.Equals(response.Type, WmiWorkerProtocol.TypeWelcome, StringComparison.Ordinal))
                throw new InvalidOperationException($"the worker answered '{response.Type}' instead of a welcome");

            if (!response.Ok) throw new InvalidOperationException($"the worker refused the handshake: {response.Error}");

            // Only the helper that received this hello can echo the value back, so a mismatch means something else
            // is on the other end of the pipe.
            if (!WmiWorkerProtocol.NonceMatches(nonce, response.Nonce))
                throw new InvalidOperationException("the worker did not echo the handshake value");

            return new WmiWorkerClient(pipe);
        }
        catch (OperationCanceledException)
        {
            pipe.Dispose();
            throw new InvalidOperationException("the worker did not answer the handshake in time");
        }
        catch (IOException ex)
        {
            // The worker drops a connection it does not recognise, which can happen before the hello is even
            // written. The caller treats every handshake failure the same way, so it gets one exception type.
            pipe.Dispose();
            throw new InvalidOperationException($"the worker closed the pipe during the handshake: {ex.Message}", ex);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>Sends one command and waits for its answer.</summary>
    /// <exception cref="IOException">The worker went away. The caller drops the client and falls back.</exception>
    public async Task<WmiWorkerResponse> SendAsync(
        WmiWorkerCommand command,
        JsonObject? args = null,
        CancellationToken cancellationToken = default)
    {
        // One exchange at a time: the write and the read that answers it are a single unit on a byte stream. See
        // _exchange for what happens without this.
        await _exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WmiWorkerProtocol.WriteAsync(_pipe, new WmiWorkerRequest
            {
                Type = WmiWorkerProtocol.TypeCommand,
                Command = command,
                Args = args
            }, cancellationToken).ConfigureAwait(false);

            return await WmiWorkerProtocol.ReadResponseAsync(_pipe, cancellationToken).ConfigureAwait(false)
                   ?? throw new IOException("the worker closed the pipe or sent a malformed reply");
        }
        finally
        {
            _exchange.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pipe.Dispose(); } catch { }

        // Not disposed: a caller may still be inside SendAsync holding it, and disposing a SemaphoreSlim underneath a
        // waiter throws ObjectDisposedException on a background thread. It holds no unmanaged resource unless
        // AvailableWaitHandle is read, which this class never does.
    }
}
