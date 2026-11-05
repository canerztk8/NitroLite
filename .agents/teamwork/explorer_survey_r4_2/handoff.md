# Handoff Report: Hardware Interop & Communications Survey

**Agent:** `explorer_survey_r4_2`  
**Date:** 2026-11-04T23:12:00Z  
**Working Directory:** `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_2`  
**Target Report:** `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_2\survey_report.md`  

---

## 1. Observation

### Item 1: AtkMouseService Native HID Asynchronous Reads & Memory Safety
- In `NitroLite/Core/AtkMouseService.cs:335-356`:
  ```csharp
  NativeOverlapped ov = new NativeOverlapped { EventHandle = hEvent };
  byte[] rxBuf = new byte[packet.Length];
  uint readBytes = 0;

  ReadFile(handle, rxBuf, (uint)packet.Length, out readBytes, ref ov);

  bool txOk = HidD_SetOutputReport(handle, packet, packet.Length);
  if (!txOk) return null;

  uint waitRes = WaitForSingleObject(hEvent, 300);
  if (waitRes == 0 && GetOverlappedResult(handle, ref ov, out readBytes, false) && readBytes > 0)
  {
      if (rxBuf.Length >= 2 && rxBuf[1] == expectedCmdId)
      {
          return rxBuf;
      }
  }
  ```
  - `rxBuf` is a managed byte array without pinning (`GCHandle`).
  - `NativeOverlapped ov` is allocated on the function stack.
  - Line 342: When `!txOk`, it returns `null` immediately without cancelling the pending `ReadFile`.
  - Line 344: When `waitRes != 0` (e.g. 300ms timeout `0x102`), it drops into `finally { CloseHandle(hEvent); }` without cancelling or waiting for the driver IRP to complete.
  - Lines 370–423: P/Invoke declarations do not include `CancelIoEx` or `CancelIo`.

### Item 2: AcerAgentClient Response Validation & MUX Supported Modes
- In `NitroLite/Core/AcerAgentClient.cs:283-285`:
  ```csharp
  var node = JsonNode.Parse(resp);
  var resStr = node?["result"]?.ToString();
  // In AASSvc on Acer Nitro, result "0" or "3" indicates command acknowledgement
  return resStr == "0" || resStr == "3" || !string.IsNullOrWhiteSpace(resp);
  ```
- In `NitroLite/Core/AcerAgentClient.cs:342-343`:
  ```csharp
  var node = JsonNode.Parse(resp);
  var resStr = node?["result"]?.ToString();
  return resStr == "0" || resStr == "3" || !string.IsNullOrWhiteSpace(resp);
  ```
  - In both methods, `|| !string.IsNullOrWhiteSpace(resp)` causes responses like `{"result":"99"}` to return `true`.
- In `NitroLite/Core/AcerAgentClient.cs:355-372`:
  - `GetGpuModeCapabilityAsync()` exists, querying `SUPPORT_GPU_MODE_CAPABILITY`.
- In `NitroLite/MainWindow.xaml:459-463`:
  ```xml
  <ComboBox x:Name="CmbGpuMode" Width="230" Margin="0,0,8,0">
      <ComboBoxItem Content="Otomatik Seçim (DDS / Advanced Optimus)" Tag="Automatic" />
      <ComboBoxItem Content="Optimus (Hibrit iGPU + dGPU)" Tag="Optimus" />
      <ComboBoxItem Content="Yalnızca Harici GPU (NVIDIA dGPU)" Tag="DiscreteGpu" />
  </ComboBox>
  ```
  - All 3 items are hardcoded in XAML.
- In `NitroLite/MainWindow.xaml.cs:1019-1041`:
  - `RefreshGpuModeAsync` never queries `GetGpuModeCapabilityAsync()`.
- In `scripts/research/mux_analysis_report.md:92-98`:
  - Acer hardware capability bitmask: `Bit 0 (1): Optimus`, `Bit 1 (2): Discrete Only (dGPU)`, `Bit 2 (4): Dynamic Display Switch (DDS / Advanced Optimus)`. Sum `7` = all supported; `3` = Optimus + dGPU; `1` = Optimus only (no MUX switch).

---

## 2. Logic Chain

1. **Memory Corruption in AtkMouseService**:
   - `ReadFile` with `FILE_FLAG_OVERLAPPED` queues an asynchronous IRP in `hidclass.sys` and returns `ERROR_IO_PENDING` (997).
   - In .NET, the P/Invoke marshaller unpins `rxBuf` as soon as `ReadFile` returns.
   - If a timeout occurs (300ms) or `HidD_SetOutputReport` fails, `WriteAndReadPacket` returns to its caller, popping `ov` off the stack and allowing GC to collect or move `rxBuf`.
   - Because `CancelIoEx` was not called and `GetOverlappedResult` was not awaited, the driver IRP is still live in kernel memory.
   - Any subsequent packet arrival or asynchronous cancel on handle close causes kernel DMA to write to deallocated stack or moved heap memory, leading directly to memory corruption and `0xC0000005` access violations.
   - Therefore, `rxBuf` must be pinned with `GCHandle.Alloc(..., GCHandleType.Pinned)`, and every exit path must invoke `CancelIoEx` and await `GetOverlappedResult(..., true)` before releasing resources.

2. **False Success in AcerAgentClient**:
   - When Acer Agent Service rejects an invalid or unsupported mode, it returns JSON with `"result": "99"`.
   - `resStr == "0"` is false; `resStr == "3"` is false; but `!string.IsNullOrWhiteSpace(resp)` is true.
   - Therefore, `SetOperatingModeAsync` and `SetGpuModeAsync` return `true` regardless of hardware rejection.
   - Removing `|| !string.IsNullOrWhiteSpace(resp)` and enforcing `resStr == "0" || resStr == "3"` ensures proper failure propagation.

3. **MUX Mode Options Display in MainWindow**:
   - Statically listing all 3 MUX modes in `MainWindow.xaml` violates hardware constraints on devices lacking DDS (`cap = 3`) or lacking MUX switching altogether (`cap = 1` or `0`).
   - By querying `GetGpuModeCapabilityAsync()`, decoding the 3-bit mask, and dynamically populating `CmbGpuMode.Items`, the UI will only display modes supported by the physical hardware.

---

## 3. Caveats

- Physical ATK mouse hardware or live Acer Agent Service daemon was not attached during automated testing; all behavioral and protocol inferences are derived from source code, Win32 specifications, existing unit tests, and empirical research logs (`mux_analysis_report.md`).
- Only read-only investigation was performed; no source code or test files were modified.

---

## 4. Conclusion

1. `AtkMouseService.cs` requires pinned memory (`GCHandle`), proper `ERROR_IO_PENDING` state tracking, Win32 `CancelIoEx` P/Invoke import, and synchronous drain via `GetOverlappedResult(handle, ref ov, out _, true)` on timeouts or early exits.
2. `AcerAgentClient.cs` must remove the `!string.IsNullOrWhiteSpace(resp)` clause from lines 285 and 343, strictly validating `result == "0" || result == "3"`.
3. `MainWindow.xaml.cs` must invoke `GetGpuModeCapabilityAsync()` inside `RefreshGpuModeAsync()`, decode the bitmask (`(cap & 1) != 0` for Optimus, `(cap & 2) != 0` for dGPU, `(cap & 4) != 0` for DDS/Auto), dynamically populate `CmbGpuMode`, and reject unsupported selections.

---

## 5. Verification Method

To independently verify these findings:
1. **Source Code Inspection**:
   - Check `NitroLite/Core/AtkMouseService.cs:330-368` for unpinned `rxBuf` and missing `CancelIoEx`.
   - Check `NitroLite/Core/AcerAgentClient.cs:285, 343` for `|| !string.IsNullOrWhiteSpace(resp)`.
   - Check `NitroLite/MainWindow.xaml:459-463` and `NitroLite/MainWindow.xaml.cs:1019-1041` for hardcoded MUX items and uncalled capability API.
2. **Unit Test Execution**:
   - Run `dotnet test NitroLite.Tests` (confirms project builds and baseline passes).
3. **Invalidation Conditions**:
   - If `resStr == "99"` were proven to be a valid acknowledgement code in Acer Agent Service (contradicted by `mux_analysis_report.md` and standard Acer IPC specifications).
   - If .NET P/Invoke automatically kept asynchronous buffers pinned across unmanaged kernel I/O completions without `GCHandle` (strictly contradicted by Microsoft CLR Marshalling specification).
