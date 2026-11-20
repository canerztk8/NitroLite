# Languages, themes and typefaces

**Eight languages.** English, Türkçe, Deutsch, Español, Русский, Português, हिन्दी and Bahasa Indonesia. Pick one in
**Settings → Language**; NitroLite restarts itself in about a second to apply it. On first launch it follows the
Windows display language when it is one of the eight, and falls back to English otherwise. Numbers, percentages and
dates follow the chosen language; technical terms (Turbo, Boost, OSD, MUX) stay untranslated.

The translations were written by the author, not by native speakers. If a word reads wrong in your language, please
[open a translation issue](https://github.com/canerztk8/NitroLite/issues/new?title=Translation%20correction&labels=translation)
with the language and the correct text.

**Fourteen themes.** **Settings → Theme** offers **System default** plus fourteen palettes, each shown as a preview
card with a row of five swatches — page, card, rule, ink and accent, in the order the palette defines them.

| | Palette | Ground |
|---|---|---|
| Light | **Warm Cream** (the default "paper" theme) | warm off-white |
| Light | **Clean White** | neutral white, cool grey rule |
| Light | **Dusty Rose** | muted rose paper, plum accent |
| Dark | **Dark** | charcoal |
| Dark | **Midnight OLED** | true black |
| Dark | **Slate Steel** | cold anthracite |
| Dark | **Amber Console** | warm amber |
| Dark | **Nord Frost** | glacier turquoise |
| Dark | **Forest** | deep green, moss accent |
| Dark | **Neon Street** | near-black, magenta accent |
| Dark | **High Contrast** | black ground, white ink, a 3:1 rule |
| Dark | **Neon Sunset** | violet-black, hot-pink accent |
| Dark | **Chrome City** | near-black, signal-yellow accent |
| Dark | **Navy** | deep navy, steel-blue accent |

All fourteen share one key set and the same paper-and-ink hierarchy; only the temperature of the ground and the hue
of the accent move. Every palette is held to the same six contrast floors — body ink on a card at 4.5:1, a card rule
against the page at 1.45:1, muted ink at 3:1, the accent against the page at 3:1 — so a palette cannot ship
unreadable, and a test fails if one does. System default follows the Windows app theme, resolved at launch. The OSD
is dark in every case. Like the language, the theme applies after a quick restart.

**Neon Sunset** and **Chrome City** are a nod to two aesthetics rather than to two products: the Miami sunset the
*Vice City* games are drawn in, and the yellow-on-black chrome of the cyberpunk genre. Neither palette is taken from
a game — the colours are NitroLite's own — and NitroLite is not affiliated with Rockstar Games or CD Projekt Red.

**Eight typefaces.** **Settings → Typeface** picks the face the whole window is drawn in, and each name in the picker
is drawn in its own face so you can see the difference before choosing. All eight travel inside the executable, so
the window looks the same on a machine that has installed nothing:

| Face | Character | Scripts it carries |
|---|---|---|
| **Quicksand** (default) | geometric sans, rounded terminals | Latin |
| **Exo 2** | technical geometric, slightly squared | Latin, Cyrillic |
| **Oxanium** | squared, HUD-like, the lightest file of the set | Latin |
| **Rubik** | rounded, low contrast | Latin, Cyrillic |
| **JetBrains Mono** | monospace, drawn for code | Latin, Cyrillic |
| **Rajdhani** | condensed, drawn for a HUD | Latin, Devanagari |
| **Space Grotesk** | retro-futuristic grotesque | Latin |
| **Chakra Petch** | squared techno, cut corners | Latin |

Every file is subset to the ranges the eight catalogues can put on screen, so a face that lists Latin carries Latin,
Latin Extended and the punctuation the interface uses, and nothing else. The five behind them are the machine's own —
Segoe UI, Arial, Verdana, Tahoma and Georgia — for a reader who would rather have the system face.

**Rajdhani is offered to Hindi readers only.** It is the one face in the set drawn for Devanagari, and a reader who
does not have Hindi on screen gains nothing from a condensed HUD face. It appears when the interface language is Hindi
*or* when the Windows display language is Hindi, so a Hindi reader who set the interface to English still sees it. A
reader who has already selected it keeps it in the list after switching away, because a picker whose selected item is
not among its items draws blank.

Every stack ends in the Devanagari fallbacks and a generic family, so a face without Devanagari — Quicksand, for
instance — falls through to Nirmala UI for Hindi rather than drawing empty boxes. Nothing renders as tofu in any of
the eight languages.

---

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
