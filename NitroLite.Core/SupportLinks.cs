namespace NitroLite.Core;

/// <summary>
/// Where the support card's buttons point.
/// </summary>
/// <remarks>
/// <para>
/// Constants rather than literals in the view model, so the three addresses are one edit and a test can hold them to
/// https. The repository address is derived from <see cref="UpdateService.Owner"/> rather than repeated, because the
/// update check and the star button must never disagree about which project this is.
/// </para>
/// <para>
/// GitHub Sponsors is derived from the same owner for the same reason. It was named on the card without a link until
/// the account was enrolled, because a button that opens a 404 is worse than a line that says the programme is
/// pending; the listing is public now, so the card has a button for it.
/// </para>
/// </remarks>
public static class SupportLinks
{
    /// <summary>The author's Ko-fi page.</summary>
    public const string Kofi = "https://ko-fi.com/cztrk";

    /// <summary>The project's own page, for the star button.</summary>
    public static string Repository => $"https://github.com/{UpdateService.Owner}/{UpdateService.Repository}";

    /// <summary>The author's GitHub Sponsors page.</summary>
    public static string Sponsors => $"https://github.com/sponsors/{UpdateService.Owner}";
}
