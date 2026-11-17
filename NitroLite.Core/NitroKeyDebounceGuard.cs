using System.Diagnostics;

namespace NitroLite.Core;

/// <summary>
/// Collapses the same physical Nitro-key press into one event.
/// </summary>
/// <remarks>
/// <para>
/// The key is reachable through more than one route on both platforms, and the routes do not agree on how many reports
/// one press produces. On Windows the Acer WMI event class and the raw-input HID report can both fire for a single
/// press; on Linux the kernel emits a press and a release, and some <c>acer-wmi</c> builds emit the press twice. Two
/// activations of the window in the same 400 ms is invisible to the user, but two <em>mode</em> changes would not be,
/// and the guard is the one place that decides.
/// </para>
/// <para>
/// The window is measured from the last accepted press, not from the last rejected one, so holding the key down does
/// not extend the suppression indefinitely: a second deliberate press 400 ms after the first is a second press.
/// </para>
/// <para>
/// The clock is injectable because a test that had to sleep 400 ms to prove a debounce would be a test nobody runs.
/// </para>
/// </remarks>
public sealed class NitroKeyDebounceGuard
{
    /// <summary>The suppression window. Long enough to swallow a duplicate report, short enough to feel immediate.</summary>
    public const int DefaultWindowMilliseconds = 400;

    private readonly long _windowTicks;
    private readonly Func<long> _clock;
    private long _lastAcceptedTicks = long.MinValue;

    /// <param name="windowMilliseconds">The suppression window; <see cref="DefaultWindowMilliseconds"/> when omitted.</param>
    /// <param name="clock">
    /// A monotonic timestamp in <see cref="Stopwatch"/> ticks. Defaults to <see cref="Stopwatch.GetTimestamp"/>.
    /// </param>
    public NitroKeyDebounceGuard(int windowMilliseconds = DefaultWindowMilliseconds, Func<long>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(windowMilliseconds);

        WindowMilliseconds = windowMilliseconds;
        _windowTicks = (long)(windowMilliseconds / 1000.0 * Stopwatch.Frequency);
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    /// <summary>The window this guard was built with, in milliseconds.</summary>
    public int WindowMilliseconds { get; }

    /// <summary>
    /// True when this report is a new press. False when it is a duplicate of one already accepted inside the window.
    /// </summary>
    public bool ShouldAccept()
    {
        long now = _clock();

        // The first press always passes. The sentinel is checked before the subtraction because long.MinValue would
        // overflow it, and an overflowed difference reads as a very recent press.
        if (_lastAcceptedTicks != long.MinValue && now - _lastAcceptedTicks < _windowTicks) return false;

        _lastAcceptedTicks = now;
        return true;
    }

    /// <summary>Forgets the last press, so the next report is accepted whatever the clock says.</summary>
    public void Reset() => _lastAcceptedTicks = long.MinValue;
}
