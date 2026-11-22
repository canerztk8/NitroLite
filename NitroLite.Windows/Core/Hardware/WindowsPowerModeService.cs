using System.Runtime.InteropServices;
using NitroLite.Core;
using NitroLite.Core.Hardware;

namespace NitroLite.Windows.Core.Hardware;

/// <summary>
/// The Windows power mode through the power-overlay calls Windows' own Settings page uses.
/// </summary>
/// <remarks>
/// <para>
/// <c>PowerSetActiveOverlayScheme</c> and <c>PowerGetEffectiveOverlayScheme</c> live in <c>powrprof.dll</c> from
/// Windows 10 1709 on. They are the calls behind the Power mode slider, take no elevation, and change nothing Windows
/// does not already let the user change. A build without them, or a power plan that disables the slider, answers null
/// or false here and the operating-mode control says so instead of pretending.
/// </para>
/// <para>
/// The three overlays are fixed GUIDs: "best power efficiency", "balanced" (the empty GUID: no overlay) and "best
/// performance".
/// </para>
/// </remarks>
public sealed class WindowsPowerModeService : IWindowsPowerMode
{
    private static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public WindowsPowerSlider? Read()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var overlay) != 0) return null;
            if (overlay == BestEfficiency) return WindowsPowerSlider.BestEfficiency;
            if (overlay == BestPerformance) return WindowsPowerSlider.BestPerformance;
            // The empty GUID is Balanced; any other overlay (an OEM's own) is closest to it too.
            return WindowsPowerSlider.Balanced;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    public bool Write(WindowsPowerSlider slider)
    {
        var overlay = slider switch
        {
            WindowsPowerSlider.BestEfficiency => BestEfficiency,
            WindowsPowerSlider.BestPerformance => BestPerformance,
            _ => Guid.Empty
        };

        try
        {
            var status = PowerSetActiveOverlayScheme(overlay);
            if (status == 0) return true;

            AppLog.Warn("The Windows power mode could not be changed",
                $"{slider}: PowerSetActiveOverlayScheme returned {status} (the active power plan may not allow it)");
            return false;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            AppLog.Warn("The Windows power mode could not be changed", $"this Windows build has no power mode: {ex.Message}");
            return false;
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);
}
