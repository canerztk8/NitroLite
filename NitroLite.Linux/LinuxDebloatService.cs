using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// The Acer background services, which do not exist on Linux.
/// </summary>
/// <remarks>
/// The Windows services this card manages are Windows services: there is no service control manager, no NitroSense
/// process and no scheduled task to disable. Reporting <see cref="IsSupported"/> false hides the card rather than
/// offering four buttons that would write nowhere.
/// </remarks>
public sealed class LinuxDebloatService : IDebloatService
{
    public bool IsSupported => false;

    public bool HasRestorableBackup => false;

    public IReadOnlyList<AcerServiceEntry> GetServiceStatuses() => Array.Empty<AcerServiceEntry>();

    public bool IsNitroSenseRunning() => false;

    public Task<DebloatResult> DisableBackgroundServicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DebloatResult.Unsupported);

    public Task<DebloatResult> RestoreServicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DebloatResult.Unsupported);

    public Task<DebloatResult> KillNitroSenseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DebloatResult.Unsupported);

    public bool IsNitroSenseLauncherBlocked => false;

    public Task<DebloatResult> BlockNitroSenseLauncherAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DebloatResult.Unsupported);

    public Task<DebloatResult> UnblockNitroSenseLauncherAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DebloatResult.Unsupported);
}
