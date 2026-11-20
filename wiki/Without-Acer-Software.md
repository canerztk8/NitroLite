# Without Acer's software

NitroLite used to be a front end for Acer's services: no `AcerAgentService`, no fan control. That is no longer
true. The hardware layer sits behind one interface with two backends, and the app chooses per call:

| Backend | Talks to | Needs |
|---|---|---|
| `AgentHardwareController` | `AcerAgentService` on `127.0.0.1:46933`, and Acer Care Center for the charge limit | NitroSense installed |
| `WmiHardwareController` | the firmware's `AcerGamingFunction` class, through an elevated helper process | one UAC prompt per session |
| `LinuxSysfsHardwareController` | `acer-wmi`, `hwmon`, `platform_profile` and Linuwu-Sense | the in-kernel driver |

`HybridHardwareController` picks one per call and reconciles the two on handover, so a machine that gains or loses
the Acer service mid-session does not end up with half its settings in one place and half in the other.

### The elevation prompt

`AcerGamingFunction` is a `dynamic` WMI class: reading it and writing it both need an elevated token. The window
itself stays `asInvoker` — it never asks for administrator rights at launch. Instead:

1. Everything Windows and NVIDIA can answer keeps working unelevated.
2. The first hardware **write** starts `NitroLite.exe --wmi-worker` with `Verb = "runas"`. That is one prompt per
   session. Declining it is remembered: the app keeps running in read-only mode and does not ask again.
3. The helper is a child process, not a service. A Job Object and a parent-process watchdog tie it to the window, so
   it dies with the app even if the app crashes.
4. The pipe it listens on is `CurrentUserOnly`, the first packet must carry a one-time 32-byte nonce, the caller is
   authenticated by process id, and the command set is a fixed list of typed operations — never a general "invoke
   this WMI method" call. The worker re-checks every bound itself (`30–100 %` fan speed, valid enum values) before
   touching the hardware.

If you would rather not see that prompt at all, **Settings → Start automatically with administrator rights (Task
Scheduler)** registers a logon task with `RunLevel HighestAvailable`. It is off by default and costs one prompt to
turn on and one to turn off; after that the helper inherits the elevated token and no prompt appears. Turning it on
removes the standard `HKCU\…\Run` entry, so Windows never starts NitroLite twice. The task definition is kept off the
disk.

### Writes are verified, not assumed

Every WMI write is followed by a read-back of the same value. If three attempts do not stick — a BIOS that accepts
the call and ignores it — that capability is switched off for the rest of the session and the app says so. Only
three consecutive sessions of failure disable it permanently, so one busy EC after a sleep/wake cycle cannot kill a
working feature. **Settings → Rescan hardware capabilities** clears the counters and probes again.

### Capability probing

Nothing about the hardware is hardcoded to one SKU. At startup NitroLite asks three questions in order:

1. **Schema and registry** — does `root\WMI` expose `AcerGamingFunction`, which methods does it declare, does
   `WMID_GUID5` exist, what does `dgpu_mode_capability` say. All read-only and unelevated.
2. **Model profile** — SMBIOS product and board are matched against an embedded, read-only `profiles.json` that
   carries sensor byte offsets, reference RPM ceilings and known-broken method signatures for 32 models across the
   Nitro 5/16/17, Nitro V 15/16 and Predator Helios/Triton families.
3. **Cache** — the result is stored under `SMBIOS product + BIOS version`, so a BIOS update invalidates it
   automatically.

The probe runs in the background while the window opens; the hardware controls stay in a "scanning" state until it
finishes, so nothing can be pressed against a half-loaded capability set.

A `profiles.user.json` in the data folder can add a model without a new release. It is validated strictly: unknown
fields, out-of-range RPM ceilings and malformed JSON are rejected and logged, and the default profile is used
instead. The thermal floors (`85 / 90 / 95 °C`), the `30–100 %` PWM bounds and the school-mode rules are compiled
into the app and cannot be relaxed by any profile file.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
