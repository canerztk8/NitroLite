# Progress — Worker M2

Last visited: 2026-11-04T23:30:00Z

## Status
Completed Milestone M2 implementation:
1. `NitroLite/Core/AtkMouseService.cs`:
   - Added `WAIT_OBJECT_0`, `WAIT_TIMEOUT`, `WAIT_FAILED`, `ERROR_IO_PENDING`, `ERROR_OPERATION_ABORTED` Win32 constants.
   - Pinned `rxBuf` with `GCHandle.Alloc(rxBuf, GCHandleType.Pinned)` in `WriteAndReadPacket`.
   - Imported `CancelIoEx(SafeFileHandle, ref NativeOverlapped)`, `CancelIoEx(IntPtr, IntPtr)`, and `CancelIo(SafeFileHandle)`.
   - Added safe cancellation and synchronous drain with `GetOverlappedResult(handle, ref ov, out _, true)` before releasing handle, event, and pinned memory in finally.
2. `NitroLite/Core/AcerAgentClient.cs`:
   - Added `public static bool IsSuccessResult(string? responseJson)` validating `resStr == "0" || resStr == "3"`.
   - Removed `|| !string.IsNullOrWhiteSpace(resp)` from `SetOperatingModeAsync` and `SetGpuModeAsync`.
   - Added response code validation to `GetGpuModeAsync` and `GetGpuModeCapabilityAsync` to reject non-success responses.
3. `NitroLite/MainWindow.xaml.cs`:
   - Added `_supportedGpuCapability` field and `public static bool IsGpuModeSupported(int capabilityBitmask, GpuOperatingMode mode)`.
   - In `RefreshGpuModeAsync`, queried `GetGpuModeCapabilityAsync()` and dynamically populated `CmbGpuMode.Items` with supported modes (Bit 2: Automatic/DDS, Bit 0: Optimus, Bit 1: DiscreteGpu).
   - Validated requested GPU mode against hardware capability in `BtnApplyGpuMode_Click`.
4. `NitroLite/Core/GameDetectionService.cs`:
   - Implemented `AutoGameModeEnabled` with backing field and lock: on disabling during active gameplay, captures `_activeGameProfile`, resets state, and fires `GameEnded` to restore system settings.
   - Reordered `ScanProcesses()` so the FAST-PATH active game exit detection executes before the `if (!AutoGameModeEnabled) return;` check.
   - Added testing helper methods `SetActiveGameForTesting` and `TriggerScanForTesting`.

## Verification
- `dotnet build NitroLite.sln -c Release`: 0 errors, 0 warnings.
- `dotnet test NitroLite.sln`: 386 passed, 0 failed.
- `dotnet test NitroLite.sln -c Release`: 386 passed, 0 failed.
