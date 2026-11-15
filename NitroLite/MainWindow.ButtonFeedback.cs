using System.Windows.Controls;
using NitroLite.Core;
using WpfButton = System.Windows.Controls.Button;

namespace NitroLite;

/// <summary>
/// In-button feedback for the one-shot system actions: repair, optimise, back up, clean the standby list. These run
/// for seconds behind a modal confirmation and then report through a toast that has already faded by the time the
/// user looks back at the button they pressed. Showing the outcome on that button closes the loop where the eye
/// already is.
/// </summary>
public partial class MainWindow
{
    /// <summary>How long "✓ Tamamlandı" / "! Başarısız" stays before the label goes back. Shortened by tests.</summary>
    internal TimeSpan ButtonFeedbackHold { get; set; } = TimeSpan.FromSeconds(1.8);

    /// <summary>
    /// One button's feedback run. The token is what makes a second press safe: the first run's restore is cancelled
    /// so two timers can never race over the same label and leave "Uygulanıyor…" on an idle button.
    /// </summary>
    private sealed class ButtonFeedbackState
    {
        public ButtonFeedbackState(string sourceText, bool wasEnabled, object? originalContent)
        {
            SourceText = sourceText;
            WasEnabled = wasEnabled;
            OriginalContent = originalContent;
        }

        /// <summary>The Turkish catalogue key the button was built from, so the restore follows a language change.</summary>
        public string SourceText { get; }

        public bool WasEnabled { get; }

        public object? OriginalContent { get; }

        public CancellationTokenSource Cts { get; } = new();
    }

    private readonly Dictionary<WpfButton, ButtonFeedbackState> _buttonFeedback = new();

    /// <summary>
    /// Runs <paramref name="action"/> with its progress and outcome shown on <paramref name="button"/>: disabled and
    /// "Uygulanıyor…" while it runs, then "✓ Tamamlandı" or "! Başarısız" for <see cref="ButtonFeedbackHold"/>, then
    /// the original label and enabled state back.
    /// </summary>
    /// <param name="sourceText">
    /// The Turkish text the button was built from. The restore goes through <see cref="Loc.T"/>, so a language change
    /// during the hold still puts the right label back.
    /// </param>
    /// <returns>What <paramref name="action"/> reported, so a caller can chain on it.</returns>
    internal async Task<bool> RunWithButtonFeedbackAsync(WpfButton? button, string sourceText, Func<Task<bool>> action)
    {
        if (button == null)
        {
            // No button to show progress on, but the caller is still an async void handler: an exception escaping
            // here would take the process down instead of ending the action the way the button path does.
            try { return await action(); }
            catch (Exception ex) { AppLog.Error("Buton işlemi", ex.ToString()); return false; }
        }

        // A second press while the first outcome is still on screen. The state to go back to is the one from before
        // the first press: the button is disabled and labelled by the feedback itself right now, so capturing the
        // live values would restore "disabled" and leave the button dead for the rest of the session.
        _buttonFeedback.TryGetValue(button, out var previous);
        var state = new ButtonFeedbackState(
            sourceText,
            previous?.WasEnabled ?? button.IsEnabled,
            previous?.OriginalContent ?? button.Content);
        CancelButtonFeedback(button);
        _buttonFeedback[button] = state;
        var token = state.Cts.Token;

        button.IsEnabled = false;
        SetButtonFeedbackText(button, Loc.T("Uygulanıyor…"));

        bool ok;
        try
        {
            ok = await action();
        }
        catch (Exception ex)
        {
            // The action is expected to handle its own failures; one that escapes must still end the feedback rather
            // than leave the button stuck on "Uygulanıyor…" for the rest of the session.
            AppLog.Error("Buton işlemi", ex.ToString());
            ok = false;
        }

        try
        {
            if (token.IsCancellationRequested) return ok;

            // The action's own busy handling re-enables the button in its finally; the feedback owns it until it ends.
            button.IsEnabled = false;
            SetButtonFeedbackText(button, ok ? Loc.T("✓ Tamamlandı") : Loc.T("! Başarısız"));
            await Task.Delay(ButtonFeedbackHold, token);
        }
        catch (OperationCanceledException)
        {
            return ok;
        }

        RestoreButtonFeedback(button, state);
        return ok;
    }

    /// <summary>
    /// Puts every button back to its own label. Called when the language changes: the hold text is already localized,
    /// but a label restored from a stale capture would not be.
    /// </summary>
    internal void RestoreAllButtonFeedback()
    {
        foreach (var (button, state) in _buttonFeedback.ToList())
        {
            CancelButtonFeedback(button);
            RestoreButtonFeedback(button, state);
        }
    }

    /// <summary>True while <paramref name="button"/> is showing feedback rather than its own label.</summary>
    internal bool IsButtonFeedbackActiveForTesting(WpfButton button) => _buttonFeedback.ContainsKey(button);

    /// <summary>The label <paramref name="button"/> is showing right now.</summary>
    internal string? ButtonFeedbackTextForTesting(WpfButton button) => button.Content as string;

    private void CancelButtonFeedback(WpfButton button)
    {
        if (!_buttonFeedback.Remove(button, out var previous)) return;
        try { previous.Cts.Cancel(); } catch { }
        try { previous.Cts.Dispose(); } catch { }
    }

    private void RestoreButtonFeedback(WpfButton button, ButtonFeedbackState state)
    {
        if (_buttonFeedback.TryGetValue(button, out var current) && ReferenceEquals(current, state))
        {
            _buttonFeedback.Remove(button);
        }

        // A string label is restored through the catalogue so it follows the current language; anything richer (a
        // panel, an icon) is put back exactly as it was.
        if (state.OriginalContent is string) SetButtonFeedbackText(button, Loc.T(state.SourceText));
        else button.Content = state.OriginalContent;

        button.IsEnabled = state.WasEnabled;
        try { state.Cts.Dispose(); } catch { }
    }

    private static void SetButtonFeedbackText(WpfButton button, string text)
    {
        if (button.Content is TextBlock block) block.Text = text;
        else button.Content = text;
    }
}
