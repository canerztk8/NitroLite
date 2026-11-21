using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>How this copy of NitroLite was installed, which decides how it can be updated.</summary>
public enum LinuxInstallKind
{
    /// <summary>From the tarball, or run from a folder: the update button opens the release page.</summary>
    Archive = 0,

    /// <summary>From the .deb: the update installs the new .deb with apt.</summary>
    Deb = 1,

    /// <summary>From the .rpm: the update installs the new .rpm with dnf.</summary>
    Rpm = 2
}

/// <summary>
/// The GitHub release check, shared with the Windows build, and the package update for the .deb and .rpm installs.
/// </summary>
/// <remarks>
/// <para>
/// A copy installed from a package updates the way the package was installed: the update downloads the matching
/// <c>.deb</c> or <c>.rpm</c> from the release, checks it against the SHA-256 GitHub publishes for that asset, and
/// hands it to <c>apt-get</c> or <c>dnf</c> through <c>pkexec</c>, which asks for the administrator password once. A
/// file that does not match the digest is never installed, and a release without a digest is not installed at all.
/// </para>
/// <para>
/// A copy from the tarball cannot replace itself safely, so its card opens the release page, as before.
/// </para>
/// </remarks>
public sealed class LinuxUpdateService : IUpdateService
{
    public const string DebSuffix = "-amd64.deb";
    public const string RpmSuffix = "-x86_64.rpm";
    public const string PackagedExecutable = "/opt/nitrolite/NitroLite.Desktop";

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly string[] RpmQuery = { "-q", "nitrolite" };

    private readonly ISysfsFileSystem _fs;
    private readonly ICommandRunner _runner;
    private readonly LinuxInstallKind _kind;

    public LinuxUpdateService(ISysfsFileSystem? fs = null, ICommandRunner? runner = null, LinuxInstallKind? kind = null)
    {
        _fs = fs ?? RealSysfsFileSystem.Instance;
        _runner = runner ?? ProcessCommandRunner.Instance;
        _kind = kind ?? DetectKind(_fs, _runner, Environment.ProcessPath);
    }

    /// <summary>
    /// A package install is one running from <see cref="PackagedExecutable"/> that the package database owns. Both
    /// halves matter: a tarball unpacked into /opt is not a package, and a package's files run from anywhere else
    /// would be a copy.
    /// </summary>
    public static LinuxInstallKind DetectKind(ISysfsFileSystem fs, ICommandRunner runner, string? processPath)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);

        if (!string.Equals(processPath, PackagedExecutable, StringComparison.Ordinal)) return LinuxInstallKind.Archive;
        if (fs.FileExists("/var/lib/dpkg/info/nitrolite.list")) return LinuxInstallKind.Deb;

        if (runner.Find("rpm") is { } rpm && runner.Run(rpm, RpmQuery, TimeSpan.FromSeconds(10)).Ok)
        {
            return LinuxInstallKind.Rpm;
        }

        return LinuxInstallKind.Archive;
    }

    public LinuxInstallKind Kind => _kind;

    public string CurrentVersionText => UpdateService.CurrentVersionText;

    /// <summary>True for a package install, which can update itself; false for the tarball.</summary>
    public bool IsPackaged => _kind != LinuxInstallKind.Archive;

    /// <summary>The release asset this install updates from.</summary>
    public string AssetSuffix => _kind == LinuxInstallKind.Rpm ? RpmSuffix : DebSuffix;

    /// <summary>
    /// The command that installs <paramref name="packagePath"/> as root. <c>apt-get install</c> with a path (not a
    /// name) installs that file and resolves its dependencies; <c>dnf install</c> does the same for an rpm.
    /// </summary>
    public static IReadOnlyList<string> InstallCommand(LinuxInstallKind kind, string packagePath) => kind switch
    {
        LinuxInstallKind.Deb => new[] { "apt-get", "install", "-y", packagePath },
        LinuxInstallKind.Rpm => new[] { "dnf", "install", "-y", packagePath },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "an archive install is not updated in place")
    };

    public string ReleasePageUrl => UpdateService.ReleasePageUrl;

    public async Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await UpdateService.CheckAsync(AssetSuffix, cancellationToken).ConfigureAwait(false);
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
            AppLog.Warn("Update could not be checked", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Downloads the package, checks its digest and installs it. Refused for a tarball install, for an address that is
    /// not https on GitHub, and for a release that publishes no digest.
    /// </summary>
    public async Task<UpdateInstallResult> InstallAsync(
        UpdateCheckResult result, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!IsPackaged) return UpdateInstallResult.Fail(Loc.T("Bu derleme kendini güncelleyemez."));

        if (result.DownloadUrl is not { Length: > 0 } url || !UpdateService.IsTrustedDownloadUrl(url) ||
            result.AssetName is not { } asset || !asset.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateInstallResult.Fail(Loc.T("Sürüm paketi için güvenli bir indirme adresi yok."));
        }

        if (string.IsNullOrWhiteSpace(result.Sha256))
        {
            return UpdateInstallResult.Fail(
                Loc.T("Sürüm paketi için yayınlanmış bir özet yok; doğrulanamayan bir dosya kurulmaz."));
        }

        var pkexec = _runner.Find("pkexec");
        if (pkexec == null)
        {
            return UpdateInstallResult.Fail(Loc.T("pkexec bulunamadı; aşağıdaki komutu terminalde çalıştırın."));
        }

        string? downloaded = null;
        try
        {
            downloaded = await UpdateService.DownloadAsync(url, result.SizeBytes, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!UpdateService.VerifyDigest(downloaded, result.Sha256))
            {
                return UpdateInstallResult.Fail(Loc.T("İndirilen paket yayınlanan özetle eşleşmiyor; kurulmadı."));
            }

            // apt and dnf pick the package by extension, so the file keeps the asset's name.
            var package = Path.Combine(Path.GetDirectoryName(downloaded)!, Path.GetFileName(asset));
            if (!string.Equals(package, downloaded, StringComparison.Ordinal)) File.Move(downloaded, package, overwrite: true);
            downloaded = package;

            var install = _runner.Run(pkexec, InstallCommand(_kind, package), InstallTimeout);
            if (!install.Ok)
            {
                AppLog.Warn("The package update was not installed",
                    $"exit {install.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}: {install.Error}");
                return UpdateInstallResult.Fail(install.ExitCode == 126
                    ? Loc.T("Şifre penceresi kapatıldı; hiçbir şey değişmedi.")
                    : Loc.T("Kurulum tamamlanamadı; aşağıdaki komutu terminalde çalıştırın."));
            }

            AppLog.Info("Update installed", $"{result.DisplayVersion} via {_kind}");
            return UpdateInstallResult.Installed(PackagedExecutable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("Update could not be installed", ex.ToString());
            return UpdateInstallResult.Fail(ex.Message);
        }
        finally
        {
            UpdateService.CleanUp(downloaded);
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
            AppLog.Warn("Machine model could not be read", ex.Message);
        }

        return Loc.T("Bilinmiyor");
    }

    public string BuildIssueUrl(string title, string body) => UpdateService.BuildIssueUrl(title, body);
}
