using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// The worker's end of the pipe: accepts exactly one client, proves it knows the nonce, then serves typed commands
/// until the client goes away.
/// <para>
/// The pipe is created with <see cref="PipeOptions.CurrentUserOnly"/>, so the operating system refuses a connection
/// from any other account before a single byte is read. That is the outer wall. The nonce is the inner one, and it
/// covers the case the ACL cannot: another process running as the same user — a script, a game overlay, anything
/// that enumerates pipe names — connecting first and driving the elevated worker. Without the nonce that process
/// would inherit administrator hardware control for free.
/// </para>
/// <para>
/// One client for the life of the process is intentional. The worker exists to serve the window that started it; a
/// second client is either a mistake or an attack, and neither deserves a retry.
/// </para>
/// </summary>
public sealed class WmiWorkerServer : IDisposable
{
    /// <summary>How long a connecting client has to complete the handshake before it is dropped.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly string _nonce;
    private readonly IWmiExecutor _executor;
    private bool _disposed;

    public WmiWorkerServer(string pipeName, string nonce, IWmiExecutor executor)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("pipe name is required", nameof(pipeName));
        _pipeName = pipeName;
        _nonce = nonce ?? "";
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>
    /// Waits for the parent, performs the handshake and serves commands until the connection ends.
    /// </summary>
    /// <returns>
    /// True when a client completed the handshake and was served; false when nobody connected in time or the
    /// handshake was rejected. Either way the caller should exit — the worker has no second act.
    /// </returns>
    public async Task<bool> RunAsync(TimeSpan acceptTimeout, CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        acceptCts.CancelAfter(acceptTimeout);

        try
        {
            await pipe.WaitForConnectionAsync(acceptCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        if (!await HandshakeAsync(pipe, cancellationToken).ConfigureAwait(false)) return false;

        await ServeAsync(pipe, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reads the hello, checks the nonce and answers. A wrong nonce, a wrong message type or a timeout all end the
    /// connection; the client is told why so the window can log something better than "the pipe closed".
    /// </summary>
    private async Task<bool> HandshakeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(HandshakeTimeout);

        string? json;
        try
        {
            json = await WmiWorkerProtocol.ReadAsync(pipe, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (json == null) return false;

        var request = WmiWorkerProtocol.DeserializeRequest(json);
        if (request == null || !string.Equals(request.Type, WmiWorkerProtocol.TypeHello, StringComparison.Ordinal))
        {
            await TryReplyAsync(pipe, WmiWorkerResponse.Failure("handshake_failed", "first message must be a hello"),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!WmiWorkerProtocol.NonceMatches(_nonce, request.Nonce))
        {
            await TryReplyAsync(pipe, WmiWorkerResponse.Failure("handshake_failed", "nonce mismatch"),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        await TryReplyAsync(pipe, new WmiWorkerResponse
        {
            Type = WmiWorkerProtocol.TypeWelcome,
            Ok = true,
            Data = new JsonObject { ["protocol"] = 1 }
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Reads commands until the client disconnects, answering each one.</summary>
    private async Task ServeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? json;
            try
            {
                json = await WmiWorkerProtocol.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (json == null) return;

            var request = WmiWorkerProtocol.DeserializeRequest(json);
            var response = request == null
                ? WmiWorkerResponse.Failure(WmiOperationResult.CodeInvalidArgument, "malformed request")
                : Dispatch(request);

            if (!await TryReplyAsync(pipe, response, cancellationToken).ConfigureAwait(false)) return;
        }
    }

    /// <summary>
    /// Runs one command. Every write is validated here, in the elevated process, before the executor sees it: the
    /// caller's own checks are a convenience, not a guarantee.
    /// </summary>
    public WmiWorkerResponse Dispatch(WmiWorkerRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case WmiWorkerCommand.Ping:
                    return WmiWorkerResponse.FromResult(_executor.Ping());

                case WmiWorkerCommand.GetWorkerCapabilities:
                    return WmiWorkerResponse.FromResult(_executor.GetCapabilities());

                case WmiWorkerCommand.ReadTelemetry:
                    return WmiWorkerResponse.FromResult(_executor.ReadTelemetry());

                case WmiWorkerCommand.ReadDeviceState:
                    return WmiWorkerResponse.FromResult(_executor.ReadDeviceState());

                case WmiWorkerCommand.SetFanBehavior:
                {
                    if (!TryReadInt(request.Args, "cpu", out var cpuValue) ||
                        !WmiWorkerValidation.TryParseFanMode(cpuValue, out var cpu))
                        return Invalid("cpu fan mode is missing or not a known mode");

                    if (!TryReadInt(request.Args, "gpu", out var gpuValue) ||
                        !WmiWorkerValidation.TryParseFanMode(gpuValue, out var gpu))
                        return Invalid("gpu fan mode is missing or not a known mode");

                    return WmiWorkerResponse.FromResult(_executor.SetFanBehavior(cpu, gpu));
                }

                case WmiWorkerCommand.SetFanSpeed:
                {
                    if (!TryReadInt(request.Args, "cpuPercent", out var cpuPercent))
                        return Invalid("cpuPercent is missing");
                    if (!WmiWorkerValidation.IsValidFanPercent(cpuPercent))
                        return Invalid($"cpuPercent {cpuPercent} is outside 0 or 30..100");

                    if (!TryReadInt(request.Args, "gpuPercent", out var gpuPercent))
                        return Invalid("gpuPercent is missing");
                    if (!WmiWorkerValidation.IsValidFanPercent(gpuPercent))
                        return Invalid($"gpuPercent {gpuPercent} is outside 0 or 30..100");

                    return WmiWorkerResponse.FromResult(_executor.SetFanSpeed(cpuPercent, gpuPercent));
                }

                case WmiWorkerCommand.SetOperatingMode:
                {
                    if (!TryReadInt(request.Args, "mode", out var value) ||
                        !WmiWorkerValidation.TryParseOperatingMode(value, out var mode))
                        return Invalid($"mode {value} is not a known operating mode");

                    return WmiWorkerResponse.FromResult(_executor.SetOperatingMode(mode));
                }

                case WmiWorkerCommand.SetMuxMode:
                {
                    if (!TryReadInt(request.Args, "mode", out var value) ||
                        !WmiWorkerValidation.TryParseMuxMode(value, out var mode))
                        return Invalid($"mode {value} is not a switchable MUX mode");

                    return WmiWorkerResponse.FromResult(_executor.SetMuxMode(mode));
                }

                case WmiWorkerCommand.SetBatteryLimit:
                {
                    if (!TryReadInt(request.Args, "mode", out var value) ||
                        !WmiWorkerValidation.TryParseBatteryMode(value, out var mode))
                        return Invalid($"mode {value} is not a known charge mode");

                    return WmiWorkerResponse.FromResult(_executor.SetBatteryLimit(mode));
                }

                default:
                    return Invalid($"unknown command {(int)request.Command}");
            }
        }
        catch (Exception ex)
        {
            // A throw here would end the worker and leave the window with a dead pipe and no explanation.
            return WmiWorkerResponse.Failure(WmiOperationResult.CodeWmiFailed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static WmiWorkerResponse Invalid(string error) =>
        WmiWorkerResponse.Failure(WmiOperationResult.CodeInvalidArgument, error);

    private static bool TryReadInt(JsonObject? args, string name, out int value)
    {
        value = 0;
        if (args == null) return false;

        var node = args[name];
        if (node == null) return false;

        try
        {
            value = node.GetValue<int>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryReplyAsync(Stream pipe, WmiWorkerResponse response, CancellationToken cancellationToken)
    {
        try
        {
            await WmiWorkerProtocol.WriteAsync(pipe, WmiWorkerProtocol.Serialize(response), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
