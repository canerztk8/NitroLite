namespace NitroLite.Core.Hardware;

/// <summary>The three positions of the Windows power mode (Settings → System → Power → Power mode).</summary>
public enum WindowsPowerSlider
{
    /// <summary>"Best power efficiency".</summary>
    BestEfficiency,

    /// <summary>"Balanced".</summary>
    Balanced,

    /// <summary>"Best performance".</summary>
    BestPerformance
}

/// <summary>
/// Reads and writes the Windows power mode. Windows hands it to the platform's own power policy — on recent Intel
/// laptops the OEM ties it to the processor's power limits — so it is a real lever on a machine whose firmware refuses
/// Acer's operating-mode command. It needs no elevation and the user can change it back in Settings at any time.
/// </summary>
public interface IWindowsPowerMode
{
    /// <summary>The position Windows is in, or null when the power mode cannot be read (an old build, a custom plan).</summary>
    WindowsPowerSlider? Read();

    /// <summary>Moves the power mode. False when Windows refused, for example under a non-Balanced power plan.</summary>
    bool Write(WindowsPowerSlider slider);
}

/// <summary>
/// The mapping between NitroLite's operating modes and the Windows power mode. Pure, so it is pinned by tests.
/// </summary>
/// <remarks>
/// Windows has three positions and NitroLite five modes, so two pairs share a position: Quiet and Eco both mean
/// "best power efficiency", Performance and Turbo both mean "best performance". The modes offered on such a machine are
/// therefore the three that are distinct; a game profile that asks for Turbo still gets the fastest position.
/// </remarks>
public static class WindowsPowerModeMapping
{
    /// <summary>The modes a Windows-power-mode machine offers: one per position.</summary>
    public static IReadOnlyList<OperatingMode> Modes { get; } =
        new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme };

    /// <summary>The position a mode asks for.</summary>
    public static WindowsPowerSlider For(OperatingMode mode) => mode switch
    {
        OperatingMode.Quiet or OperatingMode.Eco => WindowsPowerSlider.BestEfficiency,
        OperatingMode.Extreme or OperatingMode.Turbo => WindowsPowerSlider.BestPerformance,
        _ => WindowsPowerSlider.Balanced
    };

    /// <summary>
    /// The mode a position means. <paramref name="lastRequested"/> wins when it maps to that position, so a game
    /// profile's Turbo is read back as Turbo rather than as the Performance it shares a position with.
    /// </summary>
    public static OperatingMode? ToMode(WindowsPowerSlider? slider, OperatingMode? lastRequested)
    {
        if (slider is not { } position) return null;
        if (lastRequested is { } last && For(last) == position) return last;

        return position switch
        {
            WindowsPowerSlider.BestEfficiency => OperatingMode.Quiet,
            WindowsPowerSlider.BestPerformance => OperatingMode.Extreme,
            _ => OperatingMode.Default
        };
    }
}
