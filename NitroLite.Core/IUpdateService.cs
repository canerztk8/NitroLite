namespace NitroLite.Core;

/// <summary>A newer release, in the terms the update card prints it.</summary>
/// <remarks>
/// <see cref="Sha256"/> is the digest GitHub publishes for the asset. It is carried here rather than looked up again at
/// install time so the number the user was shown and the number that is verified are the same one.
/// <para>
/// <see cref="DownloadUrl"/> and <see cref="AssetName"/> are what the install needs and the card does not print. They
/// are optional so a platform that only checks for releases — the Linux build, which cannot install a Windows package
/// over itself — can leave them out and the card still works.
/// </para>
/// </remarks>
public sealed record UpdateCheckResult(
    string DisplayVersion,
    string? ReleaseNotesUrl,
    string? Sha256,
    string? DownloadUrl = null,
    string? AssetName = null,
    long SizeBytes = 0);

/// <summary>The three answers a release check can give.</summary>
public enum UpdateCheckStatus
{
    /// <summary>GitHub answered and this build is the newest release.</summary>
    Current,

    /// <summary>A newer release with a package for this platform.</summary>
    Found,

    /// <summary>The question could not be answered; "up to date" would be a guess.</summary>
    Failed
}

/// <summary>Why a release check could not be answered.</summary>
public enum UpdateCheckFailure
{
    None,

    /// <summary>No connection, a timeout, or a server error.</summary>
    Network,

    /// <summary>GitHub refused the request (403 or 429), which is almost always its anonymous rate limit.</summary>
    RateLimited,

    /// <summary>A newer release exists but carries no package this build can install from.</summary>
    NoPackage,

    /// <summary>The answer could not be read, or pointed somewhere the updater will not go.</summary>
    BadResponse
}

/// <summary>
/// A release check that tells "already current" apart from "could not ask". <see cref="IUpdateService.CheckAsync"/>
/// folds both into null, and the card then said "up to date" to a machine that was offline or rate-limited.
/// </summary>
/// <param name="Detail">The technical reason, for the log. Not localised and not meant for the card.</param>
public sealed record UpdateCheckOutcome(
    UpdateCheckStatus Status,
    UpdateCheckResult? Update = null,
    UpdateCheckFailure Failure = UpdateCheckFailure.None,
    string? Detail = null)
{
    public static UpdateCheckOutcome UpToDate() => new(UpdateCheckStatus.Current);

    public static UpdateCheckOutcome FoundRelease(UpdateCheckResult update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return new(UpdateCheckStatus.Found, update);
    }

    public static UpdateCheckOutcome CouldNotCheck(UpdateCheckFailure failure, string? detail = null) =>
        new(UpdateCheckStatus.Failed, null, failure, detail);
}

/// <summary>What an install attempt did, in the words the card will show.</summary>
public sealed record UpdateInstallResult(bool Success, string? ExecutablePath, string? Error)
{
    public static UpdateInstallResult Fail(string error) => new(false, null, error);

    public static UpdateInstallResult Installed(string executablePath) => new(true, executablePath, null);
}

/// <summary>
/// Asks GitHub for a newer release and builds the links around it.
/// </summary>
/// <remarks>
/// <para>
/// The version text, the release page and the issue body are all platform-independent in shape but Windows-specific in
/// content (the OS description, the machine model), so they sit behind this interface rather than in the shared view
/// model. A platform with no updater supplies an implementation whose <see cref="IsPackaged"/> is false, and the card
/// then offers the release page instead of a self-replacing download.
/// </para>
/// <para>
/// <see cref="CheckAsync"/> never throws: a machine with no network is not an error to report, and a silent check must
/// stay silent.
/// </para>
/// </remarks>
public interface IUpdateService
{
    /// <summary>The running version, e.g. <c>v1.2.5</c>.</summary>
    string CurrentVersionText { get; }

    /// <summary>
    /// True when the app was published as a single-file package and can therefore replace itself. A development build
    /// cannot, and the update button opens the release page instead.
    /// </summary>
    bool IsPackaged { get; }

    /// <summary>The repository's releases page, used when a self-replacing download is not possible.</summary>
    string ReleasePageUrl { get; }

    /// <summary>Asks GitHub. Null means "already current", which is a normal answer rather than a failure.</summary>
    Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks GitHub and says which of the three answers it got. Never throws, except for a cancellation the caller
    /// asked for.
    /// </summary>
    /// <remarks>
    /// The default maps <see cref="CheckAsync"/> and so cannot see a failure; a platform overrides it with
    /// <see cref="UpdateService.CheckForUpdateAsync(string, CancellationToken)"/>. It is a default rather than an
    /// abstract member so an implementation written before it existed keeps compiling.
    /// </remarks>
    async Task<UpdateCheckOutcome> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var result = await CheckAsync(cancellationToken).ConfigureAwait(false);
        return result == null ? UpdateCheckOutcome.UpToDate() : UpdateCheckOutcome.FoundRelease(result);
    }

    /// <summary>
    /// Downloads the pending release, verifies it against the digest GitHub published, and swaps the running
    /// executable. Never throws: every failure comes back as an <see cref="UpdateInstallResult"/> with the reason.
    /// </summary>
    /// <remarks>
    /// The new build is on disk when this returns success, but this process is still running the old one, so the
    /// caller tells the user to restart rather than trying to relaunch itself. A relaunch would race the
    /// single-instance guard: the new process would find the old one still holding it and exit.
    /// </remarks>
    Task<UpdateInstallResult> InstallAsync(
        UpdateCheckResult result, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The pending release's page, or the releases list when the payload carried no usable one. The address comes out
    /// of the GitHub payload, so it is checked for an https scheme before the shell sees it.
    /// </summary>
    string ReleaseNotesUrl(UpdateCheckResult? result);

    /// <summary>A prefilled issue body: version, OS build, machine model and language, and nothing else.</summary>
    string BuildIssueBody(string version, string osDescription, string machineModel, string language);

    /// <summary>The machine model, as the issue body reports it.</summary>
    string GetMachineModel();

    /// <summary>Wraps <see cref="BuildIssueBody"/> in a prefilled new-issue address.</summary>
    string BuildIssueUrl(string title, string body);
}
