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
