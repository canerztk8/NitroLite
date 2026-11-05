namespace NitroLite.Core;

/// <summary>
/// Abstraction for querying and setting the Windows primary display refresh rate.
/// Avoids direct P/Invoke calls during automated unit testing.
/// </summary>
public interface IDisplayRefreshRateService
{
    /// <summary>
    /// Gets the active display refresh rate in Hertz (e.g. 60 or 165).
    /// </summary>
    int GetCurrentRefreshRate();

    /// <summary>
    /// Discovers the maximum supported refresh rate in Hertz for the current screen resolution.
    /// Safely falls back to 60Hz if enumeration is unavailable.
    /// </summary>
    int GetMaxRefreshRate();

    /// <summary>
    /// Switches the display refresh rate to the specified frequency in Hertz.
    /// Uses Win32 ChangeDisplaySettingsEx with CDS_UPDATEREGISTRY | CDS_GLOBAL.
    /// Strictly operates at OS display mode level without touching GPU MUX or DDS registers.
    /// </summary>
    /// <param name="hz">Target refresh rate in Hz (e.g. 60 or 165).</param>
    /// <returns>True if the mode was successfully applied, false otherwise.</returns>
    bool SetRefreshRate(int hz);
}
