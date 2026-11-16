using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// The mouse-profile service for a platform with no ATK mouse.
/// </summary>
/// <remarks>
/// <para>
/// A no-op rather than a null reference: the shared window asks every platform the same question, and a null would put
/// a branch in the view model for a platform that simply has no such device. <see cref="IsSupported"/> answers false,
/// which is what hides the row.
/// </para>
/// <para>
/// The ATK mouse is a USB HID device and the protocol is a vendor report, so a Linux implementation would need a
/// <c>hidraw</c> backend and a udev rule of its own. Until that exists, saying "not supported" is the honest answer.
/// </para>
/// </remarks>
public sealed class NullMouseProfileService : IMouseProfileService
{
    /// <summary>Always false: this platform has no mouse-profile backend.</summary>
    public bool IsSupported => false;

    /// <summary>Always 0, which means "leave it alone".</summary>
    public int CurrentProfile => 0;

    /// <summary>Always false: there is nothing to switch.</summary>
    public Task<bool> ApplyProfileAsync(int profile, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>Always null: there is nothing to ask.</summary>
    public Task<int?> QueryCurrentProfileAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult((int?)null);
}
