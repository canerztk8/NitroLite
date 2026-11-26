# NitroLite — Acer Nitro and Predator compatibility

What NitroLite claims about each Acer model, where the claim comes from, and whether it has been
checked on a real machine. Two kinds of evidence are kept apart throughout:

- **Physical** — measured on the machine this audit ran on. Only one model has any of this.
- **Source** — a cited third-party report or a read-only static analysis of a published package.
  Never a guess from a similar model name.

A model with no physical evidence and no source evidence is **test pending**, and nothing about it
is enabled on the strength of its name.

---

## 1. The device at hand

Measured read-only with `NitroLite.Desktop.exe --hardware-report` and `Get-CimInstance`, before any
change. This is the only machine in this document whose behaviour was observed rather than read.

| Fact | Value | How |
| --- | --- | --- |
| Model | Acer Nitro **AN16-41** | `Win32_ComputerSystem.Model` |
| Board | `Sierra_PEH` | `Win32_BaseBoard.Product` |
| BIOS | INSYDE **V1.27** | `Win32_BIOS.SMBIOSBIOSVersion` |
| CPU vendor | AMD | `Win32_Processor.Manufacturer` |
| OS | Windows 11 Home, 10.0.26300 | `Win32_OperatingSystem` |
| Matched profile | `nitro-16-an16-41` (`verified: true`) | app report |

### 1.1 What the firmware answered

| Capability | Result | Note |
| --- | --- | --- |
| `AASSvc` | active, **not connected** | the Acer service is installed but its pipe is not open |
| `wmi.gamingFunction` | **AccessDenied** | the class is registered as a dynamic class; instance access needs elevation |
| `wmi.batteryChargeLimit` | Supported | |
| `mux.switch` | Supported | |
| `keyboard.zones` | Supported, **4 zones** | |
| `gpu.overclock` | Supported | |
| direct WMI | not started | the elevated worker is only started on demand |

### 1.2 Sensor ids, measured

The reference profile's ids were confirmed against the Acer service at the same moment, not copied
from the Linux driver:

| Query | Answer | Matches |
| --- | --- | --- |
| `GetGamingSysInfo(0x0101)` | 63 °C | `CPU_TEMPERATURE` 63 |
| `GetGamingSysInfo(0x0201)` | 2436 RPM | `CPU_FANSPEED` 2444 |
| `GetGamingSysInfo(0x0301)` | 55 °C | `SYS1_TEMPERATURE` 55 |
| `GetGamingSysInfo(0x0601)` | 2409 RPM | `GPU1_FANSPEED` 2422 |
| `GetGamingSysInfo(0x0A01)` | 57 °C | `GPU1_TEMPERATURE` 57 |

Keyboard colour is written through `SetGamingRgbKb` with a non-zero zone mask. `SetGamingKBBacklight`
rejects every payload tried on this machine.

### 1.3 The packaged binary answers the same way

The single-file package built during this audit was run with `--hardware-report` and compared with
the development build. Same device, same profile, same sensor layout:

```
| NitroLite | v1.2.8 |
| Runtime | .NET 10.0.14 |
| Model | Nitro AN16-41 |
| Model code | AN16-41 |
| Board | Sierra_PEH |
| BIOS | INSYDE Corp. 1.27 |
| Matched | nitro-16-an16-41 (embedded) |
| Cache key | AN16-41|1.27 |
```

The runtime line matters for compatibility work: the package pins `RuntimeFrameworkVersion=10.0.14`
because 10.0.12 carries CVE-2026-111497, and the packaged binary reports the pinned version, so the
pin survives the single-file bundling. The profile match is unchanged by packaging, which is what
this check was for.

### 1.4 Elevation on this machine

`ConsentPromptBehaviorAdmin=0` on this host, so the elevated WMI worker starts without a visible
prompt. On a default Windows install the same path raises a UAC prompt. This is a property of the
test machine, not of the application, and it is why the worker appeared with no prompt during the
audit.

### 1.5 A second physical unit: the AN16-61 report (2026-11-19)

The first machine in this table other than the reference unit is an **AN16-61** whose owner filed a
report against v1.2.8. Everything below is their hardware, recorded as they reported it; it is kept in
its own subsection because §1.1–§1.4 describe the reference unit and the two must not be averaged.

| Field | Reported value | How it was read |
| --- | --- | --- |
| Manufacturer / model | `Acer` / `Nitro AN16-61` | the app's own hardware report |
| Model code | `AN16-61` | SMBIOS |
| Board product | `EV9_SKF` | SMBIOS |
| BIOS | `INSYDE Corp. 1.53` | SMBIOS |
| Windows | `10.0.26300` (x64) | `Environment.OSVersion` |
| .NET | `10.0.14` | runtime description |
| CPU vendor | `Amd` | SMBIOS |
| Profile matched | `nitro-16-an16-61 (embedded)`, cache key `AN16-61\|1.53` | profile table |
| Backend | `active WMI; AASSvc not connected; direct WMI running` | hardware report |
| Acer Care Center | `AcerCCAgentSvis` running | service probe |

**Per-feature outcome, as reported** (this is the important column: it is not what the sources say,
it is what the machine did):

| Feature | Reported behaviour | State |
| --- | --- | --- |
| Fan control | works | **confirmed working** on this model |
| Operating mode | works | **confirmed working** on this model |
| Sensor temperatures | plausible | **confirmed working** on this model |
| CPU clock | shows `-- GHz` in the dashboard | **confirmed broken** in v1.2.8 — fixed in this branch, see §3.2 |
| MUX current mode | not read; panel was on Optimus | **confirmed broken** in v1.2.8 |
| MUX write | "does not apply the setting", full reboot, no hibernation/fast startup | **confirmed broken** in v1.2.8 |
| Battery charge limit | works through NitroSense; NitroLite's capability summary said unsupported | **contradiction** — the limit works on this hardware; the summary was wrong |
| Backlight timeout | works through NitroSense (later disabled in BIOS); summary said unsupported | **contradiction**, same cause |
| Keyboard backlight off | works | **confirmed working** |
| Keyboard effect/mode write | turns the backlight back on with the previous setting (static white) | **mode write applies, effect does not change** |
| Keyboard per-zone colour | changes in the app only; occasional "Write failed" | **confirmed broken** — the write is accepted and has no effect |
| Windows-key lock | "does nothing" | **confirmed no effect** on this model |
| `AcerGenericEvent` event watcher | refused with an interval, connects without one | works on the fallback |

The two lines that make the keyboard failure diagnosable are in the owner's log:

```
Keyboard lighting: No Acer service; the lighting was written through direct WMI.
...
The direct WMI capability was disabled for this session:
wmi.keyboardLighting: read-back did not match in 3 attempts (the worker refused the write)
```

So the firmware accepted `SetGamingRgbKb` (status 0) and then answered a read-back that did not match,
three times, until `ReadBackPolicy` switched the capability off for the session. That is the correct
### 1.6 Runtime sensor verification: a machine can earn the curve (2026-11-20)

The HW-04 fix left a gap the AN16-61 report exposed. `SensorLayout.Verified` answers *"did anyone measure
these sensor ids on this model?"*, and on every model except the reference unit the answer is no. A machine
that answers a real die temperature and a real tachometer every second therefore still had its custom fan
curve locked — which is why the AN16-61 owner could not use a curve at all, even though the fan and mode
writes work perfectly on that model.

`RuntimeSensorVerification` (`NitroLite.Core/Hardware`) adds the weaker question a session can answer by
itself: two consecutive telemetry samples at least 1.5 s apart, both temperatures inside `15..115 °C`, both
fans inside `0..10000 RPM`, no documented sentinel, and the sensor ids anchored — by a measured profile or
by the live probe this session. When that passes, the curve runs for the session.

| What it changes | What it does not change |
| --- | --- |
| the write gate becomes `profile.Verified || session.RuntimeVerified` | the 85 / 90 / 95 °C floors, which still read the live value |
| `--hardware-report` prints `runtime-verified (n samples)` or `unverified (…)` in the Sensors table | any profile's `verified` flag or any sensor id mapping |
| one log line per session when it promotes | HW-06, the dead-sensor hand-back, which still owns a lost reading |

The window is deliberately narrower than `SensorPlausibility` (`1..130 °C`, `0..12000 RPM`): plausibility
decides whether a number may be *shown*, this decides whether the fans may *act* on it. A refused sample
restarts the run; a too-soon sample does not, because a one-second poll would otherwise make promotion
impossible on a machine that is plainly answering.

**Evidence, and what is still missing.**

- Unit tests (`RuntimeVerificationTests`, 20 cases): promotion on two in-window samples, and refusal on each
  sentinel (`0xFFFF`, `0x7FFF`, `0x8000`, `0xDEAD`, `0xBEEF`), on static zeros, on out-of-window temperatures
  and fans, without a layout confirmation, and on a second sample that arrives too soon. Promotion is also
  pinned as one-way for the session.
- Reference unit (AN16-41): the profile is `verified: true`, so this path is never the one that decides there.
- **AN16-61: physical confirmation pending.** Nothing here writes hardware differently on that model; what
  changes is whether the fan page offers a curve. First step is in §6.

---

### 1.7 The keyboard colour moves to HID when WMI lies (2026-11-20)

The AN16-61 report carried a log that ended the argument about the keyboard:

```
[2026-11-19 20:06:42] WARN The direct WMI capability was disabled for this session:
wmi.keyboardLighting: read-back did not match in 3 attempts (the worker refused the write)
```

The WMI methods answer status `0` — success — and the LEDs do not move. NitroLite's read-back policy then
does exactly what it was built to do: three attempts, three mismatches, capability off for the session. The
user is left with a lighting tab that says *Write failed* while NitroSense sets colours on the same keyboard.

The cause is documented hardware. The 2025-era Nitro and Predator boards moved the backlight onto a separate
ENE controller, and the colour is written with an **HID feature report**, not a WMI method:

| Field | Value |
| --- | --- |
| controller | ENE **KB5130**, USB `0x0CF2:0x5130` |
| transport | HID **feature** report, 11 bytes (`HidD_SetFeature`; not `WriteFile`, which reaches output reports) |
| frame | `A4 21 mode brightness speed direction R G B mask 00` |
| mode bytes sent | `0x02` static, `0x04` breathing, `0x05` neon, `0x07` wave/shifting, `0x09` zoom |
| mode bytes **never** sent | `0x01`, `0x03`, `0x06`, `0x08`, `0x0C` — the identifiers this protocol also names for firmware work and for "black" |
| zones | mask `0x01` / `0x02` / `0x04` / `0x08`, `0x0F` for all four at once |

Source: S7. There is no Acer manual for any of this, and none is claimed.

**Writing the four zones through WMI.** One `SetGamingRgbKb` per zone, with the single-bit mask the capture
measured (`0x01`, `0x02`, `0x04`, `0x08`), and `0x0F` when all four zones agree. The combined masks were
tried first because the mask is documented as a bitmap: on the reference AN16-41 `0x03` (left half) and
`0x0C` (right half) both answered status `0` and painted **the whole keyboard** in the first run's colour, so
a halves split came out entirely red. A combined mask that paints every zone is worse than four calls,
because it looks applied. The split is therefore written per zone, twice — the EC applies a mask
asynchronously and a call that lands in that window is answered with status `0` and dropped, which is the
one-zone-wrong report of 2026-11-19 — with the gaming info polled before each call and 40 ms between them.
The worker reports the first-pass count as `zoneWrites` and every call as `zoneCalls`.

**Transport selection.** `KeyboardLightingRouter` (`NitroLite.Core/Hardware/Lighting`) keeps the WMI path
first — it is still correct on every older model — and tries HID once when a WMI write does not take.
Whichever answered is remembered per `Model|BIOS`, so the next colour change does not pay for three failing
round trips. `--hardware-report` prints the remembered choice, and the rescan button forgets it.

**What is recognised and not written.** ITE (`0x048D`), Sonix (`0x0C45`) and Chicony (`0x04F2`) controllers
exist behind Acer keyboards and are listed so a report can name the one a machine has. No frame layout for
their Acer lighting is published, so they are **report-only**: `CanWrite` is false and nothing is sent. The
same rule covers every standard HID page — a report addressed to page `0x01`, the keyboard's own input
collection, is never opened at all, and no page below `0xFF00` may carry a colour.

**The downloaded profile table.** A laptop nobody had at release time can also be supported without a new
binary: `RemoteProfiles` fetches `NitroLite.Core/Hardware/Profiles/profiles.json` from this repository once
at startup, validates it as strictly as the embedded table (every record must pass, at least one profile,
fan ceilings inside `1000..12000 RPM`, sensor ids inside `0..63`) and writes it
atomically to `%LocalAppData%\NitroLite\profiles.remote.json`. The catalogue then reads three documents:
**user > downloaded > embedded**. Offline, rate-limited, behind a proxy that answers HTML, or pointed at a
renamed repository, the refresh fails silently and the app starts with the table it already had.

The published table is the project's own document — the same file the binary embeds — so it is allowed the
same `anyAcer` catch-all, and the **user** file keeps the stricter rule that refuses one. The first live run
of this code got that distinction wrong and rejected the real download for carrying a catch-all; the log
line (`the downloaded table was rejected (… anyAcer …)`) is what caught it.

**Evidence, and what is still missing.**

- 57 new unit tests: the WMI-to-HID fallback and the `Model|BIOS` cache (`KeyboardLightingTransportTests`),
  the frame bytes and the filtering rule (`HidLightingFilterTests`), and remote validation, rejection and
  merge priority (`RemoteProfilesTests`). `dotnet build -warnaserror` is 0 warnings / 0 errors.
- The protocol is documented and confirmed on two machines by its author (`PHN16S-71`, `ANV16S-41`) — both
  are rows in §3 — but **neither machine is in this project's hands**. Nothing in this repository has been
  run against a real ENE controller.
- **Physical confirmation pending** on the AN16-61 and the ANV16S-41: §6 carries the first step.
- **The live download was run once, on the reference machine (AN16-41, 2026-11-20).** The log reads
  `The published profile table replaced the installed copy (32 profiles kept)`, the file landed at
  `%LocalAppData%\NitroLite\profiles.remote.json` (27,896 bytes), and the rescan it triggers then reported
  `runtime-verified (2 samples)`. That is one machine on one network; the offline, rate-limited and
  proxy-answering-HTML paths are unit-tested through `RemoteProfiles.Install`, not observed.

---

## 2. Sources

All read-only. No package was installed, no driver was loaded, no firmware was written.

| # | Source | What it is used for |
| --- | --- | --- |
| S1 | [`adityaruplaha/acer-wmi-linuwu` README, "Supported models"](https://github.com/adityaruplaha/acer-wmi-linuwu) | the per-model table below. Each row cites the owner report it came from (Linuwu-Sense `LS#`, Div-Linuwu-Sense `DLS#`, Div-Acer-Manager-Max `DAMX#` issue numbers) |
| S2 | [`frederik-h/acer-wmi-battery` `MODELS.md`](https://github.com/frederik-h/acer-wmi-battery/blob/main/MODELS.md) | battery charge limiter, per model, with issue links |
| S3 | [`torvalds/linux` `drivers/platform/x86/acer-wmi.c`](https://github.com/torvalds/linux/blob/master/drivers/platform/x86/acer-wmi.c) | the firmware sensor enum (`acer_wmi_predator_v4_sensor_id`) and the WMID method ids |
| S4 | [`0x7375646F/Linuwu-Sense`](https://github.com/0x7375646F/Linuwu-Sense) and [`PXDiv/Div-Linuwu-Sense`](https://github.com/PXDiv/Div-Linuwu-Sense) | the reverse-engineered NitroSense/PredatorSense method set NitroLite's WMI layer mirrors |
| S5 | [Arch Wiki, Laptop/Acer](https://wiki.archlinux.org/title/Laptop/Acer) | model-code cross-check |
| S6 | [platform-driver-x86: ANV15-51 quirk patch](https://ratatoskr.run/platform-driver-x86/2026/07/17338142/t) | ANV15-51 on BIOS V1.60 needs a quirk; recorded as a caveat |
| S7 | [`shwetankg07/kbrgb` `PROTOCOL.md`](https://github.com/shwetankg07/kbrgb) | the ENE KB5130 eleven-byte HID feature report (§1.7). Reverse-engineered by that project and verified by its author on a `PHN16S-71`; the same document records a confirmation on an `ANV16S-41` |
| S8 | [`cleyton1986/predator-sense`](https://github.com/cleyton1986/predator-sense) | the keyboard-controller matrix in §9.3: ENEK5130 (4-zone static, Breathing/Neon), the 2024+ Sunrex chip (single-zone, full effect list), Chicony (seven-colour), and per-key on a `PH16-71`. Read for chip families and capabilities, not for code |
| S9 | [Intrinsec, CVE-2026-50610](https://www.intrinsec.com/en/cve-2026-50610-elevation-privileges-acer-nitrosense/) | why the vendor service is not a transport: the `systemmonitoring_hardware_service_` / `predatorsense_hardware_service_` pipes are writable by `Authenticated Users` and the server does not impersonate its caller (§9.4) |

S1 is the primary source for the four-zone RGB and `fan_speed` columns. It is a community table, not
Acer documentation: it records what owners reported against the reverse-engineered methods, which is
exactly the interface NitroLite drives. Acer's own manuals do not document these methods at all, so
there is no official source to prefer.

---

## 3. Per-model evidence

`✅` = reported working, `❌` = reported not working, `—` = not in the source.

| Model code | Family | Four-zone RGB (S1) | `fan_speed` (S1) | Sensors (S1) | Battery limiter (S2) | BIOS / OS known | Source | NitroLite status |
| --- | --- | :-: | :-: | :-: | :-: | --- | --- | --- |
| **AN16-41** | Nitro 16 | ✅ | ✅ | ✅ | — | BIOS V1.27, Win 11 26300 | **physical** | **verified** — reference machine |
| AN16-42 | Nitro 16 | ✅ | ✅ | ✅ | — | — | S1 (LS#85) | profile, sensors unverified |
| AN16-43 | Nitro 16 | ✅ | ✅ | ✅ | ✅ | — | S1 (LS#17), S2 (#59) | profile, sensors unverified |
| AN16-61 | Nitro 16 | — | — | — | — | BIOS V1.53, Win 10.0.26300 | **physical** (§1.5) | profile, **partly verified** — fans, modes and sensors work; zone RGB now has an HID path (§1.7, physical confirmation pending); clock, MUX read/write and Win-key lock open (§3.2) |
| AN17-41 | Nitro 17 | ✅ | ✅ | ✅ | — | — | S1 (DAMX#99) | **no profile** — see §4 |
| AN17-42 | Nitro 17 | ✅ | ✅ | ✅ | — | — | S1 (DAMX#64) | profile, sensors unverified |
| AN17-51 | Nitro 17 | — | — | — | — | — | none | profile, **test pending** |
| AN515-44 | Nitro 5 | ❌ | ✅ | ✅ | ✅ | — | S1 (DAMX list), S2 (#77) | profile, RGB corrected |
| AN515-45 | Nitro 5 | ✅ | ✅ | ✅ | — | — | S1 (LS#92, DLS#12) | profile, RGB confirmed |
| AN515-46 | Nitro 5 | ✅ | ✅ | ✅ | — | — | S1 (DLS#4) | **no profile** — see §4 |
| AN515-47 | Nitro 5 | ❌ | ✅ | ✅ | ❌ | — | S1 (DAMX#44), S2 (#94) | **no profile** — see §4 |
| AN515-54 | Nitro 5 | — | — | — | — | — | none | profile, **test pending** |
| AN515-55 | Nitro 5 | ✅ | ✅ | ✅ | — | — | S1 (LS#40) | profile, RGB corrected |
| AN515-56 | Nitro 5 | — | — | — | — | — | none | profile, **test pending** |
| AN515-57 | Nitro 5 | ❌ | ✅ | ✅ | ✅ | BIOS V1.14 (S1 author's own) | S1, S2 (#88) | profile, RGB corrected |
| AN515-58 | Nitro 5 | ✅ | ✅ | ✅ | ✅ (no effect on BIOS V2.19) | BIOS V2.19 | S1, S2 (#57) | profile, RGB corrected |
| AN517-41 | Nitro 5 | — | — | — | — | — | none | profile, **test pending** |
| AN517-54 | Nitro 5 | ✅ | ✅ | ✅ | ✅ | — | S1 (DLS#5), S2 (#59) | profile, RGB confirmed |
| ANV15-41 | Nitro V 15 | ❌ | ✅ | ✅ | — | — | S1 | profile, RGB confirmed |
| ANV15-51 | Nitro V 15 | ❌ | ✅ | ✅ | ✅ | BIOS V1.60 needs a quirk (S6) | S1, S2 (#71), S6 | profile, RGB confirmed |
| ANV15-52 | Nitro V 15 | ❌ | ✅ | ✅ | — | — | S1 (LS#117, DLS#14) | profile, RGB confirmed |
| ANV16-41 | Nitro V 16 | ❌ | ✅ | ✅ | — | — | S1 (LS#37) | profile, RGB confirmed |
| ANV16-42 | Nitro V 16 | ❌ | ✅ | ✅ | ✅ | — | S1 (DAMX#174), S2 (#111) | **no profile** — see §4 |
| ANV16-71 | Nitro V 16 | ❌ | ✅ | ✅ | — | — | S1 (LS#64, DAMX#128) | profile, RGB corrected |
| ANV16S-41 | Nitro V 16 S | ❌ | ✅ | ✅ | — | — | S1 (LS#61) | profile, RGB **kept** — see §5 |
| PH16-71 | Predator Helios 16 | ❌ | ✅ | ✅ | — | — | S1 | **no profile** — see §4 |
| PH16-72 | Predator Helios 16 | ❌ | ❌ | ✅ | — | — | S1 | profile, RGB corrected, fan caveat |
| PH18-71 | Predator Helios 18 | ❌ | ✅ | ✅ | — | — | S1 | profile, RGB corrected |
| PH18-73 | Predator Helios 18 | ❌ | ✅ | ✅ | — | — | S1 (LS#123) | profile, RGB corrected |
| PH315-52 | Predator Helios 300 | ✅ | ✅ | ✅ | — | — | S1 (LS#119) | profile, RGB confirmed |
| PH315-53 | Predator Helios 300 | ✅ | ✅ | ✅ | — | — | S1 (LS#98, LS#121) | profile, RGB confirmed |
| PH315-54 | Predator Helios 300 | — | — | — | — | — | none | profile, **test pending** |
| PHN14-51 | Predator Helios Neo 14 | — | — | — | — | — | none | profile, **test pending** |
| PHN16-71 | Predator Helios Neo 16 | ✅ | ✅ | ✅ | ✅ | — | S1, S2 (#59) | profile, RGB confirmed |
| PHN16-72 | Predator Helios Neo 16 | ✅ | ✅ | ✅ | — | — | S1 | profile, RGB confirmed |
| PHN16-73 | Predator Helios Neo 16 | ❌ | ✅ | ✅ | — | — | S1 (LS#126) | profile, RGB corrected |
| PHN16S-71 | Predator Helios Neo 16 S | ❌ | ❌ | ✅ | — | — | S1 (LS#101, DAMX#270) | profile, RGB corrected, fan caveat |
| PHN18-72 | Predator Helios Neo 18 | ❌ | ✅ | ✅ | — | — | S1 (DAMX#123) | **no profile** — see §4 |
| PHIN16-71 | Predator Triton (?) | — | — | — | — | — | none | profile, **code unconfirmed** — see §5 |
| PHIN16S-71 | Predator Triton (?) | — | — | — | — | — | none | profile, **code unconfirmed** — see §5 |
| PT14-51 | Predator Triton 14 | ❌ | ❌ | ✅ | — | — | S1 | **no profile** — see §4 |
| PT316-51s | Predator Triton 316 | ✅ | ✅ | ✅ | — | — | S1 (DLS#17) | **no profile** — see §4 |
| PTX17-71 | Predator Triton 17 X | ❌ | ✅ | ✅ | — | — | S1 (LS#83) | profile, RGB corrected |

### 3.1 Caveats carried from the sources

These are recorded because they are the difference between "the method returned success" and "the
hardware changed":

- **AN515-58, BIOS V2.19** — the battery limiter is accepted and has no effect (S2 #57).
- **PH16-72, PT14-51** — the Sense settings directory is not offered at all; fan control exists only
  through the upstream hwmon `pwm` interface, which is a different mechanism from the Sense
  `fan_speed` method NitroLite drives (S1).
- **PHN16S-71** — fans read 0 RPM and do not respond to control (S1, LS#101 / DAMX#270).
- **Per-key RGB keyboards** (Helios 16 and later) are not driven by the four-zone methods at all
  (S1). A model with a per-key keyboard reports `❌` in the four-zone column even though its
  keyboard is lit.
- **ANV15-51, BIOS V1.60** needs a platform-driver quirk (S6); the same firmware generation may
  behave differently on other BIOS revisions.

### 3.2 What the AN16-61 report changed in the code

Physical evidence first, source evidence second — and here there is only physical evidence, because no
published source describes this model. Each row names the code change and how it was verified without a
second machine.

| Finding | Evidence it was real | Change | Verified by |
| --- | --- | --- | --- |
| CPU clock never read: the dashboard shows `-- GHz` on a machine with no Acer service | `CpuFrequencyMHz` was filled only from `CPU_FREQUENCY` in an AASsvc reply (`AcerAgentClient.cs:677`); nothing else ever wrote it | `HardwareMonitorService.GetNativeCpuFrequencyMhz()` asks Windows through `CallNtPowerInformation(ProcessorInformation)`, and `EnrichMonitorData` fills a zero clock before the power estimate uses it | `CpuClockFallbackTests`, 5 tests, run on the reference unit: 3801 MHz reported |
| MUX: the report said `GetGamingMiscSetting(0x09) MUX — not read (needs elevation)` **on an elevated session** | `CapabilityProbe.Probe(identity, catalog, schema)` always passed `wmiMuxSignal: null`; no production caller ever passed one | the worker's `wmiMuxSwitch` now travels in `HardwareCapabilities.MuxSwitchOverWmi` and the report reads it | `Report_ShowsTheLiveMuxSelector_EvenThoughTheProbeRanBeforeTheWorker` |
| MUX: a write that does nothing left no trace at all | `SetGpuModeAsync` returned `false` when the worker refused `ReadDeviceState`, with no log line; `GetGpuModeAsync` returned `null` the same way | both now log the worker's own refusal text | code review; needs the AN16-61 machine to see the log line |
| Capability summary claimed no battery charge limit and no backlight timeout | the summary drew a cross for every false flag, although `NegativeAnswersAreConclusive` is **false** on Windows and the platform contract says an unreachable capability must be shown as unknown | `CapabilityRow` gained a third state (a question mark and "Bilinmiyor") and only a conclusive platform may draw a cross | `CapabilityRow_ShowsAQuestionMark_ForAnAnswerNobodyGave` and two siblings |
| Battery charge limit hidden while Care Center controls it | `WmiHardwareController` can only reach `WMID_GUID5`, which this model does not register; the limit lives in Care Center | `HybridHardwareController` now asks Care Center for the mode and the write when the direct-WMI path cannot serve them, and reports the capability when that read succeeds | needs the AN16-61 machine — **test pending** |
| Keyboard per-zone colour: accepted, no effect | three read-back mismatches in the owner's log, then `ReadBackPolicy` disabled `wmi.keyboardLighting` for the session | **none** — no payload was guessed. The correct `SetGamingRgbKb` payload for this firmware is unknown | **test pending**, device required |
| Windows-key lock does nothing | `SetWinKeyLock` is documented as unverifiable: there is no firmware getter, so "written" cannot be checked | **none** | **test pending**, device required |
| `AcerGenericEvent` watcher refused with an interval, connects without one | owner's log, twice per launch | **none** — the fallback works, and the interval form is not used anywhere else | n/a |
| `The exit state could not be written: Call from invalid thread` at shutdown | owner's log, both sessions | **none** — already explained in the code: the status notification cannot cross to the UI thread at shutdown, and the marker write that matters happens before it | n/a |

The two open hardware items are deliberately not fixed by inference. A zone-RGB payload written on a
guess either does nothing (the current state) or sets a colour the user did not ask for, and a
Win-key lock has no read-back to tell the difference. Both need one run on an AN16-61.

---

## 4. Corrections applied to `profiles.json`

Only changes that a cited source supports, and only fields that cannot cause a hardware write on a
machine that has not been verified. `fourZoneKeyboard` is a **report tie-breaker only**: on Windows
nothing reads it (`grep -rn "\.FourZoneKeyboard"` finds one consumer, `LinuxCapabilityProbe.cs:85`,
which uses it to warn when the sysfs node is missing). It cannot enable or disable a control, so
correcting it cannot change what is written to any machine.

| Profile | Field | Before | After | Evidence |
| --- | --- | --- | --- | --- |
| `nitro-5-an515` | `fourZoneKeyboard` | `false` | *omitted* (null = "the table does not say") | the five codes disagree: -55 ✅, -58 ✅, -57 ❌, -54/-56 unknown. A blanket `false` was a false claim about -55 and -58; the note now names each code |
| `nitro-5-an515-44` | `fourZoneKeyboard` | `true` | `false` | S1: ❌ |
| `nitro-v-16-anv16-71` | `fourZoneKeyboard` | `true` | `false` | S1: ❌ |
| `predator-helios-16-ph16-72` | `fourZoneKeyboard` | `true` | `false` | S1: ❌ |
| `predator-helios-18-ph18-71` | `fourZoneKeyboard` | `true` | `false` | S1: ❌ |
| `predator-helios-18-ph18-73` | `fourZoneKeyboard` | `true` | `false` | S1: ❌ |
| `predator-helios-neo-16-phn16-73` | `fourZoneKeyboard` | `true` | `false` | S1: ❌ |
| `predator-helios-neo-16-phn16s-71` | `fourZoneKeyboard` | `true` | `false` | S1: ❌ |
| `predator-triton-ptx17-71` | `fourZoneKeyboard` | `true` | `false` | S1: ❌ |

Confirmed correct and left alone: `nitro-16-an16-41` (✅, physical), `nitro-16-an16-42` (✅),
`nitro-16-an16-43` (✅), `nitro-17-an17-42` (✅), `nitro-5-an515-45` (✅), `nitro-5-an517-54` (✅),
`predator-helios-16-phn16-71` (✅), `predator-helios-neo-16-phn16-72` (✅),
`predator-helios-300-ph315-52` (✅), `predator-helios-300-ph315-53` (✅),
`nitro-v-15-anv15-41` (❌, already `false`), `nitro-v-15-anv15-51` (❌, already `false`),
`nitro-v-15-anv15-52` (❌, already `false`), `nitro-v-16-anv16-41` (❌, already `false`).

### 4.1 Model codes the sources name that have no profile

These are real Acer codes with a cited report, and NitroLite has no record for them, so they fall
through to `acer-gaming-default`. Adding a profile for them would give the machine its own model
code in the report and the correct feature flags — but every one of them would ship with
`verified: false` sensors, which is the same sensor behaviour they get today from the fallback.

They are **not added in this pass**. A profile is a claim about a machine, and the only thing that
would make these claims better than the fallback is a physical check on each model, which this audit
cannot do. Recording them here is the honest state: the codes are known, the profiles are pending.

| Model code | What a profile would record | Blocked on |
| --- | --- | --- |
| AN17-41 | four-zone RGB ✅ | a machine to confirm the sensor ids |
| AN515-46 | four-zone RGB ✅ | same |
| AN515-47 | four-zone RGB ❌, battery limiter ❌ | same |
| ANV16-42 | four-zone RGB ❌ | same |
| PH16-71 | four-zone RGB ❌ | same |
| PHN18-72 | four-zone RGB ❌ | same |
| PT14-51 | four-zone RGB ❌, Sense fan control ❌ | same |
| PT316-51s | four-zone RGB ✅ | same |

---

## 5. Deliberately not changed

Recorded so the next reader does not "fix" these back.

- **`nitro-v-16-anv16s-41` keeps `fourZoneKeyboard: true`.** S1 marks ANV16S-41 `❌` for four-zone
  RGB, but the profile also sets `nitroHidKeyboard: true`, and its note says the WMI RGB methods
  answer success and change nothing, so the lighting path falls through to HID. The two fields
  describe different things: the keyboard *has* four zones, and the *WMI method* does not drive
  them. Setting `fourZoneKeyboard: false` would contradict `nitroHidKeyboard: true`.
- **`PHIN16-71` and `PHIN16S-71` are kept but flagged.** Neither code appears in S1, S2 or S5. They
  may be a mis-transcription of `PHN16-71`/`PHN16S-71`, or a regional code. They are left in place
  because deleting a profile is a claim too, and a wrong profile only affects the report — but they
  are marked **code unconfirmed** and must not be used as evidence for anything.
- **`unsupportedMethods` is enforced (2026-11-21).** `UnsupportedMethodGate` maps a
  listed method to the capability it drives (`SetGamingFanSpeed`/`SetGamingFanBehavior` → fan
  control, `SetGamingRgbKb`/`SetGamingKBBacklight` → keyboard lighting, `SetBatteryHealthControl` →
  charge limit) and switches it off; it can only take a capability away, and an unknown name is
  logged, not guessed at. A model gets an entry only when a report proves a method broken on it: the
  PHN16S-71 fan caveat is a community report, and PH16-72 has the opposite evidence (the mainline
  kernel enables its fan control because the WMI methods work, commit `d727823c134a`).
- **The first evidence-backed entries (2026-11-22).**
  - `nitro-lite-16-nl16-71g` (issue 11, v1.3.0, elevated worker): the firmware refused every
    fan-behaviour, fan-speed and keyboard-colour write three times in a row, refused all 557
    `GetGamingSysInfo` sensor reads, and answers `GetGamingMiscSetting(0x02)` with `0x0001`, which is
    not a MUX mode. The keyboard is a two-level white backlight with no RGB. The profile lists the
    four fan and lighting methods, marks the sensors unsupported (temperatures come from the Windows
    thermal zone and NVML), and lists `SetGamingMiscSetting`, the operating-mode setter the firmware
    also refused. That name is in every published build's known-method list, so no new field was
    needed: current builds read it as "switch the operating mode through the Windows power mode"
    (Quiet / Balanced / Performance, via `PowerSetActiveOverlayScheme`), older builds log it as
    ignored. Acer ships no NitroSense or AcerSense for the Nitro Lite line and fixes its power limits
    in firmware (65 W measured against the processor's 115 W), so the Windows power mode is the only
    official lever this machine has. No other profile lists the setter, so every other model keeps
    writing its mode to the firmware.
  - `nitro-16-an16-61` (issue 3) now sets `nitroHidKeyboard: true`: the owner's NitroSense snapshot
    kept the WMI zone colours unchanged while NitroSense switched red to blue, `SetGamingRgbKb` reads
    back what it was given without lighting it, and the HID list carries the ENE KB5130 (`0CF2:5130`).
    Colour writes go to the KB5130 with the documented frame (§1.7). **Physical confirmation pending.**
- **No sensor id was changed.** The only profile with measured ids is `nitro-16-an16-41`. Every
  other profile carries the firmware-wide enum with `verified: false`, and since the HW-04 fix the
  fan curve refuses to act on an unconfirmed layout — the reading is shown, the curve leaves the
  fans alone.
- **No hardware write was enabled for any model.** Nothing in this pass turns a feature on for a
  machine that has not been checked.

---

## 6. Open work

| Item | State | First step |
| --- | --- | --- |
| Physical verification of any model other than AN16-41 / AN16-61 | **test pending** — no device | run `--hardware-report` on the machine and compare the sensor answers with §1.2 |
| AN16-61 CPU clock, at the panel | **code fixed, UI unconfirmed** — the fallback is unit-tested on the reference unit, not seen on an AN16-61; round three added the PDH/`CallNtPowerInformation` chain behind it (§7.2) | open the dashboard on an AN16-61 and check the clock is not `-- GHz` |
| AN16-61 MUX write | **test pending** — read-back with a 320 ms settle, the toggle reverts on a mismatch, and the claim is recorded (§7.1) | reproduce the switch and send the new `Unconfirmed controls this session` line from the report |
| AN16-61 per-zone keyboard colour | **code + tests, physical confirmation pending** — WMI is tried first and the ENE HID frame takes over when it does not take (§1.7) | on the machine, set four different zone colours and check the report says `hid`; then check the LEDs match |
| AN16-61 Windows-key lock | **honest, not fixed** — the write is recorded unverified and the UI stops claiming it applied (§7.1); no firmware getter exists | press the toggle on the machine and check the hint appears; a key-by-key check is still the only proof |
| AN16-61 battery charge limit through Care Center | **code + tests, physical pending** — the service answer is read back after 320 ms and a mismatch moves the switch (§7.1) | toggle the limit on the machine; the log says `The charge limit did not change` if the service lies |
| AN16-61 custom fan curve | **code + tests, physical confirmation pending** — the curve is no longer gated on a static profile measurement (§1.6) | open the fan page after ~2 s of telemetry on the machine; the report should read `runtime-verified (2 samples)` |
| The eight model codes in §4.1 | **test pending** | add the profile once a machine confirms its sensor ids |
| `PHIN16-71` / `PHIN16S-71` code validity | **unconfirmed** | check Acer's regional model lists; delete or correct |
| PH16-72 / PHN16S-71 fan control | **source says ❌** | needs a machine; until then the UI may offer fan control that does nothing |
| AN515-58 battery limiter on BIOS V2.19 | **source says accepted, no effect** | needs a machine on that BIOS |
| Per-key RGB keyboards | **not supported by design** | a separate method set; out of scope for the four-zone path |
| ANV16S-41 four-zone colour over HID | **test pending** — the model is in the table and the protocol is confirmed on it by S7, not by this project | same check as the AN16-61 row, on a unit of that model |
| ITE / Sonix / Chicony keyboard lighting | **no documented frame** — recognised, reported, never written | find a published frame or a capture from one of those machines before changing anything |
| Downloaded profile table, live | **code + tests, live fetch unconfirmed** — validation and the atomic write are unit-tested; the fetch is not | on a network, check the log line `Published profiles` and that a model only the published table names matches after the rescan |
| ACPI thermal zone (PDH) on any model | **physical on AN16-41 only** — 56.9 °C, read-only | run `--hardware-report` on the model and read the `Telemetry fallbacks` table |

---

## 7. Round three — what a control can be trusted to say

Round three did not add a model. It changed what the program is allowed to *claim*, which is why it belongs
in a compatibility document: the AN16-61 report's three symptoms were all "the app said it worked".

### 7.1 The rule, per control

A write is only reported as applied when the machine's own read-back agrees with it. Anything else is stated
as what it is.

| Control | Read-back source | Evidence level | What the UI says when it disagrees |
| --- | --- | --- | --- |
| Fan behaviour / fan speed | `GetGamingFanBehavior`, `GetGamingFanSpeed` | **physical** — AN16-41, measured in round one | reverts, status `Uygulanamadı`, capability off for the session |
| Operating mode | `GetGamingMiscSetting(0x0B)` | **physical** — AN16-41 | same |
| Keyboard colour | `GetGamingKBBacklight`, or the HID frame's own answer | **physical** — AN16-41; ENE frame confirmed by S7 on `PHN16S-71` / `ANV16S-41` | falls back to HID, then reverts |
| MUX switch | `GetGamingMiscSetting(0x02)`, high byte `1..3` | **physical** — AN16-41 (hybrid, discrete). AN16-61: the *write* was reported as not applying, so this path is **test pending** there | reverts, clears the reboot badge, records why |
| Battery charge limit | `WMID_GUID5` read-back, then Acer Care Center's `GET` after 320 ms | **physical** — AN16-41 for the WMI path; Care Center path **test pending** (no AN16-61 here) | switch moves to the mode the machine reports |
| Windows-key lock | **none exists** — `SetGamingProfile` has no matching getter | **source + measurement** — `GetGamingProfile(0)` answers `0x1000000` / `0x1000001000000`, an unrelated word whose bit 24 cannot be the lock state | stays where the user put it, recorded *unverified*, hint offered |
| CoolBoost, backlight timeout | `APGeAction.GetFunction(0x207)`, `GetFunction(backlight)` | **physical** — AN16-41 | unchanged by this round |

### 7.2 Telemetry layers, per metric

| Metric | Primary | Fallback | Physical evidence |
| --- | --- | --- | --- |
| CPU temperature | Acer `GetGamingSysInfo(0x0101)` / AASSvc `CPU_TEMPERATURE` | ACPI thermal zone through PDH (`\Thermal Zone Information(*)\Temperature`), 5 s cache | **physical** — AN16-41: PDH answered **56.9 °C** while the firmware value was present, i.e. the fallback is a real measurement and not a placeholder |
| CPU clock | AASSvc `CPU_FREQUENCY` | `CallNtPowerInformation(ProcessorInformation)`, parked cores excluded | **physical** — AN16-41 (`readable`) |
| GPU temperature / clock / power / VRAM | Acer, then NVML | NVML only; a sleeping dGPU is never woken | **physical** — AN16-41 in round one |
| CPU power | *none* — a model, labelled `(tahmini)` | — | **physical**: it is an estimate and every surface says so |
| VRAM via DXGI / AMD ADL | *not queried* — detected and reported only | — | **deliberately not implemented**: hand-declared COM vtables are an access violation risk that no `catch` survives |

### 7.3 AN16-61 (model code `AN16-61`, BIOS 1.53), round-three status

| Item | State after this round | First step on the machine |
| --- | --- | --- |
| MUX write | **code changed, test pending** — 320 ms settle before the read, mismatch reverts and is recorded | click *Discrete GPU*, reboot, and send the new report: the `Unconfirmed controls this session` list will name the control and both values |
| Windows-key lock | **honest, not fixed** — recorded unverified; no firmware getter exists | press the toggle and check the hint appears; a working lock is still indistinguishable from a no-op over WMI |
| Battery charge limit (Care Center) | **code + tests, physical pending** — the service answer is now read back, mismatch moves the switch | toggle the limit and watch the battery card; the log gets `The charge limit did not change` if it lies |
| CPU clock `-- GHz` | **fallback compiled and measured on AN16-41**, AN16-61 pending | open the dashboard: the clock should now come from `CallNtPowerInformation` instead of zero |
| Four-zone colour | unchanged from round two (§1.7) | — |
## 8. Round four — how to capture what NitroSense changed (2026-11-20)

The evidence this file keeps marking *test pending* has one thing in common: it needs a machine that can show what a
NitroSense switch does to the firmware registers. NitroLite can now record exactly that, read-only, on any model —
including the ones with no profile here.

**Settings → Diagnostics → NitroSense snapshot diff** takes two captures: A before the change, B after it. The diff
prints only the selectors that moved, so it answers "which register does this switch drive" rather than "what does
this machine have".

| What to do on the laptop | What the diff gives back | Why it settles a row above |
| --- | --- | --- |
| Set the MUX to Optimus in NitroSense if it is offered, capture A, switch to Discrete, capture B | `wmi.deviceState.muxRaw` / `muxStatus`, plus `wmi.misc0x09.muxSupportOverWmi` | separates "the firmware ignores the write" from "the write reaches the wrong selector" — the AN16-61 MUX row |
| Toggle the battery charge limit in Care Center, capture A and B | `wmi.battery.chargeMode`, `wmi.deviceState.batteryLimit` | shows whether the Care Center path moves a register NitroLite can read |
| Change the keyboard colour or effect, capture A and B | `wmi.lighting.config`, and any `hid.*` feature report that changed | says whether the colour travels over WMI at all on that chassis, which is what the HID fallback needs to know |
| Press the Windows-key lock, capture A and B | *nothing changes* on every machine measured so far | the negative result is the evidence: no readable register exists, which is why the control is reported unverified |

Two constraints the tool keeps, because it exists to be run on someone else's laptop:

- **Read-only.** It calls the same Acer WMI reads the window already calls, and opens the vendor HID interfaces with
  read access only. Usage page `0x01` — the keyboard the user types through — is refused by name, and no HID string
  descriptor (where a serial lives) is read.
- **No elevation.** When the direct-WMI worker is not already running, the WMI half is replaced by one
  `unavailable: the elevated WMI worker is not running` line and the HID half is still captured. Starting the worker
  stays the settings page's own button.

**Status on the reference unit:** the comparison, the redaction and the window wiring are covered by 30 tests and
the release gate is green, but no real 2-step diff has been taken on hardware yet, and no vendor HID feature report
has been read on a physical ITE/Sonix keyboard. Both are marked *test pending* in `BUG_AUDIT.md` §10.4. The first
owner who runs the pair on an `AN16-61` (or on any model in §3 that is still unverified) turns one of those rows into
a measurement.

---

## 9. Round five — what research can and cannot buy (2026-11-20)

The question behind this round: *scrape the ecosystem, reverse engineer what NitroSense and PredatorSense do, and
copy it, so that the next laptop does not produce another issue like #3.* Part of that is already how this file was
built (§2, S1–S8); this section records where the remaining leverage actually is, and where it is not.

### 9.1 The command set is not the gap

| Layer | Source | NitroLite |
| --- | --- | --- |
| WMI method set and selectors | S3, S4 | mirrored; `--hardware-report` prints the class, the provider version and every method signature |
| Sensor id enum | S3 | used as the profile layout (`cpu=1 gpu=10 sys1=3 rpm=2/6`) |
| Four-zone HID frame | S7 | routed to when the WMI read-back disagrees (§1.7) |
| Battery charge limiter | S2 | Care Center path, read back (§7.1) |
| Keyboard controller families | S8 | **not yet modelled** — see §9.3 |

Read on the reference unit while writing this section: `Get-CimClass root\wmi -ClassName AcerGamingFunction`
answers **25** methods and their `WmiMethodId` qualifiers **1–25** *without elevation*, exactly the ordinals S4 uses.
The provider therefore publishes its own interface map on every machine, and that map can be read instead of guessed.

### 9.2 What no amount of scraping answers

- **A write that returns success and changes nothing.** `SetGamingRgbKb` answers `0` on the `AN16-61` and the colour
  stays put; the battery limiter is accepted and ignored on `AN515-58` BIOS V2.19 (S2 #57). These are per-firmware facts. No community table records them, because the table is built from
  "it worked on mine", and "it returned success" reads the same as "it worked" until someone measures.
- **The kernel driver as a model database.** The mainline `acer-wmi.c` carries **27** DMI quirk entries and **none**
  of them is a Nitro or Predator model string (parsed from the file fetched while writing this section). The
  per-model knowledge for these machines lives in owner reports and issues (S1, S2, S4), not in the driver.
- **Which selector a NitroSense switch drives.** Acer does not document it and the binary does not say it out loud.
  An A/B capture does (§8), which is why the snapshot tool exists.

### 9.3 Where research still pays

1. **Keyboard controller capability matrix (S7, S8).** Today the HID backend picks a device by vendor id (ITE
   `0x048D`, Sonix `0x0C45`, Chicony `0x04F2`) and treats it as four zones. The sources say that is one of four
   keyboards: ENEK5130 four-zone static, the 2024+ Sunrex single-zone with the full effect list, Chicony's
   seven-colour palette, and per-key on the `PH16-71` and later Helios. Reading the HID report descriptor and the
   product string locally (no serial) would let the app describe the keyboard it found and offer only the effects
   that controller has, instead of writing a four-zone frame into a per-key device.
2. **The machine's own WMI map.** Adding the `WmiMethodId` ordinal to the probe's method signature and printing
   *offered but not driven* turns every future `--hardware-report` from any model into a machine-readable map: a new
   selector on a 2027 chassis shows up as a named method nobody drives, instead of as a bug report.
3. **The remote profile feed.** Already in place: a model can be marked verified, or corrected, in `profiles.json`
   without a binary release. Every measurement from §9.3.1 and §9.3.2 lands there.

### 9.4 What we will not do

- **Do not build on the vendor service.** CVE-2026-50610 (S9): Acer's `AcerSysHardwareService.exe` runs as SYSTEM,
  its `systemmonitoring_hardware_service_` and `predatorsense_hardware_service_` pipes are writable by
  `Authenticated Users`, and the server does not impersonate the caller — a local privilege escalation. NitroLite
  connects to Acer's service as a client only when it exists, never serves such an endpoint, and does not need the
  bridge: its own elevated worker keeps a per-session pipe name and a nonce handshake (`BUG_AUDIT.md` §2.8a, §2.8b).
  Reverse engineering that service further would mean reproducing an attack surface, not adding compatibility.
- **No kernel driver, no MSR, no firmware write.** A signed driver would need a certificate and an installation,
  and the goal is a portable build that a student can run from a folder.
- **No scraping of Acer's download pages and no redistribution of Acer binaries.** The one fact worth having from a
  NitroSense package — the WMI method map — is readable from the running machine (§9.1), which is both cheaper and
  licence-clean.
- **No GPL code in this repository.** NitroLite is MIT. The community projects are read for protocol facts and each
  one is credited in §2; nothing is copied.

### 9.5 Order of work

| # | Work | Effort | Risk | What it closes |
| --- | --- | --- | --- | --- |
| 1 | `WmiMethodId` ordinals in the probe signature, *offered but not driven* in the report | small | none — read-only metadata | new selectors on unseen models become visible without a user report |
| 2 | HID report-descriptor capture + controller capability record (S7, S8) | medium | low — descriptor read only | the four-zone frame stops being written to keyboards that are not four-zone |
| 3 | A/B captures collected from issue reporters into a per-`Model\|BIOS` corpus | continuous | none | the five `AN16-61` rows in §7.3, and the model rows in §3 marked *test pending* |
