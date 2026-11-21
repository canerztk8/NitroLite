namespace NitroLite.Core;

/// <summary>
/// What the desktop the app runs on can show, decided once at start-up by the platform layer.
/// </summary>
/// <remarks>
/// The tray is the case that matters. On Windows there is always one. On Linux the icon goes to the desktop's
/// StatusNotifier host, and stock GNOME has none unless the AppIndicator extension is installed (Ubuntu ships it,
/// Fedora and Debian do not). Without a host, hiding the window to the tray would hide the app completely, so the
/// window minimises instead and "start in the tray" opens the window.
/// </remarks>
public static class DesktopShell
{
    /// <summary>True when a tray icon will be visible. Defaults to true; the Linux probe lowers it.</summary>
    public static bool TrayAvailable { get; set; } = true;
}
