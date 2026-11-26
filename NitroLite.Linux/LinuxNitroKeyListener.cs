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
/// Reading the device needs the udev rule, which hands this one node (and no other keyboard) to the user at the seat;
/// installs from before it used the <c>input</c> group, which still works. Neither is there on a stock
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

    /// <summary>How long one wait for an event lasts before the reader looks at the stop flag again.</summary>
    private static readonly TimeSpan ReadSlice = TimeSpan.FromMilliseconds(250);

    private readonly IEvdevSource _source;
    private readonly NitroKeyDebounceGuard _debounce;
    private readonly object _gate = new();

    /// <summary>
    /// Serialises whole Start and Stop calls. Stop gives up <c>_gate</c> while it waits for the readers, so without this a
    /// Start arriving in that window found <c>_probed</c> still true and opened readers on devices that were closing.
    /// Always taken before <c>_gate</c>, never the other way round.
    /// </summary>
    private readonly object _lifecycle = new();
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
        lock (_lifecycle) StartCore();
    }

    private void StartCore()
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
        lock (_lifecycle) StopCore();
    }

    private void StopCore()
    {
        CancellationTokenSource? stop;
        Task? loop;

        lock (_gate)
        {
            if (!_started)
            {
                // Never started (or already stopped): there are no readers, but a probe may still hold open devices, or
                // may have found none. Either way the next probe has to start from scratch, for example after acer_wmi
                // was reloaded and its input device was replaced.
                foreach (var device in _devices)
                {
                    try { device.Dispose(); } catch { /* the device is already gone */ }
                }

                _devices.Clear();
                _probed = false;
                return;
            }

            _started = false;
            stop = _stop;
            loop = _loop;
            _stop = null;
            _loop = null;
        }

        try { stop?.Cancel(); } catch (ObjectDisposedException) { /* already stopped */ }

        // The readers wait in short slices and look at the token between them, so cancelling is enough for the real
        // devices. Closing the devices afterwards also wakes a device that can only block (a test double), and a real
        // device waits for its reader to leave before it closes the descriptor.
        lock (_gate)
        {
            foreach (var device in _devices)
            {
                try { device.Dispose(); } catch { /* the device is already gone */ }
            }

            _devices.Clear();
        }

        try { loop?.Wait(TimeSpan.FromSeconds(1)); } catch { /* a wedged reader must not block exit */ }
        stop?.Dispose();

        // Only now may the devices be probed (opened) again: the old readers are gone, so a restart cannot end up with
        // two readers on one device.
        lock (_gate) _probed = false;
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
                // Three different causes with three different fixes, so the log names the one that applies: no input
                // device could be opened at all (the udev rule is not installed), the module that creates the
                // key's device is absent, or the module is there and this model's key is not one of its events.
                var reason = opened.Count == 0
                    ? "no input device could be opened: the udev rule that grants the Acer WMI hotkeys device is not installed."
                    : !Directory.Exists(LinuxPaths.AcerWmiModule)
                        ? "the acer-wmi module is not loaded."
                        : "acer-wmi is loaded but exposes no Nitro key event on this model.";
                AppLog.Info("The Nitro key input device was not found", reason);
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
                read = device.Read(buffer, ReadSlice);
            }
            catch (Exception ex)
            {
                // A closed device is how Stop unblocks this loop, so it is not worth a warning.
                if (!token.IsCancellationRequested) AppLog.Warn("The Nitro key device could not be read", ex.Message);
                return;
            }

            if (read == 0) return;
            if (read < 0) continue; // nothing arrived in this slice; the loop re-checks the token

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
