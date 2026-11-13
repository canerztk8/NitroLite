# NitroLite

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Windows 11](https://img.shields.io/badge/Platform-Windows_11-0078D4?logo=windows11&logoColor=white)](https://www.microsoft.com/windows)
[![Tests](https://img.shields.io/badge/Tests-977-10B981)](#-tests)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**NitroLite** is a lightweight, open-source replacement for NitroSense on Acer Nitro laptops (developed on an
**Acer Nitro 16 AN16-41**, Ryzen 7840HS + RTX 4070). It speaks the same protocols as the Acer services NitroSense
uses (`AcerAgentService`, Acer Care Center) and does not ship an Electron shell.

**Measured footprint** (AN16-41, in the tray, Release build): about 74 MB working set and roughly 0.6 % of one core
at idle. Telemetry is read every 3 seconds while minimised.

*Türkçe README: [README.tr.md](README.tr.md)*

---

## Features

### 1. Operating modes and fan
- **Five power modes:** Quiet, Balanced, Performance, Turbo and Eco. Commands go through `AcerAgentService` (AASSvc,
  `127.0.0.1:46933`) using the same protocol as NitroSense.
- **Fan:** automatic curve, maximum speed, a fixed custom speed (CPU and GPU fan separately, 30–100 %), or a
  **software fan curve** you draw yourself.
- **Software fan curve:** six points from 40 °C to 90 °C, each mapping a temperature to a fan speed. NitroLite reads
  the CPU temperature every few seconds and moves the fan to the matching speed. A 3 °C hysteresis and a minimum
  dwell time keep it from hunting up and down on a temperature that sits on a step. The curve is drawn as a trace
  that always matches the sliders, and the current operating point is marked on it. The default curve is quieter
  than the stock automatic one below 70 °C and louder above 80 °C.
- **Mode key:** the keyboard's mode/Turbo key cycles modes the way NitroSense does (on AC: Balanced → Performance →
  Turbo → Quiet; on battery: Balanced ↔ Quiet).
- If the mode cannot be read, the UI says "Unknown" instead of inventing a default.

### 2. CPU boost control
- Changes the processor boost setting in the active Windows power plan (PerfBoostMode): Aggressive, Efficient or
  Disabled. Disabled keeps the CPU at or below its base clock, which lowers temperature and fan noise.

### 3. GPU mode (MUX)
- Like NitroSense, the choice is limited to the modes the service reports (`SUPPORT_GPU_MODE_CAPABILITY`):
  Automatic, Optimus, NVIDIA only. A new mode takes effect after a restart, and NitroLite can start that restart for
  you. If the service reports no modes, the choice stays locked. NitroLite never edits the MUX registry keys itself.

### 4. Game detection and per-game profiles
- Scans installed games (Steam and the common library folders) and checks for a running game every 2 seconds.
- Each game can define a power mode, CPU boost, fan setting and mouse profile. Leaving the game restores the desktop
  settings.
- A game can also carry its **own fan curve** instead of a fixed speed, so a demanding title gets a louder curve than
  the desktop one. Turning it off falls back to the fan setting above.

### 5. ATK / VXE mouse integration (optional)
- Switches between Config 1–4 over HID, applies a per-game profile, and returns to the default profile when the game
  ends.

### 6. Battery
- **80 % charge limit:** set through Acer Care Center (`AcerCCAgent`, `wss://localhost:4343`) the same way NitroSense
  does, then read back to confirm. Without Care Center installed this option does nothing.
- **Telemetry:** live charge/discharge power, estimated time remaining, full-charge capacity and wear level. Values
  Windows does not report are labelled "(estimated)".
- **Automatic Eco on battery:** switching to battery applies a low-power mode and (optionally) 60 Hz on the internal
  display; plugging back in restores the previous settings. If the app crashes, the state is repaired on the next
  launch.

### 6b. Standby memory purge
- Converts the standby list Windows keeps cached into free memory without touching the memory of running programs.
  The card shows the current standby and free amounts, and the result reports available memory before and after —
  the figure that actually moves. The used-RAM bar does not, because cached pages were never counted as used.
- Runs [WinMemoryCleaner](https://github.com/IgorMundstein/WinMemoryCleaner), which ships inside the package under
  `tools\`, so there is nothing to install. Windows asks for confirmation (UAC) each time you press the button.
- Drop your own `WinMemoryCleaner.exe` into `%LOCALAPPDATA%\NitroLite\tools\` to use a different build; that copy
  wins over the bundled one.

### 7. OSD (on-screen display)
- Three lines: CPU (temperature, estimated power, clock, usage), GPU (temperature, power, clock, VRAM), RAM and fan.
- No DLL is injected into the game; it is only a transparent always-on-top window. It can sit in any of the four
  corners.

### 8. Keyboard RGB (4 zones)
- Static, breathing, wave, neon, slide, zoom, meteor and sparkle effects, plus your own saved profiles.
- The setting is reapplied after sleep and at sign-in. The 30-second idle timeout is read from the device.

### 9. Health page
- Battery, SSD and thermal cards. Disk temperatures without SMART access, and thermal paste/fan bearing estimates,
  are marked "(estimated)".

### 10. Integrated system and repair tools
Every Acer and Windows maintenance task runs from the **System Services & Tools** page with one click — no external
`.bat`, `.ps1` or separate `.exe`:
- **Repair the keyboard RGB service:** points the registry at the current `AcerLightingService.exe` in the driver
  store and repairs the services.
- **Repair Acer & NitroSense services:** updates the `AcerCentralService` and `AcerLightingService` paths, stops
  conflicting processes and restarts the services.
- **Repair the MUX switch entry:** verifies `discrete_gpu_support = 1`, clears stale persistent session tasks and
  starts the Quick Access service.
- **Windows & system optimisation:** HAGS (GPU hardware scheduling), NVMe TRIM, disabling telemetry tasks and
  background service cleanup. Every change is backed up to `optimize_backup.json`; **Restore** puts service start
  types, the HAGS value and the telemetry tasks back.
- **Command line:** `NitroLite.exe` also accepts `--fix-lighting`, `--fix-services`, `--restore-mux`, `--optimize-pc`
  and `--restore-optimize`. Commands that change system settings ask for confirmation:
  `NitroLite.exe --optimize-pc --yes`.
- **One-shot hardware commands:** `--mode`, `--fan` and `--profile` change the machine and exit without opening the
  window, so a script, a launcher or a macro pad can drive them. They never write to `settings.json`; a running
  NitroLite picks the change up as an external change on its next poll.

  ```bat
  NitroLite.exe --mode turbo
  NitroLite.exe --mode balanced --fan auto
  NitroLite.exe --fan 45
  NitroLite.exe --profile 2
  ```

  `--mode` takes `turbo`, `performance`, `balanced`, `quiet` or `eco`. `--fan` takes `auto`, `max`, `custom` or a
  percentage from 30 to 100 (which means custom at that speed). `--profile` takes an ATK mouse config from 1 to 4.
  Exit codes: `0` applied, `1` the hardware refused, `2` timed out, `3` the command line was wrong.

### If the Acer service is not running
When AASSvc does not answer, NitroLite keeps showing what Windows and the NVIDIA driver can report (CPU usage, RAM,
GPU). Only the sensors that live in the service are marked "No service".

---

## Languages

The interface is written in English and translated into seven more languages. Pick one in **Settings → Language**;
NitroLite restarts itself in about a second to apply it.

| | | | |
|---|---|---|---|
| English | Türkçe | Deutsch | Español |
| Русский | Português | हिन्दी | Bahasa Indonesia |

- On first launch NitroLite follows the Windows display language when it is one of the eight, and falls back to
  English otherwise.
- Numbers, percentages and dates follow the chosen language.
- Technical terms (Turbo, Boost, OSD, MUX) stay untranslated.
- The translations were written by the author, not by native speakers. If a word reads wrong in your language,
  please [open a translation issue](https://github.com/canerztk8/NitroLite/issues/new?title=Translation%20correction&labels=translation)
  with the language and the correct text.

## Theme

**Settings → Theme** offers **System default**, the light "paper" theme and a dark theme built from the same accent
colours. System default follows the Windows app theme, resolved at launch. The OSD is dark in every case. Like the
language, the theme applies after a quick restart.

## Updating

NitroLite checks GitHub for a newer release 30 seconds after launch and then every 6 hours; the check can be turned
off in Settings. When a new version exists, an **Update v1.x.x** button appears next to the version label at the top
left. One click downloads the package, compares its SHA-256 against the digest GitHub publishes, swaps the
executable and restarts.

- Nothing is downloaded during a game session.
- A development build never updates itself; the button opens the release page instead.
- If the folder is not writable, NitroLite says so and opens the release page.
- NitroLite is unsigned, so trust rests on HTTPS to GitHub plus the digest check. If the GitHub account were
  compromised, that check would not save you — two-factor authentication on the account is the real protection.

You can still update by hand: close NitroLite from the tray, extract the new zip over the same folder, and your
settings stay where they are in `%LOCALAPPDATA%\NitroLite`.

## Reporting a bug

**Settings → Report a bug** opens a GitHub issue with the version, Windows build, laptop model and language already
filled in. You see the text before anything is sent, and you can edit it. No user name, file path or log content is
added. Next to it, **Open log folder** takes you to the log if you want to paste an excerpt yourself.

---

## Architecture

```
NitroLite
├── Core/
│   ├── AcerAgentClient.cs          # AASSvc TCP protocol (mode, fan, GPU, RGB, telemetry, events)
│   ├── CareCenterClient.cs         # Acer Care Center WebSocket client (80 % charge limit)
│   ├── AtkMouseService.cs          # ATK / VXE mouse HID protocol
│   ├── BatteryWatchdogService.cs   # Battery state, health and discharge telemetry
│   ├── CpuPowerService.cs          # Windows power plan CPU boost setting
│   ├── PowerAutomationService.cs   # AC/battery transitions and Eco recovery
│   ├── GameDetectionService.cs     # Game scanning and detection
│   ├── HardwareMonitorService.cs   # NVML (GPU) and Windows fallback telemetry
│   ├── StorageHealthService.cs     # Disk health and free space
│   ├── DebloatService.cs           # Acer background services, autostart
│   ├── MemoryCleanerService.cs     # Standby memory measurement; launches WinMemoryCleaner
│   ├── SystemToolsService.cs       # Keyboard, Acer service, MUX and Windows repair engine
│   ├── Loc.cs / LocXaml.cs         # Translation catalogues and the {loc:T} markup extension
│   ├── ThemeManager.cs             # Light and dark palettes
│   ├── UpdateService.cs            # GitHub release check, digest verification, executable swap
│   ├── AppPaths.cs                 # Data folder and system tool paths
│   └── TelemetryFormatter.cs       # Value formatting and "(estimated)" labels
├── Lang/                           # en, tr, de, es, ru, pt, hi, id
├── Themes/                         # Light.xaml, Dark.xaml
├── OsdOverlayWindow.xaml           # OSD window
├── MainWindow.xaml                 # Main interface
└── App.xaml                        # Single instance, CLI flags, error logging
```

**Data location:** settings, game profiles, RGB profiles, thermal history and the log live in
`%LOCALAPPDATA%\NitroLite`. Files that older versions kept next to the exe are copied there on first launch.

---

## Install (download and run)

1. Download `NitroLite-v1.2.0-win-x64.zip` from the
   **[Releases](https://github.com/canerztk8/NitroLite/releases/latest)** page.
2. Extract it to a **permanent folder**, for example `C:\Users\<you>\Programs\NitroLite`. Do not leave it in
   Downloads: "run at Windows startup" remembers where the exe lives.
3. Open `NitroLite\NitroLite.exe`. There is no installer and **no .NET install needed**; the runtime is inside the
   exe.
4. If Windows says "**Windows protected your PC**" (SmartScreen), click **More info → Run anyway**. The warning
   appears because NitroLite is an unsigned open-source application.

Closing the window keeps NitroLite running in the system tray. To quit completely, right-click the tray icon and
choose **Exit**.

### Requirements
- **Windows 11** (Windows 10 x64 also works), 64-bit.
- **Acer services:** `AcerAgentService` (mode, fan, RGB, telemetry) and optionally Acer Care Center (80 % charge
  limit). They ship with NitroSense. The NitroSense UI does not need to be open, but it must not be uninstalled.
- **Tested hardware:** Acer Nitro 16 AN16-41. On other Nitro / Predator models some features may stay locked,
  depending on what the service supports.

### When does it ask for administrator rights?
NitroLite runs as a normal user. Windows asks for confirmation (UAC) only for:
- **Standby memory purge:** WinMemoryCleaner needs administrator rights to release the standby list, so Windows
  asks each time you press the button. The tool itself ships with NitroLite; nothing to install.
- **Disabling / restoring Acer background services.**

### Uninstall
1. On the System Services page, turn off **"Run at Windows startup"**. If you disabled services, press
   **"Restore services"**.
2. Quit NitroLite from the tray and delete its folder. To remove the settings too, delete `%LOCALAPPDATA%\NitroLite`.
   The bundled WinMemoryCleaner goes with the folder; nothing else is left behind.

---

## Trust and code signing

**NitroLite is not code-signed.** A Windows code-signing certificate that SmartScreen accepts costs a few hundred
dollars a year and needs a registered business, which this project does not have. So the exe carries no publisher
signature, and Windows shows "Windows protected your PC" the first time you run it. That warning is about the missing
signature, not about anything found in the file.

What you can check instead, without trusting the author:

| Check | How |
|---|---|
| The download came from this repository | The link on the Releases page points at `github.com/canerztk8/NitroLite`; HTTPS to GitHub is the transport. |
| The file is the one that was published | `Get-FileHash NitroLite-v1.2.0-win-x64.zip -Algorithm SHA256` and compare it with the digest GitHub lists on the release. |
| The source matches the binary | Build it yourself from the tag; `scripts\publish-release.ps1` is the same script that produced the release. |
| The updater does not take a shortcut | It compares the downloaded package's SHA-256 against the digest GitHub publishes and refuses to swap the exe on a mismatch. |

The honest limit: if this GitHub account were taken over, an attacker could publish a release and its digest together,
and the digest check would pass. Two-factor authentication on the account is what actually protects you, not the
signature. If you need a signed binary, build from source and sign it with your own certificate.

---

## Build from source

```powershell
git clone https://github.com/canerztk8/NitroLite.git
cd NitroLite

dotnet test NitroLite.Tests/NitroLite.Tests.csproj
dotnet build -c Release NitroLite/NitroLite.csproj
```

The executable is `NitroLite/bin/Release/net10.0-windows/NitroLite.exe` (this build needs the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)).

**To produce the downloadable package:**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1
```

This writes `artifacts\NitroLite-v<version>-win-x64.zip`: a self-contained single-file `NitroLite.exe`, the licence,
a third-party notice, and `tools\WinMemoryCleaner.exe` with its own licence and source archive. The script pins the
.NET runtime to the patched version (10.0.14, CVE-2026-111497) and refuses to package a build that does not reference
it. It also pins the WinMemoryCleaner version and SHA-256, so a re-uploaded or tampered asset fails the build.

---

## Tests

977 tests: protocol packets, Eco/game transitions, recovery scenarios, formatting, boundary values and concurrency.

```powershell
dotnet test NitroLite.Tests/NitroLite.Tests.csproj
```

- The tests use their own temporary data folder (`NITROLITE_DATA_DIR`) and never touch your real settings.
- Two tests that change the real system (changing a connected mouse's profile, power plan changes) are skipped by
  default. To run them:

```powershell
$env:NITROLITE_ENABLE_LIVE_HARDWARE_TESTS = "1"; dotnet test NitroLite.Tests/NitroLite.Tests.csproj
```

### Screenshots

`scripts/screenshot.ps1` captures pages without you having to open the app, centre it and grab the
window by hand. It starts NitroLite against a throwaway data folder, parks the window in a corner,
walks the pages, captures each one with `PrintWindow`, then closes the app and deletes the folder.
Your real settings are never read or written.

```powershell
# every page, dark theme, German
powershell -ExecutionPolicy Bypass -File scripts\screenshot.ps1 -All -Theme dark -Language de

# one page, into a folder of your choice
powershell -ExecutionPolicy Bypass -File scripts\screenshot.ps1 -Page NavBattery -Out C:\temp\pil
```

Pages are addressed by `AutomationId` (`NavModes`, `NavMonitoring`, `NavRgb`, `NavBattery`,
`NavDebloat`), not by their visible label, so the same command works in all eight languages.
`-Position bottom` pushes the window behind whatever you already have open. There is no fully
invisible mode: WPF stops rendering a window that is completely off-screen, and `PrintWindow` then
returns a stale frame.

---

## How many people use it

NitroLite contains **no telemetry** — no counter, no analytics, no phone-home. The only numbers are the ones GitHub
already keeps, and `scripts/stats.ps1` prints them:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\stats.ps1
```

It shows downloads per release (public) and the 14-day repository traffic — views, unique visitors, clones — which
GitHub returns only to the repository owner. Requires the [GitHub CLI](https://cli.github.com) and a one-time
`gh auth login`.

---

## License

This project is released under the [MIT License](LICENSE).

The package also contains [WinMemoryCleaner](https://github.com/IgorMundstein/WinMemoryCleaner) 3.0.8, which is
GPL-3.0. It is an unmodified, separate executable that NitroLite only launches as a child process, so the two form an
aggregate under GPL-3.0 section 5 and NitroLite's own code stays MIT. The package carries the tool's licence, a notice
and its source archive; see `THIRD-PARTY-NOTICES.md`.
