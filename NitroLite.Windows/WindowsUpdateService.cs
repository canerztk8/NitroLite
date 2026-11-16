using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The GitHub release check, through <see cref="UpdateService"/>.
/// </summary>
/// <remarks>
/// The service is static and already knows the repository, the digest rule and the issue template, so this adapter only
/// translates its <c>UpdateInfo</c> into the shared shape. A development build reports
/// <see cref="IsPackaged"/> false, which is what makes the card offer the release page instead of a download that could
/// not replace the running executable.
/// </remarks>
public sealed class WindowsUpdateService : IUpdateService
{
    public string CurrentVersionText => UpdateService.CurrentVersionText;

    public bool IsPackaged => UpdateService.IsPackaged;

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
            // A machine with no network is not an error to report; the card says "could not check" only when the user
            // asked, and that decision belongs to the caller.
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

    public string GetMachineModel() => UpdateService.GetMachineModel();

    public string BuildIssueUrl(string title, string body) => UpdateService.BuildIssueUrl(title, body);
}
