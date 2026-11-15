---
name: NitroLite
description: A lightweight NitroSense alternative for Acer Nitro laptops — the visual world of Kandilli seismograph paper
colors:
  paper: "#F2EDE3"
  paper-deep: "#E8E0D2"
  card: "#F8F4EB"
  card-hover: "#EDE6D8"
  rail: "#EAE3D5"
  rule: "#D5CBB8"
  rule-strong: "#B8AB93"
  ink: "#1B1917"
  ink-soft: "#4A4238"
  ink-faint: "#6E6558"
  vermilion: "#C0392B"
  vermilion-deep: "#9B2C1F"
  olive: "#5F6B3E"
  slate: "#4A6274"
  on-accent: "#F7F2E8"
  warn-bg: "#F0E2D2"
typography:
  display:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "18px"
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: "0em"
  title:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "14px"
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: "0em"
  body:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.45
    letterSpacing: "0em"
  label:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "11px"
    fontWeight: 400
    lineHeight: 1.4
    letterSpacing: "0em"
  hint:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "11.5px"
    fontWeight: 400
    lineHeight: 1.4
    letterSpacing: "0em"
  micro:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "10px"
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: "0.02em"
  figure:
    fontFamily: "Cascadia Mono, Consolas, Courier New, monospace"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.3
    letterSpacing: "0em"
rounded:
  none: "0px"
  sm: "1px"
  md: "2px"
  card: "8px"
  badge: "7.5px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "16px"
  xl: "20px"
components:
  mode-card:
    backgroundColor: "{colors.paper}"
    borderColor: "{colors.rule}"
    textColor: "{colors.ink-soft}"
    rounded: "{rounded.card}"
    padding: "9px 11px"
  mode-card-active:
    backgroundColor: "{colors.paper}"
    borderColor: "{colors.vermilion}"
    textColor: "{colors.ink}"
    rounded: "{rounded.card}"
    padding: "9px 11px"
  nav-item:
    backgroundColor: "transparent"
    textColor: "{colors.ink-soft}"
    rounded: "{rounded.md}"
    padding: "8px 10px"
  nav-item-active:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "8px 10px"
  seg-button:
    backgroundColor: "transparent"
    textColor: "{colors.ink-soft}"
    rounded: "{rounded.md}"
    padding: "5px 14px"
  seg-button-active:
    backgroundColor: "{colors.paper-deep}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "5px 14px"
  section:
    backgroundColor: "{colors.card}"
    borderColor: "{colors.rule}"
    textColor: "{colors.ink}"
    rounded: "{rounded.card}"
    padding: "12px 14px"
  info-badge:
    backgroundColor: "transparent"
    borderColor: "{colors.rule-strong}"
    textColor: "{colors.ink-faint}"
    rounded: "{rounded.badge}"
    padding: "0px"
  stat-cell:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    rounded: "{rounded.none}"
    padding: "8px 16px 0px 0px"
  stamp:
    backgroundColor: "transparent"
    textColor: "{colors.vermilion}"
    rounded: "{rounded.sm}"
    padding: "2px 7px"
---

## Overview

NitroLite's visual world is **Kandilli seismograph paper**: the paper a drum recorder wraps around itself. Warm cream stock covers the whole surface, a single ink carries the body, a single vermilion marks "now" and anything that genuinely needs attention. The world supplies only ground, rule, card, typography and one signature gesture; layout, navigation and control behaviour stay familiar Windows.

The signature gesture: **the recording strip.** Every screen carries the last sixty seconds as a continuous ink trace across its top, a dashed vermilion "now" line at the right edge, a vermilion dot at the end of the trace. The pen is ink, the stamp is vermilion — that distinction governs the whole world.

Content is grouped into **section cards**: a ground one tone off the paper, a 1px `rule` frame, an 8px corner. A card is the unit of grouping, not decoration — it says "these controls belong together" and nothing more. The one place where equally sized boxes are right is the operating-mode grid: four mutually exclusive choices of equal weight.

Surface mode **Operate**: the user finishes a task. Not decoration; scan speed and consistency matter. The brand lives in the precise details: alignment, one accent colour, honest empty states, measurement drawn in ink.

## Colors

- **paper `#F2EDE3`** — the window ground. The single layer under everything; a warm cream that does not tire the eye on a screen left open all day.
- **paper-deep `#E8E0D2`** — the ground of the recording strip and the chart, the fill of the selected segment button. One shade darker than the paper.
- **card `#F8F4EB`** — the section card ground and the form-field ground. A touch lighter than the paper, so a card reads as a sheet laid on the page rather than a hole cut into it.
- **card-hover `#EDE6D8`** — only while the pointer is over it: a mode tile, a row that answers a click.
- **rail `#EAE3D5`** — the sidebar. A touch darker than the ground, so navigation reads as a "frame".
- **rule `#D5CBB8`** — the default 1px line, and the frame of every section card and mode tile. **rule-strong `#B8AB93`** — focus, hover, the measurement bar track, the info badge ring.
- **ink `#1B1917`** — body text, the fill of measurement bars, the chart trace, a checked box. Not pure black; ink pressed into paper.
- **ink-soft `#4A4238`** — descriptions, secondary values. **ink-faint `#6E6558`** — micro labels, "SELECT" hints, the `hint` line under a heading, the `?` in the info badge.
- **vermilion `#C0392B`** — the world's only accent, and **for exactly three jobs**: (1) what is live right now — the mode in the title bar, the end point of the chart, the "now" line; (2) a state that needs attention — a threshold crossed, a maintenance suggestion, a blocked mode; (3) destructive action text. Nowhere else.
- **vermilion-deep `#9B2C1F`** — destructive button text and warning headings. A touch darker than the bright vermilion, so it shouts less as button text.
- **olive `#5F6B3E`** — verified good state: battery health, SSD temperature, nominal stability.
- **slate `#4A6274`** — the neutral third measurement (battery voltage, the GPU mode badge).
- **on-accent `#F7F2E8`** — text and tick on an ink fill.
- **warn-bg `#F0E2D2`** — the ground of the warning band, together with vermilion text.

Colour never carries meaning on its own: every state chip also writes text ("ACTIVE", "SELECT", "Disabled", "Service Recommended").

**Measurement bars carry no colour.** The track is `rule`, the fill is `ink`. A bar shows a measurement, not a judgement; the judgement lives in the text and the badge beside it. The only exceptions are disk usage above 85 % and a running discharge — a real threshold and a real live state.

### Palettes

The colours above are the **Light** palette. Five more ship beside it — `Dark`, `MidnightOled`, `SlateSteel`, `AmberConsole`, `NordFrost` — and `System` follows the Windows light/dark setting, so the picker offers seven entries over six dictionaries. The world does not change between them: same paper-and-ink hierarchy, same single accent, same three jobs for it. Only the temperature of the ground and the hue of the stamp move.

- **One key set, no exceptions.** Every dictionary in `NitroLite/Themes/` defines the identical set of `Color` and `SolidColorBrush` keys. A palette that is missing one throws `ResourceReferenceKeyNotFoundException` the moment a control asks for it, so `ThemeDictionaryTests` compares the key sets rather than trusting the eye.
- **`ThemeCatalog.IsDarkTheme` is the only place that decides "dark".** The DWM window frame, the OSD and the chart ink ask it; nothing compares against `AppTheme.Dark` or `!= AppTheme.Light`. A new palette is one enum member, one dictionary and one entry in `ThemeCatalog.All`.
- **Contrast floors, measured not eyeballed.** `Ink` on `CardBg` ≥ 4.5:1 (WCAG AA) and `BorderDark` on `Paper` ≥ 1.45:1, so a 1px card frame never dissolves into the ground. `ThemeDictionaryTests` computes both ratios for every palette; a hand-picked hex that fails is a failing test, not a judgement call.
- **The accent is `Vermilion`; danger is `AccentRed`.** In a palette whose accent is not red — Slate Steel's ice blue, Nord Frost's turquoise — `Vermilion` carries the stamp (the live mode, the active tile frame, the "now" line) and `AccentRed` keeps the warning hue. The three-jobs rule applies to whichever brush is the accent.
- **The stored value is the palette name.** `config.json` carries `"theme": "NordFrost"`; the legacy numbers (`0`, `1`, `2`) still read, and a name the app does not know falls back to `System` rather than leaving the window unthemed.

## Typography

Two families, a strict division of labour: **recorded figures** in Cascadia Mono (falling back to Consolas, Courier New), **written text** in Segoe UI Variable Text. No custom font is loaded — the lightweight constraint.

| Role | Size | Weight | Family | Use |
|---|---|---|---|---|
| display | 18px | SemiBold | Segoe UI | Page title, one per screen |
| title | 14px | SemiBold | Segoe UI | Section heading |
| tile | 13px | SemiBold | Segoe UI | Mode tile name |
| body | 12px | Regular | Segoe UI | Description paragraph |
| hint | 11.5px | Regular | Segoe UI | The one-line summary under a section heading |
| label | 11px | Regular / SemiBold | Segoe UI | Field label, button text, state chip, mode tile label |
| micro | 10px | SemiBold | Segoe UI | Measurement label, "SELECT", axis text |
| figure | 12–20px | Regular / SemiBold | Cascadia Mono | Every numeric reading, axis value, RPM, °C |

**The hint is one line.** A section heading is followed by at most one line of `hint` in `ink-faint`; anything longer belongs in the `(?)` badge. The heading and the figures lead, the prose recedes — a reader scanning the page should meet numbers, not sentences.

Mono figures align in a column: scanning a list of values downwards, the digits stack. Label on the left, value on the right, both on the same baseline.

WPF has no letter-spacing setting; the airiness of the micro labels comes from uppercase and `ink-faint`, not from `letter-spacing`.

## Layout

- Window: 1060×730 default, grows to 2560×1380. Sidebar **215px** default, resizable 175–290px by dragging its edge, content `*`. The width is persisted; double-clicking the edge restores 215px.
- Content padding **26px** horizontal, **20px** vertical. Between sections **14–18px**.
- Vertical rhythm in multiples of 4: 4 / 8 / 12 / 16 / 20.
- **Sections are cards.** A section is a `card` ground with a 1px `rule` frame, an 8px corner and 12px 14px padding. The card is the grouping unit: it says "these controls belong together" and nothing more. Cards stack in one column and are never nested.
- **One grid, and only for modes.** The four operating modes are the single place where equally sized boxes are correct, because they are four mutually exclusive choices of equal weight: one row of four columns, 6px gaps, no wrapping. On battery the high-power pair is hidden and the row keeps its column widths, so the remaining tiles do not stretch.
- Measurements are laid out row by row, not box by box. In a four-column reading strip each cell carries its own 1px line above it; there is no box, shadow or ground difference between cells.
- On a wide screen the rows lengthen; that is correct — paper is wide. Content is not centred, it is left-aligned.
- Form fields (ComboBox, TextBox) are bounded by their content, they do not fill the row.
- The right-aligned action column sits at the same x in every section; buttons do not float in the middle of a row.

## Elevation & Depth

**No shadow.** Hierarchy is built from three things: paper tone (rail → paper → card → paper-deep), a 1px rule, and space.

- `DropShadowEffect` is not used; anywhere. Windows draws the window edge.
- No gradient, blur, glass effect or `AllowsTransparency` — let WPF draw on the GPU and keep idle consumption low.
- The layer count is kept low: a card is one `Border`, not a stack of them. Every extra box is idle consumption.
- Depth comes from two places: the recording strip is a band one shade darker than the paper, and a section card is a ground one shade lighter, each framed by a 1px rule. Neither casts a shadow; the frame and the tone difference do the work.

## Shapes

- Corner radius **0 / 1 / 2px** for controls, **8px** for section cards and mode tiles, **7.5px** for the info badge (a 15px circle). Nothing else is rounded: a button, a chip, a field and a checkbox stay square or near-square, so the 8px corner reads as "this is a group" and never as a generic pill.
- The 1px rule keeps the same weight everywhere. 1.5px only on the chart trace.
- Measurement bars are **4–6px** tall with square ends.
- Checkboxes and radio buttons are **14×14px squares**, 1px radius. Not round — square, in the language of recording paper.
- Slider: 3px track, **8×16px** square thumb, 4px of margin at each end of the track (the thumb is not clipped at the edge).
- The navigation marker is a **5×5px** square. The mode tile carries no marker: its state is the frame and the chip.

## Icon

The icon is the world in one square: `paper-deep` ground, a single `ink` trace, a `vermilion` dot at the end of the trace. No gradient, glow, frame or rounded corner — the icon is square like a strip of paper.

- **Ground** `paper-deep` across the whole square. Not `paper`: it reads against a white Explorer background and stands forward on a dark taskbar.
- **Trace** `ink`, 72/1024 of the canvas thick, round joints. It enters from the left edge and ends in the dot on the right: the recording is running, the pen is there right now.
- **Dot** `vermilion`, 136/1024 in diameter, where the trace ends. The only colour in the icon; no other element carries colour.
- **One event.** The trace carries one: a fast rise, a deep turn, a return to the baseline. A second event turns to noise at 16px and is not added.
- **Sizes** 16 / 20 / 24 / 32 / 40 / 48 / 64 / 96 / 128 / 256. The icon is right if it reads as a single spike at 16px; every change is checked at that size.
- The icon does not change with the theme. A single `app.ico` feeds the window, the tray and the exe.

## Components

**Recording strip (signature)** — a 72px-tall `paper-deep` band across the top of the screen, framed by a 1px `rule`. Inside it, the last sixty seconds as a continuous `ink` trace (1.4px), a dashed vermilion "now" line at the right edge, a vermilion vertical tick at mode changes. Above it, micro labels: "RECORDING · 42 SECONDS" on the left, "RANGE 58–71 °C" and "NOW 64 °C" on the right. With no data it says "Waiting for a recording" in the middle; an empty line is not drawn.

**Telemetry chart** — the same language, larger (140px). The trace is `ink`, the dot at the end is vermilion. The scale sits **on the right in its own gutter**, separated by a 1px vertical line; a label never lands on top of the trace. Three horizontal guide lines in `rule`.

**Mode card** — the four operating modes as one row of four equal tiles, 6px apart. Each tile is a `paper` ground — one tone darker than the card it sits in, so the tile reads as a well rather than a second card — with a 1px `rule` frame and an 8px corner, 9px 11px padding: the mode name in `tile`, a two-word label in `label` at 80% opacity, and a state chip on the right. The chip is "SELECT" in a `rule` frame (`ink-faint`) on an inactive tile, "ACTIVE" in a vermilion frame (vermilion text) on the active one. The active tile's frame turns vermilion and its text `ink`; the rest stay `ink-soft`. **State is built by the frame and the chip, never by fill** — no tile turns into a coloured band. Hover lifts the ground to `card-hover`.

The template draws the resting frame and the accent frame as two layers, because the code-behind writes the active colour into `BorderBrush` and clears `Background`. A template that derived its resting look from `Background` would break that contract, so keep the two layers.

**Nav item** — the same language, smaller: 10px 8px padding, 2px radius. The active one takes a `paper` ground and a 5×5px vermilion square appears in its left margin. No strip, no fill.

**Seg button** — groups of 2–4 options such as fan mode and CPU boost. The empty state is a transparent ground + `rule` frame + `ink-soft` text. The selected one is a `paper-deep` ground + `ink` frame + `ink` text + SemiBold + a 2px `ink` line underneath: it reads as pressed into the paper, not painted red.

**Section** — the `CardBorder` style: `card` ground, a 1px `rule` frame, an 8px corner, 12px 14px padding. Title in `title`, at most one line of `hint` below it, then the content. All 21 uses inherit the same language, so every page groups its content the same way.

**Info badge** — a 15px circle, 1px `rule-strong` frame at 75% opacity, a 9.5px `?` in `ink-faint`, `Cursor=Hand`. It sits 6px to the right of a heading and holds the detail the one-line hint leaves out. It answers a hover with its `ToolTip` after 120ms, and a click opens the same text in a themed popup under the badge, so the detail is reachable without knowing to hover. It stays out of the tab order. One badge per heading at most.

**Stat cell** — not a card: a 1px line on top, then a micro label + mono value + note. Four of them side by side form a reading strip.

**Checkbox / Radio** — 14px square, `rule-strong` frame; when checked, an `ink` fill + `on-accent` tick. Text 7px to the right. The radio is square too — it reads as the same family.

**Slider** — 3px track (`rule`), the filled part `ink`, the square thumb `ink`.

**Stamp** — the state chip: a 1px frame, 4px radius, 6px 1px padding, 9px micro uppercase. "SELECT" in a `rule` frame with `ink-faint` text, "ACTIVE" in a vermilion frame with vermilion text. The mode grid carries four of them, one per tile; elsewhere a screen carries at most one.

**Toast** — `card` ground, `rule-strong` frame, 2px radius, 200ms fade. No shadow.

## Do's and Don'ts

**Do**

- Draw the pen in ink and the stamp in vermilion. Vermilion appears at most three times on a screen, and each one says "right now" or "attention".
- Write the state in text as well: "ACTIVE", "SELECT", "Disabled", "Waking…".
- Show an unreadable value as `--`; do not guess. Mark an estimated value with "~" and "(estimated)".
- Build hierarchy from paper tone + 1px rule + space. A card is a grouping device, not a decoration.
- Keep a section's summary to one line; move the rest into the `(?)` badge.
- Set every figure in mono; align the label and the value on the same baseline.
- When adding a new measurement, add it as a row to the existing reading strip; do not open a new card.
- Write user-facing text in Turkish; keep code identifiers in English.

**Don't**

- Shadow, gradient, blur, glass effect, glow.
- Colour a measurement bar by state — the bar measures, the badge judges.
- Paint a checkbox, radio, slider fill or selected segment vermilion.
- A small "kicker" label above a heading.
- A coloured 1px strip on top of or beside a card.
- Nested boxes: a section inside a section, a card inside a card. The mode tiles are the one exception — they are controls inside a section card, not cards inside a card.
- A grid of equally sized cards, except the four operating modes. Everywhere else a grid flattens the hierarchy.
- A paragraph under a heading. One line of `hint`, then the `(?)` badge.
- Overlap a chart axis label on the trace; the scale stays in its own gutter.
- Gamer RGB: neon, multi-colour accents, animated transitions.
- Continuous animation or an effect that burns more than one frame per second. The only motion: a 200ms toast fade.
- Emoji.
