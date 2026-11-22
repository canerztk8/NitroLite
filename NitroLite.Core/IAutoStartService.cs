namespace NitroLite.Core;

/// <summary>
/// An optional sign-in launch that does not raise an elevation prompt.
/// </summary>
/// <remarks>
/// <para>
/// The Windows implementation registers a Task Scheduler entry with the highest privileges available to the user, so
/// the app starts elevated without a UAC dialog. That is the only way to get the direct-WMI path working at sign-in
/// without asking the user to click Yes every boot.
/// </para>
/// <para>
/// An interface rather than a direct call so the shared window can offer the switch on Windows and hide it everywhere
/// else: a platform that has no such mechanism simply does not supply an implementation, and the card disappears
/// instead of offering a control that cannot work.
/// </para>
/// </remarks>
/// <summary>A refusal the user can act on by moving NitroLite.</summary>
public enum AutoStartRefusal
{
    /// <summary>Not refused for its location.</summary>
    None,

    /// <summary>NitroLite runs from Downloads or a temporary folder: a file that will be gone or replaced.</summary>
    DownloadOrTempFolder,

    /// <summary>NitroLite runs from a folder other local accounts can write to, such as one directly under C:\.</summary>
    WritableFolder
}

public interface IAutoStartService
{
    /// <summary>True when the sign-in entry is registered right now.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Registers the entry. False when the user cancelled the prompt or the registration failed.</summary>
    Task<bool> EnableAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the entry. False when it could not be removed.</summary>
    Task<bool> DisableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Why the last change was refused, when the reason is one the user can fix by moving the app. None for every
    /// other outcome, and for a platform that never refuses on location.
    /// </summary>
    AutoStartRefusal LastRefusal => AutoStartRefusal.None;
}
