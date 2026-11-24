using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>How the root helper answered the last <c>list</c> request.</summary>
public enum HelperState
{
    /// <summary>Not asked yet.</summary>
    Unknown,

    /// <summary>The helper answered with its list (which may be empty).</summary>
    Ready,

    /// <summary>The helper answered but polkit refused this session (<c>not-authorized</c>).</summary>
    NotAuthorized,

    /// <summary>No answer: the socket is missing, the helper is not ready, or the reply was unreadable.</summary>
    Unreachable
}

/// <summary>
/// Talks to the root helper over its Unix socket. Every call is short and synchronous: a sysfs write is a few
/// milliseconds, and the callers already run off the UI thread.
/// </summary>
/// <remarks>
/// The connection is opened lazily and kept. The helper exits after half a minute of silence, so a broken connection
/// is normal: the client reconnects once, and systemd starts the helper again on that connection.
/// </remarks>
public sealed class LinuxHelperClient : IDisposable
{
    public const string DefaultSocketPath = "/run/nitrolite/helper.sock";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

    private readonly string _socketPath;
    private readonly object _gate = new();
    private Socket? _socket;
    private NetworkStream? _stream;
    private HashSet<string>? _allowed;
    private HelperState _state = HelperState.Unknown;
    private long _retryAt;
    private TimeSpan _backoff = InitialBackoff;
    private bool _disposed;

    private readonly Func<long> _clock;

    public LinuxHelperClient(string socketPath = DefaultSocketPath) : this(socketPath, null) { }

    /// <summary>A test seam: <paramref name="clock"/> is the millisecond clock the retry backoff reads.</summary>
    internal LinuxHelperClient(string socketPath, Func<long>? clock)
    {
        _socketPath = socketPath;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>True when the helper's socket exists, which is when the package (or install.sh --system) set it up.</summary>
    public bool IsInstalled => File.Exists(_socketPath) || IsSocket(_socketPath);

    /// <summary>What the last <c>list</c> request found out about the helper.</summary>
    public HelperState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>
    /// The attributes the helper will write. Only a real list answer is remembered (an empty list included): a helper
    /// that is not reachable yet, or that answered <c>not-authorized</c>, is asked again after a short, growing
    /// pause, so a helper that was still starting does not stay "absent" for the whole session. Until then the
    /// answer is empty and <see cref="State"/> says why.
    /// </summary>
    public IReadOnlySet<string> Allowed
    {
        get
        {
            lock (_gate)
            {
                if (_allowed != null) return _allowed;
                if (_disposed || _clock() < _retryAt) return Empty;

                var answer = Send("{\"op\":\"list\"}");
                if (answer is not { } doc)
                {
                    return Failed(HelperState.Unreachable);
                }

                using (doc)
                {
                    if (!IsOk(doc))
                    {
                        var error = doc.RootElement.ValueKind == JsonValueKind.Object &&
                                    doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                            ? e.GetString()
                            : null;
                        return Failed(error == "not-authorized" ? HelperState.NotAuthorized : HelperState.Unreachable);
                    }

                    var list = new HashSet<string>(StringComparer.Ordinal);
                    if (!doc.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
                    {
                        return Failed(HelperState.Unreachable);
                    }

                    foreach (var p in paths.EnumerateArray())
                    {
                        if (p.ValueKind == JsonValueKind.String && p.GetString() is { } s) list.Add(s);
                    }

                    _allowed = list;
                    _state = HelperState.Ready;
                    _backoff = InitialBackoff;
                    if (list.Count > 0) AppLog.Info("The NitroLite helper is available", $"{list.Count} settings");
                    return list;
                }
            }
        }
    }

    private IReadOnlySet<string> Failed(HelperState state)
    {
        _state = state;
        _retryAt = _clock() + (long)_backoff.TotalMilliseconds;
        _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
        return Empty;
    }

    /// <summary>Asks the helper to write one attribute. The result uses the same statuses as a direct write.</summary>
    public SysfsWriteResult Write(string path, string value)
    {
        lock (_gate)
        {
            var request = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(request))
            {
                writer.WriteStartObject();
                writer.WriteString("op", "write");
                writer.WriteString("path", path);
                writer.WriteString("value", value.TrimEnd('\n'));
                writer.WriteEndObject();
            }

            var answer = Send(Encoding.UTF8.GetString(request.WrittenSpan));
            if (answer is not { } doc) return SysfsWriteResult.Denied(path);

            using (doc)
            {
                if (IsOk(doc)) return SysfsWriteResult.Success;

                var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
                var detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
                return error is "not-authorized" or "not-allowed"
                    ? SysfsWriteResult.Denied(path)
                    : SysfsWriteResult.Failed($"helper: {error} {detail}".Trim());
            }
        }
    }

    private static bool IsOk(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Object &&
        doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;

    /// <summary>One request and its answer, reconnecting once on a dropped connection. Null when unreachable.</summary>
    private JsonDocument? Send(string line)
    {
        if (_disposed) return null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                EnsureConnected();
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                _stream!.Write(bytes);
                var reply = ReadLine(_stream);
                if (reply != null) return JsonDocument.Parse(reply);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or ObjectDisposedException)
            {
                // A helper that exited for idleness drops the connection; the second attempt starts it again.
            }

            Disconnect();
        }

        return null;
    }

    private void EnsureConnected()
    {
        if (_stream != null) return;

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            ReceiveTimeout = (int)Timeout.TotalMilliseconds,
            SendTimeout = (int)Timeout.TotalMilliseconds
        };

        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(_socketPath));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
    }

    private void Disconnect()
    {
        try { _stream?.Dispose(); } catch { /* already closed */ }
        _stream = null;
        _socket = null;
    }

    private static string? ReadLine(Stream stream)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];
        while (true)
        {
            if (stream.Read(one, 0, 1) == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
            if (bytes.Count > LinuxHelperServer.MaxLineBytes * 16) return null;
        }
    }

    private static bool IsSocket(string path)
    {
        try
        {
            return OperatingSystem.IsLinux() && (File.GetAttributes(path) & FileAttributes.Device) != 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Disconnect();
        }
    }
}

/// <summary>
/// The real file system, with the helper behind it: a write this user may not make is handed to the root helper when
/// the helper allows that attribute. Reads are always direct.
/// </summary>
public sealed class HelperBackedFileSystem : ISysfsFileSystem
{
    private readonly ISysfsFileSystem _inner;
    private readonly LinuxHelperClient _helper;

    public HelperBackedFileSystem(ISysfsFileSystem inner, LinuxHelperClient helper)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
    }

    public bool FileExists(string path) => _inner.FileExists(path);
    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
    public string? ReadAllText(string path) => _inner.ReadAllText(path);
    public IReadOnlyList<string> EnumerateDirectories(string path) => _inner.EnumerateDirectories(path);
    public IReadOnlyList<string> EnumerateFiles(string path) => _inner.EnumerateFiles(path);

    public SysfsWriteResult WriteAllText(string path, string value)
    {
        var direct = _inner.WriteAllText(path, value);
        if (!direct.NeedsUdevRule || !_helper.Allowed.Contains(path)) return direct;
        return _helper.Write(path, value);
    }

    /// <summary>Writable directly, or through the helper — either way the udev rule is not needed for it.</summary>
    public bool CanWrite(string path) => _inner.CanWrite(path) || _helper.Allowed.Contains(path);
}
