using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The battery, read through <see cref="BatteryWatchdogService"/> and its wear history.
/// </summary>
/// <remarks>
/// <para>
/// The watchdog service already owns the WMI reads, the discharge-rate smoothing and the overcharge threshold, so this
/// is an adapter rather than a second implementation: two readers of the same battery would disagree about the watts
/// the moment one of them smoothed and the other did not.
/// </para>
/// <para>
/// The history file is created lazily. A machine whose data folder is read-only still gets a working page; it simply
/// cannot record a trend, which is what <see cref="RecordWearSample"/> returning false says.
/// </para>
/// </remarks>
public sealed class WindowsBatteryHealthService : IBatteryHealthService, IDisposable
{
    private readonly BatteryWatchdogService _watchdog;
    private readonly BatteryWearHistory _history;
    private readonly bool _ownsWatchdog;

    /// <param name="watchdog">
    /// The service to read through. Null builds one that does not hook the system power events, which is what a test
    /// wants: a static event handler outlives the test that registered it.
    /// </param>
    /// <param name="historyPath">Null uses the standard data folder; an explicit path is what a test passes.</param>
    public WindowsBatteryHealthService(BatteryWatchdogService? watchdog = null, string? historyPath = null)
    {
        _ownsWatchdog = watchdog == null;
        _watchdog = watchdog ?? new BatteryWatchdogService(attachSystemEvents: false);
        _history = new BatteryWearHistory(historyPath);
    }

    /// <summary>The underlying service, for the parts of the window that still talk to it directly.</summary>
    public BatteryWatchdogService Watchdog => _watchdog;

    /// <summary>
    /// A desktop or a laptop both answer: the WMI battery class exists on every Windows machine and reports zero
    /// capacity when there is no pack. The page decides what to draw from the reading, not from this flag.
    /// </summary>
    public bool IsSupported => true;

    public bool WatchdogEnabled
    {
        get => _watchdog.WatchdogEnabled;
        set => _watchdog.WatchdogEnabled = value;
    }

    public async Task<BatteryHealthSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var status = _watchdog.UpdateStatus();

        BatteryHealthDetails? details = null;
        try
        {
            details = await _watchdog.GetBatteryHealthDetailsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A machine whose battery namespace is missing still has a level and a plug state; the capacity card
            // prints "--" rather than the whole page failing.
            AppLog.Warn("Pil kapasite ayrıntıları okunamadı", ex.Message);
        }

        return new BatteryHealthSnapshot { Status = status, Details = details };
    }

    public IReadOnlyList<BatteryWearSample> GetWearSamples() => _history.GetSamples();

    public BatteryWearTrend GetWearTrend() => _history.GetTrend();

    public bool RecordWearSample(BatteryHealthDetails details) => _history.TryRecord(details);

    public bool ClearWearHistory() => _history.Clear();

    public void Dispose()
    {
        if (_ownsWatchdog) _watchdog.Dispose();
    }
}
