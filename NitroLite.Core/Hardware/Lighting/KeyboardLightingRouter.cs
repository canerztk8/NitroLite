namespace NitroLite.Core.Hardware.Lighting;

/// <summary>Which path last carried a keyboard colour to the hardware.</summary>
public enum KeyboardLightingTransport
{
    /// <summary>No transport has answered yet.</summary>
    Unknown,

    /// <summary>The Acer WMI gaming interface (<c>SetGamingRgbKb</c> / <c>SetGamingKBBacklight</c>).</summary>
    Wmi,

    /// <summary>A vendor USB HID controller, written with its documented feature report.</summary>
    Hid,

    /// <summary>The Acer service socket, which is the only path that carries effects on some machines.</summary>
    Service
}

/// <summary>What happened to one lighting request.</summary>
/// <param name="Applied">True when the transport that took the write reported success.</param>
/// <param name="Transport">The transport that carried it, or <see cref="KeyboardLightingTransport.Unknown"/>.</param>
/// <param name="Detail">One line for the log and the report: which device, and why the other path was left.</param>
/// <param name="ProbeRan">True when the HID controller probe was consulted during this request.</param>
public sealed record KeyboardLightingOutcome(
    bool Applied,
    KeyboardLightingTransport Transport,
    string Detail,
    bool ProbeRan)
{
    /// <summary>True when the colour went over HID because WMI did not take it.</summary>
    public bool FellBackToHid => Applied && Transport == KeyboardLightingTransport.Hid;
}

/// <summary>
/// Picks the transport for a keyboard colour and remembers the answer per machine.
/// </summary>
/// <remarks>
/// <para>
/// The problem this solves is specific and measured. On an <c>AN16-61</c> and an <c>ANV16S-41</c> the Acer WMI methods
/// answer status <c>0</c> — success — and the LEDs do not change, because the 2025-era boards moved the backlight onto
/// a separate controller. NitroLite's read-back policy then does exactly what it was designed to do: three attempts,
/// three mismatches, capability off for the session. The user is left with a lighting tab that reports "Write failed"
/// and no way to set a colour, while another process on the same machine could set one.
/// </para>
/// <para>
/// So the WMI path keeps its first try — it is still the correct path on every older model — and when it does not take
/// a write, the vendor HID controller is asked once. Whichever path works is remembered for the machine, keyed by
/// <c>ModelCode|BIOS</c>: a BIOS update can move the backlight again, and a different laptop must not inherit the
/// answer. The cache is what makes the fallback cheap after the first failure, because a WMI write that is destined to
/// fail costs three round trips and three waits before it gives up.
/// </para>
/// <para>
/// Nothing here is allowed to throw at the caller: a missing controller, a device that vanished and a refused handle
/// all end as "not applied" with a reason, and the caller's own fallback still runs.
/// </para>
/// </remarks>
public sealed class KeyboardLightingRouter
{
    /// <summary>Machines remembered at once. A session sees one, a test suite sees a few; the cap keeps it bounded.</summary>
    public const int MaximumCachedMachines = 16;

    private readonly object _gate = new();
    private readonly List<string> _order = new();
    private readonly Dictionary<string, KeyboardLightingTransport> _transports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _details = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The cache key: <c>AN16-61|1.53</c>. A missing model or BIOS still produces a stable key, because a machine that
    /// cannot be identified must not share the cache entry of an identified one.
    /// </summary>
    public static string CacheKey(string? modelCode, string? biosVersion)
    {
        var model = string.IsNullOrWhiteSpace(modelCode) ? "unknown" : modelCode.Trim().ToUpperInvariant();
        var bios = string.IsNullOrWhiteSpace(biosVersion) ? "unknown" : biosVersion.Trim();
        return $"{model}|{bios}";
    }

    /// <summary>The remembered transport for a machine, or null when nothing has succeeded there yet.</summary>
    public KeyboardLightingTransport? CachedTransport(string key)
    {
        lock (_gate) return _transports.TryGetValue(key, out var transport) ? transport : null;
    }

    /// <summary>The remembered one-line reason for a machine's transport, when one was recorded.</summary>
    public string? CachedDetail(string key)
    {
        lock (_gate) return _details.TryGetValue(key, out var detail) ? detail : null;
    }

    /// <summary>Forgets one machine. Used when its remembered transport stops working.</summary>
    public void Forget(string key)
    {
        lock (_gate)
        {
            _transports.Remove(key);
            _details.Remove(key);
            _order.Remove(key);
        }
    }

    /// <summary>Forgets everything. A rescan calls this: the user asked for another look at the hardware.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _transports.Clear();
            _details.Clear();
            _order.Clear();
        }
    }

    /// <summary>
    /// Runs one lighting request: WMI first unless this machine already learned that HID is the one that works, then
    /// HID once when WMI does not take it.
    /// </summary>
    /// <param name="modelCode">The SMBIOS model code, for the cache key.</param>
    /// <param name="biosVersion">The BIOS version, for the cache key.</param>
    /// <param name="frame">What to write.</param>
    /// <param name="writeThroughWmi">The existing WMI path, with its own read-back policy.</param>
    /// <param name="findHidWriter">
    /// Probes for a usable vendor controller. Called at most once per request, and only when it is needed.
    /// </param>
    /// <param name="onHidWriter">Called with the writer the request used, so the caller can log it. Optional.</param>
    public async Task<KeyboardLightingOutcome> ApplyAsync(
        string? modelCode,
        string? biosVersion,
        KeyboardLightingFrame frame,
        Func<Task<bool>> writeThroughWmi,
        Func<IHidLightingWriter?> findHidWriter,
        Action<IHidLightingWriter>? onHidWriter = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(writeThroughWmi);
        ArgumentNullException.ThrowIfNull(findHidWriter);

        var key = CacheKey(modelCode, biosVersion);
        var cached = CachedTransport(key);
        bool probeRan = false;

        // The machine already learned that HID is the path that works. Asking WMI first would cost three failing round
        // trips on every colour change, which is the whole reason the cache exists.
        if (cached == KeyboardLightingTransport.Hid)
        {
            probeRan = true;
            var writer = TryFindWriter(findHidWriter, out _);
            if (writer != null && TryWriteHid(writer, frame, onHidWriter, out _))
            {
                Remember(key, KeyboardLightingTransport.Hid, writer.Description);
                return new KeyboardLightingOutcome(
                    true, KeyboardLightingTransport.Hid, $"cached: {writer.Description}", probeRan);
            }

            // The controller is gone or refused. Fall through to WMI rather than leaving the keyboard on its last
            // colour, and forget the cache entry so the next request probes again.
            Forget(key);
        }

        bool wmiApplied;
        string? wmiError = null;
        try
        {
            wmiApplied = await writeThroughWmi().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            wmiApplied = false;
            wmiError = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (wmiApplied)
        {
            Remember(key, KeyboardLightingTransport.Wmi, "SetGamingRgbKb read-back matched");
            return new KeyboardLightingOutcome(
                true, KeyboardLightingTransport.Wmi, "WMI read-back matched", probeRan);
        }

        // WMI answered nothing, refused, or answered success without changing the colour. One HID attempt, and only
        // when the request can be expressed as a documented frame.
        probeRan = true;
        var fallback = TryFindWriter(findHidWriter, out var fallbackRefusal);
        string? fallbackError = null;
        if (fallback != null && TryWriteHid(fallback, frame, onHidWriter, out fallbackError))
        {
            Remember(key, KeyboardLightingTransport.Hid, fallback.Description);
            return new KeyboardLightingOutcome(
                true,
                KeyboardLightingTransport.Hid,
                $"WMI did not apply it ({wmiError ?? "read-back did not match"}); written through {fallback.Description}",
                probeRan);
        }

        var reason = fallback == null
            ? fallbackRefusal ?? "no vendor HID lighting controller"
            : fallbackError ?? "the HID controller did not take the write";

        return new KeyboardLightingOutcome(
            false,
            KeyboardLightingTransport.Unknown,
            $"WMI did not apply it ({wmiError ?? "read-back did not match"}); {reason}",
            probeRan);
    }

    /// <summary>
    /// The remembered decisions, one line each, newest first. The hardware report prints these so an issue says which
    /// transport answered on that machine without the owner having to read a log.
    /// </summary>
    public IReadOnlyList<string> Describe()
    {
        lock (_gate)
        {
            var lines = new List<string>(_order.Count);
            foreach (var key in _order)
            {
                if (!_transports.TryGetValue(key, out var transport)) continue;
                var detail = _details.TryGetValue(key, out var text) ? $" — {text}" : "";
                lines.Add($"{key}: {transport.ToString().ToLowerInvariant()}{detail}");
            }

            return lines;
        }
    }

    private static IHidLightingWriter? TryFindWriter(Func<IHidLightingWriter?> find, out string? refusal)
    {
        refusal = null;
        try
        {
            var writer = find();
            if (writer == null) refusal = "no vendor HID lighting controller was found";
            else if (!writer.CanWrite) refusal = $"{writer.Description}: {writer.Refusal ?? "not writable"}";
            return writer;
        }
        catch (Exception ex)
        {
            refusal = $"the HID probe failed ({ex.GetType().Name}: {ex.Message})";
            return null;
        }
    }

    public KeyboardLightingOutcome ApplyDetectedHid(string model, string bios, KeyboardLightingFrame frame, IHidLightingWriter writer)
    {
        var key = CacheKey(model, bios);
        if (!TryWriteHid(writer, frame, null, out var error))
        {
            Forget(key);
            return new(false, KeyboardLightingTransport.Unknown, error ?? "HID write refused", true);
        }
        Remember(key, KeyboardLightingTransport.Hid, writer.Description);
        return new(true, KeyboardLightingTransport.Hid, writer.Description, true);
    }

    private static bool TryWriteHid(
        IHidLightingWriter writer, KeyboardLightingFrame frame, Action<IHidLightingWriter>? onHidWriter,
        out string? error)
    {
        error = null;
        if (!writer.CanWrite)
        {
            error = $"{writer.Description}: {writer.Refusal ?? "not writable"}";
            return false;
        }

        try
        {
            if (!writer.TryWrite(frame, out error)) return false;
            onHidWriter?.Invoke(writer);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{writer.Description} threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private void Remember(string key, KeyboardLightingTransport transport, string detail)
    {
        lock (_gate)
        {
            if (!_transports.ContainsKey(key))
            {
                _order.Add(key);
                while (_order.Count > MaximumCachedMachines)
                {
                    var oldest = _order[0];
                    _order.RemoveAt(0);
                    _transports.Remove(oldest);
                    _details.Remove(oldest);
                }
            }

            _transports[key] = transport;
            _details[key] = detail;
        }
    }

    /// <summary>For a log line: how many machines are remembered. Never prints a colour.</summary>
    public string DescribeCacheSize() => $"{_transports.Count} machine(s) remembered";
}
