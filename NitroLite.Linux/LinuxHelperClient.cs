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

    /// <summary>
    /// How long the first <c>list</c> may take. It is asked during the capability probe, which the window waits for, and
    /// a polkit that is slow to start (or hung) must not hold the app's start for the full <see cref="Timeout"/> twice
    /// over. A first answer that does not arrive in time is asked again in the background with the full timeout.
    /// </summary>
    public static readonly TimeSpan FirstQueryTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the background retry keeps asking after a first answer that was not a list. Long enough for an
    /// autologin session to become active and for polkit to come up; short enough that a session polkit will never
    /// allow (SSH, a second seat) does not keep the root helper awake for the whole run.
    /// </summary>
    public static readonly TimeSpan BackgroundRetryWindow = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryTick = TimeSpan.FromMilliseconds(250);

    /// <summary>Below this much budget left, a second attempt is not started: it could only time out.</summary>
    private const long MinAttemptMilliseconds = 100;
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

    private readonly string _socketPath;

    /// <summary>
    /// Guards the state below and the socket references. Held only for field reads and writes, never across a
    /// connect, a send or a wait for the helper's answer, so <see cref="Reset"/>, <see cref="Dispose"/> and the state
    /// getters never wait behind a polkit that is not answering.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Serialises the requests themselves: there is one connection and one line in flight on it at a time. Held across
    /// the network wait. <see cref="Reset"/> and <see cref="Dispose"/> do not take it; they close the socket instead,
    /// which ends the wait at once.
    /// </summary>
    private readonly object _io = new();

    private Socket? _socket;
    private NetworkStream? _stream;
    private HashSet<string>? _allowed;
    private HelperState _state = HelperState.Unknown;
    private long _retryAt;
    private TimeSpan _backoff = InitialBackoff;
    private bool _disposed;
    private string? _lastWriteRefusal;

    /// <summary>
    /// Bumped by <see cref="Reset"/> and <see cref="Dispose"/>. A query that started under an older value discards its
    /// answer, and a background retry from before them stops.
    /// </summary>
    private int _generation;
    private bool _refreshing;

    private readonly Func<long> _clock;
    private readonly Func<TimeSpan, Task> _delay;

    public LinuxHelperClient(string socketPath = DefaultSocketPath) : this(socketPath, null) { }

    /// <summary>
    /// A test seam: <paramref name="clock"/> is the millisecond clock the retry backoff reads, and
    /// <paramref name="delay"/> the wait between two looks of the background retry.
    /// </summary>
    internal LinuxHelperClient(string socketPath, Func<long>? clock, Func<TimeSpan, Task>? delay = null)
    {
        _socketPath = socketPath;
        _clock = clock ?? (() => Environment.TickCount64);
        _delay = delay ?? (t => Task.Delay(t));
    }

    /// <summary>
    /// Raised, on a pool thread, when the background retry got an answer different from the one before it: the helper
    /// came up, polkit started allowing this session, or the list changed. The backend re-probes on it, so a slow
    /// polkit at start-up or an autologin session that became active a moment late does not leave the controls
    /// read-only for the whole run.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Forgets the cached answer, so the next <see cref="Allowed"/> asks the helper again (with the short first-query
    /// timeout). The rescan button calls this: after "Grant access", after a module was installed, or once the session
    /// became active, the helper's answer may well be different. Never waits for a request in flight.
    /// </summary>
    /// <remarks>
    /// A connection that got a list is kept: it is authorised, and it is the connection the helper counts as the owner
    /// of a manual or full-speed fan, so closing it would hand the fans back to the firmware behind the user's back. The
    /// list is simply asked again over it. Only a connection that did not get a list (not authorised, or not answering)
    /// is closed, because a fresh connection is what makes the helper ask polkit again.
    /// </remarks>
    public void Reset()
    {
        Socket? drop = null;
        lock (_gate)
        {
            var keepConnection = _state == HelperState.Ready;
            _allowed = null;
            _state = HelperState.Unknown;
            _retryAt = 0;
            _backoff = InitialBackoff;
            _lastWriteRefusal = null;
            _generation++;
            _refreshing = false;

            if (!keepConnection) drop = DetachLocked();
        }

        Close(drop);
    }

    /// <summary>
    /// Why the last <see cref="Write"/> was refused: <c>not-authorized</c>, <c>not-allowed</c> or <c>unreachable</c>.
    /// Null when it was not refused.
    /// </summary>
    public string? LastWriteRefusal
    {
        get { lock (_gate) return _lastWriteRefusal; }
    }

    /// <summary>True when the last list answer named <paramref name="path"/>. Never asks the helper.</summary>
    public bool KnowsPath(string path)
    {
        lock (_gate) return _allowed != null && _allowed.Contains(path);
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
            }

            lock (_io)
            {
                bool first;
                int generation;
                lock (_gate)
                {
                    // Another caller may have asked while this one waited for the connection.
                    if (_allowed != null) return _allowed;
                    if (_disposed || _clock() < _retryAt) return Empty;
                    first = _state == HelperState.Unknown;
                    generation = _generation;
                }

                // The first question is asked during the probe, inside one short budget; a later one (a write) may
                // wait for polkit as long as the helper does. The first one still gets its second attempt when the
                // first fails at once: after a rescan the kept connection may be one the helper closed for silence,
                // and that is a reconnect, not a slow polkit. A slow polkit spends the whole budget on the first try.
                var result = first
                    ? Query(FirstQueryTimeout, 2, generation, out _, budget: true)
                    : Query(Timeout, 2, generation, out _);

                bool startRetry;
                lock (_gate)
                {
                    startRetry = first && generation == _generation && _allowed == null && !_refreshing && !_disposed;
                    if (startRetry) _refreshing = true;
                }

                if (startRetry) _ = RetryInBackgroundAsync(generation);
                return result;
            }
        }
    }

    /// <summary>
    /// One <c>list</c> request, made with <see cref="_io"/> held and <see cref="_gate"/> not held. The answer is applied
    /// only when no <see cref="Reset"/> or <see cref="Dispose"/> happened meanwhile (<paramref name="generation"/> is
    /// still current); otherwise it is discarded and <paramref name="applied"/> is false.
    /// </summary>
    private IReadOnlySet<string> Query(TimeSpan timeout, int attempts, int generation, out bool applied, bool budget = false)
    {
        var answer = Send("{\"op\":\"list\"}", timeout, attempts, generation, budget);

        HelperState outcome;
        HashSet<string>? list = null;
        if (answer is not { } doc)
        {
            outcome = HelperState.Unreachable;
        }
        else
        {
            using (doc)
            {
                if (!IsOk(doc))
                {
                    var error = doc.RootElement.ValueKind == JsonValueKind.Object &&
                                doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                        ? e.GetString()
                        : null;
                    outcome = error == "not-authorized" ? HelperState.NotAuthorized : HelperState.Unreachable;
                }
                else if (!doc.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
                {
                    outcome = HelperState.Unreachable;
                }
                else
                {
                    list = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in paths.EnumerateArray())
                    {
                        if (p.ValueKind == JsonValueKind.String && p.GetString() is { } s) list.Add(s);
                    }

                    outcome = HelperState.Ready;
                }
            }
        }

        lock (_gate)
        {
            applied = !_disposed && generation == _generation;
            if (!applied) return Empty;

            if (list == null) return Failed(outcome);

            _allowed = list;
            _state = HelperState.Ready;
            _backoff = InitialBackoff;
        }

        if (list.Count > 0) AppLog.Info("The NitroLite helper is available", $"{list.Count} settings");
        return list;
    }

    /// <summary>
    /// Asks again, with the full timeout, after a first answer that was not a list — until a list arrives, the window
    /// closes, or <see cref="Reset"/>/<see cref="Dispose"/> starts over. Each answer that differs from the previous one
    /// raises <see cref="StateChanged"/>. The backoff and the clock are the same ones <see cref="Allowed"/> uses, so the
    /// helper is never asked more often than the app itself would ask it.
    /// </summary>
    private async Task RetryInBackgroundAsync(int generation)
    {
        var until = _clock() + (long)BackgroundRetryWindow.TotalMilliseconds;
        try
        {
            while (true)
            {
                await _delay(RetryTick).ConfigureAwait(false);

                lock (_gate)
                {
                    if (_disposed || generation != _generation || _allowed != null) return;
                    if (_clock() >= until) return;
                    if (_clock() < _retryAt) continue;
                }

                HelperState before, after;
                bool applied;
                lock (_io)
                {
                    lock (_gate)
                    {
                        if (_disposed || generation != _generation || _allowed != null) return;
                        before = _state;
                    }

                    // One attempt: a second full timeout would keep a write waiting twice as long behind a polkit that
                    // is not answering. The state lock is not held across the wait.
                    Query(Timeout, 1, generation, out applied);
                    lock (_gate) after = _state;
                }

                if (!applied) return;
                if (after != before) RaiseStateChanged();
                if (after == HelperState.Ready) return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("The helper could not be asked again", ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation) _refreshing = false;
            }
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("A helper state subscriber threw", ex.Message);
        }
    }

    /// <summary>Records a failed answer and schedules the next ask. Called with <see cref="_gate"/> held.</summary>
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
        var request = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(request))
        {
            writer.WriteStartObject();
            writer.WriteString("op", "write");
            writer.WriteString("path", path);
            writer.WriteString("value", value.TrimEnd('\n'));
            writer.WriteEndObject();
        }

        string? refusal = null;
        SysfsWriteResult result;

        lock (_io)
        {
            // A Reset or Dispose while the write waits ends it as unreachable instead of reconnecting for a second try.
            int generation;
            lock (_gate) generation = _generation;

            var answer = Send(Encoding.UTF8.GetString(request.WrittenSpan), Timeout, 2, generation);
            if (answer is not { } doc)
            {
                refusal = "unreachable";
                result = SysfsWriteResult.Denied(path);
            }
            else
            {
                using (doc)
                {
                    var error = doc.RootElement.ValueKind == JsonValueKind.Object &&
                                doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                        ? e.GetString()
                        : null;
                    var detail = doc.RootElement.ValueKind == JsonValueKind.Object &&
                                 doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString()
                        : null;

                    if (IsOk(doc))
                    {
                        result = SysfsWriteResult.Success;
                    }
                    else if (error is "not-authorized" or "not-allowed")
                    {
                        refusal = error;
                        result = SysfsWriteResult.Denied(path);
                    }
                    else
                    {
                        result = SysfsWriteResult.Failed($"helper: {error} {detail}".Trim());
                    }
                }
            }
        }

        lock (_gate) _lastWriteRefusal = refusal;
        return result;
    }

    private static bool IsOk(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Object &&
        doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;

    /// <summary>
    /// One request and its answer, reconnecting once on a dropped connection. Null when unreachable. Called with
    /// <see cref="_io"/> held; the state lock is only taken to read or swap the socket reference.
    /// </summary>
    /// <param name="budget">
    /// When true, <paramref name="timeout"/> is the budget for all attempts together rather than for each one, so a
    /// retry after an instant failure (a dead socket) fits in the same time a single attempt would have had.
    /// </param>
    private JsonDocument? Send(string line, TimeSpan timeout, int attempts, int? generation = null, bool budget = false)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var wait = timeout;
            if (budget)
            {
                var left = deadline - Environment.TickCount64;
                if (left < MinAttemptMilliseconds) return null;
                wait = TimeSpan.FromMilliseconds(left);
            }

            // A list asked before a Reset/Dispose does not reconnect after it: its answer would be discarded anyway, and
            // the reconnect would keep the next caller waiting.
            if (generation is { } asked)
            {
                lock (_gate)
                {
                    if (asked != _generation) return null;
                }
            }

            NetworkStream? stream = null;
            try
            {
                stream = EnsureConnected();
                if (stream == null) return null; // disposed

                stream.Socket.ReceiveTimeout = (int)wait.TotalMilliseconds;
                stream.Socket.SendTimeout = (int)wait.TotalMilliseconds;
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                stream.Write(bytes);
                var reply = ReadLine(stream);
                if (reply != null) return JsonDocument.Parse(reply);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or ObjectDisposedException)
            {
                // A helper that exited for idleness drops the connection; the second attempt starts it again. A Reset or
                // Dispose that closed the socket under this call ends up here too.
            }

            Socket? drop;
            lock (_gate)
            {
                // Only the connection this call used: a Reset may already have replaced it.
                drop = stream != null && ReferenceEquals(_stream, stream) ? DetachLocked() : null;
            }

            Close(drop);
            stream?.Dispose();
        }

        return null;
    }

    /// <summary>The open connection, opening one if needed; null once disposed. Called with <see cref="_io"/> held.</summary>
    private NetworkStream? EnsureConnected()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_stream != null) return _stream;
        }

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

        var stream = new NetworkStream(socket, ownsSocket: true);
        lock (_gate)
        {
            if (!_disposed)
            {
                _socket = socket;
                _stream = stream;
                return stream;
            }
        }

        stream.Dispose();
        return null;
    }

    /// <summary>Takes the connection out of the fields so it can be closed outside the lock. Called with the gate held.</summary>
    private Socket? DetachLocked()
    {
        var socket = _socket;
        _stream = null;
        _socket = null;
        return socket;
    }

    /// <summary>
    /// Closes a detached connection. Shutting the socket down first wakes a read blocked on it in another thread, so a
    /// request in flight ends at once instead of after its timeout.
    /// </summary>
    private static void Close(Socket? socket)
    {
        if (socket == null) return;
        try { socket.Shutdown(SocketShutdown.Both); } catch { /* not connected, or already closed */ }
        try { socket.Dispose(); } catch { /* already closed */ }
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

    /// <summary>Closes the connection without waiting for a request in flight; that request ends as unreachable.</summary>
    public void Dispose()
    {
        Socket? drop;
        lock (_gate)
        {
            _disposed = true;
            _generation++;
            drop = DetachLocked();
        }

        Close(drop);
    }
}

/// <summary>
/// Why a write was refused for lack of permission. The fix differs for each, so the settings page should not offer the
/// udev rule for a refusal the rule would not change.
/// </summary>
public enum LinuxWriteDenial
{
    /// <summary>The write was not refused for lack of permission.</summary>
    None,

    /// <summary>No helper in the way: the attribute is root-owned and the udev rule (or the group) is missing.</summary>
    UdevRuleMissing,

    /// <summary>The helper answered, but polkit does not allow this session (not the active local one, or not yet).</summary>
    HelperNotAuthorized,

    /// <summary>The helper is installed but did not answer: not started, polkit hung, or the socket is gone.</summary>
    HelperUnreachable,

    /// <summary>The helper answered, but its own probe did not find this attribute, so it will not write it.</summary>
    NotInHelperList
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

    /// <summary>The helper behind this file system, so a rescan can make it ask again.</summary>
    public LinuxHelperClient Helper => _helper;

    /// <summary>
    /// Why the last refused write was refused, or <see cref="LinuxWriteDenial.None"/> when it was not refused for lack
    /// of permission. Read right after the write, on the same call path.
    /// </summary>
    public LinuxWriteDenial LastDenial { get; private set; }

    public bool FileExists(string path) => _inner.FileExists(path);
    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
    public string? ReadAllText(string path) => _inner.ReadAllText(path);
    public IReadOnlyList<string> EnumerateDirectories(string path) => _inner.EnumerateDirectories(path);
    public IReadOnlyList<string> EnumerateFiles(string path) => _inner.EnumerateFiles(path);

    public SysfsWriteResult WriteAllText(string path, string value)
    {
        LastDenial = LinuxWriteDenial.None;
        var direct = _inner.WriteAllText(path, value);
        if (!direct.NeedsUdevRule) return direct;

        if (!_helper.Allowed.Contains(path))
        {
            LastDenial = _helper.State switch
            {
                HelperState.NotAuthorized => LinuxWriteDenial.HelperNotAuthorized,
                HelperState.Ready => LinuxWriteDenial.NotInHelperList,
                _ => LinuxWriteDenial.HelperUnreachable
            };
            return direct;
        }

        var result = _helper.Write(path, value);
        if (result.NeedsUdevRule)
        {
            LastDenial = _helper.LastWriteRefusal switch
            {
                "not-authorized" => LinuxWriteDenial.HelperNotAuthorized,
                "not-allowed" => LinuxWriteDenial.NotInHelperList,
                _ => LinuxWriteDenial.HelperUnreachable
            };
        }

        return result;
    }

    /// <summary>Writable directly, or through the helper — either way the udev rule is not needed for it.</summary>
    public bool CanWrite(string path) => _inner.CanWrite(path) || _helper.Allowed.Contains(path);
}
