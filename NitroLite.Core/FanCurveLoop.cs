namespace NitroLite.Core;

/// <summary>
/// Runs the fan curve on its own clock, at a fixed cadence, whatever the window is doing.
/// </summary>
/// <remarks>
/// <para>
/// The curve used to be stepped from the telemetry tick, which is the window's timer: one second while the window is
/// on screen, five seconds while it sits in the tray. Five seconds is a long time for a die that is climbing, and the
/// tray is exactly where the window lives while a game is running — the one case the curve exists for. The loop here
/// is independent of the window, so hiding it no longer slows the fans down.
/// </para>
/// <para>
/// A step that throws is logged and the loop carries on. A curve that stopped for good because one WMI call failed
/// would leave the fans wherever they happened to be, which is the failure this whole feature is meant to prevent.
/// </para>
/// </remarks>
public sealed class FanCurveLoop : IDisposable
{
    /// <summary>
    /// How often the curve is evaluated. Fast enough to catch a load spike, slow enough that a laptop idling at a
    /// steady temperature is not writing to the firmware every second.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(1500);

    private readonly Func<Task> _step;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <param name="step">One evaluation. Called on a background thread, so it must not touch the UI.</param>
    /// <param name="interval">Overridable so a test can drive the loop without waiting a second and a half.</param>
    public FanCurveLoop(Func<Task> step, TimeSpan? interval = null)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _interval = interval ?? DefaultInterval;

        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), _interval, "the interval must be positive");
    }

    /// <summary>The cadence the loop runs at.</summary>
    public TimeSpan Interval => _interval;

    /// <summary>True while a loop is running. False before the first start and after a stop.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate) return _loop is { IsCompleted: false };
        }
    }

    /// <summary>Starts the loop. Calling it twice is a no-op rather than a second loop.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _loop is { IsCompleted: false }) return;

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loop = Task.Run(() => RunAsync(cts));
        }
    }

    /// <summary>
    /// Stops the loop. Safe to call when it is not running, and does not wait: this is called from a property setter
    /// on the UI thread, and a step stuck in a WMI call must not freeze the window that asked for the stop.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cts;

        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _loop = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The loop finished on its own and disposed the source before this call arrived.
        }
    }

    /// <summary>
    /// Stops the loop and waits for the step in flight, bounded. Only shutdown uses this: everywhere else a stop is
    /// fire-and-forget, because the loop's own step checks the mode before it writes anything.
    /// </summary>
    public void StopAndWait(TimeSpan timeout)
    {
        Task? loop;
        lock (_gate) loop = _loop;

        Stop();

        try
        {
            loop?.Wait(timeout);
        }
        catch (AggregateException)
        {
            // The loop already logs its own failures; a cancelled wait is not worth a second report.
        }
    }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        using var timer = new PeriodicTimer(_interval);

        try
        {
            // The first step runs at once: the machine may already be hot when the curve is armed, and waiting a
            // second and a half to notice would be a second and a half of the fans at whatever the last write left.
            await StepAsync().ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                await StepAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() was called. Not an error.
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task StepAsync()
    {
        try
        {
            await _step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Fan eğrisi adımı başarısız", ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        StopAndWait(TimeSpan.FromSeconds(2));
    }
}
