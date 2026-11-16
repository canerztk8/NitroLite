namespace NitroLite.Core;

/// <summary>
/// Behaviour of the keyboard mode-switch ("Turbo") key. AASSvc only reports the key press
/// (PRESS_TURBO_BUTTON event); NitroSense itself picks and applies the next mode, so without
/// NitroSense running the key does nothing unless NitroLite handles it.
/// </summary>
public static class ModeSwitchKey
{
    public const string PressEvent = "PRESS_TURBO_BUTTON";
    public const string BacklightTimeoutEvent = "KEYBOARD_BACKLIGHT_30_SEC_EVENT";

    /// <summary>
    /// Mirrors NitroSense: on the adapter Default → Extreme → Turbo → Quiet → Default; on battery Default ↔ Eco.
    /// When the high-power modes are not allowed (adapter with battery below 40 %), the AC cycle skips them.
    /// </summary>
    public static OperatingMode Next(OperatingMode current, bool onAc, int? batteryPercent = null)
    {
        if (!onAc)
        {
            return current == OperatingMode.Default ? OperatingMode.Eco : OperatingMode.Default;
        }

        if (!ModePolicy.IsHighPowerAllowed(onAc, batteryPercent))
        {
            return current == OperatingMode.Default ? OperatingMode.Quiet : OperatingMode.Default;
        }

        return current switch
        {
            OperatingMode.Default => OperatingMode.Extreme,
            OperatingMode.Extreme => OperatingMode.Turbo,
            OperatingMode.Turbo => OperatingMode.Quiet,
            OperatingMode.Quiet => OperatingMode.Default,
            _ => OperatingMode.Default // Eco or unknown → back to balanced
        };
    }
}
