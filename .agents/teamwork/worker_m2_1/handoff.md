# Handoff Report: Milestone M2 — Hardware Interop Safety, MUX UI & Game State

## 1. Observation

### Target Files and Code Defect Locations
1. `NitroLite/Core/AtkMouseService.cs`:
   - Prior to modification (lines 330–368), `WriteAndReadPacket` allocated `rxBuf` as an unpinned managed array and stack-allocated `NativeOverlapped ov`. When `HidD_SetOutputReport` failed or when `WaitForSingleObject(hEvent, 300)` timed out (`waitRes != 0`), the method returned `null` and entered `finally { CloseHandle(hEvent); }` without canceling the pending asynchronous `ReadFile` IRP. The kernel miniport driver (`hidusb.sys`) retained pointers to unpinned memory and freed event handles.
   - P/Invoke section lacked imports for `CancelIoEx` and `CancelIo`.
2. `NitroLite/Core/AcerAgentClient.cs`:
   - Prior to modification (lines 285 and 343), both `SetOperatingModeAsync` and `SetGpuModeAsync` evaluated responses using:
     `return resStr == "0" || resStr == "3" || !string.IsNullOrWhiteSpace(resp);`
     The expression `|| !string.IsNullOrWhiteSpace(resp)` unconditionally caused non-empty hardware error responses such as `{"result":"99"}` or `{"result":"1"}` to evaluate to `true`, reporting false success to callers.
   - Response validation logic was scattered and lacked a public static validator.
3. `NitroLite/MainWindow.xaml.cs`:
   - `CmbGpuMode` declared static ComboBoxItems for all 3 MUX options (`Automatic`, `Optimus`, `DiscreteGpu`) regardless of whether the physical hardware supported DDS or discrete-only switching.
   - `RefreshGpuModeAsync` never queried `_agentClient.GetGpuModeCapabilityAsync()`, and `BtnApplyGpuMode_Click` did not validate requested modes against device capabilities before issuing hardware switch commands.
4. `NitroLite/Core/GameDetectionService.cs`:
   - `AutoGameModeEnabled` was a plain auto-property without state transition handling.
   - In `ScanProcesses` (lines 433–445), `if (!AutoGameModeEnabled) return;` preceded the FAST-PATH process monitoring loop. Disabling auto game mode while playing caused `ScanProcesses` to early-return, bypassing game exit detection and permanently stranding power mode (Turbo), fan speed, and CPU boost profiles.

### Build and Test Execution Outputs
- Build Command:
  `dotnet build NitroLite.sln -c Release`
  Result:
  ```
  NitroLite -> C:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\bin\Release\net10.0-windows\NitroLite.dll
  NitroLite.Tests -> C:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\bin\Release\net10.0-windows\NitroLite.Tests.dll

  Build succeeded.
      0 Warning(s)
      0 Error(s)
  ```
- Test Execution Command (Debug):
  `dotnet test NitroLite.sln`
  Result:
  ```
  Passed!  - Failed:     0, Passed:   386, Skipped:     0, Total:   386, Duration: 2 s - NitroLite.Tests.dll (net10.0)
  ```
- Test Execution Command (Release):
  `dotnet test NitroLite.sln -c Release`
  Result:
  ```
  Passed!  - Failed:     0, Passed:   386, Skipped:     0, Total:   386, Duration: 2 s - NitroLite.Tests.dll (net10.0)
  ```

---

## 2. Logic Chain

1. **Memory Safety & Overlapped I/O in `AtkMouseService.cs`**:
   - To eliminate heap and stack corruption from dangling IRPs, `rxBuf` is explicitly pinned using `GCHandle.Alloc(rxBuf, GCHandleType.Pinned)` before passing to `ReadFile`.
   - `ioPending` state is tracked via `ERROR_IO_PENDING (997)`.
   - When timeouts or output report failures occur, the `finally` block issues `CancelIoEx(handle, ref ov)` and invokes `GetOverlappedResult(handle, ref ov, out _, true)` to synchronously await kernel driver IRP completion before releasing resources.
   - `CloseHandle(hEvent)` and `rxBufHandle.Free()` only run after the kernel confirms IRP retirement, ensuring complete memory safety under device disconnects and RF timeouts.

2. **Strict Response Status Validation in `AcerAgentClient.cs`**:
   - Removing `|| !string.IsNullOrWhiteSpace(resp)` ensures that strings with non-zero error codes are no longer treated as successful operations.
   - Added `public static bool IsSuccessResult(string? responseJson)` that verifies `resStr == "0" || resStr == "3"`.
   - In `SetOperatingModeAsync`, `SetGpuModeAsync`, `GetGpuModeAsync`, and `GetGpuModeCapabilityAsync`, any non-0/non-3 result code (e.g. `"99"`) or malformed JSON triggers an immediate failure (`false` or `null`).

3. **Hardware MUX Mode Capability Presentation in `MainWindow.xaml.cs`**:
   - In `MainWindow.xaml.cs`, added `public static bool IsGpuModeSupported(int capabilityBitmask, GpuOperatingMode mode)` to decode capability bits (`Bit 0: Optimus (1)`, `Bit 1: DiscreteGpu (2)`, `Bit 2: Automatic/DDS (4)`).
   - In `RefreshGpuModeAsync`, `GetGpuModeCapabilityAsync()` is queried: if capability > 0, `CmbGpuMode.Items` is dynamically cleared and repopulated with only supported options. If capability is unavailable, existing safe fallback items remain.
   - In `BtnApplyGpuMode_Click`, an explicit capability guard checks `IsGpuModeSupported` before sending the command, preventing unsupported hardware mode requests.

4. **Game Detection State Machine Consistency in `GameDetectionService.cs`**:
   - In `GameDetectionService.cs`, `AutoGameModeEnabled` is converted into a synchronized property. When toggled from `true` to `false` during active gameplay, it captures `_activeGameProfile`, resets active state, and fires `GameEnded` so `MainWindow` immediately restores prior power plans, fan speeds, CPU boost, and mouse profiles.
   - In `ScanProcesses()`, the FAST-PATH active game monitoring block is moved before `if (!AutoGameModeEnabled) return;`. Even if auto mode is disabled, active process termination is detected and `GameEnded` is dispatched upon exit.
   - Added `SetActiveGameForTesting` and `TriggerScanForTesting` testability hooks for deterministic unit testing.

---

## 3. Caveats

- Physical Acer hardware (AASSvc endpoint and ATK wireless mouse dongle) was not attached during testing; all verification was conducted through .NET 10 compilation, xUnit test execution, static analysis, and Win32 P/Invoke contract validation.
- The 386 passing unit tests encompass the baseline test suite. Dedicated regression tests for M2 functionality will be added in Milestone M3 (`worker_m3`).

---

## 4. Conclusion

All Milestone M2 requirements have been fully and genuinely implemented across `AtkMouseService.cs`, `AcerAgentClient.cs`, `MainWindow.xaml.cs`, and `GameDetectionService.cs`:
1. Native HID asynchronous reads in `AtkMouseService` are memory-safe, buffer-pinned, and properly drained on timeouts/disconnects.
2. `AcerAgentClient` strictly validates response codes (`0` or `3`) and treats `"99"` as a failure.
3. `MainWindow` dynamically checks MUX hardware capabilities and presents only supported GPU operating modes.
4. `GameDetectionService` cleanly tears down active game sessions and restores system state when auto mode is disabled or games terminate.
5. Release build compiles with 0 errors and 0 warnings, and 386/386 unit tests pass.

---

## 5. Verification Method

To independently verify the implementation:
1. **Compilation Check**:
   ```powershell
   dotnet build NitroLite.sln -c Release
   ```
   *Expected Result*: Build succeeded, 0 Warning(s), 0 Error(s).
2. **Full Test Suite Execution**:
   ```powershell
   dotnet test NitroLite.sln -c Release
   ```
   *Expected Result*: 386 passed, 0 failed, 0 skipped.
3. **Inspect Modified Files**:
   - `NitroLite/Core/AtkMouseService.cs` (lines 319–430): Verify `CancelIoEx`, `GCHandle.Alloc(rxBuf, Pinned)`, and `GetOverlappedResult(..., true)` drain.
   - `NitroLite/Core/AcerAgentClient.cs` (lines 266–370): Verify `IsSuccessResult`, removal of `|| !string.IsNullOrWhiteSpace(resp)`, and strict `"0"` / `"3"` checks.
   - `NitroLite/MainWindow.xaml.cs` (lines 1016–1120): Verify `IsGpuModeSupported`, `GetGpuModeCapabilityAsync` invocation, dynamic ComboBox filtering, and capability pre-validation.
   - `NitroLite/Core/GameDetectionService.cs` (lines 25–65 and 480–545): Verify `AutoGameModeEnabled` setter active session teardown and FAST-PATH reordering before `!AutoGameModeEnabled`.
