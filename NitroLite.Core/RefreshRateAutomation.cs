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
    /// True when the last evaluation failed. A failure is retried on the next tick instead of being remembered as
    /// "nothing to do".
    /// </summary>
    private bool _lastFailed;

    public RefreshRateAutomation(IDisplayRefreshRateService display, AppSettings settings)
    {
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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

            // The early return is what makes a repeated tick free, and it used to swallow the retry as well: one
            // transient read failure on battery left the panel at its full rate for the rest of the session, because
            // the power state never changed again and nothing ever asked a second time.
            if (!force && sameState && !_savedRate.HasValue && !_lastFailed) return RefreshRateResult.Nothing();

            // Switched off while the panel was already lowered: put it back rather than leaving the user at 60 Hz with
            // nothing left to raise it until the adapter returns.
            if (!_settings.AutoRefreshRateSwitching) return Remember(RestoreLocked());

            if (onAc) return Remember(RestoreLocked());

            // Already lowered. A second drop would overwrite the saved rate with 60 and lose the user's own value.
            if (_savedRate.HasValue) return RefreshRateResult.Nothing(_display.GetCurrentRefreshRate());

            int current = _display.GetCurrentRefreshRate();
            if (current <= 0) return Remember(new RefreshRateResult(RefreshRateAction.Failed, 0, 0));

            // A panel already at or below 60 Hz has nothing to save: restoring it later would be a no-op that still
            // counted as a change.
            if (current <= BatteryRefreshRate) return Remember(RefreshRateResult.Nothing(current));

            if (!_display.SetRefreshRate(BatteryRefreshRate))
            {
                return Remember(new RefreshRateResult(RefreshRateAction.Failed, current, current));
            }

            _savedRate = current;
            _lastFailed = false;
            return new RefreshRateResult(RefreshRateAction.LoweredToBattery, current, BatteryRefreshRate);
        }
    }

    /// <summary>Puts the user's rate back without consulting the power state. Used on a clean exit.</summary>
    public RefreshRateResult Restore()
    {
        lock (_gate) return RestoreLocked();
    }

    /// <summary>Records whether the evaluation failed, so the next tick knows to try again.</summary>
    private RefreshRateResult Remember(RefreshRateResult result)
    {
        _lastFailed = result.Action == RefreshRateAction.Failed;
        return result;
    }

    private RefreshRateResult RestoreLocked()
    {
        if (!_savedRate.HasValue) return RefreshRateResult.Nothing();

        int saved = _savedRate.Value;
        int current = _display.GetCurrentRefreshRate();

        if (!_display.SetRefreshRate(saved))
        {
            // The saved rate is kept: the driver may refuse while the lid is closed, and the next evaluation should
            // try again rather than forget what the user had.
            return new RefreshRateResult(RefreshRateAction.Failed, current, current);
        }

        _savedRate = null;
        return new RefreshRateResult(RefreshRateAction.RestoredToMaximum, current, saved);
    }
}
