using System.Windows;

namespace NitroLite.Core;

/// <summary>
/// The title bar's update notice: the "update now" badge and the release-notes button beside it.
/// </summary>
/// <remarks>
/// The two are one notice, not two controls. The notes button only means anything while there is a release to read
/// about, so it appears and disappears with the badge; a caller that moved one and forgot the other would leave a
/// button pointing at a release the window no longer mentions. Every visibility change goes through
/// <see cref="Apply"/> for that reason, and the lifecycle is pinned by ReleaseNotesButtonTests.
/// </remarks>
public static class UpdateNotice
{
    /// <summary>Visible while a newer release is pending, collapsed otherwise.</summary>
    public static Visibility VisibilityFor(UpdateInfo? pending) =>
        pending == null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Shows or hides both halves of the notice. Either element may be null: the test harness builds a window
    /// without its XAML, so the named fields are not there.
    /// </summary>
    public static void Apply(UIElement? updateButton, UIElement? releaseNotesButton, UpdateInfo? pending)
    {
        var visibility = VisibilityFor(pending);
        if (updateButton != null) updateButton.Visibility = visibility;
        if (releaseNotesButton != null) releaseNotesButton.Visibility = visibility;
    }
}
