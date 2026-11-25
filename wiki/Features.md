# Features

Everything NitroLite does, page by page. A control your laptop does not support is hidden, or shown disabled with the
reason next to it, instead of failing when pressed.

## Power modes and fans

- **Five modes**: Quiet, Balanced, Performance, Turbo and Eco, as single-click cards. The keyboard's own mode key cycles
  them the way NitroSense does. Only the modes your firmware reports are shown.
- **Fan control**: automatic, maximum, a fixed speed per fan (30–100 %), or a **fan curve you draw yourself**: six
  points from 40 °C to 90 °C, with hysteresis so the fans do not hunt up and down on a temperature that sits on a
  step. Three templates (Balanced, Quiet, Gaming) fill all six points in one click. The curve is written to the fans as
  you move a point; there is no Apply button.
- **A thermal floor.** A hand-set speed below what the current temperature allows is raised rather than accepted, and
  the fans are handed back to the firmware before the app exits or when the temperature reading disappears.
- **CPU boost**: Aggressive, Efficient or Disabled, written into the active Windows power plan.
- **GPU mode (MUX)**: Automatic, Optimus or NVIDIA only, limited to what your firmware reports. A change takes effect
  after a restart, which NitroLite can start for you.

A higher fan speed moves more air; it does not guarantee a lower temperature. On a cooler that is already saturated
the reading barely moves. The curve trades noise against temperature; it does not promise a number.

## Keyboard lighting

- **Zones and effects come from the keyboard itself**, so a single-zone model gets the controls it can use instead of a
  four-zone picker that does nothing.
- **Two ways to reach the keyboard, chosen by trying.** On most models the colour goes through the firmware's WMI
  method. On the 2025 boards that method answers "success" and the LEDs do not move, so NitroLite tries the keyboard's
  own HID controller once (the documented ENE `KB5130` feature report), remembers which path worked for that
  model and BIOS, and uses it from then on. A controller with no published protocol is named in the hardware report and
  never written to.
- Static, breathing, wave, neon, slide, zoom, meteor and sparkle, plus your own saved profiles.
- **Every change applies as you make it.** No Apply button; each slider writes once when you let go.
- **One off switch for the whole page.** It darkens the keyboard and the page together, stops the automatic colours,
  and survives a restart. A colour that was still on its way when you pressed it is dropped, not painted afterwards.
- **Temperature colour.** The keyboard follows the processor, the graphics chip, or each on its own half. Three
  ready-made ramps (Aurora, Inferno, Nebula) are drawn on their chips; **Custom** lets you type or slide up to eight
  stops between 20 and 110 °C. It is an overlay: switching it off puts your own colour back. It costs the keyboard a
  handful of writes an hour, not one per second.
- **Charging colour.** A colour of its own while the charger is plugged in, back to your lighting when it comes out.

## Battery

- **80 % charge limit**, set through Acer Care Center or the firmware and read back to confirm. On a laptop with
  neither, the option is disabled and says why.
- **Live telemetry**: charge and discharge power, time remaining, full-charge capacity and wear level. Values Windows
  does not report are labelled "(estimated)".
- **Wear trend**: the change over the recorded period, and a per-year rate once there are seven days of history. Only
  measured values are stored.
- **60 Hz on battery** (optional: the panel is lowered when the adapter is pulled and put back when it returns), and
  **School mode**: one switch for Eco, 60 Hz, low brightness and the backlight off.
  Every value it replaces is saved first, so turning it off puts the machine back exactly as it was. The card reports
  what it changed rather than promising a number of extra hours.

## On-screen display

Three lines over your game: CPU (temperature, estimated power, clock, usage), GPU (temperature, power, clock, VRAM),
RAM and fan. Nothing is injected into the game: it is a transparent always-on-top window, in any corner of any monitor.

## Per-game profiles

NitroLite scans your Steam and common library folders and checks for a running game every two seconds. Each game can
carry its own power mode, CPU boost, fan setting (or its own fan curve) and mouse profile. Leaving the game restores
the desktop settings.

## Health

Battery, SSD and thermal cards on one page. Disk temperatures without SMART access, and thermal paste or fan bearing
estimates, are marked "(estimated)".

## Services & Tools

Acer and Windows maintenance in one place, with no external `.bat` or `.ps1`:

- **Repair the keyboard RGB service** and **the Acer / NitroSense services**: point the registry at the current
  binaries and restart them.
- **Repair the MUX switch entry**: checks `discrete_gpu_support = 1` and clears stale session tasks.
- **Windows optimisation**: HAGS, NVMe TRIM, telemetry tasks and background service cleanup. Every change is backed
  up, and **Restore** puts it all back.
- **Start-up**: start with Windows, start in the tray, and an optional elevated logon task that removes the UAC prompt.
- **The Nitro key** opens NitroLite, and no longer opens NitroSense behind it.

## First run

Three steps on first launch: what the probe found on this machine, which of it is supported, and the optional elevated
auto-start. You can skip it; **Settings → Show the walkthrough again** brings it back.

## Updating

NitroLite checks GitHub for a new release 30 seconds after launch and then every 6 hours (you can turn this off in
Settings). When one exists, an **Update** button appears next to the version label. One click downloads the package,
checks its SHA-256 against the digest GitHub publishes, swaps the program and restarts. Nothing is downloaded during a
game, and a folder NitroLite cannot write to opens the release page instead. Trust rests on HTTPS to GitHub plus the
digest check; see **[Trust and code signing](https://github.com/canerztk8/NitroLite/wiki/Trust-and-Code-Signing)**.

## Resources

About a 21 MB download, roughly 70 MB of RAM in the tray and about 0.2 % of one core at idle. No Electron, no background
service, no kernel driver and no telemetry.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
