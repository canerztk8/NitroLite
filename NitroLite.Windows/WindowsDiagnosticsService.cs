using NitroLite.Core;
using NitroLite.Core.Hardware;

namespace NitroLite.Windows;

/// <summary>
/// The diagnostics bundle, through <see cref="DiagnosticsBundle"/>.
/// </summary>
/// <remarks>
/// The bundle carries the log, the hardware report, the capability snapshot and the settings. The capability snapshot
/// is supplied by the caller because it is the probe's answer for this session, and a bundle that re-probed would
/// describe a machine the user is not running on.
/// </remarks>
public sealed class WindowsDiagnosticsService : IDiagnosticsService
{
    private readonly Func<CapabilitySnapshot?> _snapshot;
    private readonly Func<DeviceCapabilities> _capabilities;

    /// <param name="snapshot">The session's capability snapshot, or null when the probe has not run.</param>
    /// <param name="capabilities">The device capabilities the window is currently showing.</param>
    public WindowsDiagnosticsService(
        Func<CapabilitySnapshot?>? snapshot = null,
        Func<DeviceCapabilities>? capabilities = null)
    {
        _snapshot = snapshot ?? (() => null);
        _capabilities = capabilities ?? (() => new DeviceCapabilities());
    }

    public bool IsSupported => true;

    public string DataDirectory => AppPaths.DataDirectory;

    public string CreateBundle() => DiagnosticsBundle.Create(
        AppPaths.DataDirectory,
        _snapshot(),
        _capabilities(),
        AppPaths.GetDataFile("settings.json"),
        AppLog.LogFilePath);
}
