using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// The worker's end of the pipe: accepts one client, proves it is the process that started this helper, then serves
/// typed commands until the client goes away.
/// <para>
/// The pipe is created by <see cref="WorkerPipe"/>, which locks the DACL to the current user and lowers the
/// mandatory integrity label to Medium. The label matters as much as the ACL: an elevated process creates High
/// objects, and a High pipe refuses the medium-integrity window that started the worker no matter what its DACL
/// says. The operating system then refuses a connection from any other account before a single byte is read.
/// </para>
/// <para>
/// That is not enough on its own, because every process running as the same user passes the ACL. The gate that
/// actually holds is the client's process id: <c>GetNamedPipeClientProcessId</c> reports the id the kernel assigned
/// to the connecting process, and a caller cannot forge it. A connection from anything other than the window named
/// on the command line is dropped and the worker goes back to listening, so a rogue client that connects first
/// cannot take the helper away from its owner and cannot kill it either.
/// </para>
/// <para>
/// The nonce is the third layer and the weakest, which is why it is no longer the only one. It used to travel on the
/// command line, where <c>Win32_Process.CommandLine</c> handed it to any same-user process; it now arrives on the
/// pipe and is echoed back, so the window can prove the reply came from the helper it started.
/// </para>
/// </summary>
public sealed class WmiWorkerServer : IDisposable
{
    /// <summary>How long a connecting client has to complete the handshake before it is dropped.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly int _expectedClientProcessId;
    private readonly IWmiExecutor _executor;
    private NamedPipeServerStream? _pipe;
    private bool _disposed;

    /// <param name="expectedClientProcessId">
    /// The process id every connection is checked against. Zero disables the check, which is what the tests use when
    /// both ends live in one process; the host always passes the parent id it was started with.
    /// </param>
    public WmiWorkerServer(string pipeName, IWmiExecutor executor, int expectedClientProcessId = 0)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("pipe name is required", nameof(pipeName));
        _pipeName = pipeName;
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _expectedClientProcessId = expectedClientProcessId;
    }

    /// <summary>
    /// Waits for the parent, performs the handshake and serves commands until the connection ends.
    /// </summary>
    /// <returns>
    /// True when a client completed the handshake and was served; false when nobody connected in time or the
    /// accept window closed. A rejected connection is not a failure: the worker drops it and keeps listening, so a
    /// rogue client cannot deny the helper to the window that owns it.
    /// </returns>
    public async Task<bool> RunAsync(TimeSpan acceptTimeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + acceptTimeout;

        _pipe = WorkerPipe.Create(_pipeName);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;

                using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                acceptCts.CancelAfter(remaining);

                try
                {
                    await _pipe.WaitForConnectionAsync(acceptCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                if (!IsExpectedClient(_pipe))
                {
                    Disconnect();
                    continue;
                }

                if (!await HandshakeAsync(_pipe, cancellationToken).ConfigureAwait(false))
                {
                    Disconnect();
                    continue;
                }

                await ServeAsync(_pipe, cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }
        finally
        {
            var pipe = _pipe;
            _pipe = null;
            try { pipe?.Dispose(); } catch { /* the pipe is already gone */ }
        }
    }

    /// <summary>
    /// True when the connected client is the process that started this helper. The id comes from the kernel, so it
    /// cannot be spoofed; the account is checked as well so a recycled id cannot stand in for the parent.
    /// </summary>
    private bool IsExpectedClient(NamedPipeServerStream pipe)
    {
        if (_expectedClientProcessId <= 0) return true;

        uint clientProcessId;
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out clientProcessId))
            {
                AppLog.Warn(Loc.T("--wmi-worker bağlantısı reddedildi"), "istemci süreç kimliği okunamadı");
                return false;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Without the id there is no way to tell the parent from any other process running as this user, and
            // the pipe ACL cannot: they all pass it. Refusing is the safe answer — the window falls back to the
            // non-elevated backend instead of handing administrator hardware control to a stranger.
            AppLog.Warn(Loc.T("--wmi-worker bağlantısı reddedildi"), "istemci süreç kimliği bu makinede okunamıyor");
            return false;
        }

        if (clientProcessId != (uint)_expectedClientProcessId)
        {
            AppLog.Warn(Loc.T("--wmi-worker bağlantısı reddedildi"),
                $"beklenen pid={_expectedClientProcessId}, gelen pid={clientProcessId}");
            return false;
        }

        try
        {
            var clientUser = pipe.GetImpersonationUserName();
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!string.IsNullOrEmpty(clientUser) && !string.IsNullOrEmpty(identity.Name)
                && !string.Equals(clientUser, identity.Name, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn(Loc.T("--wmi-worker bağlantısı reddedildi"),
                    $"istemci hesabı '{clientUser}', beklenen '{identity.Name}'");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException
                                      or InvalidOperationException)
        {
            // The id check already passed; a pipe that cannot report the account is not a reason to refuse.
        }

        return true;
    }

    /// <summary>Drops the current connection so the accept loop can take the next one.</summary>
    private void Disconnect()
    {
        try { _pipe?.Disconnect(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Already disconnected, or the pipe is gone. Either way the loop moves on.
        }
    }

    /// <summary>
    /// Reads the hello, checks its shape and answers with a welcome that echoes the nonce. A wrong message type, a
    /// malformed nonce or a timeout all end the connection; the client is told why so the window can log something
    /// better than "the pipe closed".
    /// </summary>
    private async Task<bool> HandshakeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(HandshakeTimeout);

        WmiWorkerReadResult read;
        try
        {
            read = await WmiWorkerProtocol.ReadRequestAsync(pipe, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (read.Closed) return false;

        if (read.Request is not { } request || !string.Equals(request.Type, WmiWorkerProtocol.TypeHello, StringComparison.Ordinal))
        {
            await TryReplyAsync(pipe, WmiWorkerResponse.Failure("handshake_failed", "first message must be a hello"),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        // The value is not compared against anything held here: the caller generated it, and echoing it back is
        // what proves the reply came from the helper that received the hello. It still has to look like a nonce, so
        // the field cannot be used to smuggle a payload through the handshake.
        if (!StartupMode.IsHexNonce(request.Nonce))
        {
            await TryReplyAsync(pipe, WmiWorkerResponse.Failure("handshake_failed", "nonce is missing or malformed"),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        await TryReplyAsync(pipe, new WmiWorkerResponse
        {
            Type = WmiWorkerProtocol.TypeWelcome,
            Ok = true,
            Nonce = request.Nonce,
            Data = new JsonObject { ["protocol"] = 1 }
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Reads commands until the client disconnects, answering each one.</summary>
    private async Task ServeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            WmiWorkerReadResult read;
            try
            {
                read = await WmiWorkerProtocol.ReadRequestAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (read.Closed) return;

            var response = read.Request is { } request
                ? Dispatch(request)
                : WmiWorkerResponse.Failure(WmiOperationResult.CodeInvalidArgument, "malformed request");

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
                {
                    // The sensor ids are the caller's, so they are checked here like any other argument. A missing
                    // id falls back to the firmware-wide enum rather than failing the read: an older caller that
                    // sends no ids still gets a sample.
                    var query = new SensorQuery
                    {
                        CpuTemp = ReadSensorId(request.Args, "cpuTempSensor", AcerWmiProtocol.SensorCpuTemperature),
                        CpuFanRpm = ReadSensorId(request.Args, "cpuFanRpmSensor", AcerWmiProtocol.SensorCpuFanSpeed),
                        Sys1Temp = ReadSensorId(request.Args, "sys1TempSensor", AcerWmiProtocol.SensorSystemTemperature),
                        GpuFanRpm = ReadSensorId(request.Args, "gpuFanRpmSensor", AcerWmiProtocol.SensorGpuFanSpeed),
                        GpuTemp = ReadSensorId(request.Args, "gpuTempSensor", AcerWmiProtocol.SensorGpuTemperature),

                        // Absent means "not confirmed", which is the answer that keeps the percentage fallback in
                        // the reply. An older caller that sends no flag therefore still gets everything it used to.
                        TachometerConfirmed =
                            TryReadBool(request.Args, "tachometerConfirmed", out var confirmed) && confirmed
                    };

                    return WmiWorkerResponse.FromResult(_executor.ReadTelemetry(query));
                }

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

                case WmiWorkerCommand.GetKeyboardBacklight:
                    return WmiWorkerResponse.FromResult(_executor.GetKeyboardBacklight());

                case WmiWorkerCommand.SetKeyboardLighting:
                {
                    if (!TryReadString(request.Args, "effect", out var effect))
                        return Invalid("effect is missing");

                    // The worker refuses an effect it cannot encode rather than letting the executor fall back to a
                    // mode the user did not ask for.
                    if (AcerWmiProtocol.KbModeFor(effect) == null)
                        return Invalid($"effect '{effect}' has no firmware mode");

                    if (!TryReadInt(request.Args, "brightness", out var brightness) ||
                        !WmiWorkerValidation.IsValidBrightnessPercent(brightness))
                        return Invalid($"brightness {brightness} is outside 0..100");

                    if (!TryReadInt(request.Args, "speed", out var speed) ||
                        !WmiWorkerValidation.IsValidEffectSpeed(speed))
                        return Invalid($"speed {speed} is outside 1..{AcerWmiProtocol.MaxEffectSpeed}");

                    if (!TryReadInt(request.Args, "direction", out var direction) ||
                        !WmiWorkerValidation.IsValidEffectDirection(direction))
                        return Invalid($"direction {direction} is outside 1..{AcerWmiProtocol.MaxEffectDirection}");

                    if (!TryReadZones(request.Args, out var zones))
                        return Invalid($"zones must be {AcerWmiProtocol.LedZoneCount} packed 0xRRGGBB colours");

                    return WmiWorkerResponse.FromResult(_executor.SetKeyboardLighting(new KeyboardLightingRequest
                    {
                        Effect = effect,
                        Brightness = brightness,
                        Speed = speed,
                        Direction = direction,
                        Zones = zones
                    }));
                }

                case WmiWorkerCommand.GetCoolBoost:
                    return WmiWorkerResponse.FromResult(_executor.GetCoolBoost());

                case WmiWorkerCommand.SetCoolBoost:
                {
                    if (!TryReadBool(request.Args, "on", out var on))
                        return Invalid("on is missing or not a boolean");

                    return WmiWorkerResponse.FromResult(_executor.SetCoolBoost(on));
                }

                case WmiWorkerCommand.GetLcdOverdrive:
                    return WmiWorkerResponse.FromResult(_executor.GetLcdOverdrive());

                case WmiWorkerCommand.SetLcdOverdrive:
                {
                    if (!TryReadBool(request.Args, "on", out var on))
                        return Invalid("on is missing or not a boolean");

                    return WmiWorkerResponse.FromResult(_executor.SetLcdOverdrive(on));
                }

                case WmiWorkerCommand.SetWinKeyLock:
                {
                    if (!TryReadBool(request.Args, "enabled", out var enabled))
                        return Invalid("enabled is missing or not a boolean");

                    return WmiWorkerResponse.FromResult(_executor.SetWinKeyLock(enabled));
                }

                case WmiWorkerCommand.GetBacklightTimeout:
                    return WmiWorkerResponse.FromResult(_executor.GetBacklightTimeout());

                case WmiWorkerCommand.SetBacklightTimeout:
                {
                    if (!TryReadBool(request.Args, "enabled", out var enabled))
                        return Invalid("enabled is missing or not a boolean");

                    return WmiWorkerResponse.FromResult(_executor.SetBacklightTimeout(enabled));
                }

                default:
                    return Invalid($"unknown command {(int)request.Command}");
            }
        }
        catch (Exception ex)
        {
            // A throw here would end the worker and leave the window with a dead pipe and no explanation. The
            // exception type and message stay in the log: they name internal types and paths, and the caller is a
            // pipe that may not be the window at all.
            AppLog.Warn(Loc.T("--wmi-worker komutu başarısız"), $"{ex.GetType().Name}: {ex.Message}");
            return WmiWorkerResponse.Failure(WmiOperationResult.CodeWmiFailed, "the worker could not run the command");
        }
    }

    private static WmiWorkerResponse Invalid(string error) =>
        WmiWorkerResponse.Failure(WmiOperationResult.CodeInvalidArgument, error);

    /// <summary>
    /// Reads a boolean argument. A JSON string is refused rather than coerced: <c>"false"</c> is a truthy string in
    /// most languages, and a worker that guessed would write the opposite of what the caller meant.
    /// </summary>
    private static bool TryReadBool(JsonObject? args, string name, out bool value)
    {
        value = false;
        if (args?[name] is not JsonValue node) return false;

        try
        {
            value = node.GetValue<bool>();
            return true;
        }
        catch
        {
            return false;
        }
    }

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

    /// <summary>
    /// One sensor id out of the request, clamped to the byte the firmware reads it from. An id that is missing or
    /// out of range falls back to <paramref name="fallback"/> instead of reaching WMI: the worker is the process
    /// holding the elevated token, so a value it cannot vouch for does not get through.
    /// </summary>
    private static byte ReadSensorId(JsonObject? args, string name, byte fallback) =>
        TryReadInt(args, name, out var value) && value is >= 0 and <= 255 ? (byte)value : fallback;

    private static bool TryReadString(JsonObject? args, string name, out string value)
    {
        value = "";
        var node = args?[name];
        if (node == null) return false;

        try
        {
            value = node.GetValue<string>() ?? "";
            return value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The four packed zone colours. Exactly four are required: a shorter list would leave a zone on whatever colour
    /// the last write left it, which is a state the caller did not ask for.
    /// </summary>
    private static bool TryReadZones(JsonObject? args, out IReadOnlyList<int> zones)
    {
        zones = Array.Empty<int>();
        if (args?["zones"] is not JsonArray array) return false;
        if (array.Count != AcerWmiProtocol.LedZoneCount) return false;

        var parsed = new int[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            try
            {
                parsed[i] = array[i]!.GetValue<int>();
            }
            catch
            {
                return false;
            }

            if (!WmiWorkerValidation.IsValidPackedColor(parsed[i])) return false;
        }

        zones = parsed;
        return true;
    }

    private static async Task<bool> TryReplyAsync(Stream pipe, WmiWorkerResponse response, CancellationToken cancellationToken)
    {
        try
        {
            await WmiWorkerProtocol.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
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

        // The pipe is a field rather than a local so this can actually close it. A Dispose that only sets a flag
        // leaves the handle open until the process exits, which is the leak the field exists to avoid.
        var pipe = _pipe;
        _pipe = null;
        try { pipe?.Dispose(); } catch { /* the pipe is already gone */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
