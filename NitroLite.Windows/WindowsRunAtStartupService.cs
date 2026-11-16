using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The ordinary <c>HKCU\...\Run</c> entry, in the shape the shared window binds to.
/// </summary>
/// <remarks>
/// A thin adapter over <see cref="DebloatService"/> rather than a second registry writer: that class already builds the
/// command line, repairs an entry left pointing at the retired WPF executable, and is the one the elevated task removes
/// when it takes over. A copy would be a second place for the quoting to drift.
/// </remarks>
public sealed class WindowsRunAtStartupService : IRunAtStartupService
{
    private readonly DebloatService _inner;

    /// <summary>The real registry entry.</summary>
    public WindowsRunAtStartupService() : this(new DebloatService())
    {
    }

    /// <summary>A supplied service, so a test can drive the adapter without touching the registry.</summary>
    public WindowsRunAtStartupService(DebloatService inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public bool IsSupported => true;

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_inner.IsNitroLiteAutoStartEnabled());

    public Task<bool> SetEnabledAsync(bool enabled, bool startMinimized, CancellationToken cancellationToken = default) =>
        Task.FromResult(_inner.SetNitroLiteAutoStart(enabled, startMinimized));
}
