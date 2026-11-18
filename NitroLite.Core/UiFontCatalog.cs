namespace NitroLite.Core;

/// <summary>One selectable UI typeface.</summary>
/// <remarks>
/// <see cref="Stack"/> is a comma-separated family list, not a single name, and the tail of every stack is script
/// coverage rather than a style choice: NitroLite ships in eight languages, and Hindi needs a Devanagari face while
/// Russian needs Cyrillic. A stack that named only the Latin face would leave a Hindi user with empty boxes, so each
/// one ends in the Devanagari fallbacks and a generic family.
/// </remarks>
public sealed record UiFontChoice(string Id, string DisplayName, string Stack, string? Script = null)
{
    public override string ToString() => DisplayName;

    /// <summary>
    /// True when the face was drawn for one script and is offered only to a reader of it. Rajdhani is the case: it
    /// carries Devanagari, and a reader who does not have Hindi on screen gains nothing from a condensed HUD face.
    /// </summary>
    public bool IsScriptSpecific => Script != null;
}

/// <summary>
/// The typefaces the settings page offers, and the one place that decides which one is in effect.
/// </summary>
/// <remarks>
/// <para>
/// The default is Quicksand, carried inside the assembly rather than looked up on the machine, so the window is drawn
/// in the same face on Windows and on Linux and on a machine that has installed nothing. It is a geometric sans with
/// rounded terminals: warmer than a system dialog, and distinct enough that the window is recognisable at a glance.
/// </para>
/// <para>
/// Seven more faces travel in the assembly beside it, all SIL OFL 1.1 and all subset to the ranges the eight
/// catalogues can put on screen: <b>Exo 2</b> (technical geometric, Cyrillic), <b>Oxanium</b> (squared, the lightest
/// file of the set), <b>Rubik</b> (rounded, Cyrillic), <b>JetBrains Mono</b> (monospace, Cyrillic),
/// <b>Rajdhani</b> (condensed HUD, Devanagari), <b>Space Grotesk</b> (retro-futuristic grotesque) and
/// <b>Chakra Petch</b> (squared techno, cut corners). Each ships as three static instances — Regular, SemiBold,
/// Bold — because Avalonia 11 exposes no variation axis and would otherwise draw every weight at the file's
/// default.
/// </para>
/// <para>
/// Quicksand carries Latin, Latin Extended and Vietnamese, which covers Turkish, German, Spanish, Portuguese and
/// Indonesian. It carries no Cyrillic and no Devanagari, so Russian and Hindi fall through to the tail of the stack —
/// the same system faces those two languages were drawn in before the bundled face existed. Nothing renders as empty
/// boxes; those two languages simply keep the face they had. Exo 2, Rubik and JetBrains Mono do carry Cyrillic, so a
/// Russian reader who picks one of them gets it in that face; Rajdhani carries Devanagari, so a Hindi reader does.
/// Space Grotesk and Chakra Petch are Latin-only like Quicksand, so they fall through for the same two languages.
/// </para>
/// <para>
/// The five system faces behind them are the escape hatch, not the design: Segoe UI (the Windows default), Arial (the
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
    public const string DefaultId = "quicksand";

    /// <summary>The Devanagari and generic tail every stack ends in. Not a style choice; see the type remarks.</summary>
    private const string ScriptTail = "Nirmala UI, Noto Sans Devanagari, sans-serif";

    /// <summary>The folder the bundled faces were compiled into, as Avalonia resolves it.</summary>
    private const string BundledFolder = "avares://NitroLite.Desktop/Assets/Fonts";

    /// <summary>
    /// The bundled face, named the way Avalonia resolves an embedded family: the avares URI of the folder the fonts
    /// were compiled into, a hash, then the family name inside it. A bare "Quicksand" would match nothing, because the
    /// face is not installed on the machine.
    /// </summary>
    public const string BundledSans = $"{BundledFolder}#Quicksand";

    /// <summary>The script tag a face carries when it is offered only to a reader of that script.</summary>
    public const string DevanagariScript = "devanagari";

    /// <summary>The language code whose readers are offered the Devanagari face.</summary>
    public const string HindiLanguage = "hi";

    public static IReadOnlyList<UiFontChoice> All { get; } = new[]
    {
        new UiFontChoice(DefaultId, "Quicksand", $"{BundledSans}, {ScriptTail}"),

        new UiFontChoice("exo2", "Exo 2", $"{BundledFolder}#Exo2, {ScriptTail}"),

        new UiFontChoice("oxanium", "Oxanium", $"{BundledFolder}#Oxanium, {ScriptTail}"),

        new UiFontChoice("rubik", "Rubik", $"{BundledFolder}#Rubik, {ScriptTail}"),

        new UiFontChoice("jetbrains", "JetBrains Mono", $"{BundledFolder}#JetBrainsMono, {ScriptTail}"),

        new UiFontChoice("rajdhani", "Rajdhani", $"{BundledFolder}#Rajdhani, {ScriptTail}", DevanagariScript),

        new UiFontChoice("spacegrotesk", "Space Grotesk", $"{BundledFolder}#SpaceGrotesk, {ScriptTail}"),

        new UiFontChoice("chakrapetch", "Chakra Petch", $"{BundledFolder}#ChakraPetch, {ScriptTail}"),

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

    /// <summary>True when <paramref name="code"/> is the language the Devanagari face is offered to.</summary>
    public static bool IsHindi(string? code) =>
        string.Equals(code, HindiLanguage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The faces to offer a reader whose interface is in <paramref name="uiLanguage"/> on a machine whose display
    /// language is <paramref name="systemLanguage"/>.
    /// </summary>
    /// <remarks>
    /// A face drawn for one script is offered only to a reader of that script, and either language counts: a reader
    /// who set the interface to English on a Hindi Windows is still a Hindi reader. <paramref name="currentId"/> is
    /// the face already in effect, and it stays in the list even when the reader has moved out of its script — a
    /// picker whose selected item is not among its items draws blank, and the window is still in that face.
    /// </remarks>
    public static IReadOnlyList<UiFontChoice> VisibleFor(
        string? uiLanguage, string? systemLanguage, string? currentId = null)
    {
        if (IsHindi(uiLanguage) || IsHindi(systemLanguage)) return All;

        var visible = new List<UiFontChoice>(All.Count);
        foreach (var face in All)
        {
            if (face.Script is null || string.Equals(face.Id, currentId, StringComparison.OrdinalIgnoreCase))
            {
                visible.Add(face);
            }
        }
        return visible;
    }

    /// <summary>
    /// The choice matching <paramref name="id"/>, or the default. A settings file naming a face this build no longer
    /// ships must not leave the window in a font that does not exist.
    /// </summary>
    public static UiFontChoice Resolve(string? id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
