# Changelog

Every notable change to NitroLite is recorded here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

`.github/workflows/release.yml` reads the section for the tag it is publishing and uses it as the body of the
GitHub release, so this file *is* the release notes. A tag with no section here falls back to the commit log.
Releases before 1.2.6 are described on the [Releases](https://github.com/canerztk8/NitroLite/releases) page.

## [1.2.6] - 2026-11-18

The largest release the project has had. The Windows front end was rewritten on Avalonia, the
platform-independent half of the app was extracted into `NitroLite.Core`, a Linux build was added, and the
hardware layer learned to describe laptops it has never seen. All of it is in the download.

### The window is a new window

- **Rewritten on Avalonia.** The WPF front end is retired. One UI code base now runs on Windows and Linux,
  which is what made the Linux build possible at all. The executable is still `NitroLite.exe`, so an existing
  1.2.x install updates in place instead of ending up with two copies.
- **A real theme engine.** `ThemeCatalog` drives every colour from one key set: two light palettes — the
  warm-cream paper theme and **Clean White** — and eight dark ones: `Dark`, **Midnight OLED** (true black),
  **Slate Steel** (cold anthracite), **Amber Console** (warm amber), **Nord Frost** (glacier turquoise),
  **Forest** (deep green, moss accent), **Neon Street** (near-black, magenta accent) and **High Contrast**
  (black ground, white ink, a 3:1 rule). Settings shows a preview card per theme instead of a list of names.
- **A layout that holds its shape.** Cards, a max-width content column, a navigation rail you can resize, ink
  sliders you can drag by the track, segmented selection controls, and single-click mode cards.
- **Live charts.** The telemetry graph draws as samples arrive, with mode changes marked on the timeline, so a
  temperature spike can be read against the fan curve that caused it.
- **Six bundled typefaces.** The interface ships its own font stack, so a machine with an unusual default font
  no longer changes the layout. Quicksand is the default; Exo 2, Oxanium, Rubik, JetBrains Mono and Rajdhani
  travel beside it, and five system faces stay as the escape hatch.
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

- **Five new faces, all SIL OFL 1.1 and all inside the executable.** **Exo 2** (technical geometric, Cyrillic),
  **Oxanium** (squared, the lightest file of the set), **Rubik** (rounded, Cyrillic), **JetBrains Mono**
  (monospace, Cyrillic) and **Rajdhani** (condensed HUD, Devanagari). Each ships as three static instances —
  Regular, SemiBold, Bold — because Avalonia 11 exposes no variation axis and would otherwise draw every
  weight at the file's default.
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
  the emoji presentation selectors — and instanced at three weights. The six faces together add 1.8 MB to the
  source tree and **781 KB to the published exe**, because single-file compression absorbs most of it.
- **No Reserved Font Name.** Each upstream `OFL.txt` was checked before subsetting, so the cut files legally
  keep their family names. The per-face copyright line and the exact change are in `third-party/<face>/NOTICE.md`.
- **Four new palettes.** **Clean White** (neutral white, cool grey rule), **Forest** (deep green ground, moss
  accent), **Neon Street** (near-black, magenta accent) and **High Contrast** (black ground, white ink, a 3:1
  rule and a saturated accent). Ten palettes now, two light and eight dark.
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

- **2626 tests**, up from 1826.
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
- A completeness test asserts that all eight catalogues hold the same 1662 keys and that no English row still
  carries Turkish letters.
- A chaos suite covers suspend and resume, 254 concurrent commands against a live telemetry loop, a corrupt
  `profiles.user.json`, an atomic write that leaves no `.tmp` behind, and a `/sys` tree whose `hwmon` indices
  are renumbered underneath the reader.
- The typeface catalogue is pinned: eleven faces, the Devanagari face offered only to a Hindi reader (interface
  language or display language), the face in effect staying in the list after the reader leaves its script, and
  every bundled face's three files present on disk.
- The palette suite runs every contrast floor against all ten palettes, asserts that no two palettes are
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
