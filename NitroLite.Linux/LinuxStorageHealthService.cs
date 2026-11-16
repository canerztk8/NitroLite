using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// The drives, as far as a Linux desktop can see them without root.
/// </summary>
/// <remarks>
/// <para>
/// <c>/sys/block</c> lists the devices and <c>/proc/mounts</c> maps them to mount points, but the two facts the card
/// exists for — the S.M.A.R.T. verdict and the drive temperature — need <c>smartctl</c> with elevated rights on most
/// distributions. A list without them would be a table of blanks, so this implementation reports
/// <see cref="IsSupported"/> false and the card hides itself.
/// </para>
/// <para>
/// The free-space half is available through <see cref="DriveInfo"/>, and it is not offered on its own because a
/// storage card that shows capacity but never health reads as a broken health card.
/// </para>
/// </remarks>
public sealed class LinuxStorageHealthService : IStorageHealthService
{
    public bool IsSupported => false;

    public IReadOnlyList<StorageDrive> GetDrives() => Array.Empty<StorageDrive>();
}
