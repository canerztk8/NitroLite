namespace NitroLite.Core;

/// <summary>
/// The two questions the window has to ask the user: pick a file, and confirm something destructive.
/// </summary>
/// <remarks>
/// <para>
/// An interface rather than a direct call because the view model has no Avalonia types in it — that is what lets the
/// whole window be constructed and asserted on in a unit test with no display attached. A test supplies a stub that
/// answers "yes" or "this path" and the flow is exercised end to end.
/// </para>
/// <para>
/// Both methods return a value rather than throwing on cancel: a cancelled file picker is not an error, and a
/// confirmation the user declined is a normal answer that the caller acts on.
/// </para>
/// </remarks>
public interface IDialogService
{
    /// <summary>
    /// Asks for an executable. Returns the chosen path, or null when the user cancelled.
    /// </summary>
    /// <param name="startDirectory">
    /// Where the picker opens. Null lets the platform choose, which is what a machine with no Steam library wants.
    /// </param>
    Task<string?> PickExecutableAsync(string? startDirectory = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for any file to read. Returns the chosen path, or null when the user cancelled.
    /// </summary>
    /// <param name="extension">
    /// The extension the picker filters on, without the dot. Null offers every file, which is what a restore dialog
    /// wants: a backup renamed by a mail client still has to be selectable.
    /// </param>
    Task<string?> PickFileAsync(string title, string? extension = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks where to write a file. Returns the chosen path, or null when the user cancelled.
    /// </summary>
    /// <param name="suggestedName">The name the picker starts with, so the user does not have to invent one.</param>
    /// <param name="extension">The extension appended when the user types a bare name, without the dot.</param>
    Task<string?> SaveFileAsync(
        string title, string suggestedName, string? extension = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks a yes/no question. Returns true only when the user agreed; a cancelled dialog is a no.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
}
