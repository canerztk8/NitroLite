using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NitroLite.Core;
using NitroLite.Desktop.Views;

namespace NitroLite.Desktop;

/// <summary>
/// The two questions the window asks, answered by Avalonia.
/// </summary>
/// <remarks>
/// <para>
/// The file picker goes through <see cref="IStorageProvider"/> rather than a Win32 dialog, so the same code opens the
/// GTK picker on Linux and the shell picker on Windows. The confirmation is the app's own window, because Avalonia has
/// no message box and the system one would be the only control that ignores the palette.
/// </para>
/// <para>
/// The owner window is a delegate rather than a field: the composition root builds the view model before the window
/// exists, and a dialog service that captured a null owner would fail on the first click.
/// </para>
/// </remarks>
public sealed class AvaloniaDialogService : IDialogService
{
    private readonly Func<Window?> _owner;

    /// <param name="owner">The window a dialog is centred on. Called on every use, so it may be null at construction.</param>
    public AvaloniaDialogService(Func<Window?> owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>Asks for an executable. Returns the chosen path, or null when the user cancelled.</summary>
    public async Task<string?> PickExecutableAsync(
        string? startDirectory = null, CancellationToken cancellationToken = default)
    {
        var owner = _owner();
        if (owner == null) return null;

        var storage = owner.StorageProvider;
        if (storage == null || !storage.CanOpen) return null;

        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startDirectory) && Directory.Exists(startDirectory))
        {
            try { start = await storage.TryGetFolderFromPathAsync(startDirectory).ConfigureAwait(true); }
            catch (Exception ex) { AppLog.Warn("Başlangıç klasörü açılamadı", ex.Message); }
        }

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.T("Oyun çalıştırılabilir dosyasını seç"),
                AllowMultiple = false,
                SuggestedStartLocation = start,
                FileTypeFilter = [new FilePickerFileType(Loc.T("Çalıştırılabilir dosyalar"))
                {
                    Patterns = ["*.exe", "*"]
                }]
            }).ConfigureAwait(true);

            var file = files.Count > 0 ? files[0] : null;
            if (file == null) return null;

            // A picked file can be a virtual location with no local path; the profile needs a real one.
            return file.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Dosya seçici açılamadı", ex.ToString());
            return null;
        }
    }

    /// <summary>Asks for any file to read. Returns the chosen path, or null when the user cancelled.</summary>
    public async Task<string?> PickFileAsync(
        string title, string? extension = null, CancellationToken cancellationToken = default)
    {
        var owner = _owner();
        if (owner == null) return null;

        var storage = owner.StorageProvider;
        if (storage == null || !storage.CanOpen) return null;

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = BuildFilter(extension)
            }).ConfigureAwait(true);

            var file = files.Count > 0 ? files[0] : null;
            return file?.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Dosya seçici açılamadı", ex.ToString());
            return null;
        }
    }

    /// <summary>Asks where to write a file. Returns the chosen path, or null when the user cancelled.</summary>
    public async Task<string?> SaveFileAsync(
        string title, string suggestedName, string? extension = null, CancellationToken cancellationToken = default)
    {
        var owner = _owner();
        if (owner == null) return null;

        var storage = owner.StorageProvider;
        if (storage == null || !storage.CanSave) return null;

        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
                FileTypeChoices = BuildFilter(extension)
            }).ConfigureAwait(true);

            return file?.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kaydetme penceresi açılamadı", ex.ToString());
            return null;
        }
    }

    /// <summary>
    /// The filter list for one extension. A null extension offers every file, which is what the restore dialog needs:
    /// a backup renamed by a mail client still has to be selectable.
    /// </summary>
    private static List<FilePickerFileType>? BuildFilter(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;

        var pattern = "*." + extension.TrimStart('.');
        return [new FilePickerFileType(pattern) { Patterns = [pattern] }];
    }

    /// <summary>Asks a yes/no question. Returns true only when the user agreed.</summary>
    public async Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        var owner = _owner();
        var (confirm, cancel) = ConfirmDialog.DefaultLabels();
        var dialog = new ConfirmDialog(title, message, confirm, cancel);

        if (owner == null)
        {
            // No window to own the dialog: the only safe answer to "delete this?" is no.
            AppLog.Warn("Onay penceresi sahibi bulunamadı", title);
            return false;
        }

        try
        {
            return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Onay penceresi açılamadı", ex.ToString());
            return false;
        }
    }
}
