using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The Task Scheduler sign-in entry, in the shape the shared window binds to.
/// </summary>
/// <remarks>
/// <para>
/// A thin adapter over <see cref="ElevatedStartupService"/> rather than a second implementation: the task XML, the
/// <c>--autostart</c> flag and the "highest privileges available" setting are already written and tested there, and a
/// copy would be a second place for them to drift.
/// </para>
/// <para>
/// The switch starts the app minimised. A sign-in launch that opened a window on every boot would be the reason people
/// turn the feature off, and the tray icon is where the app belongs at sign-in.
/// </para>
/// </remarks>
public sealed class ElevatedAutoStartService : IAutoStartService
{
    private readonly ElevatedStartupService _service;

    /// <summary>The real Task Scheduler entry.</summary>
    public ElevatedAutoStartService() : this(new ElevatedStartupService())
    {
    }

    /// <summary>A supplied service, so a test can drive the adapter without touching the task store.</summary>
    public ElevatedAutoStartService(ElevatedStartupService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <summary>Why the last call failed, for the settings page.</summary>
    public string LastError => _service.LastError;

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        _service.IsEnabledAsync(cancellationToken);

    public async Task<bool> EnableAsync(CancellationToken cancellationToken = default)
    {
        var result = await _service.EnableAsync(startMinimized: true, cancellationToken).ConfigureAwait(false);
        return result.Applied;
    }

    public async Task<bool> DisableAsync(CancellationToken cancellationToken = default)
    {
        var result = await _service.DisableAsync(cancellationToken).ConfigureAwait(false);
        return result.Applied;
    }
}
