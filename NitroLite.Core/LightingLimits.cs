namespace NitroLite.Core;

/// <summary>
/// The keyboard lighting scales every backend shares, so the window can size a slider without knowing which platform
/// it is running on.
/// </summary>
/// <remarks>
/// These are the app's own units, not a backend's. Brightness is a level from 0 to 9 and speed from 1 to 9 on both
/// platforms; a backend that wants a percentage converts at its own boundary. Keeping the numbers here rather than in
/// one backend is what lets the same view model drive the WPF window and the Avalonia window.
/// </remarks>
public static class LightingLimits
{
    /// <summary>Zones the keyboard addresses. Four on every model that has a four-zone keyboard.</summary>
    public const int ZoneCount = 4;

    /// <summary>Highest brightness NitroLite stores, as a level.</summary>
    public const int MaxBrightnessLevel = 9;

    /// <summary>Highest effect speed.</summary>
    public const int MaxSpeed = 9;

    /// <summary>Highest direction. Left and right only; a vertical direction is clamped rather than rejected.</summary>
    public const int MaxDirection = 2;
}
