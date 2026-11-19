using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// The Nitro key on Linux, read straight out of <c>/dev/input</c>.
/// </summary>
/// <remarks>
/// <para>
/// The in-kernel <c>acer-wmi</c> module registers an input device called <c>Acer WMI hotkeys</c> and reports the
/// button as <c>KEY_PROG1</c> (148) or <c>KEY_PROG3</c> (202). There is no daemon to ask and no D-Bus interface to
/// subscribe to, so the listener reads the character device itself: one <c>read</c> returns a batch of
/// <c>struct input_event</c>, and the press is the one with <c>EV_KEY</c>, the right code and value 1.
/// </para>
/// <para>
/// Reading <c>/dev/input</c> needs the user to be in the <c>input</c> group. That is a normal state on a stock
/// install, not a failure: the device list comes back empty, <see cref="IsSupported"/> is false, the settings row
/// hides itself and the log carries the one-line hint. Nothing here throws into the caller.
/// </para>
/// <para>
/// The device is opened once, when support is first asked about, and kept open until <see cref="Stop"/>. Opening it
/// lazily rather than in the constructor keeps a <c>--cli</c> run from holding a file descriptor it never reads.
/// </para>
/// </remarks>
public sealed class LinuxNitroKeyListener : INitroKeyListener
{
    /// <summary>The name the kernel gives the device <c>acer-wmi</c> registers.</summary>
    public const string AcerDeviceName = "Acer WMI hotkeys";

    private const string AcerNameFragment = "acer";
    private const string WmiNameFragment = "wmi";

    private readonly IEvdevSource _source;
    private readonly NitroKeyDebounceGuard _debounce;
    private readonly object _gate = new();
    private readonly List<IEvdevDevice> _devices = new();

    private CancellationTokenSource? _stop;
    private Task? _loop;
    private bool _probed;
    private bool _started;
    private bool _disposed;

    public LinuxNitroKeyListener(IEvdevSource? source = null, NitroKeyDebounceGuard? debounce = null)
    {
        _source = source ?? new RealEvdevSource();
        _debounce = debounce ?? new NitroKeyDebounceGuard();
    }

    public event EventHandler? NitroKeyPressed;

    /// <summary>
    /// True when an Acer input device that reports the key was found and could be opened. Asking is what opens it, so
    /// the settings row can decide whether to exist before the listener is started.
    /// </summary>
    public bool IsSupported
    {
        get
        {
            EnsureProbed();
            lock (_gate) return _devices.Count > 0;
        }
    }

    /// <summary>The names of the devices the listener is reading, for the log and the hardware report.</summary>
    public IReadOnlyList<string> DeviceNames
    {
        get
        {
            EnsureProbed();
            lock (_gate) return _devices.Select(d => d.Name).ToArray();
        }
    }

    public void Start()
    {
        EnsureProbed();

        lock (_gate)
        {
            if (_disposed || _started || _devices.Count == 0) return;

            _started = true;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;

            // One reader per device rather than one for all of them: a read blocks until that device has an event, so
            // a shared loop would let a quiet device starve the one that has the key.
            var readers = _devices.Select(device => Task.Run(() => ReadDevice(device, token), token)).ToArray();
            _loop = Task.WhenAll(readers);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? stop;
        Task? loop;

        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            stop = _stop;
            loop = _loop;
            _stop = null;
            _loop = null;
        }

        try { stop?.Cancel(); } catch (ObjectDisposedException) { /* already stopped */ }

        // Closing the device is what unblocks a pending read; cancelling the token alone would leave the reader
        // waiting for a key that may never come.
        lock (_gate)
        {
            foreach (var device in _devices)
            {
                try { device.Dispose(); } catch { /* the device is already gone */ }
            }

            _devices.Clear();
            _probed = false;
        }

        try { loop?.Wait(TimeSpan.FromSeconds(1)); } catch { /* a wedged reader must not block exit */ }
        stop?.Dispose();
    }

    private void EnsureProbed()
    {
        lock (_gate)
        {
            if (_probed || _disposed) return;
            _probed = true;

            // Every device the source opened is tracked here, not just the ones that were kept: a device that is
            // opened and then rejected, or one that is still open when a later device throws, holds a file
            // descriptor on /dev/input. The finally closes whatever did not end up in _devices.
            var opened = new List<IEvdevDevice>();

            try
            {
                foreach (var device in _source.OpenAll())
                {
                    opened.Add(device);
                    if (IsNitroDevice(device)) _devices.Add(device);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("The input devices could not be scanned", ex.Message);
            }
            finally
            {
                foreach (var device in opened)
                {
                    if (_devices.Contains(device)) continue;
                    try { device.Dispose(); } catch { /* the device is already gone */ }
                }
            }

            if (_devices.Count == 0)
            {
                AppLog.Info("The Nitro key input device was not found",
                    "the acer-wmi module is not loaded, or the user is not in the 'input' group.");
            }
            else
            {
                AppLog.Info("The Nitro key input device was attached", string.Join(", ", _devices.Select(d => d.Name)));
            }
        }
    }

    /// <summary>
    /// True when the device is the Acer hotkey device. The name is what identifies it; the key bitmap is what proves
    /// it, because a machine can carry an Acer-named device that reports nothing but the lid switch.
    /// </summary>
    private static bool IsNitroDevice(IEvdevDevice device)
    {
        if (device.Name.Length == 0) return false;
        if (!device.Name.Contains(AcerNameFragment, StringComparison.OrdinalIgnoreCase)) return false;

        bool reportsTheKey = device.SupportsKey(EvdevEvent.KeyProg1) || device.SupportsKey(EvdevEvent.KeyProg3);
        return reportsTheKey || device.Name.Contains(WmiNameFragment, StringComparison.OrdinalIgnoreCase);
    }

    private void ReadDevice(IEvdevDevice device, CancellationToken token)
    {
        int eventSize = device.EventSize;
        if (eventSize < 8) return;

        var buffer = new byte[eventSize * 64];

        while (!token.IsCancellationRequested)
        {
            int read;
            try
            {
                read = device.Read(buffer);
            }
            catch (Exception ex)
            {
                // A closed device is how Stop unblocks this loop, so it is not worth a warning.
                if (!token.IsCancellationRequested) AppLog.Warn("The Nitro key device could not be read", ex.Message);
                return;
            }

            if (read <= 0) return;

            for (int offset = 0; offset + eventSize <= read; offset += eventSize)
            {
                if (!EvdevEvent.TryParse(buffer.AsSpan(offset, eventSize), eventSize, out var ev)) continue;
                if (!ev.IsNitroKeyPress) continue;

                Raise();
            }
        }
    }

    private void Raise()
    {
        if (!_debounce.ShouldAccept()) return;

        try
        {
            NitroKeyPressed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Nitro key subscriber threw", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        lock (_gate)
        {
            foreach (var device in _devices)
            {
                try { device.Dispose(); } catch { /* the device is already gone */ }
            }

            _devices.Clear();
        }
    }
}
