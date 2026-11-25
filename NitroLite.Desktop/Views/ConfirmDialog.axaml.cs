using Avalonia.Controls;
using Avalonia.Interactivity;
using NitroLite.Core;

namespace NitroLite.Desktop.Views;

/// <summary>
/// The yes/no question the window asks before it deletes something.
/// </summary>
/// <remarks>
/// A window of its own rather than a <c>MessageBox</c>: Avalonia has none, and the system dialog on Windows would be
/// the one control in the app that ignores the palette. The two captions are passed in already translated, because the
/// caller owns the language and this window has no view model.
/// </remarks>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Builds the dialog. <paramref name="confirmLabel"/> is the destructive verb ("Sil"), so the button says what it
    /// will do rather than "Tamam".
    /// </summary>
    /// <param name="destructive">
    /// True (every caller today: the confirm button is drawn in the danger colour and says "Sil") puts Enter on Cancel,
    /// so a stray Enter never deletes anything; Escape cancels either way, and a focused button still answers Enter
    /// itself. False puts Enter on the confirm button, as a system dialog does.
    /// </param>
    public ConfirmDialog(string title, string message, string confirmLabel, string cancelLabel, bool destructive = true)
        : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
        CancelButton.Content = cancelLabel;

        bool enterConfirms = EnterConfirms(destructive);
        ConfirmButton.IsDefault = enterConfirms;
        CancelButton.IsDefault = !enterConfirms;
    }

    /// <summary>Whether Enter presses the confirm button: only for a confirmation that destroys nothing.</summary>
    public static bool EnterConfirms(bool destructive) => !destructive;

    /// <summary>True when the user agreed. A window closed with Escape or the title bar is a no.</summary>
    public bool Confirmed { get; private set; }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>The labels a confirmation uses, so the caller does not have to spell them out.</summary>
    public static (string Confirm, string Cancel) DefaultLabels() =>
        (Loc.T("Sil"), Loc.T("Vazgeç"));
}
