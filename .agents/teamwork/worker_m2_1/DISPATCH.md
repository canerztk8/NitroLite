# Task Assignment: Worker M2 — Hardware Interop Safety, MUX UI & Game State

## Role & Identity
You are `worker_m2_1` (teamwork_worker).
Your working directory: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m2_1`

## Mandatory Rules & Integrity Warning
- MANDATORY: Read `ORIGINAL_REQUEST.md` at `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md` first.
- MANDATORY: Read `PROJECT.md` at `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md`.
- Read Explorer findings in `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_2\survey_report.md` and `explorer_survey_r4_3\survey_report.md`.
- **MANDATORY INTEGRITY WARNING**:
  DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

## Write Ownership
You exclusively own and may edit ONLY these files:
- `NitroLite/Core/AtkMouseService.cs`
- `NitroLite/Core/AcerAgentClient.cs`
- `NitroLite/MainWindow.xaml.cs`
- `NitroLite/Core/GameDetectionService.cs`
Do NOT modify any other files in this milestone.

## Implementation Tasks

### 1. `AtkMouseService.cs` Asynchronous HID Overlapped Memory Safety (Defect 2)
- In `NitroLite/Core/AtkMouseService.cs:330-370`:
- Import Win32 `CancelIoEx`:
  ```csharp
  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);
  ```
- In `WriteAndReadPacket(SafeFileHandle handle, byte[] packet, byte expectedCmdId)`:
  - Pin `rxBuf` with `GCHandle.Alloc(rxBuf, GCHandleType.Pinned)` before passing to `ReadFile`.
  - Handle `ReadFile` return value and `ERROR_IO_PENDING` (997).
  - On timeout (`waitRes != 0`, e.g. 300ms wait expired) or output report failure (`!txOk`):
    - Safely cancel pending I/O via `CancelIoEx(handle.DangerousGetHandle(), pOverlapped)`.
    - Synchronously wait for driver IRP completion via `GetOverlappedResult(handle, ref ov, out _, true)` before returning.
  - In a `finally` block, guarantee that `GCHandle.Free()` is called and `CloseHandle(hEvent)` is only invoked AFTER any pending I/O has completely drained.
  - Ensure receiver disconnection or invalid handles are handled gracefully without crashing or throwing unhandled access violations.

### 2. `AcerAgentClient.cs` Strict Response Validation (Defect 3)
- In `NitroLite/Core/AcerAgentClient.cs` lines 285 and 343:
- Remove `|| !string.IsNullOrWhiteSpace(resp)`.
- Strictly validate that `resStr == "0" || resStr == "3"`.
- Any other response, such as `{"result":"99"}`, non-zero/non-three status, or null/malformed JSON, MUST return `false` (indicating operation failure).

### 3. `MainWindow.xaml.cs` Dynamic Hardware MUX Mode Presentation (Defect 3)
- In `NitroLite/MainWindow.xaml.cs:1019-1041` (`RefreshGpuModeAsync`):
- Call `await _acerClient.GetGpuModeCapabilityAsync()`.
- Decode the hardware capability bitmask:
  - Bit 0 (`1`): Optimus (Hybrid iGPU + dGPU) -> Tag: `"Optimus"`, Content: `"Optimus (Hibrit iGPU + dGPU)"`
  - Bit 1 (`2`): Discrete Only (dGPU) -> Tag: `"DiscreteGpu"`, Content: `"Yalnızca Harici GPU (NVIDIA dGPU)"`
  - Bit 2 (`4`): Dynamic Display Switch (DDS / Advanced Optimus) -> Tag: `"Automatic"`, Content: `"Otomatik Seçim (DDS / Advanced Optimus)"`
- If capability > 0, dynamically populate `CmbGpuMode.Items` with ONLY the hardware-supported modes. If capability <= 0 or query fails, retain safe existing items.
- Ensure selected item matches current active GPU mode.

### 4. `GameDetectionService.cs` Active Game Mode Toggling & Restoration (Defect 6)
- In `NitroLite/Core/GameDetectionService.cs:433-445` (`ScanProcesses`):
  - Move the active PID fast-path block (`if (_activeProcessId > 0 && _activeGameProfile != null)`) ABOVE the `if (!AutoGameModeEnabled) return;` check, so that if auto game mode is disabled during active gameplay, process exit detection still runs and restores settings cleanly.
- In `AutoGameModeEnabled` property setter (or toggle handler):
  - When `AutoGameModeEnabled` transitions from `true` to `false` while a game is actively running (`_activeGameProfile != null`):
    - Capture the active game profile reference.
    - Reset `_activeGameProfile = null` and `_activeProcessId = 0`.
    - Invoke the `GameEnded` event with the captured profile so `MainWindow.OnGameEnded` restores previous power, fan, and mouse profiles immediately.

## Verification Requirements
- Run `dotnet build NitroLite.sln -c Release` — must compile with 0 errors and 0 warnings.
- Run `dotnet test NitroLite.sln` — all 386 existing tests must pass.
- Document exact commands and output in your `handoff.md`.


## 2026-11-04T23:22:04Z
You are worker_m2_1. Your working directory is c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m2_1.
MANDATORY: Read ORIGINAL_REQUEST.md at c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md first.
Read PROJECT.md at c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md.
Read your full instructions in c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m2_1\DISPATCH.md.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

You exclusively own:
- NitroLite/Core/AtkMouseService.cs
- NitroLite/Core/AcerAgentClient.cs
- NitroLite/MainWindow.xaml.cs
- NitroLite/Core/GameDetectionService.cs

Implement the changes per DISPATCH.md, run `dotnet build NitroLite.sln -c Release` and `dotnet test NitroLite.sln`, verify 0 errors/0 warnings and all tests pass.
Write your changes and test results to handoff.md in your working directory and notify me with send_message.
