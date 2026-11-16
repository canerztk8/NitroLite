using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// The sign-in entry on a platform that has no <c>HKCU\...\Run</c> key.
/// </summary>
/// <remarks>
/// Reports itself unsupported rather than writing a <c>~/.config/autostart</c> file. A desktop entry would start the
/// window at login, but the tray behaviour it is paired with does not exist here yet, so the switch would promise
/// something the rest of the app cannot keep. The card hides instead.
/// </remarks>
public sealed class LinuxRunAtStartupService : IRunAtStartupService
{
    public bool IsSupported => false;

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> SetEnabledAsync(bool enabled, bool startMinimized, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
