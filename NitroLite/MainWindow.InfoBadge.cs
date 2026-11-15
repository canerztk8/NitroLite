using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace NitroLite;

public partial class MainWindow
{
    // ---------------------------------------------------------------------------------------------------------------
    // The (?) badges. A hover already shows the text through the tooltip; a click shows the same text in a popup,
    // because a badge that does nothing when clicked reads as broken even when the tooltip works.
    // ---------------------------------------------------------------------------------------------------------------

    private Popup? _infoPopup;
    private Border? _infoPopupCard;

    /// <summary>
    /// Opens the badge's own tooltip text in a themed popup under it. Wired from the <c>InfoBadge</c> style with an
    /// <c>EventSetter</c>, so every badge in the window answers a click without a handler of its own.
    /// </summary>
    private void InfoBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement badge) return;

        var text = badge.ToolTip as string;
        if (string.IsNullOrWhiteSpace(text))
        {
            // A badge with no tooltip has nothing to say; do not open an empty box over the page.
            CloseInfoPopup();
            return;
        }

        // A second click on the same badge closes it, which is what a toggle is expected to do.
        if (_infoPopup is { IsOpen: true } && ReferenceEquals(_infoPopup.PlacementTarget, badge))
        {
            CloseInfoPopup();
            return;
        }

        ShowInfoPopup(badge, text);
        e.Handled = true;
    }

    private void ShowInfoPopup(FrameworkElement badge, string text)
    {
        CloseInfoPopup();

        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            MaxWidth = 300
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "InkSoft");

        _infoPopupCard = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 8, 11, 9),
            Child = label,
            // A drop shadow is what separates the card from the page it floats over; without it the popup reads as
            // part of the layout rather than as something on top of it.
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.28
            }
        };
        _infoPopupCard.SetResourceReference(Border.BackgroundProperty, "CardBg");
        _infoPopupCard.SetResourceReference(Border.BorderBrushProperty, "RuleStrong");

        _infoPopup = new Popup
        {
            PlacementTarget = badge,
            Placement = PlacementMode.Bottom,
            // A popup that stays open after a click elsewhere would float over other applications.
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = _infoPopupCard,
            HorizontalOffset = -8,
            VerticalOffset = 4
        };

        // Esc closes it. The popup is not in the window's focus scope, so the key has to be caught on the card.
        _infoPopupCard.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                CloseInfoPopup();
                args.Handled = true;
            }
        };

        _infoPopup.Closed += (_, _) => _infoPopup = null;
        _infoPopup.IsOpen = true;
    }

    private void CloseInfoPopup()
    {
        if (_infoPopup == null) return;
        _infoPopup.IsOpen = false;
        _infoPopup = null;
        _infoPopupCard = null;
    }

    /// <summary>
    /// Closes the popup when the window moves or resizes: a popup is positioned in screen coordinates, so it would
    /// otherwise stay where the badge used to be.
    /// </summary>
    private void OnWindowGeometryChangedForInfoPopup(object? sender, EventArgs e) => CloseInfoPopup();
}
