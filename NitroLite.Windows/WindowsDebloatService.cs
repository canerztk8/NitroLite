using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The Acer background services, managed through <see cref="DebloatService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every call here reaches the service control manager, which is a synchronous, occasionally slow API, so the caller
/// runs the reads on a worker. The adapter itself stays thin: the recording of start types, the elevation and the
/// restore script all live in the service that already had them.
/// </para>
/// <para>
/// The restore path picks the recorded backup when there is one and the factory defaults when there is not, which is
/// the same choice the WPF window made. A machine that never had the services disabled therefore still has a way back.
/// </para>
/// </remarks>
public sealed class WindowsDebloatService : IDebloatService
{
    private readonly DebloatService _inner;

    public WindowsDebloatService(DebloatService? inner = null) => _inner = inner ?? new DebloatService();

    public bool IsSupported => true;

    public bool HasRestorableBackup => _inner.HasRestorableBackup();

    public IReadOnlyList<AcerServiceEntry> GetServiceStatuses()
    {
        try
        {
            return _inner.GetServiceStatuses()
                .Select(s => new AcerServiceEntry(s.Name, s.DisplayName, s.Status, s.StartType, s.IsDisabled))
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Acer servis durumları okunamadı"), ex.Message);
            return Array.Empty<AcerServiceEntry>();
        }
    }

    public bool IsNitroSenseRunning()
    {
        try { return _inner.IsNitroSenseRunning(); }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("NitroSense durumu okunamadı"), ex.Message);
            return false;
        }
    }

    public async Task<DebloatResult> DisableBackgroundServicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ok = await _inner.ApplyTier1DebloatAsync().ConfigureAwait(false);
            return ok ? DebloatResult.Ok : DebloatResult.Failed(_inner.LastError);
        }
        catch (Exception ex)
        {
            return DebloatResult.Failed(ex.Message);
        }
    }

    public async Task<DebloatResult> RestoreServicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ok = _inner.HasRestorableBackup()
                ? await _inner.RestoreTier1ServicesAsync().ConfigureAwait(false)
                : await _inner.RestoreFactoryDefaultsAsync().ConfigureAwait(false);
            return ok ? DebloatResult.Ok : DebloatResult.Failed(_inner.LastError);
        }
        catch (Exception ex)
        {
            return DebloatResult.Failed(ex.Message);
        }
    }

    public Task<DebloatResult> KillNitroSenseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var closed = _inner.KillNitroSenseProcesses();
            return Task.FromResult(closed ? DebloatResult.Ok : DebloatResult.Failed(null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(DebloatResult.Failed(ex.Message));
        }
    }

    public bool IsNitroSenseLauncherBlocked
    {
        get
        {
            try { return _inner.IsNitroSenseLauncherBlocked; }
            catch (Exception ex)
            {
                AppLog.Warn(Loc.T("NitroSense başlatıcı durumu okunamadı"), ex.Message);
                return false;
            }
        }
    }

    public async Task<DebloatResult> BlockNitroSenseLauncherAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ok = await _inner.BlockNitroSenseLauncherAsync().ConfigureAwait(false);
            return ok ? DebloatResult.Ok : DebloatResult.Failed(_inner.LastError);
        }
        catch (Exception ex)
        {
            return DebloatResult.Failed(ex.Message);
        }
    }

    public async Task<DebloatResult> UnblockNitroSenseLauncherAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ok = await _inner.UnblockNitroSenseLauncherAsync().ConfigureAwait(false);
            return ok ? DebloatResult.Ok : DebloatResult.Failed(_inner.LastError);
        }
        catch (Exception ex)
        {
            return DebloatResult.Failed(ex.Message);
        }
    }
}
