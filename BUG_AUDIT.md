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

_(in progress — see section 3 for the running log)_

---

## 3. Running log

| When | What | Result |
| --- | --- | --- |
| baseline | `git status` inspected | 3 modified + 1 untracked file under `trailer/`, all preserved |
| baseline | branch `audit/hardening-2026-10` created | user changes carried over untouched |
| baseline | Release build | 0 warnings, 0 errors |
| baseline | test suite | 2743 passed, 0 failed, 4 skipped |
| baseline | `--hardware-report` | device confirmed as Nitro AN16-41, profile `nitro-16-an16-41` verified |
