namespace NitroLite.Core;

/// <summary>
/// Notices NitroSense coming to the front, which on an Acer laptop is what the Nitro key does.
/// </summary>
/// <remarks>
/// <para>
/// On some models the button never reaches a WMI event class or a raw-input collection: the Acer service stack owns
/// it and answers by launching NitroSense. The window appearing is then the only observable signal, and it is a good
/// one — nothing else on the machine opens that window.
/// </para>
/// <para>
/// The watcher only reports. Whether the window is closed and whether NitroLite comes forward is the shell's
/// decision, because that is where the setting lives and where the window is.
/// </para>
/// <para>
/// A machine without NitroSense, or a platform that cannot ask which window is in front, is not an error:
/// <see cref="IsSupported"/> is false and the settings row hides itself.
/// </para>
/// </remarks>
public interface INitroSenseWatcher : IDisposable
{
    /// <summary>True when this platform can watch for the window at all.</summary>
    bool IsSupported { get; }

    /// <summary>Raised on a background thread when NitroSense comes to the front.</summary>
    event EventHandler? NitroSenseActivated;

    /// <summary>Begins watching. Safe to call twice; the second call does nothing.</summary>
    void Start();

    /// <summary>Stops watching. Safe to call when never started.</summary>
    void Stop();

    /// <summary>
    /// Closes NitroSense if it is running. Returns true when a process was asked to close. Blocking, so a caller on
    /// the UI thread has to move it off.
    /// </summary>
    bool Close();

    /// <summary>
    /// Keeps closing NitroSense for the length of the window, because the Acer service launches it a moment after the
    /// key and a single close races that launch. Returns true when anything was closed. Blocking, so a caller on the
    /// UI thread has to move it off.
    /// </summary>
    bool CloseFor(TimeSpan window);
}
