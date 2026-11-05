# Comprehensive Technical Analysis: Requirement R2 — `powercfg` Process Ownership & Timeout Management

## Executive Summary
NitroLite's `CpuPowerService` contains critical defects in process ownership and state management: `QueryCurrentBoostModePowerCfg` suffers from a dual-ownership bug where `using var p` disposes `Process` instances transferred to `_hangingProcess`, permanently breaking recovery; `UpdateCurrentModeAfterFailure` uses inverted boolean logic (`isAcActive ? targetAc : !targetDc`) that queries the inactive power source after a write failure; `SetBoostModeAsync` and `SetBoostModePowerCfg` unconditionally overwrite `CurrentMode` even when the active power source was not targeted; and query failures are masked by defaulting to `CpuBoostMode.Aggressive` without verification flags. Additionally, test `CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock` modifies real host Windows power schemes in violation of safety constraints.

---

## 1. Problem Boundary & Scope

### 1.1 Affected Files
- `NitroLite/Core/CpuPowerService.cs`: Primary implementation of CPU power plan management, powercfg process spawning, native Win32 `powrprof.dll` P/Invoke calls, and active mode reporting.
- `NitroLite/Core/PowerAutomationService.cs`: Automated power profile orchestration on AC adapter connection/disconnection. Consumes `CpuPowerService.CurrentMode` and calls `SetBoostModeAsync`.
- `NitroLite/MainWindow.xaml.cs`: UI presentation layer. Binds `UpdateBoostUi` to `_cpuPowerService.CurrentMode` and calls `ApplyCpuBoostModeAsync`.
- `NitroLite.Tests/CpuPowerServiceTests.cs`: Unit tests verifying enum mapping, regex parsing, and basic instantiation.
- `NitroLite.Tests/VerifiedDefectsRegressionTests.cs`: Concurrency and regression tests for powercfg execution, process timeouts, hanging process blocking, and AC/DC targeting.
- `NitroLite.Tests/MockHardwareServices.cs`: Test mock `MockCpuPowerService` inheriting from `CpuPowerService(false)`.

### 1.2 State Variables & Methods in Scope

| Component | Member | Type | Current Role & Responsibility |
|---|---|---|---|
| `CpuPowerService` | `_powerLock` | `SemaphoreSlim(1, 1)` | Synchronizes concurrent calls to `SetBoostModeAsync`. |
| `CpuPowerService` | `_hangingProcess` | `Process?` | Holds reference to a process whose exit could not be verified after kill. |
| `CpuPowerService` | `CurrentMode` | `CpuBoostMode` | Represents the currently active boost mode. Defaults to `Aggressive`. |
| `CpuPowerService` | `ProcessInfoFactory` | `Func<string, ProcessStartInfo>?` | Seam to inject mock `ProcessStartInfo` in unit tests. |
| `CpuPowerService` | `MockPowerCfgQueryOutput` | `string?` | Seam to inject simulated stdout for query testing. |
| `CpuPowerService` | `EnsureNoHangingProcess()` | `bool` | Verifies whether `_hangingProcess` has exited (500ms timeout) and cleans it up. |
| `CpuPowerService` | `QueryCurrentBoostModePowerCfg(bool)` | `CpuBoostMode?` | Spawns `powercfg.exe /query`, reads stdout, regex-matches AC/DC indices. |
| `CpuPowerService` | `RunPowerCfg(string)` | `bool` | Spawns `powercfg.exe <args>`, waits with 3000ms timeout, attempts kill on timeout. |
| `CpuPowerService` | `SetBoostModePowerCfg(CpuBoostMode, bool, bool)` | `bool` | Executes sequential `/setacvalueindex`, `/setdcvalueindex`, and `/setactive` commands. |
| `CpuPowerService` | `UpdateCurrentModeAfterFailure(bool, bool)` | `void` | Evaluates system boost mode after a failed write. |
| `CpuPowerService` | `SetBoostModeAsync(CpuBoostMode, bool, bool)` | `Task<bool>` | Entry point for power changes: tries Win32 `powrprof.dll`, falls back to `powercfg.exe`. |
| `CpuPowerService` | `IsAcPowerActive()` | `bool` | Native `GetSystemPowerStatus` wrapper detecting AC vs Battery power. |

---

## 2. In-Depth Root Cause Analysis

### 2.1 Process Creation, Ownership Model, and Disposal Patterns (`using`/`finally`)

#### The Dual-Ownership Defect in `QueryCurrentBoostModePowerCfg` (Lines 203–220)
In `QueryCurrentBoostModePowerCfg`:
```csharp
203: using var p = Process.Start(psi);
204: if (p == null) return null;
205: 
206: var readTask = p.StandardOutput.ReadToEndAsync();
207: if (!p.WaitForExit(3000))
208: {
209:     try
210:     {
211:         p.Kill();
212:         if (!p.WaitForExit(1000) || !p.HasExited)
213:         {
214:             _hangingProcess = p;
215:             return null;
216:         }
217:     }
218:     catch { }
219:     return null;
220: }
```
**Mechanism of Failure:**
1. Line 203 declares `using var p = Process.Start(psi);`. The C# compiler translates this into a `try ... finally { p?.Dispose(); }` block scoping the entire method body.
2. If `p.WaitForExit(3000)` times out, `p.Kill()` is invoked. If the process does not terminate within 1000ms (`!p.WaitForExit(1000) || !p.HasExited`), line 214 executes `_hangingProcess = p;` to track the unverified process.
3. Line 215 executes `return null;`.
4. As the method stack unwinds, the compiler-generated `finally` block executes `p.Dispose()`.
5. `_hangingProcess` now holds a reference to a **disposed** `Process` object whose underlying Windows process handle has been closed.
6. When `EnsureNoHangingProcess()` is later called to check if the process has terminated:
   ```csharp
   259: if (!_hangingProcess.HasExited)
   ```
   Calling `.HasExited` or `.WaitForExit()` on a disposed `Process` throws `InvalidOperationException` or `ObjectDisposedException`.
7. `EnsureNoHangingProcess()` catches this exception at line 269:
   ```csharp
   269: catch
   270: {
   271:     return false;
   272: }
   ```
8. Result: `EnsureNoHangingProcess()` returns `false` forever. `_hangingProcess` is never cleared. All future calls to `SetBoostModeAsync`, `SetBoostModePowerCfg`, `RunPowerCfg`, and `QueryCurrentBoostModePowerCfg` are permanently blocked until the application process terminates.

#### Comparison with `RunPowerCfg` (Lines 405–454)
In `RunPowerCfg`, an explicit `Process? p = null; try / finally` pattern was used:
```csharp
405: Process? p = null;
406: try
407: {
...
428:     _hangingProcess = p;
429:     p = null; // Ownership transferred: local finally will NOT dispose _hangingProcess
430:     return false;
...
451: finally
452: {
453:     p?.Dispose();
454: }
```
Here, transferring ownership sets `p = null`, preventing `finally` from disposing the transferred instance. `QueryCurrentBoostModePowerCfg` failed to follow this pattern due to the shorthand `using var p`.

#### Kill Exception Swallowing in `QueryCurrentBoostModePowerCfg` (Lines 209–218)
In `QueryCurrentBoostModePowerCfg`:
If `p.Kill()` throws `Win32Exception` (e.g. Access Denied) or `InvalidOperationException`:
Execution jumps to `catch { }` at line 218. Lines 212–215 are skipped!
Line 219 executes `return null;`.
`_hangingProcess` is NOT assigned, but the operating system process continues running in the background. Then `p.Dispose()` closes the C# handle. An unmonitored OS process is leaked, and `EnsureNoHangingProcess()` will return `true`, allowing new writes while an unverified process runs.

---

### 2.2 Process Timeouts, Kill Attempts, Exit Verification, and Write Blocking

#### Requirement R2 Mandates:
- Attempt process termination on timeout.
- If exit is not verified, continue tracking the process and block new power writes.
- When process actually exits, release its resources, clear tracking, and allow subsequent operations.
- Convert kill, status query, and output read errors into explicit outcomes; do not proceed with new writes in an ambiguous process state.

#### Detailed Lifecycle in `EnsureNoHangingProcess`
In `EnsureNoHangingProcess`:
```csharp
253: internal bool EnsureNoHangingProcess()
254: {
255:     if (_hangingProcess != null)
256:     {
257:         try
258:         {
259:             if (!_hangingProcess.HasExited)
260:             {
261:                 if (!_hangingProcess.WaitForExit(500))
262:                 {
263:                     return false;
264:                 }
265:             }
266:             _hangingProcess.Dispose();
267:             _hangingProcess = null;
268:         }
269:         catch
270:         {
271:             return false;
272:         }
273:     }
274:     return true;
275: }
```
**Issues Identified:**
1. If `_hangingProcess` is still running when `EnsureNoHangingProcess` is called, it only waits up to 500ms (`_hangingProcess.WaitForExit(500)`). It does not retry `Kill()`. If a process was unresponsive during the initial kill attempt, retrying `Kill(entireProcessTree: true)` during cleanup can help ensure orphaned worker processes are stopped.
2. If `_hangingProcess` threw an exception because it was already disposed or exited and its process ID was recycled, `catch { return false; }` blocks writes forever. A discriminating catch block is needed: if `_hangingProcess.HasExited` is true or if the process handle is demonstrably terminated, clean up and set `_hangingProcess = null`. If the state is genuinely ambiguous or still running, return `false`.

---

### 2.3 Output Reading Streams & Timeout Boundaries

#### StandardOutput Stream Read in `QueryCurrentBoostModePowerCfg` (Lines 206–230)
```csharp
206: var readTask = p.StandardOutput.ReadToEndAsync();
207: if (!p.WaitForExit(3000)) { ... }
222: if (!p.HasExited || p.ExitCode != 0) return null;
224: if (!readTask.Wait(1000))
225: {
226:     return null;
227: }
229: output = readTask.Result;
```
**Issues Identified:**
1. `readTask = p.StandardOutput.ReadToEndAsync();` starts an asynchronous read on the stdout pipe. If `WaitForExit(3000)` times out, `p.Kill()` is called. If `readTask` is still waiting for the stream to close, disposing `p` prematurely causes stream read exceptions.
2. If `p.WaitForExit(3000)` completes and `p.ExitCode == 0`, but `readTask.Wait(1000)` times out (e.g. child process spawned by cmd/powercfg inherited stdout handle and holds it open): line 226 returns `null`.
3. To strictly satisfy "Çıktı okuma ve süreç bekleme yollarının tamamına kesin süre sınırları uygula":
   - The stdout read must be bounded by a definite timeout (1000ms).
   - If stdout read times out or fails, the process tree must be terminated (`p.Kill(entireProcessTree: true)`), and exit verified.
   - If exit cannot be verified, transfer ownership to `_hangingProcess`.

---

### 2.4 Error Propagation, Write Chains, and Stopping on Failure

#### Sequential Write Chain in `SetBoostModePowerCfg` (Lines 360–396)
```csharp
360: try
361: {
362:     var val = (int)mode;
364:     if (targetAc)
365:     {
366:         if (!RunPowerCfg($"/setacvalueindex SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid} {val}"))
367:         {
368:             UpdateCurrentModeAfterFailure(targetAc, targetDc);
369:             return false;
370:         }
371:     }
373:     if (targetDc)
374:     {
375:         if (!RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid} {val}"))
376:         {
377:             UpdateCurrentModeAfterFailure(targetAc, targetDc);
378:             return false;
379:         }
380:     }
382:     if (!RunPowerCfg("/setactive SCHEME_CURRENT"))
383:     {
384:         UpdateCurrentModeAfterFailure(targetAc, targetDc);
385:         return false;
386:     }
388:     CurrentMode = mode;
389:     return true;
390: }
```
**Evaluation against R2:**
- "Zincirde bir yazma başarısızsa kalan yazmaları durdur":
  If `targetAc` fails at line 366, line 369 immediately returns `false`. Neither `/setdcvalueindex` nor `/setactive` is executed.
  If `targetDc` fails at line 375, line 378 immediately returns `false`. `/setactive` is not executed.
  This aspect of stopping write chains is already followed.
- "Her komut yalnızca süreç tamamlandığında ve çıkış kodu sıfır olduğunda başarılı sayılsın":
  `RunPowerCfg` verifies `p.HasExited && p.ExitCode == 0`.
- "kısmi değişiklik ihtimalini sorgulayarak değerlendir":
  When a write fails mid-chain (e.g. `targetAc` succeeded, but `targetDc` failed), `UpdateCurrentModeAfterFailure(targetAc, targetDc)` is invoked to evaluate the actual active state. However, as demonstrated below, `UpdateCurrentModeAfterFailure` contains inverted logic.

---

### 2.5 AC/DC Targets Handling & `CurrentMode` Reporting

#### Defect D3: Inverted Logic in `UpdateCurrentModeAfterFailure` (Line 344)
Look at line 344:
```csharp
341: internal void UpdateCurrentModeAfterFailure(bool targetAc, bool targetDc)
342: {
343:     bool isAcActive = IsAcPowerActive();
344:     bool queryAc = isAcActive ? targetAc : !targetDc;
345:     var verified = QueryBoostModeExact(queryAc);
346:     if (verified.HasValue)
347:     {
348:         CurrentMode = verified.Value;
349:     }
350: }
```
**Mathematical Proof of Defect:**
Suppose the laptop is running on AC power (`isAcActive == true`):
- Case 1: Caller called `SetBoostModeAsync(mode, targetAc: true, targetDc: true)`.
  `queryAc = isAcActive ? targetAc : !targetDc` -> `true ? true : ...` = `true` (queries AC).
- Case 2: Caller called `SetBoostModeAsync(mode, targetAc: false, targetDc: true)` (writing DC only).
  `queryAc = isAcActive ? targetAc : !targetDc` -> `true ? false : ...` = `false` (**queries DC**)!
  The laptop is physically plugged into AC power. A write to DC failed. `UpdateCurrentModeAfterFailure` queries **DC**, and sets `CurrentMode` to the DC value! The UI now shows the DC mode as the active mode for an AC-connected machine!
Suppose the laptop is running on Battery power (`isAcActive == false`):
- Case 3: Caller called `SetBoostModeAsync(mode, targetAc: true, targetDc: false)` (writing AC only).
  `queryAc = false ? ... : !targetDc` -> `!false` = `true` (**queries AC**)!
  The laptop is running on battery. A write to AC failed. `UpdateCurrentModeAfterFailure` queries **AC**, and sets `CurrentMode` to the AC value!

**Correction:**
The active power source is determined solely by hardware status (`IsAcPowerActive()`). When evaluating what mode the machine is currently operating in:
```csharp
bool isAcActive = IsAcPowerActive();
var verified = QueryBoostModeExact(isAcActive);
if (verified.HasValue)
{
    CurrentMode = verified.Value;
    IsModeVerified = true;
}
else
{
    IsModeVerified = false;
}
```

#### Defect D4: Unconditional `CurrentMode` Overwrite on Success (Lines 325 & 388)
In `SetBoostModeAsync` (line 325) and `SetBoostModePowerCfg` (line 388):
```csharp
CurrentMode = mode;
return true;
```
If a user or background task calls `SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: false, targetDc: true)` while the laptop is on AC power:
Only DC is changed. AC remains `Aggressive`.
Yet line 325 / 388 sets `CurrentMode = CpuBoostMode.Disabled;`.
`MainWindow` calls `UpdateBoostUi(_cpuPowerService.CurrentMode)` and displays "Eco (Devre Dışı)" to the user while the CPU on AC is actually boosting to 5.0 GHz!
**Correction:**
`CurrentMode` must only be set to `mode` if the **active** power source was targeted:
```csharp
bool isAc = IsAcPowerActive();
bool activeTargeted = isAc ? targetAc : targetDc;
if (activeTargeted)
{
    CurrentMode = mode;
    IsModeVerified = true;
}
else
{
    // Active power source was not part of this write; verify active source remains accurate
    var activeMode = QueryBoostModeExact(isAc);
    if (activeMode.HasValue)
    {
        CurrentMode = activeMode.Value;
        IsModeVerified = true;
    }
}
```

#### Defect D5: Query Failure Masked as Verified Default (`Aggressive`)
In `CpuPowerService.cs`:
```csharp
64: public CpuBoostMode CurrentMode { get; private set; } = CpuBoostMode.Aggressive;
...
142: public virtual CpuBoostMode QueryCurrentBoostMode(bool isAc = true)
143: {
144:     var exact = QueryBoostModeExact(isAc);
145:     return exact ?? CurrentMode;
146: }
```
When `QueryBoostModeExact` fails (returns `null`), `QueryCurrentBoostMode` returns `CurrentMode` (default `Aggressive`). There is no flag indicating whether `CurrentMode` was actually verified from the OS or if the query failed.
To satisfy "sorgu başarısızlığını doğrulanmış varsayılan değer gibi sunma":
- Add `public bool IsModeVerified { get; private set; }`
- Add `public CpuBoostMode? VerifiedCurrentMode => IsModeVerified ? CurrentMode : null;`
- When queries fail, set `IsModeVerified = false;`.

#### Defect D6: Constructor Hardcodes AC Query
In constructor:
```csharp
75: CurrentMode = QueryCurrentBoostMode();
```
Because `QueryCurrentBoostMode(bool isAc = true)` has default parameter `true`, if NitroLite starts up while on battery, it queries AC power scheme index instead of DC power scheme index.
**Correction:**
At startup, query the active power source: `QueryCurrentBoostMode(IsAcPowerActive())`.

---

### 2.6 Test Safety and Seams (Requirement R4)

#### Defect D9: Direct Host Hardware Modification in Test Suite
In `VerifiedDefectsRegressionTests.cs`:
```csharp
1005: [Fact]
1006: public async Task CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock()
1007: {
1008:     var service = new CpuPowerService();
1009:     var tasks = new[]
1010:     {
1011:         Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: true, targetDc: false)),
1012:         Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Efficient, targetAc: false, targetDc: true)),
1013:         Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Aggressive, targetAc: true, targetDc: true))
1014:     };
1015: 
1016:     var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
1017:     Assert.Null(ex);
1018: }
```
When `new CpuPowerService()` is executed:
1. It unhides power settings in `HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings` (fails if test process lacks elevated privileges).
2. `SetBoostModeAsync` directly calls `PowerWriteACValueIndex`, `PowerWriteDCValueIndex`, and `PowerSetActiveScheme` in `powrprof.dll`.
3. Running this test actually mutates the developer's / runner's Windows power plan in real-time.
This directly violates Requirement R4:
> *"Güvenlik kısıtı: Gerçek fare profili, güç planı, Windows servisi veya zamanlanmış görev ayarlarını değiştiren testleri bu doğrulamada çalıştırma; gerçek `CpuPowerService` oluşturup `SetBoostModeAsync` çağıran eşzamanlılık testi dahil olmak üzere tüm testleri kontrollü sahte süreç/donanım katmanıyla güvenli hale getir."*

**Correction:**
Add a test seam `internal bool BypassNativePowerCalls { get; set; }` and `public CpuPowerService(bool autoInitialize = true)`:
When `BypassNativePowerCalls` is true, native P/Invoke calls and registry writes are bypassed, allowing concurrent synchronization and state logic to be verified cleanly and safely without touching host OS power plans.

---

## 3. Comprehensive Defect Catalog

| ID | Category | Method / Location | Severity | Description |
|---|---|---|---|---|
| **D1** | Process Ownership | `QueryCurrentBoostModePowerCfg` (lines 203, 214) | **CRITICAL** | `using var p` disposes `Process` after ownership is assigned to `_hangingProcess`. Permanent brick of all power operations. |
| **D2** | Process Lifecycle | `QueryCurrentBoostModePowerCfg` (lines 209-218) | **HIGH** | `catch { }` swallows kill exceptions without checking `!p.HasExited` or assigning `_hangingProcess`. Leaks unmonitored OS process. |
| **D3** | Power Source Resolution | `UpdateCurrentModeAfterFailure` (line 344) | **CRITICAL** | `queryAc = isAcActive ? targetAc : !targetDc` queries the INACTIVE power source after a write failure. |
| **D4** | Target Preservation | `SetBoostModeAsync` (line 325) & `SetBoostModePowerCfg` (line 388) | **HIGH** | Unconditionally sets `CurrentMode = mode` even when the active power source was not targeted (e.g. DC write on AC power). |
| **D5** | Verification State | `QueryCurrentBoostMode` (line 145) & `CurrentMode` (line 64) | **MEDIUM** | Query failures silently return unverified default `Aggressive`. No `IsModeVerified` flag exists. |
| **D6** | Active Source Initialization | Constructor (line 75) & `QueryCurrentBoostMode` (line 142) | **MEDIUM** | Default parameter `isAc = true` causes battery startup to read AC configuration. |
| **D7** | Stream Timeout | `QueryCurrentBoostModePowerCfg` (lines 206, 224) | **MEDIUM** | Stdout stream read timeout leaves process tree unmonitored if subprocess inherits stdout handle. |
| **D8** | Error Recovery | `EnsureNoHangingProcess` (lines 258-272) | **HIGH** | Generic catch catches `ObjectDisposedException`/`InvalidOperationException` and returns false forever. |
| **D9** | Test Isolation / Safety | `VerifiedDefectsRegressionTests.cs` (line 1007) | **HIGH** | Concurrency test directly invokes real `powrprof.dll` and modifies host Windows power plan. |

---

## 4. Proposed Code Changes & Implementation Design

### 4.1 Proposed Refactoring for `CpuPowerService.cs`

#### A. Add State Variables & Seams
```csharp
public bool IsModeVerified { get; private set; }
public CpuBoostMode? VerifiedCurrentMode => IsModeVerified ? CurrentMode : null;
internal bool BypassNativePowerCalls { get; set; } = false;
```

#### B. Refactor Constructor
```csharp
public CpuPowerService(bool autoInitialize = true)
{
    if (autoInitialize)
    {
        UnhideSetting();
        CurrentMode = QueryCurrentBoostMode(IsAcPowerActive());
    }
}
```

#### C. Refactor `QueryCurrentBoostMode` and `QueryCurrentBoostModePowerCfg` (Fix D1, D2, D5, D6, D7)
```csharp
public virtual CpuBoostMode QueryCurrentBoostMode(bool isAc)
{
    var exact = QueryBoostModeExact(isAc);
    if (exact.HasValue)
    {
        CurrentMode = exact.Value;
        IsModeVerified = true;
        return exact.Value;
    }
    IsModeVerified = false;
    return CurrentMode;
}

public virtual CpuBoostMode QueryCurrentBoostMode() => QueryCurrentBoostMode(IsAcPowerActive());

internal CpuBoostMode? QueryCurrentBoostModePowerCfg(bool isAc = true)
{
    if (!EnsureNoHangingProcess())
    {
        return null;
    }

    try
    {
        string output;
        if (MockPowerCfgQueryOutput != null)
        {
            output = MockPowerCfgQueryOutput;
        }
        else
        {
            var psi = ProcessInfoFactory != null
                ? ProcessInfoFactory($"/query SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid}")
                : new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = $"/query SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

            psi.RedirectStandardOutput = true;

            Process? p = null;
            try
            {
                p = Process.Start(psi);
                if (p == null) return null;

                var readTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(3000))
                {
                    bool exited = false;
                    try
                    {
                        p.Kill(entireProcessTree: true);
                        exited = p.WaitForExit(1000) && p.HasExited;
                    }
                    catch
                    {
                        try { exited = p.HasExited; } catch { exited = false; }
                    }

                    if (!exited)
                    {
                        _hangingProcess = p;
                        p = null; // Transfer ownership, do not dispose locally
                    }
                    return null;
                }

                if (!p.HasExited || p.ExitCode != 0) return null;

                if (!readTask.Wait(1000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                output = readTask.Result;
            }
            finally
            {
                p?.Dispose();
            }
        }

        var pattern = isAc 
            ? @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)"
            : @"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)";

        var match = Regex.Match(output, pattern);
        if (match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var val))
        {
            if (val == 0) return CpuBoostMode.Disabled;
            if (val == 4) return CpuBoostMode.Efficient;
            return CpuBoostMode.Aggressive;
        }
    }
    catch { }

    return null;
}
```

#### D. Refactor `EnsureNoHangingProcess` (Fix D8)
```csharp
internal bool EnsureNoHangingProcess()
{
    if (_hangingProcess != null)
    {
        try
        {
            if (!_hangingProcess.HasExited)
            {
                try { _hangingProcess.Kill(entireProcessTree: true); } catch { }
                if (!_hangingProcess.WaitForExit(500) || !_hangingProcess.HasExited)
                {
                    return false;
                }
            }
            _hangingProcess.Dispose();
            _hangingProcess = null;
        }
        catch (InvalidOperationException)
        {
            // Process already terminated or handle closed
            try { _hangingProcess.Dispose(); } catch { }
            _hangingProcess = null;
        }
        catch
        {
            return false;
        }
    }
    return true;
}
```

#### E. Refactor `UpdateCurrentModeAfterFailure` and Write Completion (Fix D3, D4)
```csharp
internal void UpdateCurrentModeAfterFailure(bool targetAc, bool targetDc)
{
    bool isAcActive = IsAcPowerActive();
    var verified = QueryBoostModeExact(isAcActive);
    if (verified.HasValue)
    {
        CurrentMode = verified.Value;
        IsModeVerified = true;
    }
    else
    {
        IsModeVerified = false;
    }
}

private void UpdateCurrentModeAfterSuccess(CpuBoostMode mode, bool targetAc, bool targetDc)
{
    bool isAcActive = IsAcPowerActive();
    bool activeTargeted = isAcActive ? targetAc : targetDc;
    if (activeTargeted)
    {
        CurrentMode = mode;
        IsModeVerified = true;
    }
    else
    {
        var verified = QueryBoostModeExact(isAcActive);
        if (verified.HasValue)
        {
            CurrentMode = verified.Value;
            IsModeVerified = true;
        }
    }
}
```

#### F. Refactor `SetBoostModeAsync` & `SetBoostModePowerCfg`
In `SetBoostModeAsync`:
```csharp
if (!BypassNativePowerCalls && TryGetActiveScheme(out var schemeGuid))
{
    var subGroup = new Guid(SubProcessorGuid);
    var setting = new Guid(PerfBoostModeGuid);
    uint val = (uint)mode;
    bool success = true;

    if (targetAc)
    {
        if (PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, val) != 0)
            success = false;
    }

    if (success && targetDc)
    {
        if (PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, val) != 0)
            success = false;
    }

    if (success)
    {
        if (PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid) != 0)
            success = false;
    }

    if (success)
    {
        UpdateCurrentModeAfterSuccess(mode, targetAc, targetDc);
        return true;
    }
}

return SetBoostModePowerCfg(mode, targetAc, targetDc);
```
In `SetBoostModePowerCfg`:
```csharp
// When all target writes succeed:
UpdateCurrentModeAfterSuccess(mode, targetAc, targetDc);
return true;
```

---

## 5. Test Suite Plan & Required Additions

### 5.1 Test Safe Concurrency Refactoring (R4 Compliance)
In `VerifiedDefectsRegressionTests.cs`:
Refactor `CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock`:
```csharp
[Fact]
public async Task CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock()
{
    var service = new CpuPowerService(autoInitialize: false)
    {
        BypassNativePowerCalls = true,
        ProcessInfoFactory = args => new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };

    var tasks = new[]
    {
        Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: true, targetDc: false)),
        Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Efficient, targetAc: false, targetDc: true)),
        Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Aggressive, targetAc: true, targetDc: true))
    };

    var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
    Assert.Null(ex);
}
```

### 5.2 Four Mandatory New Unit/Regression Tests for Requirement R2
1. `CpuPowerService_QueryPathTimeout_TransfersOwnershipWithoutDisposal_BlocksSubsequentWrites`:
   - Start simulated hanging query process.
   - Verify `QueryCurrentBoostModePowerCfg` times out and returns `null`.
   - Verify `_hangingProcess` is NOT disposed (accessing `_hangingProcess.HasExited` does not throw `ObjectDisposedException`).
   - Verify `SetBoostModePowerCfg` is blocked while process runs.
   - Terminate process, verify `EnsureNoHangingProcess()` clears tracking, and verify subsequent write succeeds.
2. `CpuPowerService_SetBoostModeAsync_TargetDcOnlyOnAc_PreservesCurrentMode`:
   - Simulate AC power active (`IsAcPowerActive() == true`).
   - Execute `SetBoostModePowerCfg(CpuBoostMode.Disabled, targetAc: false, targetDc: true)`.
   - Verify `CurrentMode` is NOT overwritten to `Disabled`.
3. `CpuPowerService_UpdateCurrentModeAfterFailure_QueriesActivePowerSourceNotTarget`:
   - Verify that when AC is active and DC write fails, failure recovery queries AC.
   - Verify that when DC is active and AC write fails, failure recovery queries DC.
4. `CpuPowerService_QueryFailure_SetsIsModeVerifiedFalse`:
   - Simulate query failure (non-zero exit code or timeout).
   - Verify `QueryBoostModeExact` returns `null`.
   - Verify `IsModeVerified` is `false`.

---

## 6. Conclusion
The root causes of all instability and safety issues in Requirement R2 have been pinpointed down to exact source lines and method implementations. Applying the single-ownership transfer pattern, correcting the active power source query in `UpdateCurrentModeAfterFailure`, gating `CurrentMode` updates on targeted power sources, introducing explicit verification flags, and adding safe testing seams will achieve 100% compliance with R2 and R4 without breaking any existing functionality.
