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
| SEC-02 | YUKSEK | the elevated error log could be redirected through a planted hardlink | `071cf45` |
| SEC-03 | ORTA | the pipe handshake echoed a nonce instead of identifying the server | `071cf45` |
| BUG-02 | ORTA | one transient WMI failure disabled the battery limit and CoolBoost for the session | `566154e` |
| LEAK-01 | ORTA | an adopted COM object was dropped without being disposed when a lookup failed | `566154e` |
| LEAK-02/03/04 | ORTA | the Nitro-key watcher leaked on a failed start, a failed stop, and every event | `566154e` |
| LEAK-05 | DUSUK | the probe task was abandoned on timeout without its exception being observed | `566154e` |
| RACE-02 | ORTA | `_suppressWrite` held across eight awaits discarded a click and reverted it | `566154e` |
| RACE-03 | ORTA | the write gate was disposed while a step could still be inside it | `566154e` |
| RACE-04 | ORTA | the log writer was dropped outside the lock that guards it | `566154e` |
| HW-06 | ORTA | a dead temperature sensor froze the fans at the last speed the curve wrote | `566154e` |
| HW-07 | ORTA | an unverified profile locked the custom fan curve even while real temperatures arrived | `7010e99` |
| HW-08 | ORTA | the four-zone colour had one transport, and on the 2025 boards that transport answers success without changing anything | `34f7922` |
| BUG-03 | ORTA | the remote profile refresh applied the user-file catch-all ban to the project's own published table, so every real download would have been rejected | `d5ed527` |

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

### 2.8a SEC-02 — the elevated error log could be redirected through a planted hardlink

**YUKSEK.** `NitroLite.Windows/Core/SystemToolsService.cs`,
`NitroLite.Windows/Core/DebloatService.cs`, `NitroLite.Windows/Core/ElevatedErrorLog.cs`.

The elevated PowerShell wrote its error message to a file in `%TEMP%`, and that path is inside the base64 command
line. A command line is readable by every process on the machine, so a same-user process could learn the path and
create an NTFS hardlink there pointing at a file it wanted overwritten — and `Out-File` would write through that
link with administrator rights. The path being unguessable did not help: it was not guessed, it was read.

*Repro, measured:* a hardlink `link.log` pointing at `target.txt` holding `ORIGINAL CONTENT`. Running the old code
path (`'PWNED BY THE ATTACKER' | Out-File -FilePath link.log`) replaced the target's contents. Running
`Write-NitroLiteError link.log 'PWNED BY THE ATTACKER'` left `target.txt` untouched, and a fresh path was written
normally.

*Fix:* every elevated script now carries `Write-NitroLiteError`, which opens with `FileMode.CreateNew`. A hardlink
means the path already exists, so the open fails and nothing is written. The `Remove-Item` the scripts already did
is kept, but it is not what makes this safe — the attacker can recreate the link after it, and only the
`CreateNew` open refuses.

*Re-check:* a new test parses every generated script with the PowerShell parser, so a snippet that reads correctly
in C# but is not valid PowerShell fails in the suite instead of on a user's machine.

### 2.8b SEC-03 — the handshake echoed a nonce instead of identifying the server

**ORTA.** `NitroLite.Windows/Core/Hardware/Wmi/WmiWorkerClient.cs`, `WmiWorkerLauncher.cs`.

The nonce echo proved the peer *received* the hello, not that it was the helper that was started. The pipe name is
on the worker's command line, so a same-user process could create the pipe first and echo the value back — and the
window would then trust that process's answers, including the temperatures the fan curve acts on.

*Fix:* `GetNamedPipeServerProcessId` asks the kernel who is serving the pipe, and the answer is compared with the
process the launcher started. The process on that end cannot forge it.

*Re-check:* two new tests. A server that is the test process is refused when a different pid is expected
(`"not the helper that was started"`), and accepted when its own pid is expected.

### 2.8c BUG-02 — one transient WMI failure disabled two features for the session

**ORTA.** `NitroLite.Windows/Core/Hardware/Wmi/AcerWmiExecutor.cs`.

`EnsureBattery` and `EnsureApge` set `_batteryLookupDone` / `_apgeLookupDone` *before* the lookup. A throw from
`scope.Connect()` therefore left the flag set, and every later call returned null immediately: the battery charge
limit and CoolBoost stayed disabled for the rest of the session because of one bad moment.

*Fix:* the flag is set only after the enumeration finishes, which is the only thing that is actually an answer. A
throw leaves it clear so the next call tries again.

*Re-check:* build and full suite. No automated test covers this path — it needs a real WMI failure, and the class
has no seam for one. Recorded as verified by inspection, not by test.

### 2.8d LEAK-01..05 — COM objects released on every path

**ORTA / DUSUK.** `AcerWmiExecutor.cs`, `WindowsNitroKeyListener.cs`, `AcerWmiProbe.cs`.

- **LEAK-01** the catch blocks in `EnsureGaming`, `EnsureBattery` and `EnsureApge` cleared the cached instance
  without disposing it. Narrow — the enumeration has to fail halfway — but the adopted COM object is still ours.
- **LEAK-02** `TryStartWatcher` created and subscribed a `ManagementEventWatcher` before `Start()` could throw,
  and dropped it. One leaked watcher per class tried.
- **LEAK-03** `Stop()` skipped `Dispose()` when `Stop()` itself threw.
- **LEAK-04** `OnWmiEvent` never disposed `e.NewEvent`. The NitroSense watcher fires every 400 ms, so this was one
  object per event for as long as the app ran.
- **LEAK-05** `AcerWmiProbe.ReadWithTimeout` abandoned the probe task on timeout without observing it, so a
  provider that failed after the timeout surfaced later as an unobserved task exception.

*Fix:* dispose before clearing in the three catches; `try`/`finally` around the watcher start and stop;
`using var ev = e.NewEvent` in the handler; a fault-only continuation that observes the abandoned task.

*Re-check:* build and full suite. The watcher paths need a real ACPI-WMI event class, so they are verified by
inspection.

### 2.8e RACE-02/03/04 — three races that lost a click, a write, or a log line

**ORTA.** `MainViewModel.cs`, `NitroLite.Core/AppLog.cs`.

- **RACE-02** `ReadStateAsync` held `_suppressWrite` across eight awaits. A click that landed during them was
  silently discarded by the setters and then reverted by the value the read had already fetched — the card moved
  back under the user's finger. *Fix:* every read happens first, and the values are applied afterwards with no
  await between them, so the suppressed window is synchronous.
- **RACE-03** `Dispose` called `_writeGate.Dispose()` after a bounded `StopAndWait`, so a step stuck in a WMI call
  could still be inside the gate and its `Release` would throw `ObjectDisposedException` out of a `finally` on a
  background thread. *Fix:* the gate is no longer disposed. A `SemaphoreSlim` only needs disposal when
  `AvailableWaitHandle` was used, and this one never is.
- **RACE-04** `AppLog.Write` called `DropWriter()` outside the lock, so a failed write could dispose the shared
  writer while another thread was inside the lock writing to it. *Fix:* taken under the same lock. The catch runs
  after the lock block has unwound, so this cannot deadlock.

*Re-check:* a new test writes 400 lines from 8 threads and asserts every one lands, reading the rotated file too
so the count does not depend on how full the log was. It guards the lock discipline rather than RACE-04 itself:
the trigger for that one is a write failure, which a test cannot produce.

### 2.8f HW-06 — a dead sensor froze the fans at the last speed

**ORTA.** `MainViewModel.cs`.

`StepFanAsync` returned without writing when the temperature was unusable. The loop is the only thing that would
ever change the fans again, so they stayed at whatever speed the curve last wrote: a machine that was cool when
the sensor died ran at 30 % while the die heated up, and the 95 °C floor never fired because there was no reading
to fire it.

*Fix:* the loop hands the fans back to the firmware once, through the same bounded gate the shutdown hand-back
uses, and reflects it in the card.

*Re-check:* a new test drives the curve to 42 % at 70 °C, then makes the sensor read 0, and asserts an `Auto` write
arrives. The first version of this fix was caught by an existing test —
`TheManualSliderWritesTheFloorAndKeepsTheStoredTarget` failed because the hand-back fired on the zero temperature
of a machine that had not read its sensors yet. That is why the `_hadTemperatureReading` guard exists: "no reading
yet" is not "the sensor died".

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

## 3. Cleanup — what was removed, and what was deliberately kept

Phase 4. Every candidate below was checked against the build, the tests, CI, the
packaging scripts and the release flow before it was touched. "Nothing calls it"
was treated as a starting point, not as proof: each removal also had to survive a
search for reflection, JSON binding, `nameof`, doc-comment `<see cref>` and
target-typed `new(...)`.

### 3.1 Removed

| Item | Evidence it was dead | Why removing it is safe |
| --- | --- | --- |
| `NitroLite.slnx` | 25-byte `<Solution></Solution>` stub, added once in `b7cf9db` and never edited | It was not a solution at all, and its only effect was to break the documented entry point: `dotnet build` at the repo root failed with `MSB1011: Specify which project or solution file to use`. After the removal the same command builds: 0 warnings, 0 errors. CI, `scripts/publish-release.ps1` and the README all name `NitroLite.sln` or a `.csproj` explicitly, so nothing depended on the stub. |
| `NitroLite.Windows/Core/DeviceCapabilityProbe.cs` | `grep -rn "DeviceCapabilityProbe"` over every `.cs`, `.axaml`, `.json` and `.md` returns the declaration only. No reflection, no `typeof`, no test. | Its one method, `ProbeAsync(AcerAgentClient)`, had no caller. See §3.3 for the gap this leaves. |
| `NitroLite.Windows/Core/UserActivity.cs` | `grep -rn "UserActivity"` / `"LastInputTick"` / `"GetLastInputInfo"` returns the file itself. | Its own doc comment says "the ATK profile retry uses this"; that retry no longer exists. Nothing else reads the last-input tick. |
| `ThermalStatusLevel`, `ThermalStabilityTrend` (`NitroLite.Core/Models.cs`) | No reference in any project or test; no serializer names them; `thermal_health.json` is only a `.gitignore` entry, nothing reads or writes it. | A model with no producer and no consumer. |
| `TelemetryFormatter.FormatThermalSlope`, `FormatStabilityScore` | Only their own declarations; no test asserts them. | They formatted the two fields of the type removed above. |
| `.audit/` (gitignore entry) | The directory holds this audit's build and test logs and is untracked. | It was showing up in `git status` on every run. Ignoring it keeps the working tree honest without deleting the evidence. |

Removal was verified the same way as every other group: `dotnet build NitroLite.sln
-c Release -warnaserror` → 0 warnings, 0 errors, and the full suite → **2753 passed,
0 failed, 4 skipped**, identical to the run before the deletion.

### 3.2 Deliberately kept

| Item | Why it stays |
| --- | --- |
| `unsupportedMethods` in `profiles.json` and `ModelProfile` | Nothing reads it, which makes it look dead — but `ProfileCatalog` validates user profiles against an allow-list of known keys. Dropping the key would make an existing `profiles.user.json` that carries it fail validation. A dead field is cheaper than rejecting a user's file. |
| `CleanExitMarker`, `ModeSwitchKey` | Referenced only by tests. They are tested behaviour that the app does not yet call, so deleting them would delete tests and a design decision rather than dead weight. Recorded in §3.3 instead. |
| `TestDisplayRefreshRateServiceStub`, `TestScreenBrightnessService` | Test doubles that live in the Windows assembly because the tests need the same `internal` surface. Moving them is a refactor, not a cleanup. |
| `scripts/research/` (24 files) | The record of how the Acer WMI surface was reverse-engineered, and the source for `ACER_COMPATIBILITY.md`. Two of the `verify_*` scripts assert "201 tests" and would fail today; they are stale documentation, not build inputs, and deleting them would lose provenance. |
| `autonomous-cleanup.ps1` | A generic agent-workflow template the author committed on purpose. It is not app code and not referenced by anything, but it is the author's process, not dead product code. |
| `tools/lighting_fixed.log` | Untracked and already covered by `*.log`. A 101-byte debug capture from 2026-11-05. Left in place: it is not in the repository, and untracked files are the user's. |
| `artifacts/` (2.0 GB, 0 tracked files) | Gitignored, and it is the documented output directory for `scripts/publish-release.ps1` and both workflows. It also holds the only local copy of `NitroLite-v1.2.7-win-x64.zip`. Reproducible, but not mine to delete. |
| `docs/video/nitrolite-tour.mp4`, `trailer/` media | 48.6 MB of tracked files in total, most of it the trailer project and the README's demo video. Both are referenced and both are the author's. |

### 3.3 Found while cleaning, not fixed

| Item | State |
| --- | --- |
| The diagnostics bundle never carries live capabilities | `WindowsDiagnosticsService` is constructed with no arguments at `AppHost.cs:319`, so its `_capabilities` falls back to `() => new DeviceCapabilities()`. `hardware-report.txt` and `capabilities.json` inside the bundle therefore describe an empty device. `DeviceCapabilityProbe` was written to fill exactly this and was never called; it has been removed rather than left as a decoy. Wiring it needs an async capability read at bundle time — `CreateBundle()` is synchronous and is called from the UI thread, so a `GetAwaiter().GetResult()` there would be a deadlock risk. Left as open work with the evidence recorded. |
| `CleanExitMarker` is not wired | `MarkRunning()` and `MarkCleanExit()` have no production caller, so `PreviousRunWasUnclean()` can never report anything. The crash-recovery feature it was written for does not exist yet. |
| Dead public methods | `GameLibraryRules.IsPathCompatible`, `ModePolicy.FanDisplayName`, `UpdateService.StartInstalled`, `AppHost.WhenReadyAsync`, `HardwareReport.WriteToDirectory`, `DebloatService.IsNitroSenseLauncherEnabled`, `SystemToolsService.FindDriverStoreFolder`, `Loc.AppendLiteral`, `DirectoryTrust.IsWorldWritable` (referenced only from `<see cref>` in its own file). Each has zero call sites, but each is public API surface and removing it is a product decision rather than a cleanup. Listed here so the next pass does not have to rediscover them. |

---

## 4. Final build, tests and packaging

Phase 5. The code was frozen at `18f4159`; the only later commits are `31cd54b`
(`scripts/publish-release.ps1`, not compiled) and `2e2b033` (documentation). The suite was
re-run on the final HEAD to confirm that.

### 4.1 Build and tests

| Step | Command | Result |
| --- | --- | --- |
| Release build | `dotnet build NitroLite.sln -c Release --nologo -warnaserror` | 0 warnings, 0 errors |
| Bare build | `dotnet build -c Release --nologo -warnaserror` | 0 warnings, 0 errors — this failed with `MSB1011` before `NitroLite.slnx` was removed |
| Full suite | `dotnet test NitroLite.Tests/NitroLite.Tests.csproj -c Release --no-build --nologo` | **2753 passed, 0 failed, 4 skipped**, 3 m 23 s |
| Full suite, re-run on the final HEAD `2e2b033` | same command | **2751 passed, 0 failed, 6 skipped**, 3 m 32 s |

The skip count moved from 4 to 6 between those two runs, and the reason matters:

| Skipped test | Why |
| --- | --- |
| `BatteryChargeLimitProbeTests.LiveProbe_AgreesWithTheInterfacesItCanSee` | needs `NITROLITE_ENABLE_LIVE_HARDWARE_TESTS=1` |
| `CpuPowerServiceTests.ServiceInitialization_InstantiatesAndQueriesCurrentMode` | same |
| `SchoolModeServiceTests.EverySchoolModeSetting_ExistsInTheActiveScheme_AndAcceptsAWrite` | same |
| `VerifiedDefectsRegressionTests.Defect5_AtkMouseService_SwitchProfile_SafeExecution` | same |
| `UpdateServiceTests.ExtractToStaging_AcceptsTheRealReleasePackage` | no release package in `artifacts/` |
| `UpdateServiceTests.FullUpdateFlow_TakesTheRealPackageIntoAnEmptyInstallFolder` | same |

The last two ran in the first suite, against the 1.2.7 package that was in `artifacts/`.
The packaging run in §4.2 deleted that package and could not build a replacement on this
host, so the two tests now skip. That is the bug fixed in §4.3, seen from the other side:
the script took the last good package with it. They are recorded as **not run**, not as
passed, and the package was deliberately not hand-assembled to make them run again —
`ReleasePackageFactAttribute` exists to test the archive the release script actually
produced, and a hand-built zip would defeat the point.

### 4.2 Packaging

`scripts/publish-release.ps1 -SkipTests` was run. It **failed**, and the failure is
environmental rather than a defect in the tree:

```
You must install or update .NET to run this application.
  App: ...\microsoft.net.illink.tasks.0.14	ools
et\illink.dll
  Framework: 'Microsoft.NETCore.App', version '10.0.14-servicing.26522.126' (x64)
  The following frameworks were found: 3.1.32, 8.0.13, 10.0.11, 10.0.12
error MSB6006: "dotnet.exe" exited with code -2147450730
error NETSDK1144: Optimizing assemblies for size failed.
```

The script pins `RuntimeFrameworkVersion=10.0.14` on purpose — the comment in it says
10.0.14 fixes CVE-2026-111497 and that the SDK's bundled 10.0.12 is affected. The
ILLink task that `PublishTrimmed=true` invokes is itself a .NET app, and it needs the
10.0.14 runtime to *run* on the build host. This machine has 10.0.11 and 10.0.12, so
the trimmer cannot start. Nothing in the tree can fix that; the host needs the
runtime, or the build has to happen on CI.

To separate "the packaging path is broken" from "this host lacks a runtime", the same
publish was run with the pin kept and only the trimmer switched off:

```
dotnet publish NitroLite.Desktop/NitroLite.Desktop.csproj -c Release -f net10.0-windows   -r win-x64 --self-contained true -p:PublishSingleFile=true   -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true   -p:PublishTrimmed=false -p:RuntimeFrameworkVersion=10.0.14 -p:DebugType=none   -o artifacts/audit-publish --nologo
```

That produced a single 50,523,264-byte `NitroLite.Desktop.exe`, and the packaged
binary answers `--hardware-report` correctly:

```
| NitroLite | v1.2.8 |
| Runtime | .NET 10.0.14 |
| Model | Nitro AN16-41 |
| Matched | nitro-16-an16-41 (embedded) |
```

So the publish, the single-file bundling, the runtime pin and the packaged binary all
work. **The trimmed package was not produced on this host** and is recorded as not
verified here.

The same command was re-run on the merged `main` (`34f7922`, 2026-11-19 23:45) to check that the
round-two changes did not break packaging. It fails at the same place and for the same reason — the
ILLink task ships as a .NET app pinned to `10.0.14`, and this host carries `10.0.11`/`10.0.12`:

```
Microsoft.NET.ILLink.targets(150,5): error MSB6006: "dotnet.exe" exited with code -2147450730
Microsoft.NET.ILLink.targets(103,5): error NETSDK1144: Optimizing assemblies for size failed.
NitroLite (self-contained single file, runtime 10.0.14) failed (exit 1)
```

Nothing in the tree changed; GitHub's Windows job does carry the matching runtime and its packaging job
is green on both merged PRs, which is the evidence that the trimmed path itself still works.

### 4.3 A packaging bug found by running it

The script deleted the previous package *before* it knew the new one could be built:

```powershell
Get-ChildItem $artifacts -Force | Where-Object { $_.Name -like 'NitroLite*' } | Remove-Item -Recurse -Force
```

The failed run above took `artifacts/NitroLite-v1.2.7-win-x64.zip` with it, so a
build that fails leaves the folder with no package at all. Fixed: the up-front delete
now clears only the staging *directories*, and the previous zip is removed just
before `Compress-Archive`, after everything that can fail has succeeded. The script
still parses (`[Parser]::ParseFile` → no errors).

---

## 5. Endurance run

Phase 5. One local script, `.audit/endurance.ps1`, one application instance, no restart.

### 5.1 Setup

| Item | Value |
| --- | --- |
| Script | `.audit/endurance.ps1` (gitignored; a local harness, not product code) |
| Log | `.audit/endurance-180.log`, one timestamped line per event |
| Binary | `NitroLite.Desktop/bin/Release/net10.0-windows/NitroLite.Desktop.exe`, built from `31cd54b` |
| Start | 2026-11-19 17:36:22 |
| End | 2026-11-19 20:36:30 |
| Target | 180 min, sample every 5 min, flow replay every 15 min |
| Hardware | read-only throughout: the replayed flows only navigate between pages, which makes the app read telemetry. No fan, mode, lighting or battery write was issued. |

The script starts the app, waits for the main window, then loops. Every 20 s it checks that the
process is still alive and that `git rev-parse HEAD` has not changed; a change means the app code
moved underneath the test, so the elapsed clock resets. Every 5 min it records working set, private
bytes, handle count, thread count and CPU. Every 15 min it replays the five main pages through UI
Automation and reads the app log for new `[ERROR]`/`[FATAL]` lines. A crash, a missing window or an
exit before the deadline is written as a failure.

### 5.2 Result

**PASS.** One instance for the whole run, no crash, no hang, no early exit, and no `[FAIL]` line. The
log carries exactly one `[WARN]`, the script's own note that it ended the run with `Stop-Process -Force`.

| Metric | First sample (t=0) | Last sample (t=176.7) | Peak | Mean |
| --- | --- | --- | --- | --- |
| Working set | 198.4 MB | 295.5 MB | 295.5 MB | — |
| Private bytes | 163.4 MB | 238.4 MB | 238.4 MB | — |
| Handles | 1207 | 1196 | 1282 | — |
| Threads | 101 | 91 | 101 | — |
| CPU | 1.17 % | 0.39 % | 1.17 % | 0.25 % |

36 samples, 11 flow replays, every replay found 5 of 5 nav buttons. The script's own closing lines:

```
2026-11-19 20:36:30 [INFO] run ended after 180.0 min of a 180 min target
2026-11-19 20:36:30 [INFO] TREND working set 198.4MB -> 295.5MB (+97.1MB); private 163.4MB -> 238.4MB (+75.0MB); handles 1207 -> 1196 (-11); threads 101 -> 91
2026-11-19 20:36:30 [INFO] PEAK working set 295.5MB; peak handles 1282; mean cpu 0.25%
2026-11-19 20:36:30 [WARN] stopping pid 21924 with Stop-Process -Force; the exit is forced, not a clean shutdown
2026-11-19 20:36:33 [INFO] RESULT: PASS - 180.0 min with one instance, no crash, no hang, no early exit
```

### 5.3 The trend, read honestly

The working set is **not** flat from zero: it climbs from 198 MB to about 273 MB over the first
30 minutes and then holds. That first climb is the app warming up — JIT, the first visit to each
page, the telemetry caches filling — and it is bounded, which is what matters. After t=30 min the
working set stays inside a 22.8 MB band (272.7–295.5 MB) for the remaining two and a half hours. The
last 12 MB of that band arrives between t=151 and t=161, right after a flow replay, and then holds:
the final three samples are 295.1, 295.5 and 295.5 MB. A leak would keep climbing through those; this
does not.

The handle count is the sharper test, because a leaked handle is what a long run exposes first. It
**falls** over the run: 1207 at t=0, a peak of 1282 in the first five minutes, then 1166–1203 for the
rest, ending at 1196 — eleven fewer than it started with. A leak would trend up monotonically; this
does not. Threads fall from 101 to 91 and stay there. CPU averages 0.25 % with a 1.17 % peak, which is
the idle cost of a tray app polling telemetry.

The run ended with `Stop-Process -Force`, so the exit was forced and is not evidence of a clean
shutdown path. That path was exercised separately in §2.8f.

---

## 6. Running log




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
| group 4 | SEC-02, SEC-03 fixed | commit `071cf45` |
| group 4 | hardlink repro measured | old `Out-File` overwrote the target; `Write-NitroLiteError` did not |
| group 4 | Release build | 0 warnings, 0 errors |
| group 4 | full suite | 2752 passed, 0 failed, 4 skipped |
| group 5 | BUG-02, LEAK-01..05, RACE-02/03/04, HW-06 fixed | commit `566154e` |
| group 5 | Release build | 0 warnings, 0 errors |
| group 5 | `FanSafetyTests` | 39 passed, 0 failed |
| group 5 | full suite | 2753 passed, 0 failed, 4 skipped |
| group 6 | dead code removed: `NitroLite.slnx`, `DeviceCapabilityProbe`, `UserActivity`, `ThermalStabilityTrend`, `ThermalStatusLevel`, `FormatThermalSlope`, `FormatStabilityScore` | see §3.1 |
| group 6 | bare `dotnet build` before the removal | `MSB1011: Specify which project or solution file to use` |
| group 6 | bare `dotnet build` after the removal | 0 warnings, 0 errors |
| group 6 | Release build | 0 warnings, 0 errors |
| group 6 | full suite | 2753 passed, 0 failed, 4 skipped |
| group 7 | `scripts/publish-release.ps1 -SkipTests` | **failed**: the ILLink task needs the 10.0.14 runtime, this host has 10.0.11/10.0.12 |
| group 7 | same publish with `PublishTrimmed=false`, pin kept | single 50,523,264-byte exe; `--hardware-report` reports `Runtime .NET 10.0.14`, `nitro-16-an16-41 (embedded)` |
| group 7 | trimmed package | **not produced on this host** — recorded as not verified, not as passed |
| group 7 | packaging bug fixed | the previous zip is no longer deleted before the new one is built |
| endurance | `.audit/endurance.ps1 -Minutes 180 -SampleMinutes 5 -FlowMinutes 15` started | 2026-11-19 17:36:22, pid 21924, HEAD `31cd54b` |
| endurance | 36 samples, 11 flow replays | every replay found 5 of 5 nav buttons; 0 `[FAIL]` |
| endurance | run ended | 2026-11-19 20:36:30, **180.0 min**, `RESULT: PASS` |
| endurance | trend | ws 198.4 -> 295.5 MB, private 163.4 -> 238.4 MB, handles 1207 -> **1196**, threads 101 -> 91, mean cpu 0.25 % |
| endurance | exit | forced with `Stop-Process -Force`; recorded as forced, not as a clean shutdown |
| final | Release build on `2e2b033` | 0 warnings, 0 errors |
| final | full suite on `2e2b033` | 2751 passed, 0 failed, 6 skipped — the 2 extra skips are the release-package tests, see §4.1 |
| final | branch pushed | `audit/hardening-2026-10` -> origin, 10 commits |
| final | draft PR opened | https://github.com/canerztk8/NitroLite/pull/4 |

| issue round | AN16-61 report triaged: 8 findings, I-01..I-05 fixed, I-06..I-08 deliberately open | see §7 |
| issue round | Release build | 0 warnings, 0 errors |
| issue round | `CpuClockFallbackTests` | 5 passed, 0 failed; the fallback reads 3801 MHz on this machine |
| issue round | filtered `CapabilityVisibilityTests` / `HalStep9Tests` / `CpuClockFallbackTests` | 67 passed, 0 failed |
| issue round | full suite | 2761 passed, 0 failed, 6 skipped |
| round two | PR #6 merged to `main` | `7010e99` — runtime verification, USB HID table, GitHub compatibility report |
| round two | Release build | 0 warnings, 0 errors |
| round two | full suite | 2834 passed, 0 failed, 6 skipped |
| round two | CI on PR #6 | Windows **pass**, Linux **pass** |
| round two | PR #7 merged to `main` | `34f7922` — `WMI -> HID` fallback, ENE KB5130 frame, downloaded profile table |
| round two | Release build | 0 warnings, 0 errors |
| round two | full suite | 2891 passed, 0 failed, 6 skipped (57 new) |
| round two | CI on PR #7 | Windows **pass**, Linux **pass** |
| round two | app started the normal way | `The published profile table replaced the installed copy (32 profiles kept)`; then `runtime-verified (2 samples)` |
| round two | remote table on disk | `%LocalAppData%\NitroLite\profiles.remote.json`, 27 896 bytes, 32 profiles |
| round two | `--hardware-report` | new `Keyboard lighting` table renders; `(not used yet)` before a write |
| round two | BUG-03 found by the live run and fixed | `d5ed527`, full suite re-run: 2891 passed |
| round two | feature branch deleted, remote pruned | `main` is the only branch, local and remote |
| round two | endurance | not repeated, at the owner's instruction; phase 5's 180.0 min stands for `31cd54b` (§8.3) |

---

## 7. Follow-up round — the AN16-61 issue report

A user on an **Acer Nitro AN16-61** (`EV9_SKF`, BIOS `INSYDE 1.53`, Windows 10.0.26300, v1.2.8) filed a
report with eight separate complaints. Their hardware report and log are quoted in
`ACER_COMPATIBILITY.md` §1.5; this section is the code side.

The unit under test is still the AN16-41, so the rule from phase 3 applies unchanged: a finding is only
called fixed when a test on this machine proves the code path, and anything that needs the reporter's
hardware stays **test pending**.

| # | Finding | Where it was | Fix | State |
| --- | --- | --- | --- | --- |
| I-01 | The dashboard shows `-- GHz`: the CPU clock is never read | `CpuFrequencyMHz` had exactly one writer, the `CPU_FREQUENCY` field of an AASsvc reply. With no Acer service it stayed 0 forever. | `HardwareMonitorService.GetNativeCpuFrequencyMhz()` reads the running clock from `CallNtPowerInformation(ProcessorInformation)`, and `EnrichMonitorData` applies it when the sample has no clock — before the power estimate reads it, so the estimate stops assuming a ratio of 1.0 | **fixed and tested** |
| I-02 | The hardware report says `GetGamingMiscSetting(0x09) MUX — not read (needs elevation)` while the session is elevated | `CapabilityProbe.Probe(identity, catalog, schema)` always passed `wmiMuxSignal: null`; the 4-argument overload existed for tests only, and nothing carried the worker's `wmiMuxSwitch` back | new `HardwareCapabilities.MuxSwitchOverWmi`, filled by `WmiHardwareController.GetCapabilitiesAsync` and preferred by `HardwareReport`. "not read (needs elevation)" is now reserved for a session that really has no capabilities at all | **fixed and tested** |
| I-03 | "MUX Switch doesn't read current mode and switching doesn't apply" left no trace in the log | `GetGpuModeAsync` returned `null` and `SetGpuModeAsync` returned `false` when the worker refused `ReadDeviceState`, both silently. The reporter's log has no MUX line at all, so the failure could not be triaged | both now log the worker's own `errorCode`/`error` text (`MUX mode could not be read`, `MUX write was not attempted`), and an undecodable answer logs the raw word | **fixed; the underlying refusal is test pending** |
| I-04 | The capability summary said no battery charge limit and no backlight timeout, while NitroSense controlled both | the summary drew a cross for every false flag. `IPlatformSnapshot.NegativeAnswersAreConclusive` is `false` on Windows and its own documentation says such an answer "is reported as unknown"; the code ignored it | `CapabilityRow` has a third state — `?` and `Bilinmiyor` — and only a conclusive platform draws a cross. Colour classes are driven by `Ok`/`No` so an unknown row is not painted as a missing feature | **fixed and tested** |
| I-05 | The battery card was hidden on a machine where Care Center holds the charge limit | `WmiHardwareController` can only reach `WMID_GUID5`; this model does not register it. The probe already reported the capability as supported through Care Center, so two parts of the app disagreed | `HybridHardwareController` asks Care Center for the mode, the write and the capability when the direct-WMI path cannot serve them | **test pending** — needs the machine |
| I-06 | Per-zone keyboard colour changes in the app only; "Write failed" | the firmware accepts `SetGamingRgbKb` (status 0) and the read-back does not match, three times, until `ReadBackPolicy` disables the capability for the session. The reporter's log shows the disable and the fallback | the WMI path is unchanged and still tried first; when it does not take the write, the documented ENE HID frame is sent instead and the working transport is remembered (§8.1, HW-08) | **code + tests, physical confirmation pending** — no payload was guessed, but no ENE controller is here either |
| I-07 | Windows-key lock does nothing | `SetWinKeyLock` is documented in the code as unverifiable; there is no firmware getter | **none** | **test pending** — device required |
| I-08 | `The exit state could not be written: Call from invalid thread` | the shutdown status notification cannot cross to the UI thread; the code already catches it and writes the marker that matters first | **none** — cosmetic, and the marker is written | n/a |

Two of the eight are honestly left open. A guessed zone payload and a guessed Win-key selector would
both be hardware writes that cannot be verified on this machine, which is the exact thing phase 3 of
this audit forbade.

### 7.1 What was measured, not assumed

| Check | Command | Result |
| --- | --- | --- |
| The clock fallback answers on real silicon | `dotnet test --filter FullyQualifiedName~CpuClockFallbackTests` | 5 passed, 0 failed; the test prints the measured clock, **3801 MHz** on this machine |
| The test really pins the fix | mutation: the fill removed from `EnrichMonitorData`, rebuilt | `EnrichMonitorData_FillsTheClock_WhenTheServiceDidNotReportIt` **fails** (1 failed, 4 passed); fill restored → 5 passed. A test that cannot fail proves nothing. |
| A service reading still wins over the fallback | same run | `EnrichMonitorData_KeepsTheServiceReading` — 3210 MHz survives |
| The report shows the live MUX selector | `--filter "CapabilityVisibilityTests\|HalStep9Tests\|CpuClockFallbackTests"` | 67 passed, 0 failed — includes the two new report rows |
| The whole suite still passes | `dotnet test -c Release --no-build` | **2761 passed, 0 failed, 6 skipped** (the 6 are the 4 live-hardware and 2 release-package skips from phase 4; a skip is not a pass) |
| Release build with warnings as errors | `dotnet build NitroLite.sln -c Release -warnaserror` | 0 warnings, 0 errors |

### 7.2 The application, started the normal way

| Step | Evidence |
| --- | --- |
| Launched `NitroLite.Desktop.exe` from the Release build at 21:59:50 | log: `The Windows hardware backend was attached: AN16-41 · direct WMI enabled`, `Acer hardware detected: model='Acer Nitro AN16-41' event classes=2`, `--wmi-worker connected: pid=34160` |
| Telemetry live, no error lines | UI Automation read the dashboard: CPU 58 °C, GPU 57 °C, FAN 2128 RPM, and the fan card CPU 2128 / GPU 2111 RPM |
| The settings page bindings compile against the new row shape | `AvaloniaUseCompiledBindingsByDefault=true` in `NitroLite.Desktop.csproj`, so `{Binding Ok}` / `{Binding No}` on `CapabilityRow` are compiled, not resolved at runtime; the build is what proves they exist |
| Closed | no `NitroLite.Desktop` process left, including the elevated worker |
| `--hardware-report` on this machine | still prints the live-selector rows as `not read (needs elevation)` — correct, because that CLI path passes no capabilities. The reporter's Markdown report is the other path, and it now reads the worker's answer |

One line in the reporter's log turned out not to be theirs: this machine logs
`The Acer WMI event watcher was refused: class='AcerGenericEvent' interval=yes: Invalid query` at
every launch too, then connects with `interval=none`. It is a constant of the event API, not a symptom
of the AN16-61's backend.

### 7.3 Not verified, and why

- **Nothing on an AN16-61.** The audit machine cannot reproduce a machine with no Acer service, a
  firmware that ignores the zone payload, or a `WMID_GUID5`-less battery limit. Everything above that
  touches those paths is marked test pending in `ACER_COMPATIBILITY.md` §6.
- **The MUX write itself.** The reporter's machine refuses `ReadDeviceState`; this change makes that
  refusal visible, it does not make the write work. One reader line from their next run decides whether
  the cause is the class, the selector, or the answer.
- **The endurance run from phase 5 was not repeated.** No timer, thread or file-handle behaviour was
  touched in this round: the changes add a P/Invoke inside an existing enrich call, a log line on a
  failure path, and a delegate call in the hybrid controller. Phase 5's 180-minute result still applies
  to the code it ran on, and to this round only insofar as nothing in the polling loop changed.

---

## 8. Round two — universal compatibility (2026-11-20)

Two branches, both merged to `main`, both green before the merge:

| PR | Merge commit | What it carries |
| --- | --- | --- |
| [#6](https://github.com/canerztk8/NitroLite/pull/6) | `7010e99` | runtime sensor verification, the USB HID collection table in the hardware report, the one-click GitHub compatibility report |
| [#7](https://github.com/canerztk8/NitroLite/pull/7) | `34f7922` | the `WMI -> HID` keyboard colour fallback, the documented ENE KB5130 frame, the downloaded profile table |

### 8.1 New findings, and what closed them

| ID | Severity | Area | Fixed in |
| --- | --- | --- | --- |
| HW-07 | ORTA | every model except the reference unit had its custom fan curve locked, because `Verified` is a static per-profile fact and nothing a session measures could change it — even while real temperatures and tachometers arrived every second | `7010e99` — `RuntimeSensorVerification`, two consecutive samples 1.5 s apart inside `15..115 °C` / `0..10000 RPM`, no sentinel, layout confirmed by the live probe; the 85/90/95 °C floors and the dead-sensor hand-back are untouched |
| HW-08 | ORTA | the four-zone colour had exactly one transport. On the 2025 boards the firmware answers `0` and the LEDs do not move, which the read-back policy correctly reads as a broken capability and disables for the session | `34f7922` — one documented HID attempt after a WMI write that did not take, cached per `Model|BIOS`; nothing is guessed for a controller without a published frame |
| BUG-03 | ORTA | the first live run of the remote profile refresh fetched the real published table and rejected it, because the user-file `anyAcer` ban was applied to the project's own document — which is the same file the binary embeds, so the feature could never have worked | `d5ed527` — the downloaded table is validated with the shipped rule, the hand-edited user file keeps the stricter one |

BUG-03 is the reason the live run exists in this audit at all. Unit tests proved the parser, the bounds
and the atomic write; only starting the application against the real URL showed that the *rule* was
wrong. The log line is quoted in `ACER_COMPATIBILITY.md` §1.7.

### 8.2 Evidence

| Check | Command / source | Result |
| --- | --- | --- |
| Release build | `dotnet build NitroLite.sln -c Release --nologo -warnaserror` | 0 warnings, 0 errors — before and after every group, and on the merged `main` |
| Full suite, PR #6 | `dotnet test NitroLite.Tests/NitroLite.Tests.csproj -c Release --no-build` | **2834 passed, 0 failed, 6 skipped** |
| Full suite, PR #7 | same | **2891 passed, 0 failed, 6 skipped**, 3 m 24 s (57 new tests: `KeyboardLightingTransportTests`, `HidLightingFilterTests`, `RemoteProfilesTests`) |
| CI, PR #6 and #7 | GitHub Actions | Windows build+tests **pass**, Linux build+package **pass** |
| The HID frame, byte for byte | `HidLightingFilterTests` | `A4 21 02 64 04 01 FF 00 00 0F 00` for one colour; four masks for four colours; no report can carry `01`, `03`, `06`, `08` or `0C` |
| The input collection is never opened | `HidLightingFilterTests` | page `0x01` refused for every usage, including `0x01/0x06`; no page below `0xFF00` may carry a colour |
| The download, live | app started the normal way, `nitrolite.log` | `The published profile table replaced the installed copy (32 profiles kept)`; file `profiles.remote.json`, 27 896 bytes; the rescan it triggers then logged `runtime-verified (2 samples)` |
| The download, rejected and offline paths | `RemoteProfilesTests` | corrupt JSON, wrong schema, empty table, impossible ceiling, out-of-enum sensor id, and a user-file catch-all all refused; a rejected download leaves the previous file byte-identical |
| Report surface | `NitroLite.Desktop.exe --hardware-report` | new `Keyboard lighting` table prints `(not used yet)` before any write, and the remembered transport after one |
| Packaging, re-run on merged `main` | `scripts/publish-release.ps1 -SkipTests` | same environmental failure as §4.2 — ILLink needs the `10.0.14` runtime, this host has `10.0.11`/`10.0.12`; GitHub's packaging job is green on both PRs |

### 8.3 Still open after this round

- **Nothing was written to an ENE controller.** The protocol is documented and confirmed on a `PHN16S-71`
  and an `ANV16S-41` by the project that reverse-engineered it; no such machine is here. Marked *physical
  confirmation pending* in `ACER_COMPATIBILITY.md` §1.7 and §6, and not claimed as fixed.
- **ITE / Sonix / Chicony** backlights are recognised and reported, never written: no frame layout for
  their Acer lighting is published, and this audit does not guess hardware payloads.
- **The AN16-61 items that need that machine** (MUX read/write, Windows-key lock, Care Center battery
  path) stay test pending. Round two did not change them; it changed what the report says about them.
- **The 180-minute endurance run was not repeated for round two, at the owner's instruction.** Phase 5's
  run stands for the code it ran on (`31cd54b`). What round two adds to the polling loop is one
  `Task.Run` per session (the profile fetch, once, bounded at 15 s and affecting no timer), a P/Invoke
  inside an existing enrichment call, and a HID probe that runs only after a WMI lighting write fails.
  That is a smaller surface than phase 5's, not a larger one — but it is an argument, not a measurement,
  and it is recorded as such.

---

## 9. Round three — universal read-back and a user-mode sensor chain (2026-11-20)

Branch `feat/universal-readback-and-sensors`, cut from `main` at `bea9209`. Nothing under `trailer/` was
touched.

The round exists because of one sentence in the AN16-61 report: *"the capability says my device does not
support battery charge limit … both functions work fine via NitroSense"*, together with *"MUX switch
option doesn't read current mode & switching to discrete GPU does not apply"* and *"Windows key lock also
does nothing"*. All three are the same defect class: **a control whose write answer is treated as the
truth**. Round two fixed the read-back for fan and colour; this round extends it to the rest and adds the
screen that tells the user when the machine did not agree.

### 9.1 Findings and what closed them

| ID | Severity | Area | Fixed by |
| --- | --- | --- | --- |
| HW-09 | ORTA | the charge-limit switch stayed where the user put it whatever the backend answered. On the AN16-61 the toggle could read "80 %" while the battery charged to 100 | `MainViewModel.ApplyBatteryLimitAsync` — after a refused or mismatched write the switch is moved to the state `GetBatteryChargeModeAsync` reports, and the mismatch is logged |
| HW-10 | ORTA | the read-back covered fan, colour, mode, MUX and battery; the **Windows-key lock** claimed success from a status byte, and a MUX refusal was a silent `false` | `ControlVerdict` + `SessionVerification` (one ledger for the session); the key lock is recorded `Unverifiable` and no longer pretends to be confirmed; both MUX refusal paths now record why |
| HW-11 | ORTA | the first read-back ran 120 ms after the write. That is a register-poke delay; a MUX word is staged for POST and Care Center's agent applies the limit after it has answered | `ReadBackPolicy.SettleDelay` — 320 ms before the *first* read, 120 ms between retries (one attempt per delay, so the total stays bounded) |
| HW-12 | DÜŞÜK | a missing WMI transport was counted as a **mismatch**, which is a claim about the firmware that was never observed | transport-missing verdict: recorded, never counted towards the three sessions, never offered as a compatibility bug |
| HW-13 | ORTA | a setting with no getter was indistinguishable from a setting that worked | `ApplyUnverifiableAsync`: the write is accepted, the control is *not* called applied, the capability is *not* disabled, and the hint offers a report |
| — | UI | there was no way to act on "the toggle does nothing" from the page the user was on | inline hint on the dashboard: `Doğrulanamayan denetimler: …` plus the existing one-click compatibility button |
| HW-14 | ORTA | CPU temperature and clock had exactly one source each. A machine whose sensor layout is unread shows `0 °C`, and AASSvc answers `0 MHz` for a field it never implemented | `TelemetryFallbackChain` (pure, tested) wired to the ACPI thermal zone through PDH (5 s cache) and to `CallNtPowerInformation` for the clock; every reading records where it came from |

### 9.2 Evidence

| Check | Command / source | Result |
| --- | --- | --- |
| Release build, whole solution | `dotnet build NitroLite.sln -c Release --nologo -warnaserror` | **0 warnings, 0 errors** |
| Full suite | `dotnet test NitroLite.Tests/NitroLite.Tests.csproj -c Release --no-build` | **2909 passed, 0 failed, 6 skipped**, 4 m 55 s (18 new tests in `UniversalReadbackAndSensorsTests`) |
| The four read-back outcomes | `UniversalReadbackAndSensorsTests` | confirmed · contradicted (control reverted, note recorded, capability disabled for the session) · unverifiable (kept, not claimed) · no transport (not counted) |
| The settle delay | same | exactly one 320 ms delay before the first read; `SettleDelay > RetryDelay` |
| The charge-limit reversion | same, on the stub machine | a write that fails while the machine still reports `Full` puts the switch back to off; a write that lands keeps it on |
| The hint | same | hidden with an empty ledger; after one mismatch it names the control (`MUX switch`) and the report button is the one that already exists |
| Fallback precedence | same | firmware `0` rejected → PDH 61.5 accepted; `6553.5`, `0`, `-1`, `NaN`, `∞` rejected; a provider that throws is skipped; nothing answering is `none`, never `0` |
| The fallback, live | `NitroLite.Desktop.exe --hardware-report` | `cpu-temp fallback | PDH thermal zone readable (56.9 °C)`; `cpu-clock fallback | CallNtPowerInformation (ntdll) · readable` |
| Provenance | `UniversalReadbackAndSensorsTests` | a sample with no firmware behind it carries `cpu-temp`, `cpu-clock`, `cpu-power=estimate` and a GPU row that never says "firmware" |
| Report surfaces | `--hardware-report` | new `| Telemetry fallbacks |` table; `| Read-back policy |` now lists `wmi.winKeyLock`; `| Unconfirmed controls this session |` appears only after an unconfirmed write |
| The application, normal launch | `NitroLite.Desktop.exe`, `nitrolite.log` | started, `runtime-verified (2 samples)`, published profile table refreshed (32 kept), telemetry ticking, no exception, no new warning |
| Trim / packaging | `PublishTrimmed` untouched, no reflection added | `PdhOpenQueryW`/`PdhGetFormattedCounterArrayW` are plain `DllImport`s; the new Core types are ordinary classes and records |

### 9.3 Deliberately not done

- **DXGI and AMD ADL are detected, not queried.** Both are COM interfaces that would have to be declared
  by hand to stay trim-safe, and a wrong vtable slot is an access violation that no `catch` can survive.
  The reading they would add (VRAM budget) is already available from NVML on the machines that have a
  dGPU. `WindowsTelemetryFallbacks.Describe()` says so in the report instead of pretending.
- **No kernel driver, and no MSR read.** The CPU wattage stays an estimate and is labelled as one.
- **No PDH `% Processor Performance` counter.** `CallNtPowerInformation` already answers the clock on
  this machine, and two sources for one number is a way to disagree with yourself.
- **The Windows-key lock is still not confirmed on any hardware.** No Acer firmware we have evidence for
  exposes a getter: `GetGamingProfile` with the lock's selector echoes the selector, and
  `GetGamingProfile(0)`'s bit 24 means "overdrive supported" (`0x1000000` on a machine with overdrive
  off). The control is now honest instead of fixed.

### 9.4 Still open after this round

- **The AN16-61 items need that machine**: MUX read/write, the Windows-key lock, the Care Center charge
  limit and the new hint's appearance after a real failed write. All four are recorded as *test pending*
  in `ACER_COMPATIBILITY.md` §6 and §7, not as fixed.
- **The ACPI thermal zone was read on the reference unit only** (AN16-41, 56.9 °C). Whether a 2025
  Nitro publishes a zone at all is a fact about that firmware, and it is unknown.
- **No 180-minute endurance run for this round.** The owner's instruction after round one was that the
  clock must not block delivery; phase 5's measurement stands for the code it ran on (`31cd54b`). What
  this round adds to the polling loop is one PDH probe every five seconds (only while the firmware has no
  CPU temperature), one `Task.Delay(320 ms)` inside a write that already blocks the write gate, and two
  property notifications per write. That is an argument, not a measurement, and it is recorded as such.
## 10. Round four — the 2-step NitroSense snapshot diff (2026-11-20)

The reports that keep arriving say the same thing in different words: *a setting works in NitroSense and does
nothing in NitroLite*. The hardware report describes what a machine has; it cannot describe what changed when the
owner moved one switch in Acer's own tool. This round adds the tool that can, and it is read-only by construction.

### 10.1 What it does

- **`HardwareSnapshotService`** (`NitroLite.Core/Hardware/Snapshot/`) runs one or more sources and merges them into
  one ordered, scrubbed `HardwareSnapshot`. A source that throws, or a selector inside one that throws, becomes an
  `unavailable: …` entry rather than a missing line: the refusal is the evidence.
- **`WmiSnapshotSource`** captures the raw `ReadDeviceState` answer the worker already produces —
  `GetGamingMiscSetting(0x02 MUX, 0x0B profile)`, `GetGamingFanBehavior`, `GetGamingFanSpeed`, the battery register
  and the `dgpuModeCapability` mask — plus `0x09`/`0x0A`, `APGeAction(0x207)`, overdrive, backlight timeout, GPU
  mode, fan behaviour and the lighting configuration. **No new WMI command** was added: a diagnostic that
  introduced a selector would be a diagnostic that could itself change what the firmware is doing.
- **`HidSnapshotSource`** walks the HID class once, opens each interface with `GenericRead` only, and reads one
  `HidD_GetFeature` report from the interfaces `HidSnapshotFilter` accepts: vendor id `0x048D` (ITE), `0x0C45`
  (Sonix) or `0x04F2` (Chicony), on a vendor-defined lighting page (`0xFF00`–`0xFF05`). Usage page `0x01` and the
  LED page `0x07` are refused by name, so the keyboard the user types through is never opened.
- **`HardwareSnapshotDiff`** prints only what moved, sorted by selector, with `~` / `+` / `-` lines, the old and new
  value, and — for a report of equal length — the byte offsets that changed (`byte 2: 0x81 -> 0x82`). The text is
  cut at 3 200 characters on a line boundary, with a note saying so.
- **Privacy**: every value goes through `SnapshotRedaction` twice (per entry, then on the finished diff):
  `SerialNumber=…`, `UUID: …` and `UserName = …` become `<redacted>`, a GUID becomes `<uuid>`, and the profile folder
  in a path goes through the log redactor's `<user>`.
- **The window** (`Settings → Diagnostics → NitroSense snapshot diff`) offers **Capture state A**, then
  **Capture state B and diff**, which stays disabled until A is in hand. Step 2 copies the hardware report with the
  diff under it, and opens the existing prefilled compatibility issue with the diff *first*, because the body is cut
  when it does not fit and the diff is the half that cannot be reproduced afterwards.

> **Addendum (1.3.3, 2026-11-23):** the `ProbeModeSelectors` sweep added after this round did put new selectors into
> **Capture state A**, against the rule above — and a user report confirmed why the rule exists: on one Acer model
> the undocumented `APGeAction` pokes hard-reset the machine mid-capture (twice, reproducibly). The sweep is out of
> the snapshot entirely now; it runs only from the confirmation-gated **Sweep all selectors** button and from
> `scripts/probe-mode-selectors.ps1`, both of which say what it does before they do it.

### 10.2 Evidence

| Check | How | Result |
| --- | --- | --- |
| Release build, whole solution | `dotnet build NitroLite.sln -c Release --nologo -warnaserror` | **0 warnings, 0 errors** |
| Full suite | `dotnet test NitroLite.Tests/NitroLite.Tests.csproj -c Release --no-build` | **2939 passed, 0 failed, 6 skipped**, 2945 total, 5 m (30 new tests) |
| Hex form and round trip | `HardwareSnapshotTests` | `10 FF 81 00`; a status word and a half group are rejected |
| The diff prints only what moved | same | 2 changed / 1 unchanged → two `~` lines, the unchanged selector absent, counts in the header |
| Byte-level detail | same | `(byte 2: 0x81 -> 0x82, byte 3: 0x00 -> 0x01)`; a status word is never described as bytes |
| Determinism | same | two captures taken in a different source order produce byte-identical text |
| Overlong diff | same | cut to ≤ 3 200 characters, ends with the truncation note, all 200 changes still counted |
| No identifier survives | same | an asset tag, a profile path and a hardware UUID are gone from the diff; `<redacted>`, `<uuid>` and `<user>` present |
| The issue is acceptable | same | `IsCompatibilityUrl` true, ≤ 7 000 characters, the `~ wmi…` line present in the body |
| A throwing source | same | its half is one `unavailable` entry; the other source's entries survive |
| A throwing selector | same | `TimeoutException` recorded as the value, the next selector unaffected |
| HID eligibility | same | 9 cases: ITE/Sonix/Chicony lighting pages accepted; `0x01/0x06`, `0x01/0x02`, `0x07`, `0x0C`, an unknown vendor and `0xFF09` refused |
| The two buttons | `SnapshotDiffViewModelTests` | B locked until A; after A it is enabled; the status is the localised sentence; B copies report + diff and opens exactly one compatibility URL |
| Nothing moved | same | the "no differences" sentence, and the report is still copied |
| A platform without the backend | same | both commands refuse to run and no status is written |
| The application, normal launch | `NitroLite.Desktop.exe`, `nitrolite.log` | started, `runtime-verified (2 samples)`, published profile table refreshed (32 kept), no exception, no new warning |
| Trim | `PublishTrimmed`/`TrimMode=partial` untouched | no reflection added; the new types are ordinary classes, records and `DllImport`s |
| Merge | `gh pr merge 9 --merge --delete-branch`, then `git fetch --prune` | PR #9 merged as `ababcba`; `main` and `origin/main` sit on the same commit, the feature branch is gone from both, and `origin/main` is the only branch the prune leaves |
| CI on `main` | run [44653190433](https://github.com/canerztk8/NitroLite/actions/runs/44653190433) for `ababcba` | **success, 8 m 12 s**: Windows build + 2 939 tests ✓, Linux build + package ✓ |

### 10.3 Deliberately not done

- **No write path and no elevation.** The WMI half is skipped when the worker is not already connected, so pressing
  a capture button cannot raise a UAC prompt; the report says `the elevated WMI worker is not running` instead.
  Starting direct WMI is the settings page's own button, where the elevation question belongs.
- **No new worker command.** The raw selectors come from the read the window already makes.
- **No HID strings.** The capture reads feature reports only. A serial number lives in
  `HidD_GetSerialNumberString`, and that call is not in this build.
- **The diff is not written to disk.** It goes to the clipboard and to the issue body, both of which the user sees
  before anything leaves the machine.

### 10.4 Still open after this round

- **A real 2-step diff has not been captured on hardware.** The suite proves the comparison, the privacy pass and
  the wiring; it cannot prove that a NitroSense toggle moves a selector this build reads. The first owner who runs
  the pair on an AN16-61 — or any unverified model — produces that evidence. *Test pending.*
- **No vendor HID feature report has been read on a real ITE/Sonix keyboard.** The filter and the read are covered
  by unit tests and the path is read-only, but whether those controllers answer report 0 with a colour payload is a
  fact about the controller. *Test pending.*
- **The AN16-61 items from §9.4 remain**: MUX read/write, the Windows-key lock, the Care Center charge limit, and
  the unconfirmed-control hint after a real failed write.

### 10.5 A vendor bridge this project will not build on

Asked whether the compatibility work could go further by reverse engineering more of Acer's stack, one candidate is
the local service NitroSense talks to. It was checked and rejected on evidence, not taste:

- **CVE-2026-50610** (Intrinsec, 2026): `AcerSysHardwareService.exe` runs as SYSTEM, its
  `systemmonitoring_hardware_service_` and `predatorsense_hardware_service_` pipes grant read/write/execute to
  `Authenticated Users`, and the server imports neither `ImpersonateNamedPipeClient` nor `AccessCheck` — any local
  user becomes SYSTEM through it.
- **NitroLite's relationship to it is one-directional.** The app connects to `AcerAgentService` as a client when
  that service exists and falls back to direct WMI when it does not (the AN16-61 log shows exactly that fallback).
  It never serves a privileged endpoint of its own, and its elevated worker keeps a per-session pipe name plus the
  nonce handshake from §2.8a and §2.8b.
- **Decision**: no compatibility work is built on that bridge, and the service is not reverse engineered further.
  The alternative — reading the firmware's own WMI map, which the provider publishes without elevation — is both
  cheaper and safer. The full argument and the order of work are in `ACER_COMPATIBILITY.md` §9.

No code changed for this note; it records a boundary that a future round would otherwise have to rediscover.
