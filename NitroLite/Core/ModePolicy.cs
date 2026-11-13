namespace NitroLite.Core;

/// <summary>
/// Operating-mode rules and display names shared by the mode cards, tray menu, mode key and game profiles.
/// </summary>
public static class ModePolicy
{
    /// <summary>NitroSense: Turbo/Performance need the AC adapter and at least 40 % battery.</summary>
    public const int MinBatteryPercentForHighPower = 40;

    internal const string HighPowerBlockedSource =
        "Turbo ve Performans modları yalnızca adaptör takılıyken ve pil en az %40 iken kullanılabilir (NitroSense kuralı).";

    public static string HighPowerBlockedMessage => Loc.T(HighPowerBlockedSource);

    public static bool IsHighPowerMode(OperatingMode mode) =>
        mode == OperatingMode.Turbo || mode == OperatingMode.Extreme;

    /// <param name="batteryPercent">0-100, or null when unknown / no battery (treated as allowed on AC).</param>
    public static bool IsHighPowerAllowed(bool onAc, int? batteryPercent) =>
        onAc && (!batteryPercent.HasValue || batteryPercent.Value >= MinBatteryPercentForHighPower);

    public static bool IsModeAllowed(OperatingMode mode, bool onAc, int? batteryPercent) =>
        !IsHighPowerMode(mode) || IsHighPowerAllowed(onAc, batteryPercent);

    /// <summary>Used for automatic changes (game profiles): a blocked high-power target falls back to Balanced.</summary>
    public static OperatingMode ClampForPower(OperatingMode mode, bool onAc, int? batteryPercent) =>
        IsModeAllowed(mode, onAc, batteryPercent) ? mode : OperatingMode.Default;

    public static string DisplayName(OperatingMode mode) => mode switch
    {
        OperatingMode.Quiet => Loc.T("Sessiz"),
        OperatingMode.Default => Loc.T("Dengeli"),
        OperatingMode.Extreme => Loc.T("Performans"),
        OperatingMode.Turbo => Loc.T("Turbo"),
        OperatingMode.Eco => Loc.T("Eco"),
        _ => Loc.T($"Mod {(int)mode}")
    };

    /// <summary>
    /// Windows PerfBoostMode names. Values NitroLite does not set itself (3, 5, 6) are shown by number instead of
    /// being mislabelled as Aggressive.
    /// </summary>
    public static string BoostDisplayName(CpuBoostMode mode) => (int)mode switch
    {
        0 => Loc.T("Devre Dışı"),
        1 => Loc.T("Windows Varsayılanı"),
        2 => Loc.T("Agresif"),
        4 => Loc.T("Verimli"),
        _ => Loc.T($"Diğer (Windows değeri {(int)mode})")
    };

    public static string FanDisplayName(FanMode mode) => mode switch
    {
        FanMode.Auto => Loc.T("Otomatik"),
        FanMode.Max => Loc.T("Maksimum"),
        FanMode.Custom => Loc.T("Özel"),
        _ => Loc.T($"Fan {(int)mode}")
    };
}
