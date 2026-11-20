# Compatibility

NitroLite targets the whole Acer Nitro line (Nitro 5, 16 and 17; Nitro V 15 and 16; Intel and AMD), not one SKU.
Nothing about the hardware is hardcoded: the app asks, and hides what the answer does not cover.

| What | Where the answer comes from | When the answer is missing |
|---|---|---|
| Model, family, BIOS, CPU vendor | SMBIOS (`Win32_ComputerSystem`, `Win32_BaseBoard`, `Win32_BIOS`) | the device block says SMBIOS did not answer |
| Operating modes | `SUPPORT_OPERATING_MODE_CAPABILITY`, or the WMI method list | every mode stays visible, because a failed query is not evidence a mode is absent |
| Keyboard zones and effects | `GET_CONNECTED_DEVICE` | the four-zone layout stays, for the same reason |
| GPU mode (MUX) | `SUPPORT_GPU_MODE_CAPABILITY`, or `dgpu_mode_capability` in the registry | mode switching is disabled with a note, never guessed |
| 80 % charge limit | Acer Care Center (`AcerCCAgent`), or `WMID_GUID5` | the checkbox is disabled and says why |
| Fan curve | the Acer service's own fixed-speed command, or `SetGamingFanSpeed` | the curve turns itself off and says so |
| The Acer service itself | `AASSvc` on `127.0.0.1:46933` | the direct-WMI backend takes over; if that is unavailable too, a standing note names the missing service and everything Windows and NVIDIA can measure keeps working |

Two things decide whether the direct-WMI fallback can take over, and both are worth checking when a machine reports "nothing detected":

- **The install folder must be trusted.** The elevated WMI worker is only launched from an executable no other local account can replace. Anything under your own profile (`%LOCALAPPDATA%\NitroLite`, a portable or Scoop folder) and anything in `C:\Program Files` qualifies. A folder you created directly under a drive root (`C:\WinTools`) does **not**: the drive root grants `Authenticated Users` modify to everything created beneath it, so the files in that folder are replaceable by any local account, and the worker refuses to start from there **when it would have to ask for elevation**. That is the only case the rule is about: a helper started by a NitroLite that is already running as administrator inherits that token, so nothing is granted that the process did not hold already — which is why **Run as administrator**, or the Task Scheduler entry, makes fan and mode control work from `C:\WinTools` as well. Without either, the log says so once, with the path, the settings row names the refusal, and the fan and mode rows keep pointing at the Acer service.
- **The `AcerGamingFunction` WMI class must be registered.** On models where Acer ships only the service, the service path is the only path; NitroLite then needs `AASSvc` running.

**No kernel driver, no EC register writes.** Fan and lighting control go through the Acer service that NitroSense
already uses, or through the firmware's own WMI class, and CPU boost through the Windows power plan. Nothing here
loads `WinRing0` or any other driver, which is the usual reason an anti-cheat refuses to start next to a hardware
tool. That is a statement about what NitroLite loads, not a guarantee: anti-cheat vendors keep their own allow-lists,
and a game that blocks unsigned overlays will still block the OSD.

**A model can be added without a new release.** At startup NitroLite fetches the profile table published with this
project, checks it against the same rules the built-in table has to pass — every record valid, sensible fan ceilings
and sensor ids — and keeps it next to your own file. The order is: your `profiles.user.json`, then the downloaded
table, then the one inside the binary. If you are offline, or the download is damaged, nothing changes and nothing is
reported as an error. Delete `%LOCALAPPDATA%\NitroLite\profiles.remote.json` to go back to the built-in table.

### If your model is not covered

**Settings → Export hardware report** writes `hardware-report-<timestamp>.txt` and `.json` into the data folder
and opens it. The report lists the model code, BIOS, Acer service states, the keyboard report, the operating
modes, the GPU mode capability and whether the Acer WMI class answers. It also lists the USB HID collections the
machine exposes — the ITE (`0x048D`), Sonix (`0x0C45`) and Chicony controllers a backlight is often wired to when
no Acer class admits to driving it — and the sensor verdict the fan curve ran under, `runtime-verified` or
`unverified`. Attach it to a GitHub issue: it is the difference between "the fan slider does nothing" and a fix.
It contains no serial number, user name, file path or log content — every line goes through the same redactor the
log uses.

**Settings → Report compatibility on GitHub** does both steps at once: it copies the report to the clipboard and
opens a new issue with `[Compatibility] <model> (<board>)` as the title and the report as the body. If the report
is longer than a URL can safely carry, the body is cut and the note says so — the full report is already on your
clipboard, ready to paste over it.

When a setting simply does nothing, the **NitroSense snapshot diff** under **Settings → Diagnostics** narrows it
down to the exact register. Press **Capture state A** in NitroLite, change the one setting in NitroSense, then press
**Capture state B and diff**: the two read-only captures of the Acer WMI selectors and the vendor HID feature
reports are compared and only what moved is printed — a changed selector with its old and new value, and for a
report, the byte offsets that changed. The diff goes to the clipboard with the hardware report and opens the same
prefilled issue with the diff first. Nothing is written and nothing is elevated: a session with no direct-WMI
worker behind it captures the HID half and says so.

The same report is available without the window: `NitroLite.exe --hardware-report` (add `--json` for JSON, or
`--out <folder>` to write both files).

---

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
