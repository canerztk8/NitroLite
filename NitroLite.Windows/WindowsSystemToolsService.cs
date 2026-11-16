using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The repair and optimisation tools, through <see cref="SystemToolsService"/>.
/// </summary>
/// <remarks>
/// The service is static and already returns a <see cref="ToolExecutionResult"/> with a sentence in it, so this is a
/// straight translation into the shared shape. The elevation prompt is raised inside the service, which is why every
/// method here can come back with a failure that is a user decision rather than an error.
/// </remarks>
public sealed class WindowsSystemToolsService : ISystemToolsService
{
    public bool IsSupported => true;

    public Task<ToolResult> FixKeyboardLightingAsync(CancellationToken cancellationToken = default) =>
        RunAsync(SystemToolsService.FixKeyboardLightingAsync);

    public Task<ToolResult> FixAcerServicesAsync(CancellationToken cancellationToken = default) =>
        RunAsync(SystemToolsService.FixAcerServicesAsync);

    public Task<ToolResult> RestoreMuxSwitchAsync(CancellationToken cancellationToken = default) =>
        RunAsync(SystemToolsService.RestoreMuxSwitchAsync);

    public Task<ToolResult> OptimizeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(SystemToolsService.OptimizePcAsync);

    public Task<ToolResult> RestoreOptimizeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(SystemToolsService.RestoreOptimizePcAsync);

    private static async Task<ToolResult> RunAsync(Func<Task<ToolExecutionResult>> run)
    {
        try
        {
            var result = await run().ConfigureAwait(false);
            return new ToolResult(result.Success, result.Message);
        }
        catch (Exception ex)
        {
            // A tool that threw is a tool that failed; the card prints the sentence either way.
            return ToolResult.Failed(ex.Message);
        }
    }
}
