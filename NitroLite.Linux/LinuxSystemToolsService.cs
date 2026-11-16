using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// The repair and optimisation tools, which are Windows tools.
/// </summary>
/// <remarks>
/// Every one of them edits the registry, the service control manager or a scheduled task. There is nothing to repair
/// on a Linux desktop, so the card hides itself instead of offering five buttons that would each report "unsupported".
/// </remarks>
public sealed class LinuxSystemToolsService : ISystemToolsService
{
    public bool IsSupported => false;

    public Task<ToolResult> FixKeyboardLightingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolResult.Unsupported);

    public Task<ToolResult> FixAcerServicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolResult.Unsupported);

    public Task<ToolResult> RestoreMuxSwitchAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolResult.Unsupported);

    public Task<ToolResult> OptimizeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolResult.Unsupported);

    public Task<ToolResult> RestoreOptimizeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolResult.Unsupported);
}
