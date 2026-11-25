namespace NitroLite.Core;

/// <summary>What one power-state evaluation did to the panel.</summary>
public enum RefreshRateAction
{
    /// <summary>Nothing to do: the state did not change, or the panel was already where it should be.</summary>
    None = 0,

    /// <summary>The machine went to battery and the panel was lowered to <see cref="RefreshRateAutomation.BatteryRefreshRate"/>.</summary>
    LoweredToBattery = 1,

    /// <summary>The machine came back to mains and the rate the user had before was put back.</summary>
    RestoredToMaximum = 2,

    /// <summary>This platform cannot change the refresh rate, so nothing was attempted.</summary>
    Unsupported = 3,

    /// <summary>The display driver refused the mode, or no internal panel is active.</summary>
    Failed = 4
}

/// <summary>One evaluation, with the rate it started from and the rate now in effect.</summary>
public readonly record struct RefreshRateResult(RefreshRateAction Action, int From, int To)
{
    /// <summary>Nothing happened. <paramref name="rate"/> is the rate in effect, or 0 when it was not read.</summary>
    public static RefreshRateResult Nothing(int rate = 0) => new(RefreshRateAction.None, rate, rate);

    public bool Changed => Action is RefreshRateAction.LoweredToBattery or RefreshRateAction.RestoredToMaximum;
}

/// <summary>
/// Drops the built-in panel to 60 Hz on battery and puts the user's own rate back on mains.
/// </summary>
/// <remarks>
/// <para>
/// The one thing the Eco automation does not already cover elsewhere: the panel. Mode, fan and CPU boost belong to
/// school mode and the game profiles, and duplicating them here would give the machine two writers for one register.
/// </para>
/// <para>
/// The rate that was replaced is remembered rather than assumed. A 165 Hz panel restored to "the maximum" would be
/// right, but a user who deliberately runs at 120 Hz to save power would silently be promoted to 165 Hz on every
/// replug — so the value read at the moment of the drop is the value that comes back.
/// </para>
/// <para>
/// Evaluation is transition-driven. The telemetry tick calls <see cref="Apply"/> once a second, and after the first
/// call the whole thing is a boolean comparison: reading the current mode costs a <c>QueryDisplayConfig</c> and a
/// <c>EnumDisplaySettings</c> walk, which is not something to do sixty times a minute for an answer that changes when
/// the charger is plugged in.
/// </para>
/// </remarks>
public sealed class RefreshRateAutomation
{
    /// <summary>The rate the panel is dropped to on battery. Not configurable: 60 Hz is what every panel supports.</summary>
    public const int BatteryRefreshRate = 60;

    private readonly IDisplayRefreshRateService _display;
    private readonly AppSettings _settings;
    private readonly object _gate = new();

    /// <summary>The rate the battery drop replaced, or null when the panel is at the user's own rate.</summary>
    private int? _savedRate;

    /// <summary>The last power state that was evaluated, so a repeated tick costs one comparison.</summary>
    private bool? _lastOnAc;

    /// <summary>
    /// True when the last evaluation failed or found no panel. It is tried again instead of being remembered as
    /// "nothing to do" — but not on every tick: see <see cref="_skipTicks"/>.
    /// </summary>
    private bool _lastFailed;

    /// <summary>How many evaluations in a row failed or found no panel.</summary>
    private int _failures;

    /// <summary>
    /// Ticks to sit out before the next retry. A lid closed on an external monitor, or a panel without a 60 Hz mode,
    /// answers the same way every second, and retrying it every second cost a display walk per tick for as long as the
    /// machine ran. The first retry is immediate (a transient read failure is the common case); after that the wait
    /// doubles up to <see cref="MaxRetrySkip"/> ticks. A power change, a forced pass or a success starts over.
    /// </summary>
    private int _skipTicks;

    /// <summary>The longest wait between two retries, in evaluations (about five minutes at one tick a second).</summary>
    internal const int MaxRetrySkip = 300;

    public RefreshRateAutomation(IDisplayRefreshRateService display, AppSettings settings)
    {
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // A rate replaced in an earlier run that ended on battery (an exit, a crash) is still owed: the first evaluation
        // on the adapter puts it back.
        if (settings.RefreshRateBeforeBattery is int saved && saved > 0) _savedRate = saved;
    }

    /// <summary>False on a platform that cannot change the refresh rate, which is the Linux case today.</summary>
    public bool IsSupported => _display.IsSupported;

    /// <summary>The rate the battery drop replaced, or null when nothing was replaced.</summary>
    public int? SavedRate
    {
        get { lock (_gate) return _savedRate; }
    }

    /// <summary>
    /// Applies the policy for <paramref name="onAc"/>. <paramref name="force"/> re-evaluates even when the power state
    /// has not changed, which is what the settings switch needs: turning the option on while already on battery has to
    /// take effect without waiting for a replug.
    /// </summary>
    public RefreshRateResult Apply(bool onAc, bool force = false)
    {
        lock (_gate)
        {
            if (!_display.IsSupported) return new RefreshRateResult(RefreshRateAction.Unsupported, 0, 0);

            bool sameState = _lastOnAc == onAc;
            _lastOnAc = onAc;

            // A real change of power source, or a pass the user asked for, is worth trying at once.
            if (force || !sameState) _skipTicks = 0;

            // The early return is what makes a repeated tick free, and it used to swallow the retry as well: one
            // transient read failure on battery left the panel at its full rate for the rest of the session, because
            // the power state never changed again and nothing ever asked a second time.
            if (!force && sameState && !_savedRate.HasValue && !_lastFailed) return RefreshRateResult.Nothing();

            // A retry that is not due yet.
            if (_lastFailed && _skipTicks > 0)
            {
                _skipTicks--;
                return RefreshRateResult.Nothing();
            }

            // Switched off while the panel was already lowered: put it back rather than leaving the user at 60 Hz with
            // nothing left to raise it until the adapter returns.
            if (!_settings.AutoRefreshRateSwitching) return Remember(RestoreLocked(), retry: _savedRate.HasValue);

            if (onAc) return Remember(RestoreLocked(), retry: _savedRate.HasValue);

            // Already lowered. A second drop would overwrite the saved rate with 60 and lose the user's own value.
            if (_savedRate.HasValue) return RefreshRateResult.Nothing(_display.GetCurrentRefreshRate());

            int current = _display.GetCurrentRefreshRate();

            // No internal panel right now (lid closed on an external monitor) is not a failure: there is nothing to
            // lower. It is looked at again later, on the back-off, in case the panel comes back on the same battery.
            if (current <= 0) return Remember(RefreshRateResult.Nothing(), retry: true);

            // A panel already at or below 60 Hz has nothing to save: restoring it later would be a no-op that still
            // counted as a change.
            if (current <= BatteryRefreshRate) return Remember(RefreshRateResult.Nothing(current));

            if (!_display.SetRefreshRate(BatteryRefreshRate))
            {
                return Remember(new RefreshRateResult(RefreshRateAction.Failed, current, current));
            }

            _savedRate = current;
            _settings.RefreshRateBeforeBattery = current;
            return Remember(new RefreshRateResult(RefreshRateAction.LoweredToBattery, current, BatteryRefreshRate));
        }
    }

    /// <summary>
    /// Puts the user's rate back without consulting the power state. Used on a clean exit; a rate that cannot be put
    /// back now stays in <see cref="AppSettings.RefreshRateBeforeBattery"/> for the next start.
    /// </summary>
    public RefreshRateResult Restore()
    {
        lock (_gate) return RestoreLocked();
    }

    /// <summary>
    /// Records whether the evaluation has to be tried again, and when. <paramref name="retry"/> marks an answer that is
    /// not a failure but is not final either (no panel right now).
    /// </summary>
    private RefreshRateResult Remember(RefreshRateResult result, bool retry = false)
    {
        _lastFailed = retry || result.Action == RefreshRateAction.Failed;
        if (!_lastFailed)
        {
            _failures = 0;
            _skipTicks = 0;
            return result;
        }

        _failures++;
        _skipTicks = _failures <= 1 ? 0 : Math.Min(1 << Math.Min(_failures - 1, 9), MaxRetrySkip);
        return result;
    }

    private RefreshRateResult RestoreLocked()
    {
        if (!_savedRate.HasValue) return RefreshRateResult.Nothing();

        int saved = _savedRate.Value;
        int current = _display.GetCurrentRefreshRate();

        // The panel is not there (lid closed on an external monitor): nothing failed, and the saved rate waits for it.
        if (current <= 0) return RefreshRateResult.Nothing();

        // The panel is no longer at the battery rate: someone chose another rate since (in Windows, perhaps while the
        // app was closed after a run that ended on battery). That choice is newer than the saved rate, so the saved one
        // is dropped without a write rather than forced over it.
        if (current > BatteryRefreshRate)
        {
            _savedRate = null;
            _settings.RefreshRateBeforeBattery = null;
            return RefreshRateResult.Nothing(current);
        }

        if (!_display.SetRefreshRate(saved))
        {
            // The saved rate is kept: the driver may refuse while the lid is closed, and the next evaluation should
            // try again rather than forget what the user had.
            return new RefreshRateResult(RefreshRateAction.Failed, current, current);
        }

        _savedRate = null;
        _settings.RefreshRateBeforeBattery = null;
        return new RefreshRateResult(RefreshRateAction.RestoredToMaximum, current, saved);
    }
}
