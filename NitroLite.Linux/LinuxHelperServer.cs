using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>Who is on the other end of a helper connection, and whether they may use it.</summary>
public interface ICallerAuthorizer
{
    /// <summary>True when the process connected on <paramref name="client"/> may change hardware settings.</summary>
    bool IsAuthorized(Socket client);
}

/// <summary>
/// The real check: the kernel's <c>SO_PEERCRED</c> names the connecting process, and polkit decides, through
/// <c>pkcheck</c>, whether that process may run <see cref="Action"/>. The policy file allows the user at the active
/// local session without a password and nobody else.
/// </summary>
/// <remarks>
/// The process is identified to polkit as <c>pid,start-time,uid</c>, not by pid alone: a pid can be reused between the
/// connection and the check, and the start time is what makes the identity unforgeable.
/// </remarks>
public sealed class PolkitCallerAuthorizer : ICallerAuthorizer
{
    public const string Action = "io.github.canerztk8.nitrolite.control";

    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    private readonly ICommandRunner _runner;
    private readonly ISysfsFileSystem _fs;

    public PolkitCallerAuthorizer(ICommandRunner? runner = null, ISysfsFileSystem? fs = null)
    {
        _runner = runner ?? ProcessCommandRunner.Instance;
        _fs = fs ?? RealSysfsFileSystem.Instance;
    }

    public bool IsAuthorized(Socket client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var credentials = new byte[12];
        try
        {
            if (client.GetRawSocketOption(SolSocket, SoPeerCred, credentials) < 12) return false;
        }
        catch (SocketException)
        {
            return false;
        }

        var pid = BitConverter.ToInt32(credentials, 0);
        var uid = BitConverter.ToInt32(credentials, 4);
        return IsAuthorized(pid, uid);
    }

    /// <summary>The check behind <see cref="IsAuthorized(Socket)"/>, for a known process.</summary>
    public bool IsAuthorized(int pid, int uid)
    {
        if (pid <= 0 || uid < 0) return false;

        var start = StartTime(_fs.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat"));
        if (start == null) return false;

        var pkcheck = FindPkcheck();
        if (pkcheck == null)
        {
            AppLog.Warn("The helper refused a request", "pkcheck is not installed, so no caller can be authorized");
            return false;
        }

        var subject = string.Create(CultureInfo.InvariantCulture, $"{pid},{start},{uid}");
        var result = _runner.Run(pkcheck, new[] { "--action-id", Action, "--process", subject }, TimeSpan.FromSeconds(10));
        return result.Ok;
    }

    /// <summary>
    /// pkcheck from the locations distributions install it, never from <c>PATH</c>: this runs as root, and the one
    /// program it trusts with the authorization answer must not be one a changed environment can substitute.
    /// </summary>
    private string? FindPkcheck()
    {
        foreach (var path in PkcheckLocations)
        {
            if (_runner.Find(path) is { } found) return found;
        }

        return null;
    }

    /// <summary>Where pkcheck lives on Debian/Ubuntu, Fedora, openSUSE and Arch.</summary>
    public static IReadOnlyList<string> PkcheckLocations { get; } = new[] { "/usr/bin/pkcheck", "/bin/pkcheck" };

    /// <summary>
    /// Field 22 of <c>/proc/PID/stat</c>. The second field is the command name in parentheses and may itself contain
    /// spaces and parentheses, so the fields are counted from the <em>last</em> closing parenthesis.
    /// </summary>
    public static string? StartTime(string? stat)
    {
        if (string.IsNullOrEmpty(stat)) return null;

        var close = stat.LastIndexOf(')');
        if (close < 0) return null;

        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // After the name come fields 3, 4, … so field 22 is at index 19.
        return fields.Length > 19 && fields[19].All(char.IsAsciiDigit) ? fields[19] : null;
    }
}

/// <summary>
/// The root helper's server: one JSON request per line on a Unix socket, each answered with one JSON line.
/// </summary>
/// <remarks>
/// <para>
/// Two requests exist. <c>{"op":"list"}</c> answers the attributes the policy allows, so the app knows which writes to
/// route here. <c>{"op":"write","path":…,"value":…}</c> writes one of them. Every write passes
/// <see cref="LinuxHelperPolicy.Check"/> first, and the caller is authorized once per connection before the first
/// request is served.
/// </para>
/// <para>
/// The server is started by systemd socket activation and exits by itself after <see cref="IdleTimeout"/> with no
/// connection, so it costs nothing while nobody is changing a setting. Lines are capped at
/// <see cref="MaxLineBytes"/>; a longer one closes the connection.
/// </para>
/// </remarks>
public sealed class LinuxHelperServer : IDisposable
{
    public const int MaxLineBytes = 4096;

    /// <summary>
    /// Connections served at once. The app holds one; the cap stops a local process from exhausting the root
    /// service's descriptors. A connection over the cap is closed at once, before any authorization work.
    /// </summary>
    public const int MaxConnections = 8;

    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly Socket _listener;
    private readonly LinuxHelperPolicy _policy;
    private readonly ISysfsFileSystem _fs;
    private readonly ICallerAuthorizer _authorizer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private int _active;
    private long _lastActivity = Environment.TickCount64;

    /// <summary>Connections that have put a fan in manual mode, for the watchdog.</summary>
    private readonly HashSet<int> _manualOwners = new();
    private int _nextConnection;

    public LinuxHelperServer(Socket listener, LinuxHelperPolicy policy, ISysfsFileSystem fs, ICallerAuthorizer authorizer,
        TimeSpan? idleTimeout = null)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        IdleTimeout = idleTimeout ?? DefaultIdleTimeout;
    }

    public TimeSpan IdleTimeout { get; }

    /// <summary>Serves until the idle timeout passes with no connection open, or <paramref name="cancel"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancel = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var watchdog = WatchIdleAsync(stop);
        var connections = new List<Task>();

        try
        {
            while (!stop.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await _listener.AcceptAsync(stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    // Out of descriptors, or a client that vanished mid-accept: a bad accept must not end a root
                    // service that other clients rely on. A short pause keeps a persistent error from spinning.
                    AppLog.Warn("The helper could not accept a connection", ex.SocketErrorCode.ToString());
                    await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                Touch();
                connections.RemoveAll(t => t.IsCompleted);
                if (Volatile.Read(ref _active) >= MaxConnections)
                {
                    client.Dispose();
                    continue;
                }

                connections.Add(ServeAsync(client, stop.Token));
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(connections).ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);

            // Whatever ended the server, a fan left in manual mode goes back to the firmware.
            await HandBackFansAsync().ConfigureAwait(false);
        }
    }

    private async Task WatchIdleAsync(CancellationTokenSource stop)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(IdleTimeout.TotalMilliseconds / 4, 20, 5000));
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(tick, stop.Token).ConfigureAwait(false);
                var idle = Environment.TickCount64 - Interlocked.Read(ref _lastActivity);
                if (Volatile.Read(ref _active) == 0 && idle >= IdleTimeout.TotalMilliseconds)
                {
                    await stop.CancelAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping, by idle timeout or by the caller.
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    private async Task ServeAsync(Socket client, CancellationToken cancel)
    {
        Interlocked.Increment(ref _active);
        var id = Interlocked.Increment(ref _nextConnection);
        try
        {
            using (client)
            await using (var stream = new NetworkStream(client, ownsSocket: false))
            {
                // Authorized as soon as it connects, so an unauthorized connection cannot sit on one of the slots.
                if (!_authorizer.IsAuthorized(client))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(Error("not-authorized") + "\n"), cancel).ConfigureAwait(false);
                    return;
                }

                while (!cancel.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(stream, cancel).ConfigureAwait(false);
                    if (line == null) break;
                    Touch();

                    var response = await HandleAsync(line, id).ConfigureAwait(false);
                    var bytes = Encoding.UTF8.GetBytes(response + "\n");
                    await stream.WriteAsync(bytes, cancel).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // The client went away; nothing to answer.
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Touch();
            await ReleaseManualAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The fan watchdog. A connection that put a fan in manual mode owns that state; when the last owner goes away
    /// (the app closed or crashed), the fans go back to the firmware. Manual speeds are only safe while something is
    /// watching the temperature, and the helper itself does not.
    /// </summary>
    private async Task ReleaseManualAsync(int id)
    {
        bool last;
        lock (_manualOwners)
        {
            if (!_manualOwners.Remove(id)) return;
            last = _manualOwners.Count == 0;
        }

        if (last) await HandBackFansAsync().ConfigureAwait(false);
    }

    /// <summary>Writes "automatic" to every fan mode attribute that is currently manual.</summary>
    private async Task HandBackFansAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (path, manual, automatic) in _policy.FanResets)
            {
                var current = _fs.ReadAllText(path)?.Trim();
                if (current == null || current == automatic) continue;
                if (manual != "*" && current != manual) continue;

                var result = _fs.WriteAllText(path, automatic + "\n");
                AppLog.Info("The helper handed the fans back to the firmware", $"{path}: {(result.Ok ? "ok" : result.Status.ToString())}");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Answers one request line. Internal for the tests, which drive it without a socket.</summary>
    internal Task<string> HandleAsync(string line) => HandleAsync(line, 0);

    internal async Task<string> HandleAsync(string line, int connection)
    {
        string? op, path, value;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Error("bad-request");

            op = Text(root, "op");
            path = Text(root, "path");
            value = Text(root, "value");
        }
        catch (JsonException)
        {
            return Error("bad-request");
        }

        switch (op)
        {
            case "list":
                return List();

            case "write":
            {
                var refusal = _policy.Check(path, value);
                if (refusal != null)
                {
                    AppLog.Warn("The helper refused a write", $"{path}: {refusal}");
                    return Error(refusal);
                }

                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var trimmed = value!.TrimEnd('\n');
                    var result = _fs.WriteAllText(path!, trimmed + "\n");
                    if (result.Ok) TrackManual(connection, path!, trimmed);
                    return result.Ok ? Ok() : Error("write-failed", $"{result.Status} {result.Detail}");
                }
                finally
                {
                    _writeGate.Release();
                }
            }

            default:
                return Error("bad-request");
        }
    }

    /// <summary>Records which connection last set a fan mode, so the watchdog knows whose departure to act on.</summary>
    private void TrackManual(int connection, string path, string value)
    {
        foreach (var (fanPath, manual, automatic) in _policy.FanResets)
        {
            if (fanPath != path) continue;

            var isManual = manual == "*" ? value != automatic : value == manual;
            lock (_manualOwners)
            {
                if (isManual) _manualOwners.Add(connection);
                else if (value == automatic) _manualOwners.Remove(connection);
            }
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private string List()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("paths");
            foreach (var path in _policy.Paths.OrderBy(p => p, StringComparer.Ordinal)) writer.WriteStringValue(path);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Ok() => "{\"ok\":true}";

    private static string Error(string error, string? detail = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", error);
            if (detail != null) writer.WriteString("detail", detail);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public void Dispose() => _writeGate.Dispose();

    /// <summary>One line, without its newline; null at end of stream or when the line exceeds the cap.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancel)
    {
        var bytes = new List<byte>(256);
        var one = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(one, cancel).ConfigureAwait(false);
            if (read == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray());

            bytes.Add(one[0]);
            if (bytes.Count > MaxLineBytes) return null;
        }
    }
}
