namespace NitroLite.Core;

/// <summary>
/// The optional direct-WMI backend: whether it is running, and the one button that starts it.
/// </summary>
/// <remarks>
/// <para>
/// The Acer service is the default path and needs no privilege. Direct WMI is the fallback for a machine whose service
/// is missing or too old, and it needs an elevated helper, which is why starting it is a button rather than something
/// the app does on its own: a UAC prompt at launch is a prompt the user did not ask for.
/// </para>
/// <para>
/// A platform with no such backend supplies no implementation, and the row disappears instead of offering a button
/// that cannot work.
/// </para>
/// </remarks>
public interface IDirectWmiControl
{
    /// <summary>True when the elevated helper is connected right now.</summary>
    bool IsAvailable { get; }

    /// <summary>One line describing which backend is live, in the user's language.</summary>
    string StatusText { get; }

    /// <summary>
    /// True when the elevated helper was refused because NitroLite runs from a folder other accounts can write to.
    /// The hardware controls stay dark until the folder changes or the app is started as administrator, so the
    /// dashboard says so instead of leaving the reason in a settings row nobody opens.
    /// </summary>
    bool HelperFolderRefused => false;

    /// <summary>
    /// Starts the helper. False when the user declined the prompt or the helper failed to start; declining is a
    /// legitimate answer, not an error.
    /// </summary>
    Task<bool> StartAsync(CancellationToken cancellationToken = default);
}
