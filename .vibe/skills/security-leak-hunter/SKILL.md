---
name: security-leak-hunter
description: Load this skill when auditing NitroLite (or a similar .NET desktop app) for security vulnerabilities, memory and handle leaks, race conditions, file-corruption risks, or hardware/EC safety gaps. It runs a twelve-agent read-only sweep and produces a numbered findings catalogue with file:line evidence.
---

# Security & Leak Hunter

A twelve-agent audit of a .NET desktop application that talks to hardware. Each
agent owns one risk class, reads its files end to end, and reports findings with
evidence. The output is a numbered catalogue, not a summary.

## The one rule that is not negotiable

**The first pass is read-only.** Do not edit application code while auditing.
The only file written during a sweep is this skill and the report. A finding is
worth nothing if the audit itself changed the code it was measuring, and a
half-applied fix hides the next finding.

Fixes are proposed, never applied, until the user approves the roadmap.

## What "read every file" means

Summarising a file is not reading it. For each file in scope:

- Read it from the first line to the last, in order.
- For every `using`, `try`, `lock`, `Process.Start`, `IntPtr`, `Dispose`,
  `File.WriteAllText`, `+=`, and `async void`, ask what happens on the failure
  path, not the happy path.
- A file with no findings still gets read. "Nothing here" is a conclusion you
  are allowed to reach, but only after reading it.

Scope for NitroLite: `NitroLite.Core/`, `NitroLite.Desktop/`,
`NitroLite.Windows/`, `NitroLite.Linux/`, `tools/`, and every `.csproj`.

## The five risk classes that matter most

These are the ones that turn into a CVE, a bricked laptop, or a user losing
every setting. Hunt them first, and hunt them hardest.

### 1. Privilege escalation and IPC hijacking

The app runs an elevated worker (`--wmi-worker`) and a single-instance guard.
Both are message channels into a higher-privileged process.

- Does the named pipe carry a `PipeSecurity` ACL, or does it accept any local
  user? A pipe created with default security is writable by every process on
  the machine, and the worker acts on what it reads.
- Does the worker validate that the client is the same user, or the same
  session, before executing?
- Is the single-instance lock file in a world-writable directory (`/tmp`) with
  a predictable name? A symlink planted there redirects the write.
- Does the scheduled task point at a path an unprivileged user can replace
  (binary hijacking), and is it registered without UAC?

### 2. Unmanaged handle and WMI leaks in a polling loop

A leak that costs 4 KB once is nothing. A leak that costs 4 KB every 500 ms is
28 MB an hour, and the app is designed to run for days.

- Every `ManagementObject`, `ManagementObjectSearcher`, `ManagementEventWatcher`,
  `RegistryKey`, `SafeHandle`, and raw `IntPtr` in a timer callback must be
  disposed on every path, including the exception path.
- `ManagementObjectSearcher` holds a COM object; `using` on the searcher does
  not dispose the collection it returns.
- `Marshal.AllocHGlobal` needs a matching `FreeHGlobal` in a `finally`.
- `GetRawInputData` and friends need the buffer size checked before the copy.

### 3. Command injection and ZipSlip

- `Process.Start` with a concatenated `Arguments` string is injectable. Use
  `ArgumentList`, which quotes for you.
- `UseShellExecute = true` with a user-controlled path is a shell injection.
- A zip entry named `..\..\Windows\System32\evil.dll` escapes the extraction
  root unless every entry is resolved with `Path.GetFullPath` and checked
  against the destination root before writing.
- Game `.exe` launching, `sc.exe`, `schtasks`, `powercfg`, and diagnostic
  packaging are the usual suspects.

### 4. Hardware and thermal safety floor bypass

The fan curve is the one feature that can cook a laptop.

- Can a custom curve or a manual fan percentage write a value below the safety
  floor, or `0`, or a negative number, to the EC?
- Do the `85 / 90 / 95 °C` floors hand control back to firmware `Auto` in every
  path, including when the curve evaluator throws?
- On `ProcessExit`, `SessionEnding`, and an unhandled exception, are the fans
  returned to the firmware? A crash that leaves the fans at 20 % is a hardware
  fault, not a bug.
- Is the write range-checked before it reaches the EC, or does the EC clamp it?

### 5. Non-atomic file writes and data loss

`File.WriteAllText` truncates first and writes second. A crash or a power cut
between the two leaves a zero-byte settings file, and the user loses every
preference.

- Every settings, RGB state, game profile, and battery-history write must go to
  a temporary file in the same directory, then `File.Move(..., overwrite: true)`.
- The temporary file must be flushed before the move.
- A read of a corrupt file must fall back to defaults, not throw.

## The twelve agents

Run them in parallel. Each returns findings in the catalogue format below.

| # | Agent | Owns |
|---|-------|------|
| 1 | Privilege & IPC | `--wmi-worker` pipe ACL, `SingleInstanceGuard`, scheduled task paths, UAC |
| 2 | Injection & Traversal | every `Process.Start`/`ProcessStartInfo`, `ZipFile`/`ZipArchive` extraction, URL and folder openers |
| 3 | Hardware & Thermal | WQL string building, fan/power/MUX/RGB range checks, `85/90/95 °C` floors, exit hand-back |
| 4 | Network & Deserialization | update check, HTTPS enforcement, download integrity, oversized or hostile JSON |
| 5 | Information Disclosure | `AppLog`, hardware report, diagnostic zip, log rotation race, disk-full behaviour |
| 6 | Unmanaged Leaks | `IntPtr`, `SafeHandle`, `AllocHGlobal`, `OpenProcess`, `RegistryKey`, WMI objects, `GetRawInputData` |
| 7 | Managed Leaks | `+=` without `-=`, timers never stopped, windows recreated on show, Avalonia control leaks |
| 8 | Concurrency | shared `List`/`Dictionary` across threads, nested `lock`, `Dispatcher.UIThread` marshalling, `async void` |
| 9 | Atomic Writes | every persistence path, TOCTOU between check and write, corrupt-file recovery |
| 10 | Polling Cost | work still done while minimised, `Process.GetProcesses()` in a loop, disk I/O frequency |
| 11 | Build Hardening | `.csproj` settings, `unsafe`, `DllImport`/`LibraryImport` marshalling, `SetLastError`, buffer sizes |
| 12 | Test Design | regression tests for every critical finding above |

## Finding IDs

- `SEC-nn` — a security vulnerability (privilege, injection, traversal, disclosure).
- `LEAK-nn` — a resource leak (unmanaged handle, managed event, timer, window).
- `RACE-nn` — a concurrency defect (race, deadlock, unsynchronised collection).
- `DATA-nn` — a data-integrity defect (non-atomic write, corrupt-file crash).
- `HW-nn` — a hardware or thermal safety gap.

Number them in the order found, never reuse an ID, and never renumber after
publishing: a fix commit references the ID.

## Catalogue format

Every finding carries all six fields. A finding without a line number is a
rumour, not a finding.

```
### SEC-04 [KRİTİK] — Named pipe accepts any local user
- Konum: NitroLite.Windows/WmiWorkerServer.cs:88
- Mekanizma: NamedPipeServerStream is created with PipeDirection.InOut and no
  PipeSecurity. The default DACL grants FILE_GENERIC_READ|WRITE to Everyone, so
  any process on the machine can connect and send a WMI query that the elevated
  worker executes.
- Etki: A standard user escalates to SYSTEM by asking the worker to run an
  arbitrary WQL query or a write command.
- Düzeltme: Build a PipeSecurity with a single ACE for the current user SID,
  pass it to the constructor, and reject a client whose token SID differs.
```

Severity is one of `[KRİTİK]`, `[YÜKSEK]`, `[ORTA]`, `[DÜŞÜK]`.

## Report shape

1. The skill file exists, with its path.
2. The catalogue, grouped by ID prefix, ordered by severity.
3. A remediation roadmap: packages of fixes that close findings together
   without regressions, each naming the IDs it closes and the tests that prove
   it.

Do not claim a finding is fixed. The audit ends at the roadmap.
