namespace NitroLite.Core;

/// <summary>
/// The laptop's own Nitro key, as a source of one event: "the user pressed it".
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not a hotkey service. There is no key to choose, no modifier to record and no combination to
/// validate: the key is a physical button the machine already has, and the only thing the app can do with it is come
/// forward. A configurable shortcut would be a second, unrelated feature wearing the same name.
/// </para>
/// <para>
/// The two platforms reach the same button by different routes — Windows through the Acer WMI event class and raw
/// input, Linux through the <c>acer-wmi</c> input device in <c>/dev/input</c> — and both routes can report the same
/// physical press twice. <see cref="NitroKeyDebounceGuard"/> is what turns that into one event, and it lives in this
/// assembly so neither platform can forget it.
/// </para>
/// <para>
/// A machine without the key, or a process that may not read the device, is not an error: <see cref="IsSupported"/>
/// is false, the settings row hides itself, and nothing else changes.
/// </para>
/// </remarks>
public interface INitroKeyListener : IDisposable
{
    /// <summary>
    /// True when this machine has a Nitro key this process can actually hear. False is the normal answer on a desktop,
    /// on a non-Acer laptop, and on a Linux install where the user is not in the <c>input</c> group.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Raised once per physical press, on a background thread. A subscriber that touches the UI has to marshal.
    /// </summary>
    event EventHandler? NitroKeyPressed;

    /// <summary>Begins listening. Safe to call twice; the second call does nothing.</summary>
    void Start();

    /// <summary>Stops listening and releases the device. Safe to call when never started.</summary>
    void Stop();
}
