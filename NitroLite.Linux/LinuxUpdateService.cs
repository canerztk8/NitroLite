using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// The GitHub release check, shared with the Windows build.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UpdateService"/> is platform-independent apart from the machine model, which is supplied here from the
/// DMI files rather than from WMI. The release asset is a Windows zip, so <see cref="IsPackaged"/> is false on Linux:
/// the card then offers the release page instead of a download that could not replace a running ELF binary.
/// </para>
/// <para>
/// The check itself still runs, because a Linux user watching the project wants to know a release happened.
/// </para>
/// </remarks>
public sealed class LinuxUpdateService : IUpdateService
{
    private readonly ISysfsFileSystem _fs;

    public LinuxUpdateService(ISysfsFileSystem? fs = null) => _fs = fs ?? RealSysfsFileSystem.Instance;

    public string CurrentVersionText => UpdateService.CurrentVersionText;

    /// <summary>
    /// False on purpose. The published package is a Windows zip; a Linux build cannot install it over itself, so the
    /// update button opens the release page rather than starting a download it would have to throw away.
    /// </summary>
    public bool IsPackaged => false;

    public string ReleasePageUrl => UpdateService.ReleasePageUrl;

    public async Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await UpdateService.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (info == null) return null;

            return new UpdateCheckResult(
                info.DisplayVersion,
                UpdateService.ReleaseNotesUrl(info),
                info.Sha256);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Güncelleme denetlenemedi", ex.Message);
            return null;
        }
    }

    public string ReleaseNotesUrl(UpdateCheckResult? result) =>
        result?.ReleaseNotesUrl is { Length: > 0 } url && UpdateService.IsSafeHttpsUrl(url)
            ? url
            : UpdateService.ReleasePageUrl;

    public string BuildIssueBody(string version, string osDescription, string machineModel, string language) =>
        UpdateService.BuildIssueBody(version, osDescription, machineModel, language);

    public string GetMachineModel()
    {
        try
        {
            var identity = LinuxDmiProbe.Read(_fs);
            var text = string.Join(" ", new[] { identity.Manufacturer, identity.Model }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            if (text.Length > 0) return text;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Makine modeli okunamadı", ex.Message);
        }

        return Loc.T("Bilinmiyor");
    }

    public string BuildIssueUrl(string title, string body) => UpdateService.BuildIssueUrl(title, body);
}
