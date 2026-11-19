using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// Releases the Windows standby list through the bundled WinMemoryCleaner copy.
/// </summary>
/// <remarks>
/// The service is a static helper in the WPF front end, so this is a thin adapter rather than a rewrite: it exists so
/// the view model can hold an interface and the Linux build can hold a different one.
/// </remarks>
public sealed class WindowsMemoryCleaner : IMemoryCleaner
{
    public bool IsSupported => true;

    /// <summary>WinMemoryCleaner is a separate GPL-3.0 program, so the card offers its download page.</summary>
    public bool NeedsExternalTool => true;

    public async Task<MemoryCleanResult> CleanStandbyMemoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var service = new MemoryCleanerService();
            var result = await service.CleanMemoryAsync().ConfigureAwait(false);

            return new MemoryCleanResult
            {
                Cleaned = result.Cleaned,
                FreedMegabytes = result.FreedMegabytes,
                AvailableBeforeGigabytes = result.AvailableBeforeGigabytes,
                AvailableGigabytes = result.AvailableGigabytes,
                ToolMissing = result.ToolMissing,
                ElevationRefused = result.ElevationRefused,
                Error = result.Error
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn("The standby memory could not be purged", ex.ToString());
            return new MemoryCleanResult { Cleaned = false, Error = ex.Message };
        }
    }
}
