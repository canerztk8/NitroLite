namespace NitroLite.Core;

/// <summary>A newer release, in the terms the update card prints it.</summary>
/// <remarks>
/// <see cref="Sha256"/> is the digest GitHub publishes for the asset. It is carried here rather than looked up again at
/// install time so the number the user was shown and the number that is verified are the same one.
/// </remarks>
public sealed record UpdateCheckResult(string DisplayVersion, string? ReleaseNotesUrl, string? Sha256);

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
