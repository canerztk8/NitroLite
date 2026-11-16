namespace NitroLite.Core;

/// <summary>
/// The mouse's own onboard profile, when the machine has a mouse that keeps one.
/// </summary>
/// <remarks>
/// <para>
/// The Nitro 16 ships with an ATK mouse that stores four configurations in its own firmware. Switching between them
/// is a USB control transfer, not a Windows call, so the shared window can offer the choice on any platform that
/// supplies an implementation and hide the row everywhere else.
/// </para>
/// <para>
/// <see cref="IsSupported"/> is separate from "the implementation exists" on purpose: the service is created on every
/// Windows machine, but only answers true once it has found a device. A row that appeared because the class was
/// constructed would offer four buttons that write nowhere.
/// </para>
/// </remarks>
public interface IMouseProfileService
{
    /// <summary>True when a supported mouse answered on the USB bus.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// The profile the mouse is in right now, or 0 when it is not known. 0 means "leave it alone"; 1 to 4 are the
    /// mouse's own configurations.
    /// </summary>
    int CurrentProfile { get; }

    /// <summary>
    /// Switches the mouse to <paramref name="profile"/>. False when the device refused or was not found.
    /// </summary>
    Task<bool> ApplyProfileAsync(int profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the mouse which profile it is in. Null when it cannot say, which is different from "profile 0": a mouse
    /// that has never been asked must not be reported as deliberately passive.
    /// </summary>
    Task<int?> QueryCurrentProfileAsync(CancellationToken cancellationToken = default);
}
