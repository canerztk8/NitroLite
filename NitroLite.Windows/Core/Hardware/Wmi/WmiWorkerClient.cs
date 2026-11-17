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

            await WmiWorkerProtocol.WriteAsync(pipe, WmiWorkerProtocol.Serialize(new WmiWorkerRequest
            {
                Type = WmiWorkerProtocol.TypeHello,
                Nonce = nonce
            }), cts.Token).ConfigureAwait(false);

            var json = await WmiWorkerProtocol.ReadAsync(pipe, cts.Token).ConfigureAwait(false);
            if (json == null) throw new InvalidOperationException("the worker closed the pipe during the handshake");

            var response = WmiWorkerProtocol.DeserializeResponse(json);
            if (response == null) throw new InvalidOperationException("the worker sent a malformed handshake reply");

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
        await WmiWorkerProtocol.WriteAsync(_pipe, WmiWorkerProtocol.Serialize(new WmiWorkerRequest
        {
            Type = WmiWorkerProtocol.TypeCommand,
            Command = command,
            Args = args
        }), cancellationToken).ConfigureAwait(false);

        var json = await WmiWorkerProtocol.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false);
        if (json == null) throw new IOException("the worker closed the pipe");

        return WmiWorkerProtocol.DeserializeResponse(json)
               ?? throw new IOException("the worker sent a malformed reply");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pipe.Dispose(); } catch { }
    }
}
