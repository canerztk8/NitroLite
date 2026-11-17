using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The GitHub release check and the self-replacing install, through <see cref="UpdateService"/>.
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
                info.Sha256,
                info.DownloadUrl,
                info.AssetName,
                info.SizeBytes);
        }
        catch (Exception ex)
        {
            // A machine with no network is not an error to report; the card says "could not check" only when the user
            // asked, and that decision belongs to the caller.
            AppLog.Warn("Güncelleme denetlenemedi", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Downloads, verifies and swaps the executable. Every refusal is a returned reason rather than an exception: the
    /// card has to be able to print why the update did not happen.
    /// </summary>
    /// <remarks>
    /// The digest is not optional. NitroLite is unsigned, so the SHA-256 GitHub publishes for the asset is the only
    /// thing standing between the user and a tampered build; a release without one is not installed automatically.
    /// </remarks>
    public async Task<UpdateInstallResult> InstallAsync(
        UpdateCheckResult result, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsPackaged) return UpdateInstallResult.Fail(Loc.T("Bu derleme kendini güncelleyemez."));

        if (result.DownloadUrl is not { Length: > 0 } url || !UpdateService.IsSafeHttpsUrl(url))
            return UpdateInstallResult.Fail(Loc.T("Sürüm paketi için güvenli bir indirme adresi yok."));

        if (string.IsNullOrWhiteSpace(result.Sha256))
            return UpdateInstallResult.Fail(
                Loc.T("Sürüm paketi için yayınlanmış bir özet yok; doğrulanamayan bir dosya kurulmaz."));

        string? package = null;
        string? staging = null;
        try
        {
            package = await UpdateService.DownloadAsync(url, result.SizeBytes, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!UpdateService.VerifyDigest(package, result.Sha256))
                return UpdateInstallResult.Fail(Loc.T("İndirilen paket yayınlanan özetle eşleşmiyor; kurulmadı."));

            staging = UpdateService.ExtractToStaging(package);
            var executable = UpdateService.InstallFromStaging(staging, AppContext.BaseDirectory);

            AppLog.Info("Güncelleme kuruldu", $"{result.DisplayVersion} → {executable}");
            return UpdateInstallResult.Installed(executable);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Güncelleme kurulamadı", ex.ToString());
            return UpdateInstallResult.Fail(Loc.T($"Güncelleme kurulamadı: {ex.Message}"));
        }
        finally
        {
            // The package and the staging folder are both under %TEMP% and both are useless once the swap is done or
            // has failed. Leaving them behind is how a failed update used to fill the drive.
            UpdateService.CleanUp(package, staging);
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
