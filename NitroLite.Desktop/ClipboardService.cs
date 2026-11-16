namespace NitroLite.Desktop;

/// <summary>
/// The one platform service the view model needs: putting text on the clipboard.
/// </summary>
/// <remarks>
/// A delegate rather than an interface because there is exactly one call. The app sets it to Avalonia's clipboard at
/// startup; a test leaves it null and the copy command becomes a no-op instead of needing a display.
/// </remarks>
public static class ClipboardService
{
    /// <summary>Set once by the app. Null means "no clipboard here", which is not an error.</summary>
    public static Action<string>? Setter { get; set; }

    public static void SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Setter?.Invoke(text);
    }
}
