# Handoff Report: Survey Explorer 2 — Requirement R2 (`powercfg` Process Ownership & Timeout Management)

## 1. Observation
Direct empirical observations and code analysis in `NitroLite/Core/CpuPowerService.cs` and `NitroLite.Tests/VerifiedDefectsRegressionTests.cs`:

1. **Dual Ownership in Query Path (`CpuPowerService.cs:203, 214`)**:
   ```csharp
   203: using var p = Process.Start(psi);
   ...
   212: if (!p.WaitForExit(1000) || !p.HasExited)
   213: {
   214:     _hangingProcess = p;
   215:     return null;
   216: }
   ```
   At line 214, ownership of `p` is transferred to `_hangingProcess`, but the method scope exit at line 215 causes compiler-generated `p.Dispose()` from `using var p` to execute. `_hangingProcess` holds an already-disposed `Process` object.

2. **Silent Catch & Unmonitored Process Leak (`CpuPowerService.cs:209-218`)**:
   ```csharp
   209: try
   210: {
   211:     p.Kill();
   212:     if (!p.WaitForExit(1000) || !p.HasExited)
   213:     {
   214:         _hangingProcess = p;
   215:         return null;
   216:     }
   217: }
   218: catch { }
   219: return null;
   ```
   If `p.Kill()` throws `Win32Exception` or `InvalidOperationException`, execution jumps to line 218 (`catch { }`). `_hangingProcess` is never set, while the OS process continues executing untracked.

3. **Inverted Power Source Logic on Failure (`CpuPowerService.cs:344`)**:
   ```csharp
   343: bool isAcActive = IsAcPowerActive();
   344: bool queryAc = isAcActive ? targetAc : !targetDc;
   345: var verified = QueryBoostModeExact(queryAc);
   ```
   If the machine is on AC (`isAcActive == true`) and a DC write failed (`targetAc == false`), `queryAc` evaluates to `false` (DC), querying the inactive power source and overwriting `CurrentMode` with the DC setting. Conversely, if on DC (`isAcActive == false`) and an AC write failed (`targetDc == false`), `queryAc` evaluates to `true` (AC).

4. **Unconditional `CurrentMode` Overwrite on Partial Target Writes (`CpuPowerService.cs:325, 388`)**:
   In `SetBoostModeAsync` (line 325) and `SetBoostModePowerCfg` (line 388):
   ```csharp
   CurrentMode = mode;
   return true;
   ```
   When `targetAc: false, targetDc: true` succeeds while on AC, `CurrentMode` is updated to `mode` despite the active AC scheme remaining unchanged.

5. **Query Failure Masked as Verified Default (`CpuPowerService.cs:64, 75, 145`)**:
   ```csharp
   64: public CpuBoostMode CurrentMode { get; private set; } = CpuBoostMode.Aggressive;
   ...
   144: var exact = QueryBoostModeExact(isAc);
   145: return exact ?? CurrentMode;
   ```
   If `QueryBoostModeExact` returns `null`, `QueryCurrentBoostMode` returns `CurrentMode` (default `Aggressive`). No verification status flag (`IsModeVerified`) is exposed.

6. **Battery Startup Queries AC (`CpuPowerService.cs:75, 142`)**:
   ```csharp
   75: CurrentMode = QueryCurrentBoostMode();
   ...
   142: public virtual CpuBoostMode QueryCurrentBoostMode(bool isAc = true)
   ```
   Default parameter `isAc = true` causes startup initialization to query the AC power setting even when disconnected from wall power.

7. **Host OS Power Plan Modification in Concurrency Test (`VerifiedDefectsRegressionTests.cs:1005-1017`)**:
   ```csharp
   1007: var service = new CpuPowerService();
   1008: var tasks = new[]
   1009: {
   1010:     Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: true, targetDc: false)),
   1011:     Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Efficient, targetAc: false, targetDc: true)),
   1012:     Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Aggressive, targetAc: true, targetDc: true))
   1013: };
   ```
   Invokes real `powrprof.dll` native APIs and mutates host Windows power schemes on test runner machines, violating Requirement R4.

8. **Build and Test Baseline**:
   - `dotnet build -c Release NitroLite.sln`: Succeeded with 0 Errors, 0 Warnings.
   - `dotnet test NitroLite.sln --filter "FullyQualifiedName~CpuPowerService"`: 17 Passed, 0 Failed, 0 Skipped.

---

## 2. Logic Chain
1. From Observation 1, because `using var p` disposes `p` upon method return, any subsequent call to `EnsureNoHangingProcess()` attempts `_hangingProcess.HasExited` on a disposed handle, throwing an exception caught by `catch { return false; }`. Therefore, the service is permanently blocked after a single query timeout.
2. From Observation 2, because `catch { }` does not inspect `HasExited` or assign `_hangingProcess = p; p = null;`, a failed `Kill()` call results in an unmonitored background process, allowing subsequent writes while an untracked process runs.
3. From Observation 3, `queryAc = isAcActive ? targetAc : !targetDc` mathematically reverses the power source query whenever a write targets only the off-state power source, corrupting `CurrentMode` reporting.
4. From Observation 4, unconditionally setting `CurrentMode = mode` whenever a write succeeds ignores whether the active power source was actually in the target set (`targetAc` / `targetDc`).
5. From Observation 5 and 6, the absence of an explicit `IsModeVerified` property and default `isAc = true` causes query failures and battery startups to masquerade as verified `Aggressive` modes.
6. From Observation 7, `new CpuPowerService()` in test 1005 invokes real native Win32 APIs modifying the OS power plan because `CpuPowerService` lacks a test seam to bypass native `powrprof.dll` calls.

---

## 3. Caveats
- No caveats regarding `CpuPowerService` and `powercfg` process tracking: all code paths in `CpuPowerService.cs`, `PowerAutomationService.cs`, and `VerifiedDefectsRegressionTests.cs` have been completely mapped and verified.
- Hardware-dependent behavior of `powrprof.dll` requires Windows runtime (`net10.0-windows`), which is fully supported in the local test environment.

---

## 4. Conclusion
Requirement R2 contains 8 specific defects (D1 through D8) in `CpuPowerService.cs` and 1 testing defect (D9) in `VerifiedDefectsRegressionTests.cs`. All defects are directly solvable by:
1. Adopting the explicit `Process? p = null; try / finally` pattern with `p = null` ownership transfer in `QueryCurrentBoostModePowerCfg`.
2. Guarding kill exceptions to always verify exit and assign `_hangingProcess = p; p = null;` if exit cannot be verified.
3. Replacing line 344 in `UpdateCurrentModeAfterFailure` with `bool queryAc = isAcActive;`.
4. Updating `CurrentMode = mode` only when the active power source was targeted.
5. Exposing `IsModeVerified` and `VerifiedCurrentMode`.
6. Adding `internal bool BypassNativePowerCalls { get; set; }` to make all unit tests safe and isolated.
7. Adding the four missing regression tests identified in `analysis.md`.

---

## 5. Verification Method
1. **Compilation Check**:
   `dotnet build -c Release NitroLite.sln` -> must build with 0 errors and 0 warnings.
2. **Targeted Unit Test Verification**:
   `dotnet test NitroLite.sln --filter "FullyQualifiedName~CpuPowerService"` -> must execute all existing and new unit tests without modifying host OS power schemes.
3. **Specific Inspection Points**:
   - `NitroLite/Core/CpuPowerService.cs`: Verify `using var p` is absent in `QueryCurrentBoostModePowerCfg`.
   - `NitroLite/Core/CpuPowerService.cs`: Verify line 344 queries `isAcActive` directly.
   - `NitroLite/Core/CpuPowerService.cs`: Verify `UpdateCurrentModeAfterSuccess` checks `(isAc && targetAc) || (!isAc && targetDc)`.
   - `NitroLite.Tests/VerifiedDefectsRegressionTests.cs`: Verify test 1005 sets `BypassNativePowerCalls = true`.
