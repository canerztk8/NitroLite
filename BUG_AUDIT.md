# NitroLite — Bug and Resource-Leak Audit

Working branch: `audit/hardening-2026-10` (branched from `main` at `66ce36c`).
Nothing in this document is a claim about a test that was not run. Every result
below names the command that produced it and the log file it was written to.

---

## 0. Scope and method

The audit follows the twelve risk classes of the `security-leak-hunter` skill:
privilege and IPC, injection and traversal, hardware and thermal safety, network
and deserialization, information disclosure, unmanaged leaks, managed leaks,
concurrency, atomic writes, polling cost, build hardening, and test design.

A finding is only recorded when it carries a file path and a line number and is
reachable in the shipped application. A suspected issue that turned out to be
unreachable is recorded as such rather than deleted, so the next reader does not
re-investigate it.

Severity: `KRITIK` / `YUKSEK` / `ORTA` / `DUSUK`.

---

## 1. Baseline, before any change

Recorded on the device at hand, before a single line was edited.

### 1.1 Environment

| Fact | Value |
| --- | --- |
| OS | Microsoft Windows 11 Home, 10.0.26300 |
| .NET SDK | 10.0.401 |
| Runtime | Microsoft.NETCore.App 10.0.12 |
| Host | `CZTRK` |

### 1.2 Device identity (read-only, from the app's own report)

Produced by `NitroLite.Desktop.exe --hardware-report`, which only reads.

| Fact | Value |
| --- | --- |
| Manufacturer | Acer |
| Model | Nitro AN16-41 |
| Model code | AN16-41 |
| Family | Acer Nitro 16 |
| Board | Sierra_PEH |
| BIOS | INSYDE Corp. 1.27 |
| CPU vendor | Amd |
| Matched profile | `nitro-16-an16-41` (embedded, `verified: true`) |

This is the reference machine the embedded profile was measured on, so the
profile match is a measurement and not a name guess.

### 1.3 Build

```
dotnet build NitroLite.sln -c Release --nologo -warnaserror
```

| Result | Value |
| --- | --- |
| Exit code | 0 |
| Warnings | 0 |
| Errors | 0 |
| Duration | 13.35 s |
| Log | `.audit/baseline-build.log` |

`Directory.Build.props` sets `TreatWarningsAsErrors` and
`AnalysisLevel=latest-recommended`, so a clean build here means the .NET
analyzers are clean too, not merely the compiler.

### 1.4 Tests

```
dotnet test NitroLite.Tests/NitroLite.Tests.csproj -c Release --no-build --nologo
```

| Result | Value |
| --- | --- |
| Exit code | 0 |
| Passed | 2743 |
| Failed | 0 |
| Skipped | 4 |
| Total | 2747 |
| Duration | 3 m 15 s |
| Log | `.audit/baseline-test.log` |

The four skips are the live-hardware tests, which require
`NITROLITE_ENABLE_LIVE_HARDWARE_TESTS=1` and a machine with the Acer service
answering:

- `SchoolModeServiceTests.EverySchoolModeSetting_ExistsInTheActiveScheme_AndAcceptsAWrite`
- `VerifiedDefectsRegressionTests.Defect5_AtkMouseService_SwitchProfile_SafeExecution`
- `BatteryChargeLimitProbeTests.LiveProbe_AgreesWithTheInterfacesItCanSee`
- `CpuPowerServiceTests.ServiceInitialization_InstantiatesAndQueriesCurrentMode`

They are recorded as **not run**, not as passed.

### 1.5 Hardware backend state on this device

From the same read-only report:

| Capability | Outcome | Detail |
| --- | --- | --- |
| `wmi.gamingFunction` | AccessDenied | dynamic class, instance needs elevation |
| `wmi.batteryChargeLimit` | Supported | Acer Care Center (`AcerCCAgentSvis` running) |
| `mux.switch` | Supported | `dgpu_mode_capability=7` |
| `keyboard.zones` | Supported | 4 zones |
| `gpu.overclock` | Supported | `GPU_OC_Support=1` |
| `panel.overdrive` | Supported | `LCD_Overdrive_support=1` |

Backend state: `active AASSvc; AASSvc not connected; direct WMI not started`.

Consequence for this audit: the Acer service is not answering and the direct-WMI
path needs elevation, so **every hardware write on this machine is unavailable
without a UAC prompt**. The audit therefore runs read-only against the hardware,
which is what the goal asks for anyway. A feature that cannot be exercised
without elevation is marked *test pending*, never *passed*.

---

## 2. Findings

Findings are added here as they are proven, in the order they were verified.
Each carries the reproduction step or measurement that showed it, the fix, and
the re-check that confirmed the fix.

Six read-only audit agents swept the tree: `managed-leaks`, `unmanaged-leaks`,
`concurrency`, `data-integrity`, `hardware-safety`, `security-ipc`. Everything
below is a finding one of them produced **and** that was reproduced here before
being touched. Suspected issues that turned out to be unreachable are listed in
§2.9 rather than deleted, so the next reader does not re-investigate them.

### 2.1 Summary

| ID | Severity | Area | Fixed in |
| --- | --- | --- | --- |
| SEC-01 | KRITIK | elevated logon task registered against an untrusted path | `0e9c191` |
| RACE-01 | YUKSEK | pipe framing corruption between the fan loop and the UI tick | `0e9c191` |
| BUG-01 | YUKSEK | the telemetry poll started the elevated worker, raising UAC on first tick | `0e9c191` |
| LEAK-01..06 | ORTA | managed objects never disposed at shutdown | `0e9c191` |
| DATA-06 | ORTA | an unrotatable log grew without bound | `0e9c191` |
| SEC-04 | DUSUK | a raw error string reached the diagnostics bundle unscrubbed | `0e9c191` |
| DATA-01 | YUKSEK | a power cut could leave a zero-content `NitroLite.exe` | `8ead4d0` |
| DATA-02/03 | ORTA | pre-restore safety copies overwrote each other; RGB presets had none | `8ead4d0` |
| DATA-04 | ORTA | the debloat backup was saved before the completeness loop | `8ead4d0` |
| DATA-05 | ORTA | a failed diagnostics bundle deleted the previous archive | `8ead4d0` |
| DATA-07 | ORTA | a corrupt RGB preset file was quarantined under a fixed name | `8ead4d0` |
| HW-01 | YUKSEK | a fixed fan speed could be written after the shutdown hand-back | group 3 |
| HW-02 | ORTA | a fan write was confirmed by reading the mode, not the speed | group 3 |
| HW-03 | ORTA | a refused fan write left the firmware in Custom mode | group 3 |
| HW-04 | YUKSEK | the fan curve acted on an unconfirmed temperature layout | group 3 |
| HW-05 | ORTA | the hand-back used `Stop()`, which does not wait for the loop | group 3 |

### 2.2 SEC-01 — the elevated logon task could be pointed at a user-writable directory

**KRITIK.** `NitroLite.Windows/Core/ElevatedStartupService.cs`.

The service registers a logon task with `HighestAvailable` so the WMI worker can
start elevated without a prompt. It refused paths under `%TEMP%` and `Downloads`
by name, but nothing else. `C:\WinTools` — a directory the drive root grants
`Authenticated Users` modify on a default Windows install — passed that test, so
a local user could drop an executable there, let the task register against it,
and get code running as administrator at the next logon.

*Fix:* the same `DirectoryTrust.IsExecutableTrusted(exePath)` predicate
`WmiWorkerLauncher` already used is now applied before the task is registered.

*Re-check:* build 0/0; the existing `ElevatedStartupService` tests pass; the app
starts and the worker connects (`--wmi-worker connected` in the log).

### 2.3 RACE-01 — two threads shared one pipe without a lock

**YUKSEK.** `NitroLite.Windows/Core/Hardware/Wmi/WmiWorkerClient.cs`.

`SendAsync` wrote a request and then read the reply with no mutual exclusion. The
fan loop (1.5 s) and the UI telemetry tick (1 s) both call it, so a write from one
could be answered by the other's read: the fan loop would parse a telemetry reply
as a fan result and the UI would show a fan result as a temperature.

*Fix:* a `SemaphoreSlim _exchange = new(1, 1)` held across write **and** read.
`Dispose` deliberately does not dispose it — a holder may still be inside.

*Re-check:* full suite green; the app ran with both loops active and the log shows
no framing error.

### 2.4 BUG-01 — the telemetry poll raised a UAC prompt

**YUKSEK.** `NitroLite.Desktop/ViewModels/MainViewModel.cs`,
`NitroLite.Windows/Core/Hardware/Wmi/HybridHardwareController.cs`,
`NitroLite.Core/Hardware/IHardwareController.cs`.

The poll called `GetMonitorDataAsync`, which resolves the backend and therefore
*starts* the elevated worker. On a default machine that is a UAC prompt one second
after launch, before the user has asked for anything.

*Fix:* a default interface method `GetTelemetryAsync()` that falls back to
`GetMonitorDataAsync()`; `HybridHardwareController` overrides it to read whatever
backend is already attached and never resolve a new one. The poll calls it.

*Re-check:* the app starts with `direct WMI not started` in the report and the
worker only appears once a fan or mode write needs it.

### 2.5 LEAK-01..06 — objects that were never disposed

**ORTA.** `NitroLite.Desktop/AppHost.cs`, `App.axaml.cs`,
`Views/MainWindow.axaml.cs`, `NitroLite.Core/SchoolModeService.cs`.

`AppHost.Dispose` released four of the services it owned; `_nitroSense`,
`_schoolMode`, `_cpuBoost` and `_monitor` were dropped on the floor.
`SchoolModeService` held a `_cpuBoost` it never disposed and was not
`IDisposable`. `App.axaml.cs` only tore `_nitroSense` down inside the
`if (_nitroKey != null)` branch, so a machine without the Nitro key leaked it.
`MainWindow` subscribed `OsdChanged` with a lambda, which cannot be unsubscribed.
Two `Process.Start` results were never disposed.

*Fix:* `SchoolModeService : IDisposable` with a `_disposed` guard; `AppHost`
disposes all eight; the `_nitroSense` teardown moved out of the branch; the tray
icon is removed from `TrayIcon.GetIcons(this)` before dispose;
`Dispatcher.UIThread.UnhandledException -= OnUiException`; `OsdChanged` became a
named handler unsubscribed in `Closed`; `Process.Start(...)?.Dispose()` on both
signature-link paths. `ShutdownHandbackTimeout` raised 2 s → 5 s so the hand-back
has room on a slow machine.

*Re-check:* the app starts, runs, and exits with no `ObjectDisposedException` in
the log.

### 2.6 DATA-01..07 — writes that could lose data

**YUKSEK / ORTA.** `NitroLite.Core/AtomicFile.cs`, `UpdateService.cs`,
`BackupService.cs`, `RgbProfileService.cs`,
`NitroLite.Windows/Core/DebloatService.cs`, `DiagnosticsBundle.cs`.

- **DATA-01** `UpdateService` copied the new executable with `File.Copy`. A power
  cut between the rename and the flush leaves a correctly named, correctly sized,
  zero-content `NitroLite.exe` — and the repair pass can never run, because the
  corrupt file *is* the executable. *Fix:* `AtomicFile.CopyDurable`, which opens
  with `FileOptions.WriteThrough` and calls `Flush(flushToDisk: true)`.
- **DATA-02/03** `BackupService` wrote its pre-restore copies to a fixed name, so
  restoring twice kept only the second. RGB presets got no safety copy at all.
  *Fix:* `{path}{PreRestoreSuffix}-{yyyyMMdd-HHmmss-fff}`, and the RGB presets are
  copied too.
- **DATA-04** `DebloatService` saved its backup state *before* the completeness
  loop, so a crash mid-loop recorded a backup that was never finished. *Fix:* the
  loop moved above `SaveBackupState`.
- **DATA-05** `DiagnosticsBundle.Create` deleted the partial zip on failure — but
  on the first failure `stream` was still null and the delete removed the
  *previous* archive. *Fix:* wrapped in try/catch, and the delete only runs when
  `stream != null`. An existing test caught the first attempt at this.
- **DATA-06** `AppLog.RotateIfNeeded` returned `false` on every path, so the
  caller's "did it rotate?" guard was dead and an unrotatable log grew without
  bound. *Fix:* `return true` in the catch.
- **DATA-07** `RgbProfileService` quarantined a corrupt preset file under a fixed
  `.bad` name, so a second corruption overwrote the first. *Fix:*
  `{_presetsFile}.bad-{yyyyMMdd-HHmmss-fff}` with `overwrite: false`.

*Re-check:* two new tests (`RgbProfileService_QuarantiningTwice_KeepsBothCopies`,
`BackupService_RestoringTwice_KeepsTheSettingsFromBeforeTheFirstRestore`) plus the
updated corrupt-file test; suite 2745 passed, 0 failed.

### 2.7 HW-01..05 — fan writes that could outlive the loop that owns them

**YUKSEK / ORTA.** `NitroLite.Desktop/ViewModels/MainViewModel.cs`,
`NitroLite.Core/Models.cs`,
`NitroLite.Windows/Core/Hardware/Wmi/WmiHardwareController.cs`.

- **HW-01** `RestoreFansToAutoAsync` called `_curveLoop.Stop()`, which signals but
  does not wait. A step already past the mode check could reach the write gate
  *after* the hand-back wrote `Auto` and put a fixed speed back over the
  firmware's own control, with no loop left to correct it. *Fix:* a
  `_handingBackFans` flag set before the stop, `StopAndWait(1 s)`, and the flag
  re-checked **inside** the gate in `WriteFanPercentAsync`.
- **HW-02** `SetFanModeAsync(Custom)` confirmed the write by reading the *mode*
  back and comparing it to `Custom`. The firmware accepts the mode and ignores the
  speed, so a refused speed looked applied. *Fix:* read `cpuFanPercent` back and
  accept within `FanPercentReadBackTolerance = 2`.
- **HW-03** when the fan method was `DisabledForSession` or `PermanentlyDisabled`,
  the controller returned without writing anything, leaving the firmware in
  whatever mode the last successful call set. *Fix:* write `FanMode.Auto` through
  `ReadBackPolicy.FanBehavior`.
- **HW-04** the curve followed `CpuTemperature` even when the sensor layout was
  unconfirmed — a guess about which id answers what. A guess that reads the 55 °C
  system sensor as the die asks for a third of full speed at 95 °C, and the 95 °C
  floor never fires because the curve never sees 95 °C. *Fix:*
  `MonitorData.IsTemperatureMeasured` (defaults **true**, so nothing changes for a
  confirmed machine); `BuildMonitorData` sets it from `temperaturesConfirmed`
  while still publishing the temperatures, so the page is not blank;
  `StepFanAsync` returns early and logs once per session.
- **HW-05** the hand-back gate wait had no timeout. *Fix:* `FanHandbackGateTimeout`
  and `FanHandbackLoopTimeout` at 1 s each, with the flag cleared on every failure
  path.

*Re-check:* `FanSafetyTests` 38/38. The two new tests were mutation-checked: with
the `_temperatureConfirmed` guard replaced by `if (_temperatureConfirmed && false) { }`
the unconfirmed test fails with `Expected: 1, Actual: 2` (the curve wrote a second
time), and passes with the guard restored. On the physical machine the fan card
was switched to Custom and back to Automatic through UI Automation: the log shows
`Applied: Fan mode` both times and the fan fell from 4529 RPM to 2516 RPM.

### 2.8 Compatibility data corrected on evidence

`NitroLite.Core/Hardware/Profiles/profiles.json`. Nine `fourZoneKeyboard` values
contradicted the cited source. The field is a report tie-breaker only — nothing on
Windows reads it — so correcting it cannot change what is written to any machine.
Full table, sources and the reasoning for every value left alone:
`ACER_COMPATIBILITY.md` §4 and §5.

### 2.9 Ruled out — do not re-investigate

Each of these was suspected by an agent and then shown unreachable or already
correct. They are recorded so the next pass does not spend time on them again.

| Suspect | Why it is not a finding |
| --- | --- |
| `PowerAutomationService` | never constructed in production |
| `GameStarted` / `GameEnded` | no production subscribers |
| `ReconcileAsync` | never called |
| named-pipe ACL | correct `PipeSecurity` with a Medium integrity label |
| single-instance lock file | per-user directory, `0700` |
| `Process.Start` injection | every argument is a constant, a GUID, or in `ArgumentList` |
| ZipSlip in the diagnostics bundle | allowlist plus `GetFullPath` containment |
| HTTPS / digest verification | certificate pinned, SHA-256 mandatory |
| fan percentage below 30 | clamped at six layers |
| lighting ranges | clamped at every boundary |

---

## 3. Running log

| When | What | Result |
| --- | --- | --- |
| baseline | `git status` inspected | 3 modified + 1 untracked file under `trailer/`, all preserved |
| baseline | branch `audit/hardening-2026-10` created | user changes carried over untouched |
| baseline | Release build | 0 warnings, 0 errors |
| baseline | test suite | 2743 passed, 0 failed, 4 skipped |
| baseline | `--hardware-report` | device confirmed as Nitro AN16-41, profile `nitro-16-an16-41` verified |
| group 1 | SEC-01, RACE-01, BUG-01, LEAK-01..06, DATA-06, SEC-04 fixed | commit `0e9c191` |
| group 1 | Release build | 0 warnings, 0 errors |
| group 1 | test suite | 2743 passed, 0 failed, 4 skipped |
| group 1 | app launched, worker connected, exited with its parent | no error in `nitrolite.log` |
| group 2 | DATA-01..05, DATA-07 fixed | commit `8ead4d0` |
| group 2 | Release build | 0 warnings, 0 errors |
| group 2 | test suite | 2745 passed, 0 failed, 4 skipped |
| group 3 | HW-01..05 fixed | uncommitted at the time of writing |
| group 3 | Release build | 0 warnings, 0 errors |
| group 3 | `FanSafetyTests` | 38 passed, 0 failed |
| group 3 | mutation check on HW-04 | guard removed → test fails `Expected: 1, Actual: 2`; guard restored → passes |
| group 3 | app launched normally, fan card → Custom → Automatic via UI Automation | `Applied: Fan mode` twice; fan 4529 RPM → 2516 RPM |
| group 3 | tray Exit path | **not exercised** — the tray icon is in the Windows overflow and could not be driven through UI Automation on this host. The hand-back write itself was exercised by the Automatic click above. |
| group 3 | `profiles.json` `fourZoneKeyboard` corrected on evidence | 9 values; see `ACER_COMPATIBILITY.md` §4 |
| group 3 | profile tests (`EveryShippedProfileIsWellFormed`, `CapabilityProbeTests`, `KeyboardCapabilityTests`) | 67 passed, 0 failed |
| group 3 | `--hardware-report` after the profile change | unchanged: `nitro-16-an16-41 (embedded)`, same sensor layout |
