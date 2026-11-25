# NitroSense inventory on the reference AN16-41 (2026-11-25)

Read-only inspection of the NitroSense installation on the maintainer's Nitro AN16-41 (Windows 11). Nothing was
executed, modified or copied into NitroLite. Configuration files are quoted as facts about the machine; no Acer code is
reproduced. Every finding below is **evidence of what Acer's software expects**, not proof of what a given model's
firmware does: a model still needs its owner's report before NitroLite changes behaviour for it.

## Where the pieces are

| Component | Location | What it is |
|---|---|---|
| NitroSense app 5.0.1327.0 | `C:\Program Files\WindowsApps\ULICTekInc.NitroSenseforNotebook_5.0.1327.0_x64__nt9dgb7efx6bt\app` | Electron UI (publisher ULICTek). The folder listing is protected, but the package folder itself is readable by name. |
| Hardware bridge | `…\resources\app.asar.unpacked\node_modules\xsense_native\…\xsense_native.node` | Native Node addon the UI calls. |
| Model allowlist | `…\resources\src\assets\SupportedModel.txt` | 16 model names (below). |
| Game list | `…\win32\Settings.json` | ~80 executables NitroSense treats as games. |
| Config installer | `…\win32\NitroSenseConfig.msi` (132 MB) | Not inspected yet; likely carries newer per-model files. |
| Acer services package | `C:\Windows\System32\DriverStore\FileRepository\predatorservice.inf_amd64_269fd3cb3a95f72c` | `AcerCentralService.exe` (= **AASSvc**, "Acer Agent Service"), `AcerHardwareService.exe`, `AcerLightingService.exe`, `AcerAgentService.exe`, EC/USB lighting DLLs, bundled `OpenRGB.exe`, `hidapi.dll`, `libusb-1.0.dll`, `inpoutx64.dll`, 18 per-model `.ini` files, `RGBDevice.ini`. Files dated 2023-07-21. |
| Other Acer services | `C:\Program Files\AcerCCAgent`, `AcerDIAgent`, `AcerQAAgent` | Care Center (charge limit), device info, quick access. |

Running on this machine: AcerLightingService, AcerCCAgent, AcerCentralService, AcerAgentService, AcerHardwareService.
The NitroSense window itself was not running: NitroLite's own Nitro-key routing closed it at 22:38:58
(`NitroSenseWatcher`, log line "The NitroSense process was closed").

## Models

`SupportedModel.txt` (the app's allowlist): AN14-41, AN16-41, AN16-42, AN16-43, AN16-51, AN16-72, AN16-73, AN17-41,
AN17-42, AN17-51, AN17-71, AN17-72, ANV14-61, ANV15-41, ANV15-51, ANV16-41.

Per-model `.ini` in the services package: AN16-41, AN16-51, AN16-71, AN17-41, AN17-51, AN17-71, AN515-58, AN517-55,
ANV15-51, PH16-71, PH18-71, PH315-55, PH315-55s, PH3D15-71, PHN16-71, PT14-51, PT16-51, PTX17-71.

**Not in NitroLite's profile table (36 codes) yet:** AN14-41, AN16-51, AN16-71, AN16-72, AN16-73, AN17-41, AN17-71,
AN17-72, AN517-55, ANV14-61, PH16-71, PH315-55, PH315-55s, PH3D15-71, PT14-51, PT16-51. They are matched by the
`acer-gaming-default` catch-all today.

## What the per-model files say

- `[OC_GPU]`: the NVIDIA GPUs (by PCI `VEN/DEV/SUBSYS`) each model ships with, and a GPU core/memory offset per mode.
  Typical Nitro: Quiet 0, Default 0, Extreme +50, Turbo +100 (MHz), memory 0. PH315-55: Extreme and Turbo +100, memory
  +200. PTX17-71: Turbo only. The SUBSYS id identifies the board (AN16-41: `…1679/168B1025`).
- `[OC_CPU]` / `[PL_CPU]` (AN515-58, AN517-55, PH315-55, PH3D15-71): the CPUs that get CPU overclocking and power-limit
  control, by exact name; PL also keys on a BIOS version.
- `[FanSupport]` (PTX17-71 only): fan count per type, CPU 1, GPU 2, system 0. The other files do not declare fans, so
  fan topology is not in these files for most models.
- PH315-55 family: `SpecialHotkey`, hotkey default groups and an app-launcher list.

`RGBDevice.ini`: the keyboard controllers NitroSense knows — Chicony USB `04F2:0117/0119/011A` (usage page FF02),
Sunrex USB `05AF:766A–766E` (FF02), and two EC devices (`AcerECKeyboardController`, `AcerECLightbarController`).
NitroLite's HID table currently knows ENE `0CF2:5130` and lists Chicony `04F2:*` as read-only (fixed palette); the
Sunrex `05AF` ids are not in it.

## WMI methods the services use

From string tables of the service binaries (names only):

- `AcerHardwareService.exe`: the `AcerGamingFunction` set NitroLite already uses (`Get/SetGamingFanBehavior`,
  `…FanSpeed`, `…FanTable`, `…MiscSetting`, `…Profile`, `…LED`, `GetGamingSysInfo`) **plus a second generation** of
  methods NitroLite does not know: `Get/SetGamingFanGroupBehavior`, `Get/SetGamingFanGroupSpeed`,
  `Get/SetGamingProfileConfiguration`, `GetGamingSystemInformation`, `Get/SetGamingMiscSettingBehavior`,
  `SetGamingLEDBehavior`. Several appear with a `treadstone` suffix, which looks like a platform code name.
  `APGeAction.Get/SetFunction` and the `APGeEventMessage` event are used too.
- `AcerLightingService.exe`: `SetGamingRgbKb`, `SetGamingKBBacklight`, `GetGamingKBBacklight`, `SetGamingLEDBehavior`,
  and `MSSMBios` (it reads SMBIOS to pick the model).

## What this changes for NitroLite (proposals, none implemented)

1. **Profiles for the 16 missing models**, marked `verified: false`, with `fourZoneKeyboard`/`muxSwitch` left unset
   until a report. Low risk: a profile only narrows plausibility bounds and names the model.
2. **Fan-group methods.** Newer boards may answer `SetGamingFanGroup*` where the classic `SetGamingFanSpeed` is ignored
   (the PH16-72 / PHN16S-71 fan reports). Needs the argument layout from the service binary and a test on one of those
   models before any write — R3.
3. **Sunrex `05AF:766x` keyboards**: add to the HID table as recognised but read-only until a documented frame exists.
4. **Inspect `NitroSenseConfig.msi`** for newer per-model files (2024–2025 models).

## Limits

- One machine, one NitroSense version (5.0.1327.0) and one service package (2023-07-21). Newer models may ship
  different files.
- String tables show which methods a binary references, not which ones it calls on a given model or with which
  arguments.
- Acer's software license applies to these binaries; this document records facts about the installed files and does not
  reproduce Acer code.

## How NitroSense controls the hardware (2026-11-26)

Read-only reading of NitroSense's own app code (`main.js` and the renderer bundle inside `app.asar`; the native
service binaries still need Ghidra, which is not installed). The answer to "how does it work on every model" is that
**NitroSense keeps no model table for its controls: it asks Acer's service what the machine has.**

- Transport: TCP `127.0.0.1:46933` (AASSvc), frames `"ACER"` + 32-bit length + JSON — the same service and framing
  NitroLite's `AcerAgentClient` already uses.
- Packet ids: `0` INITIALIZATION (capability questions), `10` GET_MONITOR_DATA, `20` GET_UPDATED_DATA (read one
  setting), `30` GET_CONNECTED_DEVICE (lighting devices), `40` GET_LIGHTING_EFFECT_CAPABILITY, `100` SET_DEVICE_DATA
  (`{Function, Parameter}`).
- Capability questions it sends at start-up (`{Function: …}` on packet 0): `SUPPORT_OPERATING_MODE_CAPABILITY`,
  `SUPPORT_GPU_MODE`, `SUPPORT_GPU_MODE_CAPABILITY`, `SUPPORT_FAN_CONTROL`, `SUPPORT_LCD_OVERDRIVE`,
  `SUPPORT_KEYBOARD_INFO`, `SUPPORT_CPU_INFO`, `SUPPORT_GPU_INFO`, `SUPPORT_AUDIO_MODE`, `SUPPORT_CUSTOM_BOOT_LOGO`,
  `SUPPORT_OVERCLOCKING_MODE_CAPABILITY`, plus `BATTERY_BOOST` on packet 20.
- `SUPPORT_FAN_CONTROL` answers a **list of fans**, each with `fan_index`, `fan_custom_min_speed` and
  `fan_custom_max_speed`. The fan setting it writes is `{mode, custom_fan_data: [{fan_custom_auto, fan_custom_speed,
  fan_index, fan_name}]}` with names `CPU`, `GPU` and **`GPU2`** (index 1) — a third fan on some models.
- Lighting: devices come from `GET_CONNECTED_DEVICE` and their effects from `GET_LIGHTING_EFFECT_CAPABILITY`
  (device types keyboard, lightbar, logo …; lightbar via EC or USB).

### NitroLite today (AcerAgentClient)

Already used: `VERSION`, `GET_MONITOR_DATA`, `SUPPORT_OPERATING_MODE_CAPABILITY`, `SUPPORT_GPU_MODE_CAPABILITY`,
`FAN_CONTROL`, `OPERATING_MODE`, `GPU_MODE`, `LIGHTING`, `BACKLIGHT_30S`, `WIN_KEY`, `GET_CONNECTED_DEVICE`.

Not used, and each one is a place where NitroLite guesses from its own table while NitroSense asks the machine:
`SUPPORT_FAN_CONTROL` (fan count, the third `GPU2` fan, per-fan minimum and maximum), `SUPPORT_LCD_OVERDRIVE`,
`SUPPORT_KEYBOARD_INFO`, `SUPPORT_GPU_MODE`, `GET_LIGHTING_EFFECT_CAPABILITY`, `BATTERY_BOOST`,
`SUPPORT_OVERCLOCKING_MODE_CAPABILITY`. NitroLite always writes exactly two fans (CPU, GPU) with its own floor.

### Proposed order (none implemented)

1. **Done (2026-11-26):** a read-only capability dump, `NitroLite.Desktop.exe --agent-capabilities`, that asks every
   question above and prints the raw answers; the reference AN16-41's answers are in the next section.
2. Use those answers as the capability source when the service answers, and the profile table only as the fallback
   (direct WMI, Linux): fan list and limits, overdrive, keyboard type, GPU modes.
3. Write the third fan (`GPU2`) and respect each fan's min/max from `SUPPORT_FAN_CONTROL` — R3, needs a test on a
   three-fan Predator before release.
4. Install Ghidra 12.2+ (`GHIDRA_INSTALL_DIR`) to read the service binaries: that is where the service maps each
   model to WMI calls (`SetGamingFanGroup*` and the rest), which matters for machines without the service.

### First capability dump: reference AN16-41 (2026-11-26)

`NitroLite.Desktop.exe --agent-capabilities` (read-only; packets 0, 20, 30, 40 only) on the maintainer's AN16-41,
BIOS V1.27, AASSvc running:

| Question | Answer |
|---|---|
| SUPPORT_OPERATING_MODE_CAPABILITY | `[0,1,4,5,6]` — Quiet, Default, Extreme, Turbo, Eco |
| SUPPORT_GPU_MODE | `[0]` |
| SUPPORT_GPU_MODE_CAPABILITY | `[7]` (Optimus, Discrete, Automatic) |
| SUPPORT_FAN_CONTROL | two fans, `CPU` and `GPU`, both `fan_index 0`, custom range **0–100 %** |
| SUPPORT_LCD_OVERDRIVE | `[1]` |
| SUPPORT_KEYBOARD_INFO | `backlight_color_control 2`, `keyboard_language 3`, `keyboard_led_type 4`, `lightbar_type 2` |
| SUPPORT_CPU_INFO | AMD Ryzen 7 7840HS, max 5000 MHz |
| SUPPORT_GPU_INFO | NVIDIA GeForce RTX 4070 Laptop GPU |
| SUPPORT_AUDIO_MODE | result `1` (not supported) |
| SUPPORT_CUSTOM_BOOT_LOGO | `[0]` |
| SUPPORT_OVERCLOCKING_MODE_CAPABILITY | result `3` |
| BATTERY_BOOST | `{status: 1}` |
| GET_CONNECTED_DEVICE | `AcerECKeyboard Device`, type 5, brightness 5, modes from `DIRECT` (4 colours) upwards |
| GET_LIGHTING_EFFECT_CAPABILITY | result `0`, no data |

Notes: the service's own fan floor is 0 %, so NitroLite's minimum stays its own safety rule, not the firmware's.
The answers match what NitroLite's profile says for this model (MUX 7, overdrive on, four zones) — the first time that
has been read from the machine itself rather than from the table. The third fan (`GPU2`) does not exist here.
