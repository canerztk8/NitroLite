namespace NitroLite.Core.Hardware;

/// <summary>
/// A backend that may start an elevated helper to write the keyboard lighting, and can be told not to.
/// </summary>
/// <remarks>
/// The automatic re-assertions — start-up, resume, the window coming back — must never raise a UAC prompt: a prompt
/// at login for a colour nobody asked to change is exactly what makes people distrust a tray app. Those writes turn the
/// switch off for their duration and use only a transport that is already open; the writes the user asks for leave it
/// on.
/// </remarks>
public interface ILightingElevationControl
{
    /// <summary>True when a lighting write may start the elevated helper.</summary>
    bool AllowElevationForLighting { get; set; }
}
