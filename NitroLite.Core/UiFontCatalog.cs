namespace NitroLite.Core;

/// <summary>One selectable UI typeface.</summary>
/// <remarks>
/// <see cref="Stack"/> is a comma-separated family list, not a single name, and the tail of every stack is script
/// coverage rather than a style choice: NitroLite ships in eight languages, and Hindi needs a Devanagari face while
/// Russian needs Cyrillic. A stack that named only the Latin face would leave a Hindi user with empty boxes, so each
/// one ends in the Devanagari fallbacks and a generic family.
/// </remarks>
public sealed record UiFontChoice(string Id, string DisplayName, string Stack)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// The typefaces the settings page offers, and the one place that decides which one is in effect.
/// </summary>
/// <remarks>
/// <para>
/// The default is IBM Plex Sans, carried inside the assembly rather than looked up on the machine, so the window is
/// drawn in the same face on Windows and on Linux and on a machine that has installed nothing. It is the face the
/// design system is drawn against: a technical, slightly squared grotesque that reads as an instrument label rather
/// than as a system dialog. IBM Plex Mono carries the figures for the same reason, and the two were drawn as a pair.
/// </para>
/// <para>
/// The five system faces behind it are the escape hatch, not the design: Segoe UI (the Windows default), Arial (the
/// most widely installed sans), Verdana (drawn for screen legibility), Tahoma (compact, which is what makes it
/// readable at the 10–11px sizes this UI uses) and Georgia (a serif, for a reader who wants one). The Linux fallbacks
/// follow each name so the same choice works on the other platform.
/// </para>
/// <para>
/// The names are proper nouns and are not translated; the label above the picker is.
/// </para>
/// </remarks>
public static class UiFontCatalog
{
    /// <summary>The face a fresh install starts in: the one the design system was drawn against.</summary>
    public const string DefaultId = "plex";

    /// <summary>The Devanagari and generic tail every stack ends in. Not a style choice; see the type remarks.</summary>
    private const string ScriptTail = "Nirmala UI, Noto Sans Devanagari, sans-serif";

    /// <summary>
    /// The bundled sans, named the way Avalonia resolves an embedded family: the avares URI of the folder the fonts
    /// were compiled into, a hash, then the family name inside it. A bare "IBM Plex Sans" would match nothing, because
    /// the face is not installed on the machine.
    /// </summary>
    public const string BundledSans = "avares://NitroLite.Desktop/Assets/Fonts#IBM Plex Sans";

    public static IReadOnlyList<UiFontChoice> All { get; } = new[]
    {
        new UiFontChoice(DefaultId, "IBM Plex Sans", $"{BundledSans}, {ScriptTail}"),

        new UiFontChoice("segoe", "Segoe UI",
            $"Segoe UI Variable Text, Segoe UI, Ubuntu, Noto Sans, {ScriptTail}"),

        new UiFontChoice("arial", "Arial",
            $"Arial, Liberation Sans, Noto Sans, {ScriptTail}"),

        new UiFontChoice("verdana", "Verdana",
            $"Verdana, DejaVu Sans, Noto Sans, {ScriptTail}"),

        new UiFontChoice("tahoma", "Tahoma",
            $"Tahoma, DejaVu Sans, Noto Sans, {ScriptTail}"),

        new UiFontChoice("georgia", "Georgia",
            "Georgia, Noto Serif, DejaVu Serif, Nirmala UI, Noto Sans Devanagari, serif")
    };

    /// <summary>
    /// The choice matching <paramref name="id"/>, or the default. A settings file naming a face this build no longer
    /// ships must not leave the window in a font that does not exist.
    /// </summary>
    public static UiFontChoice Resolve(string? id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
