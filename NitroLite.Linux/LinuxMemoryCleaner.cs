using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// Linux has no standby list to release, so the card hides itself instead of offering a button that does nothing.
/// </summary>
/// <remarks>
/// The Windows standby list is a specific kernel structure with a specific tool that empties it. Linux's page cache is
/// dropped through <c>/proc/sys/vm/drop_caches</c>, which needs root and throws away every cached file — a different
/// operation with a different cost, not a port of this one. Reporting <see cref="IsSupported"/> false is the honest
/// answer, and the view model hides the whole card on it.
/// </remarks>
public sealed class LinuxMemoryCleaner : IMemoryCleaner
{
    public bool IsSupported => false;

    public Task<MemoryCleanResult> CleanStandbyMemoryAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(MemoryCleanResult.Unsupported);
}
