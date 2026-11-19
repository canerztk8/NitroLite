namespace NitroLite.Core;

/// <summary>
/// Where the support card's buttons point.
/// </summary>
/// <remarks>
/// <para>
/// Constants rather than literals in the view model, so the two addresses are one edit and a test can hold them to
/// https. The repository address is derived from <see cref="UpdateService.Owner"/> rather than repeated, because the
/// update check and the star button must never disagree about which project this is.
/// </para>
/// <para>
/// GitHub Sponsors is listed but not offered. The account is not enrolled yet, and a button that opens a 404 is worse
/// than a line that says the programme is pending — so the card prints <see cref="Sponsors"/> nowhere and the view
/// model has no command for it. When the account is approved, the button is one command and one line of markup.
/// </para>
/// </remarks>
public static class SupportLinks
{
    /// <summary>The author's Ko-fi page: the one live way to send money.</summary>
    public const string Kofi = "https://ko-fi.com/cztrk";

    /// <summary>The project's own page, for the star button.</summary>
    public static string Repository => $"https://github.com/{UpdateService.Owner}/{UpdateService.Repository}";

    /// <summary>Where the sponsors button will point once the account is enrolled.</summary>
    public static string Sponsors => $"https://github.com/sponsors/{UpdateService.Owner}";
}
