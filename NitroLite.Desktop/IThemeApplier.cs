using NitroLite.Core;

namespace NitroLite.Desktop;

/// <summary>
/// Puts a palette on screen. The one platform service the view model needs for theming.
/// </summary>
/// <remarks>
/// An interface rather than a direct call into the Avalonia theme manager for the same reason
/// <see cref="ClipboardService"/> is a delegate: <see cref="ViewModels.MainViewModel"/> holds no Avalonia types, which
/// is what lets a test construct it and assert that picking a theme applied it and saved it, with no display attached.
/// </remarks>
public interface IThemeApplier
{
    /// <summary>Swaps the running window's palette. Never throws: a missing application is a no-op.</summary>
    void Apply(AppTheme theme);

    /// <summary>
    /// Swaps the running window's typeface. <paramref name="fontStack"/> is a comma-separated family list, because
    /// the tail of every stack is the script coverage the eight UI languages need. Never throws: a missing
    /// application is a no-op.
    /// </summary>
    void ApplyFont(string fontStack);
}
