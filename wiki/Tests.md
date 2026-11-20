# Tests

2909 tests: protocol packets, Eco/game transitions, recovery scenarios, formatting, boundary values,
concurrency, and the read-back rule that decides whether a hardware write may be called applied.

```powershell
dotnet test NitroLite.Tests/NitroLite.Tests.csproj
```

- The tests use their own temporary data folder (`NITROLITE_DATA_DIR`) and never touch your real settings.
- Six tests are skipped by default: four that change the real system (changing a connected mouse's profile, power
  plan changes, the live charge limit probe, the live CPU power service) and two that need a real release package in
  `artifacts/`. To run the first four:

```powershell
$env:NITROLITE_ENABLE_LIVE_HARDWARE_TESTS = "1"; dotnet test NitroLite.Tests/NitroLite.Tests.csproj
```

Three suites are worth knowing about, because they are what keeps a hardware app honest without a hardware lab:

- **`HardwareMatrixAndChaosTests`** drives five chassis profiles — a fully equipped AN16-41, a Predator
  Helios/Triton, an old Nitro 5 with a single lighting zone and no MUX, a Nitro V 15 with restricted WMI, and an
  unsupported device — through every page and command, and asserts no unhandled exception and a `--` fallback for
  every sensor that is missing.
- **The fake `sysfs` tree** builds `hwmon` (`acer`, `k10temp`, `coretemp`) and `platform_profile` in a temporary
  folder, then covers a missing node, a corrupt sensor file and a read-only `platform_profile`.
- **`ChaosHardwareDecorator`** injects `false`, `TimeoutException`, `0 °C`, `127 °C` and `NaN` through 100
  consecutive telemetry and setting cycles, and the settings, `FanCurve` and `ManualFanFloor` are fed `NaN`,
  `Infinity`, `-100` and `500` to prove they clamp instead of propagating.

### Capturing screenshots

`scripts/screenshot.ps1` captures pages without you having to open the app, centre it and grab the
window by hand. It starts NitroLite against a throwaway data folder, parks the window in a corner,
walks the pages, captures each one with `PrintWindow`, then closes the app and deletes the folder.
Your real settings are never read or written, and the NitroLite you already have in the tray is left
alone: the harness asks for a copy that owns no single-instance slot.

```powershell
# every page, dark theme, German
powershell -ExecutionPolicy Bypass -File scripts\screenshot.ps1 -All -Theme dark -Language de

# one page, into a folder of your choice
powershell -ExecutionPolicy Bypass -File scripts\screenshot.ps1 -Page NavBattery -Out C:\temp\pil

# let the telemetry chart fill before the first capture
powershell -ExecutionPolicy Bypass -File scripts\screenshot.ps1 -All -Theme dark -Settle 62000
```

Pages are addressed by `AutomationId` (`NavModes`, `NavMonitoring`, `NavLighting`, `NavBattery`,
`NavTools`), not by their visible label, so the same command works in all eight languages. The two
names the retired WPF build used — `NavRgb` and `NavDebloat` — are still accepted and map onto
`NavLighting` and `NavTools`. `-Position bottom` pushes the window behind whatever you already have
open. There is no fully invisible mode: the compositor stops rendering a window that is completely
off-screen, and `PrintWindow` then returns a stale frame.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
