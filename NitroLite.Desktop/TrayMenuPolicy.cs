using NitroLite.Core;

namespace NitroLite.Desktop;

/// <summary>
/// The rules the tray menu follows, kept out of <see cref="TrayIconHost"/> so they can be asserted without a display.
/// </summary>
/// <remarks>
/// The host itself needs a <c>TrayIcon</c>, which needs a platform; the decisions it makes do not. Everything here is
/// a pure function of what the view model already knows, which is what makes the menu's behaviour testable at all.
/// </remarks>
public static class TrayMenuPolicy
{
    /// <summary>
    /// The three CPU-boost rows, in the order the WPF submenu listed them: off, then the two levels the firmware
    /// understands. The order is the order of the enum, so the menu and the card cannot drift apart.
    /// </summary>
    public static IReadOnlyList<CpuBoostMode> BoostModes { get; } = new[]
    {
        CpuBoostMode.Disabled,
        CpuBoostMode.Efficient,
        CpuBoostMode.Aggressive
    };

    /// <summary>
    /// Whether a quick-mode row is drawn at all. A mode the firmware does not advertise is hidden, unless the machine
    /// is already in it: a menu with no marked row would leave the user guessing which mode is running.
    /// </summary>
    public static bool ShowMode(bool supported, OperatingMode mode, OperatingMode? active) =>
        supported || mode == active;

    /// <summary>
    /// Whether a drawn row can be clicked. A mode the battery rule forbids stays visible but greyed out, so the user
    /// can see that Turbo exists and why it is unavailable rather than wondering where it went.
    /// </summary>
    public static bool EnableMode(bool allowed) => allowed;

    /// <summary>The reason a greyed-out row is greyed out, or null when the row is clickable.</summary>
    public static string? ModeToolTip(bool allowed) => allowed ? null : ModePolicy.HighPowerBlockedMessage;

    /// <summary>The boost submenu only exists on a machine that can write the boost mode.</summary>
    public static bool ShowBoost(bool available) => available;

    /// <summary>
    /// The "try the desktop settings again" row: shown only while a restore is pending, and clickable only when no
    /// game is running, because a game session owns the mode and would overwrite the retry immediately.
    /// </summary>
    public static bool ShowRetry(bool pending) => pending;

    public static bool EnableRetry(bool pending, bool gameActive) => pending && !gameActive;
}
