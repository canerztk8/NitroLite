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
| `panel.overdrive` | Supported | |
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
reaction to a write that did not take; the defect is the payload, not the policy. No new payload was
written here — §6 keeps it open as a device-required item.

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
| AN16-61 | Nitro 16 | — | — | — | — | BIOS V1.53, Win 10.0.26300 | **physical** (§1.5) | profile, **partly verified** — fans, modes and sensors work; clock, MUX read/write, zone RGB and Win-key lock open (§3.2) |
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
- **AN515-57** — `lcd_override` writes are accepted and ignored (S1, the package author's own machine).
- **AN515-47** — LCD overdrive has no effect (S1).
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
- **`unsupportedMethods` is left empty everywhere.** The field is parsed and stored but **nothing
  reads it** (`grep -rn "UnsupportedMethods"` finds only the declaration and the parser). Adding
  `SetGamingFanSpeed` for PH16-72 or PHN16S-71 would look like enforcement and would enforce
  nothing. The fan caveats for those two models are recorded in this document and in the profile
  `notes` instead, which is documented as free-form and never interpreted.
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
| AN16-61 CPU clock, at the panel | **code fixed, UI unconfirmed** — the fallback is unit-tested on the reference unit, not seen on an AN16-61 | open the dashboard on an AN16-61 and check the clock is not `-- GHz` |
| AN16-61 MUX write | **test pending** — the write path is now logged, not fixed | reproduce the switch and send the new `MUX write was not attempted` / `MUX mode could not be read` line |
| AN16-61 per-zone keyboard colour | **test pending** — the correct `SetGamingRgbKb` payload is unknown for this firmware | capture the worker reply and a read-back on the machine; do not guess a payload |
| AN16-61 Windows-key lock | **test pending** — no firmware getter exists, so a working write cannot be told from a no-op | needs the machine and a key-by-key check |
| AN16-61 battery charge limit through Care Center | **test pending** — the fallback is written and compiles, nothing more | on the machine, check the battery card appears and the mode reads back |
| The eight model codes in §4.1 | **test pending** | add the profile once a machine confirms its sensor ids |
| `PHIN16-71` / `PHIN16S-71` code validity | **unconfirmed** | check Acer's regional model lists; delete or correct |
| PH16-72 / PHN16S-71 fan control | **source says ❌** | needs a machine; until then the UI may offer fan control that does nothing |
| AN515-58 battery limiter on BIOS V2.19 | **source says accepted, no effect** | needs a machine on that BIOS |
| Per-key RGB keyboards | **not supported by design** | a separate method set; out of scope for the four-zone path |
