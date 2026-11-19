# Changelog

Every notable change to NitroLite is recorded here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

`.github/workflows/release.yml` reads the section for the tag it is publishing and uses it as the body of the
GitHub release, so this file *is* the release notes. A tag with no section here falls back to the commit log.
Releases before 1.2.6 are described on the [Releases](https://github.com/canerztk8/NitroLite/releases) page.

## [1.2.7] - 2026-11-19

A settings page rebuilt around what people actually change, eight more palettes, seven more typefaces, and a
release that finally ships the trimmed build. 44 commits since 1.2.6.

### Settings, rebuilt

The page had grown a card per feature and a paragraph per card. It now opens with three collapsed sections and
only the controls, and the prose that was worth keeping moved into a `?` next to the heading.

- **Services & Tools** is one card instead of three. The subtitle is gone, the short service name under every
  Acer service is gone, and **System and hardware repair tools** is merged into it rather than sitting beside it
  under its own heading. The card is collapsed by default, so the page no longer opens on a wall of switches.
- **Start-up and integration** is collapsed by default too, and every description under it is gone. The one
  exception is the elevated logon task, which keeps a `(recommended)` note. **Run at Windows startup** is no
  longer a separate row on Windows — **Start in the tray at startup** covers it, and turning the elevated task on
  now turns tray start on with it, so the two can no longer disagree.
- **The Nitro key** moved into the same card. Its three paragraphs are behind the `?`; the row itself shows only
  the launcher state, Enabled or Disabled.
- **Update, diagnostics and backup** is grouped under two captions — *Diagnostics and reports* and *Backup* —
  instead of eight buttons in a row.
- **The Notes card is gone.** It was a scratch pad nobody used. In its place is a **Support** card with a Ko-fi
  button, a GitHub button, and a line saying what the project has cost. Translated into all eight languages.
- **The font picker sits beside the language picker**, and every description under both is deleted. Each font
  name in the list is drawn in its own face, and so is the closed box, so the difference is visible before you
  pick.
- **The window can be resized by dragging.** A grip in the bottom-right corner calls `BeginResizeDrag`, and it
  hides itself when the window is maximised. No layout code changed, so nothing about the footprint moved.

### Palettes

Eight new ones, for fourteen in total. Each is held to the same six contrast floors as the rest, and a test fails
if one ships unreadable.

- **Clean White** — neutral white with a cool grey rule, for a reader who finds Warm Cream too warm.
- **Forest** — deep green ground, moss accent.
- **Neon Street** — near-black with a magenta accent.
- **High Contrast** — black ground, white ink, a 3:1 rule.
- **Neon Sunset** — violet-black, hot-pink accent.
- **Chrome City** — near-black, signal-yellow accent.
- **Dusty Rose** — muted rose paper with a plum accent, the third light palette.
- **Navy** — deep navy with a steel-blue accent.

**Neon Sunset** and **Chrome City** are a nod to two aesthetics rather than to two products: the Miami sunset the
*Vice City* games are drawn in, and the yellow-on-black chrome of the cyberpunk genre. Neither palette is taken
from a game — the colours are NitroLite's own — and NitroLite is not affiliated with Rockstar Games or CD Projekt
Red. The theme card is now a strip of five swatches rather than a large preview, which fits two more palettes on
the page.

### Typefaces

Seven new faces, for eight bundled in total, all SIL OFL 1.1 and all subset to the ranges the eight catalogues
can put on screen:

- **Exo 2** — technical geometric, slightly squared. Latin and Cyrillic.
- **Oxanium** — squared and HUD-like, the lightest file of the set.
- **Rubik** — rounded, low contrast. Latin and Cyrillic.
- **JetBrains Mono** — monospace, drawn for code. Latin and Cyrillic.
- **Rajdhani** — condensed, drawn for a HUD. Latin and Devanagari.
- **Space Grotesk** — retro-futuristic grotesque.
- **Chakra Petch** — squared techno with cut corners.

**Rajdhani is offered to Hindi readers only.** It is the one face in the set drawn for Devanagari, and a reader
who does not have Hindi on screen gains nothing from a condensed HUD face. It appears when the interface language
is Hindi *or* when the Windows display language is Hindi, so a Hindi reader who set the interface to English still
sees it. A reader who has already selected it keeps it in the list after switching away, because a picker whose
selected item is not among its items draws blank.

Every stack still ends in the Devanagari fallbacks and a generic family, so a face without Devanagari falls
through to Nirmala UI for Hindi rather than drawing empty boxes.

### Performance

- **The capability probe runs off the UI thread.** The window opens while SMBIOS, the WMI schema and the model
  profile are still being read; the hardware controls stay in a "scanning" state until it finishes, so nothing can
  be pressed against a half-loaded capability set. `WhenReadyAsync()` gives the tests a deterministic hook instead
  of a `Task.Delay`.
- **The release is trimmed.** `PublishTrimmed=true` with `TrimMode=partial`: the framework is trimmed, while
  Avalonia, `System.Management` and the NitroLite assemblies are copied whole, because the XAML loader and the WMI
  COM interop resolve members by name. That is what takes the exe from 43 MB to 24 MB, and the download from
  44.6 MB to 19.5 MB. The published 1.2.6 asset predated this change; 1.2.7 is the first release that ships it.
- **Linux telemetry reads the sensor again.** `LinuxHwmon` was replaying the values the probe had cached, so a
  temperature that moved after startup never reached the window. It now reads `temp1_input` and the fan node on
  every tick, under the same lock the writer takes.
- **The Linux tick stopped allocating.** The `OrderBy().Select().ToArray()` chain per device and per file is gone,
  and the 64 KB `File.ReadAllBytes` buffer is rented from `ArrayPool<byte>.Shared` at 256 bytes instead.
- **The WMI worker protocol frames through one pooled buffer** instead of three arrays per packet.
- **Telemetry the sample never used is no longer read.** `GetGamingMiscSetting` and `GetGamingFanBehavior` were
  called every tick and thrown away; `AppendFanSpeed` now runs only when there is no tachometer.
- **The NitroSense watcher stops when its switches are off**, instead of polling a process that cannot appear.
- **Brushes are cached.** `HexToBrushConverter` no longer builds a `SolidColorBrush` per colour per binding.
- **The English catalogue is not re-read** on every `SetLanguage`, and the rows that were never translated are
  translated.

### Fixes

- **The fan floor clamps its own output.** A nonsense target — `NaN`, `Infinity`, a negative number — could reach
  the firmware through `ManualFanFloor`; it is now clamped to the `30–100 %` PWM bounds before it leaves.
- **Two startup races closed.** A probe that finished after the window had already been closed, and a second
  probe started while the first was still running, both left state behind.
- **CoolBoost and LCD overdrive are gone from the capability list.** Neither is a feature NitroLite has, and the
  first-run walkthrough was claiming to check for them.
- **The font licence stays out of the package.** `OFL.txt` was being copied into the zip root by the publish
  script; it now travels with the faces it belongs to.
- **The diagnostics libraries a loose publish ships are dropped** — `mscordaccore.dll`, `mscordbi.dll` and
  `Microsoft.DiaSymReader.Native.amd64.dll` were in the package and never loaded.

### Tests

**2652 tests, 2648 of which run by default** (four that change the real system stay opt-in behind
`NITROLITE_ENABLE_LIVE_HARDWARE_TESTS`). New this release:

- **`HardwareMatrixAndChaosTests`** drives five chassis profiles — a fully equipped AN16-41, a Predator
  Helios/Triton, an old Nitro 5 with a single lighting zone and no MUX, a Nitro V 15 with restricted WMI, and an
  unsupported device — through every page and command, and asserts no unhandled exception and a `--` fallback for
  every sensor that is missing.
- **A fake `sysfs` tree** builds `hwmon` (`acer`, `k10temp`, `coretemp`) and `platform_profile` in a temporary
  folder, then covers a missing node, a corrupt sensor file and a read-only `platform_profile`.
- **`ChaosHardwareDecorator`** injects `false`, `TimeoutException`, `0 °C`, `127 °C` and `NaN` through 100
  consecutive telemetry and setting cycles, and the settings, `FanCurve` and `ManualFanFloor` are fed `NaN`,
  `Infinity`, `-100` and `500` to prove they clamp instead of propagating.
- **Suspend and resume**, hundreds of concurrent mode, fan-curve and RGB commands against a live telemetry loop,
  a corrupt `profiles.json`, and a `/sys` tree whose `hwmon` directories renumber while the app is running.
- **`SettingsPageReworkTests`** pins the page this release rebuilt: the collapsed defaults, the merged cards, the
  removed rows, the tooltip that replaced the Nitro key prose, and the support card in all eight catalogues.

### Docs

`README.md` was rewritten for the person who just wants to install it: the download, the demo and the
compatibility table are at the top, a GIF of the app runs under them, and everything technical — the backends,
the elevation prompt, the command line, the architecture, the build, the tests — moved into an **Advanced**
section at the end. `DESIGN.md` records the two new palettes and the new counts.

### Upgrading

Extract the new zip over the folder you already have, or let the in-app updater do it. Settings, game profiles,
RGB profiles, thermal history and battery wear history live in `%LOCALAPPDATA%\NitroLite` and are not touched.
A theme or typeface you had selected keeps working; the two new light palettes and the seven new faces appear in
the pickers on the next launch.

## [1.2.6] - 2026-11-18

The largest release the project has had. The Windows front end was rewritten on Avalonia, the
platform-independent half of the app was extracted into `NitroLite.Core`, a Linux build was added, and the
hardware layer learned to describe laptops it has never seen. All of it is in the download.

### The window is a new window

- **Rewritten on Avalonia.** The WPF front end is retired. One UI code base now runs on Windows and Linux,
  which is what made the Linux build possible at all. The executable is still `NitroLite.exe`, so an existing
  1.2.x install updates in place instead of ending up with two copies.
- **A real theme engine.** `ThemeCatalog` drives every colour from one key set: two light palettes — the
  warm-cream paper theme and **Clean White** — and ten dark ones: `Dark`, **Midnight OLED** (true black),
  **Slate Steel** (cold anthracite), **Amber Console** (warm amber), **Nord Frost** (glacier turquoise),
  **Forest** (deep green, moss accent), **Neon Street** (near-black, magenta accent), **High Contrast**
  (black ground, white ink, a 3:1 rule), **Neon Sunset** (violet-black, hot-pink accent) and **Chrome City**
  (near-black, signal-yellow accent). Settings shows a preview card per theme instead of a list of names.
- **A layout that holds its shape.** Cards, a max-width content column, a navigation rail you can resize, ink
  sliders you can drag by the track, segmented selection controls, and single-click mode cards.
- **Live charts.** The telemetry graph draws as samples arrive, with mode changes marked on the timeline, so a
  temperature spike can be read against the fan curve that caused it.
- **Eight bundled typefaces.** The interface ships its own font stack, so a machine with an unusual default
  font no longer changes the layout. Quicksand is the default; Exo 2, Oxanium, Rubik, JetBrains Mono, Rajdhani,
  Space Grotesk and Chakra Petch travel beside it, and five system faces stay as the escape hatch.
- **Tray parity.** The tray menu carries the same modes, fan presets and lighting shortcuts as the window.
  Returning from the tray no longer rebuilds the window.
- **First-run walkthrough.** Three steps: what the probe found on this machine, which of it is supported, and
  the optional elevated auto-start. Skippable, and it does not come back once finished.
- **Capability-driven UI.** A control the firmware does not support is hidden, or disabled with the reason
  next to it, instead of failing when pressed.
- **Actionable toasts.** A failure says what to do next, and a long message stays on screen long enough to
  read.
- **Diagnostics bundle.** One button collects the log, the settings and the hardware report into a folder to
  attach to an issue.
- **Game Profiles page.** Per-game operating mode, CPU boost and fan curve, applied the moment the game
  starts, with a scan for installed games and a manual `.exe` picker.

### Typefaces and palettes

- **Seven new faces, all SIL OFL 1.1 and all inside the executable.** **Exo 2** (technical geometric,
  Cyrillic), **Oxanium** (squared, the lightest file of the set), **Rubik** (rounded, Cyrillic),
  **JetBrains Mono** (monospace, Cyrillic), **Rajdhani** (condensed HUD, Devanagari), **Space Grotesk**
  (retro-futuristic grotesque) and **Chakra Petch** (squared techno, cut corners). Each ships as three static
  instances — Regular, SemiBold, Bold — because Avalonia 11 exposes no variation axis and would otherwise draw
  every weight at the file's default.
- **Rajdhani is offered to Hindi readers only.** It is the one face drawn for Devanagari, and a reader without
  Hindi on screen gains nothing from a condensed HUD face. It appears when the interface language is Hindi *or*
  when the display language is Hindi, so a Hindi reader who set the interface to English still sees it. A reader
  who has already selected it keeps it in the list after switching away, because a picker whose selected item is
  not among its items draws blank.
- **Nothing renders as tofu.** Every stack ends in the Devanagari fallbacks and a generic family, so a face
  without Devanagari falls through to Nirmala UI for Hindi instead of drawing empty boxes. Verified by rendering
  the Hindi interface in each face.
- **The files were subset, not shipped whole.** Each face was cut to the ranges the eight catalogues can put on
  screen — Latin, Latin Extended, Cyrillic, Devanagari, punctuation, currency, arrows, geometric shapes and
  the emoji presentation selectors — and instanced at three weights. The eight faces together add 2.1 MB to the
  source tree and **926 KB to the published exe**, because single-file compression absorbs most of it.
- **No Reserved Font Name.** Each upstream `OFL.txt` was checked before subsetting, so the cut files legally
  keep their family names. The per-face copyright line and the exact change are in `third-party/<face>/NOTICE.md`.
- **Six new palettes.** **Clean White** (neutral white, cool grey rule), **Forest** (deep green ground, moss
  accent), **Neon Street** (near-black, magenta accent), **High Contrast** (black ground, white ink, a 3:1 rule
  and a saturated accent), **Neon Sunset** (violet-black ground, hot-pink accent) and **Chrome City**
  (near-black ground, signal-yellow accent). Twelve palettes now, two light and ten dark.
- **A nod, not a tie-in.** Neon Sunset and Chrome City are drawn after two aesthetics rather than two products:
  the Miami sunset the *Vice City* games are painted in, and the yellow-on-black chrome of the cyberpunk genre.
  Neither palette is sampled from a game, neither typeface is a game's logo face, and NitroLite is not
  affiliated with Rockstar Games or CD Projekt Red. The colours are the project's own and the fonts are SIL
  OFL 1.1, so nothing here is redistributed under someone else's terms.
- **Every palette is held to the same six contrast floors** — body ink on a card at 4.5:1, a card rule against
  the page at 1.45:1, muted ink at 3:1, the accent against the page at 3:1, and a strong rule stronger than a
  weak one — so a palette cannot ship unreadable. High Contrast clears every floor by a wide margin, and a test
  asserts that too.

### The hardware layer

- **31 model profiles.** Nitro 5, 16 and 17, Nitro V 15 and 16, Predator Helios 300/16/18/Neo, Predator
  Triton — Intel and AMD — each with its sensor byte offsets, reference RPM ceilings and known-broken method
  signatures. `profiles.user.json` adds a model without a new release.
- **Dynamic capability discovery.** A model that is not in the table is probed instead of assumed: the WMI
  schema, the method list and the registry decide what the window offers.
- **Full 4-zone WMI RGB.** The complete firmware sequence, per-zone colour writes, brightness scaling and the
  dynamic effects, routed through the one path that actually holds the colours.
- **MUX polarity corrected.** The GPU-mode value is read the way the firmware means it, and the row is honest
  about the third mode and the restart it needs.
- **CoolBoost, LCD Overdrive and WinKey lock** are exposed where the firmware reports them.
- **Battery charge limit detection through Acer Care Center**, for machines whose firmware has no
  `WMID_GUID5`.
- **Fan behaviour decoding and the GPU fan bitmap**, so a fan that reports `0` is reported as missing rather
  than as stopped.
- **Privacy-safe hardware report.** Model, BIOS, service states and capabilities only — no serial number, user
  name, file path or log content. Every line goes through the shared redactor, and the same report is
  available from `--hardware-report`.

### The Nitro key

- The laptop's own Nitro key now opens and focuses NitroLite.
- Raw Input is the fallback when the ACPI event class says nothing, and the APGE WMI class is discovered on
  the AN16-41 specifically.
- The ACPI event class is polled, and the key is answered through NitroSense when that is the only path the
  firmware offers.
- NitroSense is swept for a few seconds after the key, and the real NitroSense processes are closed rather
  than left half-started behind the window.
- The key no longer opens NitroSense while NitroLite is closed.

### Keyboard lighting

- Every lighting change is applied, and the change is no longer announced with a toast.
- The backlight-off state is remembered across restarts, and the zones are drawn black while the lights are
  off instead of showing the last colour.
- A colour set by hand marks the profile as **Custom** instead of pretending a preset is selected.
- The shipped reds read as red on the keyboard, and a keyboard left on the old red is migrated to `#FF0000`
  once.

### Fans and power

- The manual fan sliders are held to the thermal floor, and the fans are handed back to the firmware before
  the app exits.
- A hidden window is no longer polled, and the reads that remain are hardened against a device that answers
  late.
- A null power scheme during early boot no longer crashes the app.

### Security

- The WMI worker is authenticated by process id, and its task definition is kept off the disk.
- The worker pipe's integrity label was lowered so the unelevated window can connect to it.
- The elevated auto-start is offered after the first zone colour write, not at launch.

### Data

- Every save is durable: a settings write is flushed before the app reports success.
- The tray return no longer rebuilds the window, so nothing is re-read from disk on the way back.

### Translations

- Every Turkish string that reached the log or the interface is wrapped in `Loc.T()` and carried by all eight
  catalogues.
- 162 catalogue entries that still held Turkish were translated, and the machine-translated ones were
  corrected.
- `BackupService` interpolation became `string.Format`, so a translated template with a placeholder no longer
  prints the placeholder.
- The RGB preset picker shows the translated name of a built-in preset instead of its Turkish identity.

### Linux (experimental)

- **`NitroLite.Linux`** talks to the kernel's own `acer-wmi` driver and to Linuwu-Sense. Nothing is
  reverse-engineered and nothing runs as root.
- Temperatures, fan RPM and the fan curve from `hwmon`; operating modes from
  `/sys/firmware/acpi/platform_profile`; fan speed, CoolBoost, LCD overdrive, backlight timeout and 4-zone RGB
  from Linuwu-Sense; the 80 % charge limit from `charge_control_end_threshold`.
- Keyboard lighting through the OpenRGB SDK on `127.0.0.1:6742` when the kernel exposes no RGB node.
- Tray background mode, game detection from `/proc`, MUX, School Mode, RGB profiles and the hardware report.
- `install.sh` / `uninstall.sh`, a desktop entry, a udev rule that grants the seat user an ACL on exactly the
  Acer attributes, and a `ubuntu-latest` CI job that builds the tarball.

### Performance

Measured on the AN16-41 with the published single-file build, before and after this pass.

- **The Linux telemetry tick stopped allocating.** `LinuxHwmon` reads the `hwmon` tree through spans and a
  reused device list instead of `OrderBy().Select().ToArray()` per tick, and `RealSysfsFileSystem` opens its
  files with a one-byte buffer. A tick against a real `/sys` went from **19,232 to 6,656 bytes allocated**.
- **Switching language stopped re-reading the catalogue.** `Loc` caches each parsed catalogue, so a language
  change costs **88 bytes instead of 657,804**.
- **The WMI worker frames through one pooled buffer.** The length prefix, the payload and the read buffer are
  rented from `ArrayPool` rather than allocated per message: a write went from **432 to 168 bytes**, a read
  from **896 to 704**. The wire format is unchanged.
- **The lighting page stopped rebuilding brushes.** `HexToBrushConverter` keeps a bounded cache of 256
  brushes, so a colour that has been drawn once is not parsed and allocated again.
- **The capability probe runs off the window's critical path.** The window opens while the WMI/SMBIOS probe is
  still running, and the hardware controls stay in a "scanning" state until it lands. `WhenReadyAsync()`
  exposes the probe to tests, so none of them need a `Task.Delay`.
- **The fan floor clamps its own output.** `ManualFanFloor.EffectivePercent` now returns a value in `0..100`
  even when the caller hands it a nonsense target, so nothing out of range can reach the firmware.

### Tests

- **2628 tests**, up from 1826.
- A five-chassis matrix — AN16-41, Predator Helios/Triton, an old Nitro 5, a Nitro V 15 with restricted WMI,
  and an unsupported device — drives every page and command and asserts no unhandled exception and a `--`
  fallback for every missing sensor.
- A fake `sysfs`/`hwmon` tree in a temporary folder covers the Linux reader and writer, including a missing
  node, a corrupt sensor file and a read-only `platform_profile`.
- A chaos backend injects `false`, `TimeoutException`, `0 °C`, `127 °C` and `NaN` through 100 consecutive
  telemetry and setting cycles.
- `AppSettings.Normalize()`, `FanCurve` and `ManualFanFloor` are fed `NaN`, `Infinity`, `-100` and `500` and
  must clamp to a safe range.
- URL opening is injectable, so a test no longer launches a browser.
- A probe stress suite covers a slow WMI answer, a missing sensor and a window opened and closed mid-probe.
- Allocation tests pin the Linux telemetry tick, the language switch and the WMI worker framing to their
  measured budgets, so a regression shows up as a failing test rather than as a slower app.
- A completeness test asserts that all eight catalogues hold the same 1664 keys and that no English row still
  carries Turkish letters.
- A chaos suite covers suspend and resume, 254 concurrent commands against a live telemetry loop, a corrupt
  `profiles.user.json`, an atomic write that leaves no `.tmp` behind, and a `/sys` tree whose `hwmon` indices
  are renumbered underneath the reader.
- The typeface catalogue is pinned: thirteen faces, the Devanagari face offered only to a Hindi reader
  (interface language or display language), the face in effect staying in the list after the reader leaves its
  script, and every bundled face's three files present on disk.
- The palette suite runs every contrast floor against all twelve palettes, asserts that no two palettes are
  visually identical, and asserts that High Contrast clears each floor by a wide margin.

### Packaging

- The Scoop manifest is rewritten from the package on every release, and the WinGet manifest for this version is
  in `packaging/winget/manifests/c/canerztk8/NitroLite/1.2.6/` with the published SHA-256.
- `scripts/screenshot.ps1` captures the Avalonia pages, and it no longer needs the user's own NitroLite to be
  closed: it asks for a copy that owns no single-instance slot and runs it against a throwaway data folder.

### Upgrading

Extract the new zip over the folder you already have, or let the in-app updater do it. Settings, game
profiles, RGB profiles, thermal history and battery wear history live in `%LOCALAPPDATA%\NitroLite` and are
not touched. The WPF build's files next to the exe are copied there on first launch.
