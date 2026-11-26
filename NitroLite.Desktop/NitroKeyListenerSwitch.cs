using NitroLite.Core;

namespace NitroLite.Desktop;

/// <summary>
/// Keeps a Nitro-key listener running exactly while its setting is on. Start and Stop go through one queue, so a
/// switch flipped off and on again cannot reorder them, and neither call blocks the UI thread: the Windows listener's
/// first start enumerates every class in root\WMI.
/// </summary>
/// <remarks>
/// Off means off: the Windows listener otherwise keeps a one-second WMI poll and a raw-input window alive for a key
/// the user has disabled. Stop is never final on either platform: the Linux listener closes its devices on Stop and
/// probes them again on the next Start, which is also what lets the predator_v4 step (it reloads acer_wmi and so
/// replaces the input device) restart it without the switch losing track.
/// </remarks>
public sealed class NitroKeyListenerSwitch
{
    private readonly INitroKeyListener _listener;
    private readonly object _gate = new();
    private Task _queue = Task.CompletedTask;
    private bool _running;

    public NitroKeyListenerSwitch(INitroKeyListener listener)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
    }

    /// <summary>Completes when every queued start or stop has finished.</summary>
    public Task Idle
    {
        get { lock (_gate) return _queue; }
    }

    /// <summary>Brings the listener in line with <paramref name="enabled"/>. Repeating the same value does nothing.</summary>
    public void Apply(bool enabled)
    {
        lock (_gate)
        {
            if (enabled == _running) return;
            _running = enabled;
            _queue = _queue.ContinueWith(_ => Run(enabled), TaskScheduler.Default);
        }
    }

    private void Run(bool start)
    {
        try
        {
            if (start) _listener.Start();
            else _listener.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Warn(start ? "The Nitro key listener could not be started" : "The Nitro key listener could not be stopped",
                ex.Message);
        }
    }
}
