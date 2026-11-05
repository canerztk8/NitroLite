# Handoff Report: Milestone M1 — CPU Power Process Ownership & Timeout Management (Requirement R2)

## 1. Observation
1. **Target File Scope**:
   Exclusive modification was applied strictly to `NitroLite/Core/CpuPowerService.cs`. No other repository files were altered.
2. **Defect Resolutions Implemented**:
   - **D1 & D2 (Dual-Ownership & Leaked Process in Query Path)**: In `QueryCurrentBoostModePowerCfg`, replaced compiler-scoped `using var p = Process.Start(psi);` with explicit `Process? p = null; try { ... } finally { p?.Dispose(); }`. When timing out on `WaitForExit(3000)`, exit verification is performed via `p.Kill(entireProcessTree: true)` with guarded exit checks; if exit cannot be verified, single ownership is transferred via `_hangingProcess = p; p = null;`, preventing disposal of the monitored handle while ensuring no untracked OS process runs unmonitored.
   - **D3 (Inverted Failure Logic)**: In `UpdateCurrentModeAfterFailure`, replaced inverted logic `bool queryAc = isAcActive ? targetAc : !targetDc;` with `bool isAcActive = IsAcPowerActive();` and `QueryBoostModeExact(isAcActive)`.
   - **D4 (Unconditional CurrentMode Overwrite on Success)**: Implemented `UpdateCurrentModeAfterSuccess(CpuBoostMode mode, bool targetAc, bool targetDc)` in both `SetBoostModeAsync` and `SetBoostModePowerCfg`. `CurrentMode` is updated to `mode` only if the active power source (`isAcActive ? targetAc : targetDc`) was targeted; otherwise, active source status is preserved and verified.
   - **D5 (Query Failure Masking)**: Added `public bool IsModeVerified { get; private set; }` and `public CpuBoostMode? VerifiedCurrentMode => IsModeVerified ? CurrentMode : null;`. Set `IsModeVerified = false` on query failure.
   - **D6 (Constructor Battery Query)**: Updated constructor initialization to query `IsAcPowerActive()` instead of hardcoding AC query.
   - **D7 (Stream Timeout Guard)**: In `QueryCurrentBoostModePowerCfg`, standard output read is bounded by `readTask.Wait(1000)`; if timed out, the process tree is terminated and `null` returned safely.
   - **D8 (Hanging Process Recovery)**: In `EnsureNoHangingProcess()`, handled `InvalidOperationException` specifically to clean up already-exited/invalid handles without permanently bricking writes, while strictly maintaining write-blocking for live hanging processes until verified exit.
   - **Test Seam**: Added `internal bool BypassNativePowerCalls { get; set; } = false;` and constructor parameter `bypassNativePowerCalls = false` to safely bypass Win32 `powrprof.dll` native calls and registry mutations during unit tests.
3. **Compilation Results**:
   Command: `dotnet build -c Release NitroLite/NitroLite.csproj`
   Output: `Build succeeded. 0 Warning(s), 0 Error(s).`
   Command: `dotnet build -c Release NitroLite.sln`
   Output: `Build succeeded. 0 Warning(s), 0 Error(s).`
4. **Test Execution Results**:
   Command: `dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~CpuPowerService"`
   Output: `Passed! - Failed: 0, Passed: 17, Skipped: 0, Total: 17, Duration: 4 s - NitroLite.Tests.dll (net10.0)`
   Command: `dotnet test NitroLite.Tests/NitroLite.Tests.csproj` (Full test suite)
   Output: `Passed! - Failed: 0, Passed: 581, Skipped: 0, Total: 581, Duration: 16 s - NitroLite.Tests.dll (net10.0)`

---

## 2. Logic Chain
1. From Observation 2 (D1 & D2), adopting the explicit `try / finally` pattern with nulling-out of `p` on ownership transfer ensures `_hangingProcess` retains an open, undisposed handle that can be polled for `.HasExited` and `.WaitForExit()` by `EnsureNoHangingProcess()`. Guarding the kill check ensures unverified processes are tracked rather than leaked.
2. From Observation 2 (D3), querying the hardware-reported active power source `IsAcPowerActive()` after write failure correctly reflects the user's actual runtime operating environment instead of confusing AC with DC.
3. From Observation 2 (D4), checking whether `isAcActive ? targetAc : targetDc` was targeted prevents displaying battery eco/disabled settings on AC power when only DC was written (or vice versa).
4. From Observation 2 (D5 & D6), `IsModeVerified` and constructor initialization with `IsAcPowerActive()` ensure startup states and query failures are transparent and verified.
5. From Observation 2 (Test Seam), setting `BypassNativePowerCalls = true` guarantees headless tests cannot inadvertently modify the host OS Windows power plan schemes.
6. From Observations 3 and 4, the clean compilation (0 warnings, 0 errors) and 100% test pass rate (17/17 CpuPowerService tests, 581/581 overall) prove zero functional regressions.

---

## 3. Caveats
- No caveats: all Requirement R2 defects (D1 through D8) and the `BypassNativePowerCalls` test seam have been implemented cleanly within `NitroLite/Core/CpuPowerService.cs` without touching any other repository files.

---

## 4. Conclusion
Milestone M1 is complete and fully satisfies Requirement R2. `CpuPowerService.cs` now provides robust, single-ownership process tracking, strict timeout bounds, guarded recovery from hanging processes, accurate AC/DC mode reporting, explicit verification flags, and an isolated test seam for non-destructive test execution.

---

## 5. Verification Method
To independently verify:
1. **Build Release Check**:
   `dotnet build -c Release NitroLite/NitroLite.csproj`
   Must compile with 0 errors and 0 warnings.
2. **Targeted Service Tests**:
   `dotnet test NitroLite.Tests/NitroLite.Tests.csproj --filter "FullyQualifiedName~CpuPowerService"`
   Must execute and pass all 17 tests.
3. **Full Suite Regression Check**:
   `dotnet test NitroLite.Tests/NitroLite.Tests.csproj`
   Must execute and pass all 581 tests.
4. **Code Inspection**:
   Inspect `NitroLite/Core/CpuPowerService.cs`:
   - Line 67: `internal bool BypassNativePowerCalls { get; set; } = false;`
   - Line 65: `public bool IsModeVerified { get; private set; }`
   - Line 79: `CurrentMode = QueryCurrentBoostMode(IsAcPowerActive());`
   - Line 240: `_hangingProcess = p; p = null;` inside `QueryCurrentBoostModePowerCfg`
   - Line 303: `try { _hangingProcess?.Dispose(); } catch { }` in `EnsureNoHangingProcess()`
   - Line 378: `UpdateCurrentModeAfterSuccess` checks `isAcActive ? targetAc : targetDc`
   - Line 404: `UpdateCurrentModeAfterFailure` queries `IsAcPowerActive()`
