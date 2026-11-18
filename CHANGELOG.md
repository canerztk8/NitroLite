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
- **A real theme engine.** `ThemeCatalog` drives every colour from one key set: the warm-cream paper theme,
  `Dark`, and four dark variants — **Midnight OLED** (true black), **Slate Steel** (cold anthracite),
  **Amber Console** (warm amber) and **Nord Frost** (glacier turquoise). Settings shows a preview card per
  theme instead of a list of names.
- **A layout that holds its shape.** Cards, a max-width content column, a navigation rail you can resize, ink
  sliders you can drag by the track, segmented selection controls, and single-click mode cards.
- **Live charts.** The telemetry graph draws as samples arrive, with mode changes marked on the timeline, so a
  temperature spike can be read against the fan curve that caused it.
- **UI fonts.** The interface ships its own font stack, so a machine with an unusual default font no longer
  changes the layout.
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

### Tests

- **2483 tests**, up from 1826.
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

### Packaging

- Scoop and WinGet manifests are regenerated on every release, and the WinGet manifest for this version is in
  `packaging/winget/manifests/c/canerztk8/NitroLite/1.2.6/`.
- `scripts/screenshot.ps1` captures the Avalonia pages, and it no longer needs the user's own NitroLite to be
  closed: it asks for a copy that owns no single-instance slot and runs it against a throwaway data folder.

### Upgrading

Extract the new zip over the folder you already have, or let the in-app updater do it. Settings, game
profiles, RGB profiles, thermal history and battery wear history live in `%LOCALAPPDATA%\NitroLite` and are
not touched. The WPF build's files next to the exe are copied there on first launch.
