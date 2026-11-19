using Avalonia.Controls;
using Avalonia.Media;
using NitroLite.Core;

namespace NitroLite.Desktop.Themes;

/// <summary>
/// One palette, expressed as the brushes the Avalonia views ask for by name.
/// </summary>
/// <remarks>
/// <para>
/// The colours are the ones the retired WPF front end used, so a user who moves across recognises the window. What
/// differs is the delivery: the palette is built here rather than merged from a <c>ResourceDictionary</c> file, and
/// it is read through <c>DynamicResource</c> so a swap repaints a live window with nothing re-bound by hand.
/// </para>
/// <para>
/// The soft variants are derived rather than listed. A palette that had to carry four more hand-picked colours would
/// be four more chances for one of them to be wrong in one theme, and the derived value is by construction the accent
/// at a fixed opacity over whatever surface it lands on.
/// </para>
/// </remarks>
public sealed class ThemePalette
{
    /// <summary>The window background, behind every card.</summary>
    public Color Page { get; init; }

    /// <summary>A card's surface.</summary>
    public Color Card { get; init; }

    /// <summary>A card's surface under the pointer, and the fill of a pill.</summary>
    public Color CardHover { get; init; }

    /// <summary>An inset panel: the thermal-floor note, the udev command box.</summary>
    public Color Panel { get; init; }

    /// <summary>The navigation rail and the header/footer bars.</summary>
    public Color Rail { get; init; }

    /// <summary>The one-pixel rule around a card.</summary>
    public Color CardBorder { get; init; }

    /// <summary>A rule that has to be seen: a divider inside a card, a focused field.</summary>
    public Color CardBorderStrong { get; init; }

    public Color TextPrimary { get; init; }
    public Color TextSecondary { get; init; }
    public Color TextMuted { get; init; }

    /// <summary>The one live signal: the selected tab, the primary button, the sparkline.</summary>
    public Color Accent { get; init; }

    /// <summary>Text drawn on top of <see cref="Accent"/>.</summary>
    public Color OnAccent { get; init; }

    public Color Success { get; init; }
    public Color Warning { get; init; }
    public Color Danger { get; init; }

    /// <summary>The wash under a sparkline.</summary>
    public Color ChartFill { get; init; }

    /// <summary>The accent lifted a step, for a hovered primary button.</summary>
    public Color AccentHover => Blend(Accent, Colors.White, 0.16);

    /// <summary>The accent at 14 % over the page, for a badge or a selected row.</summary>
    public Color AccentSoft => WithAlpha(Accent, 0x24);

    public Color SuccessSoft => WithAlpha(Success, 0x24);
    public Color WarningSoft => WithAlpha(Warning, 0x24);
    public Color DangerSoft => WithAlpha(Danger, 0x24);

    /// <summary>
    /// The five colours a theme card shows as swatches, as the <c>#RRGGBB</c> strings the view paints.
    /// </summary>
    /// <remarks>
    /// Page, card, rule, ink and accent: the five that answer "what does this palette look like" without a screenshot.
    /// Hex strings rather than brushes because the view model holds no Avalonia types — the view turns them into
    /// brushes with <c>HexToBrushConverter</c>, the same route every other colour in the window takes.
    /// </remarks>
    public static IReadOnlyList<string> SwatchHexes(AppTheme theme)
    {
        var palette = For(theme);
        return new[] { palette.Page, palette.Card, palette.CardBorder, palette.TextPrimary, palette.Accent }
            .Select(colour => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}")
            .ToArray();
    }

    /// <summary>The palette for a concrete theme. <see cref="AppTheme.System"/> is resolved by the caller.</summary>
    public static ThemePalette For(AppTheme theme) => theme switch
    {
        AppTheme.WarmCream => WarmCream,
        AppTheme.MidnightOled => MidnightOled,
        AppTheme.SlateSteel => SlateSteel,
        AppTheme.AmberConsole => AmberConsole,
        AppTheme.NordFrost => NordFrost,
        AppTheme.CleanWhite => CleanWhite,
        AppTheme.Forest => Forest,
        AppTheme.NeonStreet => NeonStreet,
        AppTheme.HighContrast => HighContrast,
        AppTheme.ViceSunset => ViceSunset,
        AppTheme.CyberChrome => CyberChrome,
        AppTheme.RoseQuartz => RoseQuartz,
        AppTheme.Navy => Navy,
        _ => Dark
    };

    /// <summary>
    /// The brushes, keyed by the names the views use. Every key is a <c>SolidColorBrush</c>: no gradients, no effects,
    /// no transparency layers, so the window stays as cheap to render as it is to read.
    /// </summary>
    /// <remarks>
    /// The <c>Slider*</c> keys are Fluent's own resource names. The Fluent slider template resolves them with
    /// <c>DynamicResource</c>, so overriding them here is what makes the thumb and the filled part of the track follow
    /// the palette instead of staying the default blue — verified against Avalonia 11.3.23 by reading the thumb's
    /// background back out of a live visual tree. Every state is covered, because a thumb that turns blue on hover is
    /// worse than one that never changed.
    /// </remarks>
    public ResourceDictionary ToResources()
    {
        var resources = new ResourceDictionary
        {
            ["PageBrush"] = Brush(Page),
            ["CardBrush"] = Brush(Card),
            ["CardHoverBrush"] = Brush(CardHover),
            ["PanelBrush"] = Brush(Panel),
            ["RailBrush"] = Brush(Rail),
            ["CardBorderBrush"] = Brush(CardBorder),
            ["CardBorderStrongBrush"] = Brush(CardBorderStrong),
            ["TextPrimaryBrush"] = Brush(TextPrimary),
            ["TextSecondaryBrush"] = Brush(TextSecondary),
            ["TextMutedBrush"] = Brush(TextMuted),
            ["AccentBrush"] = Brush(Accent),
            ["AccentHoverBrush"] = Brush(AccentHover),
            ["AccentSoftBrush"] = Brush(AccentSoft),
            ["OnAccentBrush"] = Brush(OnAccent),
            ["SuccessBrush"] = Brush(Success),
            ["SuccessSoftBrush"] = Brush(SuccessSoft),
            ["WarningBrush"] = Brush(Warning),
            ["WarningSoftBrush"] = Brush(WarningSoft),
            ["DangerBrush"] = Brush(Danger),
            ["DangerSoftBrush"] = Brush(DangerSoft),
            ["ChartFillBrush"] = Brush(ChartFill),

            // The slider, in every state Fluent asks about.
            ["SliderThumbBackground"] = Brush(Accent),
            ["SliderThumbBackgroundPointerOver"] = Brush(AccentHover),
            ["SliderThumbBackgroundPressed"] = Brush(Accent),
            ["SliderThumbBackgroundDisabled"] = Brush(TextMuted),
            ["SliderTrackValueFill"] = Brush(Accent),
            ["SliderTrackValueFillPointerOver"] = Brush(AccentHover),
            ["SliderTrackValueFillPressed"] = Brush(Accent),
            ["SliderTrackValueFillDisabled"] = Brush(TextMuted),
            ["SliderTrackFill"] = Brush(CardBorderStrong),
            ["SliderTrackFillPointerOver"] = Brush(CardBorderStrong),
            ["SliderTrackFillPressed"] = Brush(CardBorderStrong),
            ["SliderTrackFillDisabled"] = Brush(CardBorder),

            // The switch, the same way. Fluent's default is the system accent, which on a machine whose Windows accent
            // is red leaves a red switch in an ice-blue window. Verified against Avalonia 11.3.23 by reading the
            // template's own parts back out of a live visual tree: FillOn paints Border#SwitchKnobBounds and
            // KnobFillOn paints Ellipse#SwitchKnobOn.
            ["ToggleSwitchFillOn"] = Brush(Accent),
            ["ToggleSwitchFillOnPointerOver"] = Brush(AccentHover),
            ["ToggleSwitchFillOnPressed"] = Brush(AccentHover),
            ["ToggleSwitchStrokeOn"] = Brush(Accent),
            ["ToggleSwitchStrokeOnPointerOver"] = Brush(AccentHover),
            ["ToggleSwitchStrokeOnPressed"] = Brush(AccentHover),
            ["ToggleSwitchKnobFillOn"] = Brush(OnAccent),
            ["ToggleSwitchKnobFillOnPointerOver"] = Brush(OnAccent),
            ["ToggleSwitchKnobFillOnPressed"] = Brush(OnAccent)
        };

        return resources;
    }

    private static IBrush Brush(Color color) => new SolidColorBrush(color);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>Mixes <paramref name="from"/> towards <paramref name="to"/>; 0 keeps the first colour, 1 the second.</summary>
    public static Color Blend(Color from, Color to, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t, MidpointRounding.AwayFromZero);
        return Color.FromArgb(from.A, Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    /// <summary>
    /// The warm-cream "recording paper" surface NitroLite shipped with, and the palette a fresh install starts in.
    /// </summary>
    /// <remarks>
    /// The card frame is a shade darker than the paper's own rule was: a hairline at the original tone measured 1.20:1
    /// against the page, under the 1.45:1 floor <c>ThemePaletteTests</c> holds every palette to, so the frame was
    /// invisible on a bright screen. The accent is the vermilion the WPF window used, lifted a step so it clears 3:1
    /// on the paper.
    /// </remarks>
    public static ThemePalette WarmCream { get; } = new()
    {
        Page = Color.Parse("#F2EDE3"),
        Card = Color.Parse("#F8F4EB"),
        CardHover = Color.Parse("#EDE6D8"),
        Panel = Color.Parse("#E8E0D2"),
        Rail = Color.Parse("#EAE3D5"),
        CardBorder = Color.Parse("#CFC4AF"),
        CardBorderStrong = Color.Parse("#B8AB93"),
        TextPrimary = Color.Parse("#1B1917"),
        TextSecondary = Color.Parse("#4A4238"),
        TextMuted = Color.Parse("#6E6558"),
        Accent = Color.Parse("#C0392B"),
        OnAccent = Color.Parse("#F7F2E8"),
        Success = Color.Parse("#5F6B3E"),
        Warning = Color.Parse("#B45309"),
        Danger = Color.Parse("#9B2C1F"),
        ChartFill = Color.Parse("#104A4238")
    };

    /// <summary>The same paper after dark: warm graphite, bone ink, a lifted vermilion.</summary>
    public static ThemePalette Dark { get; } = new()
    {
        Page = Color.Parse("#15130F"),
        Card = Color.Parse("#1B1813"),
        CardHover = Color.Parse("#24201A"),
        Panel = Color.Parse("#1E1B15"),
        Rail = Color.Parse("#1A1712"),
        CardBorder = Color.Parse("#3D382D"),
        CardBorderStrong = Color.Parse("#524B3D"),
        TextPrimary = Color.Parse("#EDE6D8"),
        TextSecondary = Color.Parse("#B8AE9C"),
        TextMuted = Color.Parse("#8A8071"),
        Accent = Color.Parse("#E05A48"),
        OnAccent = Color.Parse("#15130F"),
        Success = Color.Parse("#9DB06A"),
        Warning = Color.Parse("#E0A458"),
        Danger = Color.Parse("#E05A48"),
        ChartFill = Color.Parse("#1AE05A48")
    };

    /// <summary>Pure black for OLED panels, with Nitro red as the only accent.</summary>
    public static ThemePalette MidnightOled { get; } = new()
    {
        Page = Color.Parse("#09090B"),
        Card = Color.Parse("#111114"),
        CardHover = Color.Parse("#18181C"),
        Panel = Color.Parse("#0C0C0E"),
        Rail = Color.Parse("#0A0A0C"),
        CardBorder = Color.Parse("#303035"),
        CardBorderStrong = Color.Parse("#3F3F46"),
        TextPrimary = Color.Parse("#F4F4F5"),
        TextSecondary = Color.Parse("#A1A1AA"),
        TextMuted = Color.Parse("#787882"),
        Accent = Color.Parse("#E11D48"),
        OnAccent = Color.Parse("#09090B"),
        Success = Color.Parse("#4ADE80"),
        Warning = Color.Parse("#FBBF24"),
        Danger = Color.Parse("#E11D48"),
        ChartFill = Color.Parse("#1AE11D48")
    };

    /// <summary>Cold anthracite with an ice-blue accent.</summary>
    public static ThemePalette SlateSteel { get; } = new()
    {
        Page = Color.Parse("#0F172A"),
        Card = Color.Parse("#1E293B"),
        CardHover = Color.Parse("#263349"),
        Panel = Color.Parse("#131C31"),
        Rail = Color.Parse("#111A2E"),
        CardBorder = Color.Parse("#334155"),
        CardBorderStrong = Color.Parse("#475569"),
        TextPrimary = Color.Parse("#F1F5F9"),
        TextSecondary = Color.Parse("#94A3B8"),
        TextMuted = Color.Parse("#7D8FA6"),
        Accent = Color.Parse("#38BDF8"),
        OnAccent = Color.Parse("#0F172A"),
        Success = Color.Parse("#4ADE80"),
        Warning = Color.Parse("#FBBF24"),
        Danger = Color.Parse("#F43F5E"),
        ChartFill = Color.Parse("#1A38BDF8")
    };

    /// <summary>A warm console: amber phosphor on near-black.</summary>
    public static ThemePalette AmberConsole { get; } = new()
    {
        Page = Color.Parse("#0E0C09"),
        Card = Color.Parse("#17140F"),
        CardHover = Color.Parse("#211D16"),
        Panel = Color.Parse("#120F0B"),
        Rail = Color.Parse("#100D0A"),
        CardBorder = Color.Parse("#362F23"),
        CardBorderStrong = Color.Parse("#4D4332"),
        TextPrimary = Color.Parse("#F3E8D2"),
        TextSecondary = Color.Parse("#B8A88A"),
        TextMuted = Color.Parse("#918268"),
        Accent = Color.Parse("#F59E0B"),
        OnAccent = Color.Parse("#0E0C09"),
        Success = Color.Parse("#A3B565"),
        Warning = Color.Parse("#F59E0B"),
        Danger = Color.Parse("#E5484D"),
        ChartFill = Color.Parse("#1AF59E0B")
    };

    /// <summary>Nord: polar night surfaces with a frost accent.</summary>
    public static ThemePalette NordFrost { get; } = new()
    {
        Page = Color.Parse("#242933"),
        Card = Color.Parse("#2E3440"),
        CardHover = Color.Parse("#373E4C"),
        Panel = Color.Parse("#272C36"),
        Rail = Color.Parse("#262B35"),
        CardBorder = Color.Parse("#434C5E"),
        CardBorderStrong = Color.Parse("#4C566A"),
        TextPrimary = Color.Parse("#ECEFF4"),
        TextSecondary = Color.Parse("#D8DEE9"),
        TextMuted = Color.Parse("#9AA5B8"),
        Accent = Color.Parse("#88C0D0"),
        OnAccent = Color.Parse("#242933"),
        Success = Color.Parse("#A3BE8C"),
        Warning = Color.Parse("#EBCB8B"),
        Danger = Color.Parse("#BF616A"),
        ChartFill = Color.Parse("#1A88C0D0")
    };

    /// <summary>Neutral daylight white with a blue accent: the light palette for a bright room.</summary>
    /// <remarks>
    /// The second light palette, and the one that is not warm. WarmCream is paper; this is a whiteboard. The card is
    /// pure white against a very slightly grey page, so the cards read as raised without a shadow, and the rule is
    /// dark enough to clear the 1.45:1 floor the tests hold every palette to.
    /// </remarks>
    public static ThemePalette CleanWhite { get; } = new()
    {
        Page = Color.Parse("#F6F7F9"),
        Card = Color.Parse("#FFFFFF"),
        CardHover = Color.Parse("#EEF1F5"),
        Panel = Color.Parse("#E9EDF2"),
        Rail = Color.Parse("#F0F2F6"),
        CardBorder = Color.Parse("#C2CBD7"),
        CardBorderStrong = Color.Parse("#A7B3C2"),
        TextPrimary = Color.Parse("#111827"),
        TextSecondary = Color.Parse("#44505F"),
        TextMuted = Color.Parse("#5F6B7A"),
        Accent = Color.Parse("#2563EB"),
        OnAccent = Color.Parse("#FFFFFF"),
        Success = Color.Parse("#15803D"),
        Warning = Color.Parse("#B45309"),
        Danger = Color.Parse("#B91C1C"),
        ChartFill = Color.Parse("#1A2563EB")
    };

    /// <summary>Deep green-black with an emerald accent.</summary>
    /// <remarks>
    /// The green the set was missing. The surfaces carry a green cast rather than being neutral grey, so the accent
    /// reads as part of the palette instead of a stripe laid over it, and the emerald clears 9:1 on the page — the
    /// strongest accent of the dark palettes.
    /// </remarks>
    public static ThemePalette Forest { get; } = new()
    {
        Page = Color.Parse("#0A1310"),
        Card = Color.Parse("#111E19"),
        CardHover = Color.Parse("#172A22"),
        Panel = Color.Parse("#0D1814"),
        Rail = Color.Parse("#0C1613"),
        CardBorder = Color.Parse("#2A4038"),
        CardBorderStrong = Color.Parse("#3C5A4D"),
        TextPrimary = Color.Parse("#E6F2EC"),
        TextSecondary = Color.Parse("#A8C4B6"),
        TextMuted = Color.Parse("#7E9C8D"),
        Accent = Color.Parse("#34D399"),
        OnAccent = Color.Parse("#0A1310"),
        Success = Color.Parse("#4ADE80"),
        Warning = Color.Parse("#FBBF24"),
        Danger = Color.Parse("#F87171"),
        ChartFill = Color.Parse("#1A34D399")
    };

    /// <summary>Near-black with a cyan accent, for a machine that lives under a keyboard light.</summary>
    /// <remarks>
    /// The one palette with a cool accent on a blue-black page. The surfaces are darker than SlateSteel's and the
    /// accent is cyan rather than ice-blue, which is what keeps the two apart at a glance: SlateSteel is a desk
    /// palette, this one is a lit keyboard.
    /// </remarks>
    public static ThemePalette NeonStreet { get; } = new()
    {
        Page = Color.Parse("#08080F"),
        Card = Color.Parse("#101019"),
        CardHover = Color.Parse("#181826"),
        Panel = Color.Parse("#0B0B14"),
        Rail = Color.Parse("#0A0A11"),
        CardBorder = Color.Parse("#33334F"),
        CardBorderStrong = Color.Parse("#3F3F63"),
        TextPrimary = Color.Parse("#F0F0FF"),
        TextSecondary = Color.Parse("#A9A9D0"),
        TextMuted = Color.Parse("#7E7EA8"),
        Accent = Color.Parse("#22D3EE"),
        OnAccent = Color.Parse("#08080F"),
        Success = Color.Parse("#4ADE80"),
        Warning = Color.Parse("#FDE047"),
        Danger = Color.Parse("#F472B6"),
        ChartFill = Color.Parse("#1A22D3EE")
    };

    /// <summary>Pure black, pure white ink and a yellow accent: the palette for a screen read at a glance.</summary>
    /// <remarks>
    /// Not a mood, a measurement. Every pair here is pushed as far apart as the sRGB cube allows: white ink on a
    /// near-black card is 19.4:1, the rule is 3:1 against the page rather than the 1.45:1 the other palettes settle
    /// for, and the yellow accent is 14.7:1. A reader who needs the window legible from across a room, or who has
    /// the display at 30 % brightness, gets the same information as everyone else.
    /// </remarks>
    public static ThemePalette HighContrast { get; } = new()
    {
        Page = Color.Parse("#000000"),
        Card = Color.Parse("#0D0D0D"),
        CardHover = Color.Parse("#1A1A1A"),
        Panel = Color.Parse("#050505"),
        Rail = Color.Parse("#030303"),
        CardBorder = Color.Parse("#5A5A5A"),
        CardBorderStrong = Color.Parse("#8A8A8A"),
        TextPrimary = Color.Parse("#FFFFFF"),
        TextSecondary = Color.Parse("#E0E0E0"),
        TextMuted = Color.Parse("#B8B8B8"),
        Accent = Color.Parse("#FFD400"),
        OnAccent = Color.Parse("#000000"),
        Success = Color.Parse("#00E676"),
        Warning = Color.Parse("#FFB300"),
        Danger = Color.Parse("#FF5252"),
        ChartFill = Color.Parse("#1AFFD400")
    };

    /// <summary>Deep violet ground with a hot-pink accent: the sunset palette.</summary>
    /// <remarks>
    /// The one palette whose ground is a colour rather than a grey. The page is a violet-black, the card a step
    /// lighter and a step warmer, and the accent is a hot pink that reads at 5.5:1 against the page. The secondary
    /// ink is a pale lilac rather than a neutral grey, which is what keeps the whole window in one temperature
    /// instead of putting a grey card on a purple page.
    /// </remarks>
    public static ThemePalette ViceSunset { get; } = new()
    {
        Page = Color.Parse("#140A22"),
        Card = Color.Parse("#1E1030"),
        CardHover = Color.Parse("#2A1742"),
        Panel = Color.Parse("#180C28"),
        Rail = Color.Parse("#110820"),
        CardBorder = Color.Parse("#4A2A6B"),
        CardBorderStrong = Color.Parse("#6B3E96"),
        TextPrimary = Color.Parse("#FFF0F8"),
        TextSecondary = Color.Parse("#D9B8E8"),
        TextMuted = Color.Parse("#B08CC8"),
        Accent = Color.Parse("#FF2E88"),
        OnAccent = Color.Parse("#140A22"),
        Success = Color.Parse("#3BE8A0"),
        Warning = Color.Parse("#FFC24B"),
        Danger = Color.Parse("#FF5252"),
        ChartFill = Color.Parse("#1AFF2E88")
    };

    /// <summary>Near-black with a signal-yellow accent: the chrome palette.</summary>
    /// <remarks>
    /// The accent is the one colour in the set that is not a hue but a warning: signal yellow on a near-black
    /// ground, 16.4:1 against the page, so the selected tab and the primary button are the first thing the eye
    /// lands on. The surfaces are neutral rather than blue-black, which is what keeps it apart from NeonStreet:
    /// that palette is a lit keyboard, this one is a printed circuit.
    /// </remarks>
    public static ThemePalette CyberChrome { get; } = new()
    {
        Page = Color.Parse("#0A0A0C"),
        Card = Color.Parse("#15151A"),
        CardHover = Color.Parse("#1F1F26"),
        Panel = Color.Parse("#0D0D10"),
        Rail = Color.Parse("#08080A"),
        CardBorder = Color.Parse("#3A3A44"),
        CardBorderStrong = Color.Parse("#55555F"),
        TextPrimary = Color.Parse("#F2F2EC"),
        TextSecondary = Color.Parse("#B8B8B0"),
        TextMuted = Color.Parse("#8E8E88"),
        Accent = Color.Parse("#FCEE0A"),
        OnAccent = Color.Parse("#0A0A0C"),
        Success = Color.Parse("#00E5A0"),
        Warning = Color.Parse("#FF9F1C"),
        Danger = Color.Parse("#FF3B5C"),
        ChartFill = Color.Parse("#1AFCEE0A")
    };

    /// <summary>Rose-tinted paper with a plum accent: the light palette that is not neutral.</summary>
    /// <remarks>
    /// The third light palette, and the only one with a colour cast of its own. WarmCream is paper and CleanWhite is a
    /// whiteboard; this one is a rose-grey stock, so the page carries a hue rather than a temperature. The accent is a
    /// deep plum rather than the brick red WarmCream uses, which is what keeps the two apart in the picker: a reader
    /// comparing the swatches sees a pink page and a plum rule against a beige page and a red one.
    /// </remarks>
    public static ThemePalette RoseQuartz { get; } = new()
    {
        Page = Color.Parse("#F7EAEF"),
        Card = Color.Parse("#FFFAFC"),
        CardHover = Color.Parse("#F4E2E9"),
        Panel = Color.Parse("#F0DEE5"),
        Rail = Color.Parse("#F3E6EC"),
        CardBorder = Color.Parse("#D2AEBD"),
        CardBorderStrong = Color.Parse("#BC8C9D"),
        TextPrimary = Color.Parse("#2A1620"),
        TextSecondary = Color.Parse("#5E3F4B"),
        TextMuted = Color.Parse("#7C5A66"),
        Accent = Color.Parse("#9E2B5C"),
        OnAccent = Color.Parse("#FFFFFF"),
        Success = Color.Parse("#2F7D4F"),
        Warning = Color.Parse("#9A5B12"),
        Danger = Color.Parse("#A81E3C"),
        ChartFill = Color.Parse("#1A9E2B5C")
    };

    /// <summary>Deep navy with a brass accent: the one cool ground carrying a warm accent.</summary>
    /// <remarks>
    /// Every other palette keeps its accent in the same temperature family as its ground — a blue page gets a blue
    /// accent, a warm page a warm one. This one deliberately does not: the surfaces are an indigo-leaning navy and the
    /// accent is brass, so the selected tab and the primary button read as a lit lamp on a dark blue desk. The navy is
    /// pushed towards indigo rather than SlateSteel's cyan-blue, which is what stops the two reading as one palette at
    /// two brightnesses.
    /// </remarks>
    public static ThemePalette Navy { get; } = new()
    {
        Page = Color.Parse("#0A0F2E"),
        Card = Color.Parse("#121A45"),
        CardHover = Color.Parse("#1A2459"),
        Panel = Color.Parse("#0D1338"),
        Rail = Color.Parse("#0B1132"),
        CardBorder = Color.Parse("#2A3570"),
        CardBorderStrong = Color.Parse("#3B4A93"),
        TextPrimary = Color.Parse("#EDEFFA"),
        TextSecondary = Color.Parse("#B4BCE0"),
        TextMuted = Color.Parse("#8B95C4"),
        Accent = Color.Parse("#F5C542"),
        OnAccent = Color.Parse("#0A0F2E"),
        Success = Color.Parse("#4ADE80"),
        Warning = Color.Parse("#FBBF24"),
        Danger = Color.Parse("#F87171"),
        ChartFill = Color.Parse("#1AF5C542")
    };
}
