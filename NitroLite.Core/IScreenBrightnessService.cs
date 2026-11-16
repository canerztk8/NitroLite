namespace NitroLite.Core;

/// <summary>Screen (panel backlight) brightness, the single largest power draw on a laptop.</summary>
/// <remarks>
/// The contract lives here, next to the school-mode state machine that is its only consumer, so the state machine
/// can be compiled and tested without the WMI implementation. The Windows implementation
/// (<c>ScreenBrightnessService</c>) lives in NitroLite.Windows because it talks to <c>WmiMonitorBrightness</c>.
/// </remarks>
public interface IScreenBrightnessService
{
    /// <summary>Current brightness in percent, or null when the panel does not expose it.</summary>
    int? GetBrightness();

    /// <summary>Sets the brightness in percent. False when the panel does not accept the command.</summary>
    bool SetBrightness(int percent);
}
