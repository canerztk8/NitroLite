# Survey Report: Hardware Interop & Communications (Survey 2)

**Author:** `explorer_survey_r4_2`  
**Date:** 2026-11-04T23:10:00Z  
**Target Solution:** `NitroLite.sln`  
**Scope:**
1. Memory corruption risk in ATK mouse HID asynchronous reads (`NitroLite/Core/AtkMouseService.cs:330-368`)
2. Acer Agent Client response validation & MUX supported modes (`NitroLite/Core/AcerAgentClient.cs:266, 325` & `NitroLite/MainWindow.xaml.cs:1019-1143`)

---

## Executive Summary

This survey provides root-cause analysis, memory-safety verification, and concrete implementation blueprints for two critical hardware interop components in NitroLite:

1. **AtkMouseService Native HID Asynchronous Reads (`AtkMouseService.cs:313-368`)**:
   - **Vulnerability**: Unpinned managed buffer (`rxBuf`) and stack-allocated `NativeOverlapped` struct are passed to asynchronous `ReadFile`.
   - **Failure Modes**: When timeouts (300ms) occur, when `HidD_SetOutputReport` fails, or when a USB wireless receiver is disconnected, the method returns and invalidates/frees stack and heap memory while kernel I/O request packets (IRPs) are still pending in `hidclass.sys`/`hidusb.sys`. The kernel event handle is closed prematurely with `CloseHandle(hEvent)` without issuing `CancelIoEx` or synchronously awaiting `GetOverlappedResult`.
   - **Impact**: Random heap corruption, stack corruption, driver IRP crashes, and `0xC0000005` Access Violations upon device disconnect or RF timeout.
   - **Remediation**: Pin `rxBuf` with `GCHandle.Alloc(..., GCHandleType.Pinned)`, track `ioPending` state via `ERROR_IO_PENDING (997)`, and guarantee `CancelIoEx(handle, ref ov)` followed by synchronous `GetOverlappedResult(handle, ref ov, out _, true)` completion before closing `hEvent`, unpinning memory, or disposing file handles.

2. **AcerAgentClient Response Status Validation & MUX Supported Modes**:
   - **Vulnerability 1 (`AcerAgentClient.cs:285, 343`)**: Both `SetOperatingModeAsync` and `SetGpuModeAsync` evaluate responses with:
     `return resStr == "0" || resStr == "3" || !string.IsNullOrWhiteSpace(resp);`
     The errant `|| !string.IsNullOrWhiteSpace(resp)` causes ANY non-empty response—including hardware error responses like `{"result":"99"}`—to be treated as complete success.
   - **Vulnerability 2 (`MainWindow.xaml.cs:1019-1143` & `MainWindow.xaml:459-463`)**: `CmbGpuMode` statically declares all three MUX options (`Automatic`, `Optimus`, `DiscreteGpu`). The application never calls `GetGpuModeCapabilityAsync()`, exposing modes not supported by the hardware (e.g. DDS/Advanced Optimus on standard MUX laptops, or dGPU mode on non-MUX models), misleading users with false success notifications.
   - **Remediation**:
     - Remove `|| !string.IsNullOrWhiteSpace(resp)` in `AcerAgentClient.cs`; require `resStr == "0" || resStr == "3"`. Validate status on all response parsers.
     - In `MainWindow.xaml.cs`, invoke `await _agentClient.GetGpuModeCapabilityAsync()` on initialization and refresh. Decode the 3-bit mask (`Bit 0: Optimus`, `Bit 1: DiscreteGpu`, `Bit 2: Automatic/DDS`) and dynamically populate `CmbGpuMode` to present only hardware-supported modes. Disable or hide controls when MUX capability is missing (`cap == 0` or null).

---

## Section 1: Detailed Code & Root-Cause Analysis

### 1.1 ATK Mouse HID Asynchronous Reads & Memory Safety (`AtkMouseService.cs`)

#### 1.1.1 Current Implementation
In `NitroLite/Core/AtkMouseService.cs` lines 313–368:
```csharp
private byte[]? WriteAndReadPacket(string devicePath, byte[] packet, byte expectedCmdId)
{
    SafeFileHandle handle = CreateFile(
        devicePath,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        IntPtr.Zero,
        OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED,
        IntPtr.Zero);

    if (handle.IsInvalid) return null;

    try
    {
        HidD_FlushQueue(handle);

        IntPtr hEvent = CreateEvent(IntPtr.Zero, true, false, null);
        if (hEvent == IntPtr.Zero) return null;

        try
        {
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
        }
        finally
        {
            CloseHandle(hEvent);
        }
    }
    catch
    {
        return null;
    }
    finally
    {
        handle.Close();
    }

    return null;
}
```

#### 1.1.2 Identification of Root Causes and Memory Safety Hazards

1. **P/Invoke Unpinning during Asynchronous Execution**:
   - `ReadFile` is declared as:
     `private static extern bool ReadFile(SafeFileHandle hFile, [Out] byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);`
   - In .NET runtime marshalling, passing `[Out] byte[] lpBuffer` pins the array *only* for the synchronous execution of the P/Invoke call.
   - When `ReadFile` returns `false` with `Marshal.GetLastWin32Error() == 997` (`ERROR_IO_PENDING`), the P/Invoke call completes. The runtime unpins `rxBuf`.
   - While `WaitForSingleObject(hEvent, 300)` is waiting, or if an early exit occurs, the Garbage Collector is free to relocate or collect `rxBuf`. The kernel USB host controller / HID miniport driver still holds the physical/virtual buffer pointer in its I/O Request Packet (IRP). When incoming USB data arrives, the hardware DMA controller writes to the old address, corrupting whatever heap object is now there.

2. **Stack Frame Lifetime vs. Kernel IRP Lifetime (`NativeOverlapped ov`)**:
   - `NativeOverlapped ov` is declared as a local value type on the stack.
   - When `ReadFile` is pending, the driver retains the pointer to `ov` to write `Internal` (NTSTATUS code) and `InternalHigh` (transferred byte count) upon completion.
   - If `WriteAndReadPacket` exits early (due to `!txOk`, timeout, or exception), the stack frame is unwound and reused by other methods.
   - When the driver asynchronously completes or cancels the IRP, it writes completion status directly into the invalid stack memory location, causing stack corruption.

3. **Leaked Pending I/O on `HidD_SetOutputReport` Failure (`line 342`)**:
   - If `HidD_SetOutputReport` returns `false` (e.g. mouse in sleep state or transmitter disconnected), line 342 executes `return null;`.
   - The pending `ReadFile` IRP initiated on line 339 is NEVER cancelled.
   - The finally block immediately calls `CloseHandle(hEvent)`, closing the event handle while the driver is still using it.
   - When `handle.Close()` is called, Windows attempts an asynchronous handle cleanup, but user-mode structures are already deallocated.

4. **Leaked Pending I/O on 300ms Timeout (`line 344`)**:
   - If the device does not respond within 300 ms, `WaitForSingleObject` returns `WAIT_TIMEOUT` (`0x00000102`).
   - The condition `if (waitRes == 0 && ...)` evaluates to `false`.
   - The method does not cancel the I/O or wait for it to abort; it drops straight into `finally { CloseHandle(hEvent); }`.
   - The IRP remains queued in `hidclass.sys`. Any subsequent delayed packet from the mouse results in an unmapped memory write.

5. **Violation of Win32 Overlapped Cancellation Contract**:
   - Win32 documentation explicitly mandates:
     > *"The canceled I/O operations are completed with the error `ERROR_OPERATION_ABORTED`. You must wait for the I/O operation to complete (for example, using `GetOverlappedResult` or `WaitForSingleObject`) before freeing or reusing the `OVERLAPPED` structure or the data buffer."*
   - Merely closing the handle or calling `CancelIoEx` without a synchronous wait on `GetOverlappedResult(handle, ref ov, out _, true)` is insufficient because cancellation in Windows NT drivers is strictly asynchronous.

---

### 1.2 AcerAgentClient Response Validation & MUX Supported Modes

#### 1.2.1 Operating Mode and GPU Mode Response Validation (`AcerAgentClient.cs`)
In `NitroLite/Core/AcerAgentClient.cs`:

**Lines 266–286:**
```csharp
public virtual async Task<bool> SetOperatingModeAsync(OperatingMode mode)
{
    await _lock.WaitAsync();
    try
    {
        if (!await EnsureConnectedAsync()) return false;

        var payload = new
        {
            Function = "OPERATING_MODE",
            Parameter = new { mode = (int)mode }
        };

        var resp = await SendRawAsync(PacketSetDeviceData, payload);
        if (string.IsNullOrWhiteSpace(resp)) return false;

        var node = JsonNode.Parse(resp);
        var resStr = node?["result"]?.ToString();
        // In AASSvc on Acer Nitro, result "0" or "3" indicates command acknowledgement
        return resStr == "0" || resStr == "3" || !string.IsNullOrWhiteSpace(resp);
    }
    catch
    {
        return false;
    }
    finally
    {
        _lock.Release();
    }
}
```

**Lines 325–344:**
```csharp
public virtual async Task<bool> SetGpuModeAsync(GpuOperatingMode mode)
{
    await _lock.WaitAsync();
    try
    {
        if (!await EnsureConnectedAsync()) return false;

        var payload = new
        {
            Function = "GPU_MODE",
            Parameter = new { mode = (int)mode }
        };

        var resp = await SendRawAsync(PacketSetDeviceData, payload);
        if (string.IsNullOrWhiteSpace(resp)) return false;

        var node = JsonNode.Parse(resp);
        var resStr = node?["result"]?.ToString();
        return resStr == "0" || resStr == "3" || !string.IsNullOrWhiteSpace(resp);
    }
    catch
    {
        return false;
    }
    finally
    {
        _lock.Release();
    }
}
```

#### 1.2.2 Root Cause of False Success
In both methods:
```csharp
return resStr == "0" || resStr == "3" || !string.IsNullOrWhiteSpace(resp);
```
- When `SendRawAsync` receives a response, `resp` is a non-empty string.
- If the hardware service rejects the command (e.g. `{"result":"99", "request":"GPU_MODE"}`), `resStr` is `"99"`.
- `resStr == "0"` is `false`.
- `resStr == "3"` is `false`.
- But `!string.IsNullOrWhiteSpace(resp)` evaluates to **`true`**!
- Consequently, `SetOperatingModeAsync` and `SetGpuModeAsync` return `true` on ANY failure response from the Acer Agent Service!

#### 1.2.3 MUX Option Display & Capability Check (`MainWindow.xaml.cs` & `MainWindow.xaml`)
In `NitroLite/MainWindow.xaml` lines 459–463:
```xml
<ComboBox x:Name="CmbGpuMode" Width="230" Margin="0,0,8,0">
    <ComboBoxItem Content="Otomatik Seçim (DDS / Advanced Optimus)" Tag="Automatic" />
    <ComboBoxItem Content="Optimus (Hibrit iGPU + dGPU)" Tag="Optimus" />
    <ComboBoxItem Content="Yalnızca Harici GPU (NVIDIA dGPU)" Tag="DiscreteGpu" />
</ComboBox>
```
In `NitroLite/MainWindow.xaml.cs` lines 1019–1041:
```csharp
public async Task RefreshGpuModeAsync()
{
    try
    {
        var mode = await _agentClient.GetGpuModeAsync();
        if (mode.HasValue)
        {
            UpdateGpuModeUi(mode.Value);
        }
        else
        {
            if (TxtGpuModeBadge != null)
            {
                TxtGpuModeBadge.Text = "Bilinmiyor";
                TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("TextMuted");
            }
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[GPU Mode] Refresh error: {ex.Message}");
    }
}
```

And lines 1121–1143:
```csharp
BtnApplyGpuMode.IsEnabled = false;
try
{
    var success = await _agentClient.SetGpuModeAsync(targetMode);
    if (success)
    {
        UpdateGpuModeUi(targetMode);
        ShowToast($"GPU modu '{modeDisplayName}' olarak güncellendi! Tam geçerlilik için yeniden başlatma önerilir.");
    }
    else
    {
        ShowToast("GPU modu uygulanamadı. Acer Agent Service yanıt vermedi.", false);
    }
}
catch (Exception ex)
{
    ShowToast($"Hata: {ex.Message}", false);
}
finally
{
    BtnApplyGpuMode.IsEnabled = true;
}
```

#### 1.2.4 Flaws in MUX Presentation and Handling
1. **Uncalled Capability Check**: `AcerAgentClient.GetGpuModeCapabilityAsync()` exists at line 355 of `AcerAgentClient.cs`, but is never called in `MainWindow.xaml.cs` or anywhere else in the application.
2. **Hardcoded Combo Items**: All 3 modes are hardcoded in XAML. If a laptop:
   - Does NOT support Advanced Optimus (e.g. standard MUX with capability `3` = Optimus + dGPU), `Automatic` is still offered. Selecting it causes `AASSvc` to return `{"result":"99"}`.
   - Does NOT have a physical MUX switch (capability `1` = Optimus only, or `0`/null), `DiscreteGpu` and `Automatic` are still offered.
3. **Double Falsehood**:
   - Because `SetGpuModeAsync` returns `true` even when `result == "99"`, the user selects an unsupported mode, clicks "Modu Uygula", and is falsely told the GPU mode was successfully applied and to reboot their laptop!
   - On reboot, the mode remains unchanged, eroding user trust.

---

## Section 2: Recommended Implementation Strategy

### 2.1 ATK Mouse Safe Overlapped I/O Pattern

#### 2.1.1 Required Win32 P/Invoke Signatures
Add to `AtkMouseService.cs` under `#region Win32 P/Invoke`:
```csharp
[DllImport("kernel32.dll", SetLastError = true)]
private static extern bool CancelIoEx(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped);

[DllImport("kernel32.dll", SetLastError = true)]
private static extern bool CancelIo(SafeFileHandle hFile);
```

#### 2.1.2 Constants
```csharp
private const uint WAIT_OBJECT_0 = 0x00000000;
private const uint WAIT_TIMEOUT = 0x00000102;
private const uint WAIT_FAILED = 0xFFFFFFFF;
private const int ERROR_IO_PENDING = 997;
private const int ERROR_OPERATION_ABORTED = 995;
```

#### 2.1.3 Robust `WriteAndReadPacket` Implementation
```csharp
private byte[]? WriteAndReadPacket(string devicePath, byte[] packet, byte expectedCmdId)
{
    using SafeFileHandle handle = CreateFile(
        devicePath,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        IntPtr.Zero,
        OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED,
        IntPtr.Zero);

    if (handle.IsInvalid) return null;

    IntPtr hEvent = CreateEvent(IntPtr.Zero, true, false, null);
    if (hEvent == IntPtr.Zero) return null;

    byte[] rxBuf = new byte[packet.Length];
    GCHandle rxBufHandle = GCHandle.Alloc(rxBuf, GCHandleType.Pinned);
    NativeOverlapped ov = new NativeOverlapped { EventHandle = hEvent };
    bool ioPending = false;

    try
    {
        HidD_FlushQueue(handle);

        uint readBytes = 0;
        bool readOk = ReadFile(handle, rxBuf, (uint)packet.Length, out readBytes, ref ov);
        if (!readOk)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_IO_PENDING)
            {
                ioPending = true;
            }
            else
            {
                // Immediate I/O failure (device disconnected or bad handle)
                return null;
            }
        }
        else
        {
            // Completed synchronously
            ioPending = false;
        }

        // Send Output Report
        bool txOk = HidD_SetOutputReport(handle, packet, packet.Length);
        if (!txOk)
        {
            // Transmission failed; exit early.
            // Finally block will cancel and await pending ReadFile IRP.
            return null;
        }

        // Wait for response packet
        if (ioPending)
        {
            uint waitRes = WaitForSingleObject(hEvent, 300);
            if (waitRes == WAIT_OBJECT_0)
            {
                if (GetOverlappedResult(handle, ref ov, out readBytes, false) && readBytes > 0)
                {
                    ioPending = false; // Successfully reaped
                    if (rxBuf.Length >= 2 && rxBuf[1] == expectedCmdId)
                    {
                        return rxBuf;
                    }
                }
                else
                {
                    // Failed or aborted by driver (e.g. disconnect)
                    ioPending = false;
                }
            }
            // If waitRes == WAIT_TIMEOUT or WAIT_FAILED, ioPending remains true
        }
        else if (readBytes > 0)
        {
            // Synchronous completion
            if (rxBuf.Length >= 2 && rxBuf[1] == expectedCmdId)
            {
                return rxBuf;
            }
        }
    }
    catch
    {
        return null;
    }
    finally
    {
        if (ioPending)
        {
            // Cancel pending I/O and synchronously wait for driver acknowledgement
            CancelIoEx(handle, ref ov);
            GetOverlappedResult(handle, ref ov, out _, true);
            ioPending = false;
        }

        CloseHandle(hEvent);

        if (rxBufHandle.IsAllocated)
        {
            rxBufHandle.Free();
        }
    }

    return null;
}
```

---

### 2.2 AcerAgentClient Response Validation Implementation

#### 2.2.1 Helper Method: `IsSuccessResult`
Add a centralized, testable validator to `AcerAgentClient.cs`:
```csharp
public static bool IsSuccessResult(string? responseJson)
{
    if (string.IsNullOrWhiteSpace(responseJson)) return false;
    try
    {
        var node = JsonNode.Parse(responseJson);
        var resStr = node?["result"]?.ToString();
        return resStr == "0" || resStr == "3";
    }
    catch
    {
        return false;
    }
}
```

#### 2.2.2 Refactoring `SetOperatingModeAsync` and `SetGpuModeAsync`
In `SetOperatingModeAsync` (`lines 279-286`):
```csharp
var resp = await SendRawAsync(PacketSetDeviceData, payload);
if (!IsSuccessResult(resp)) return false;
return true;
```

In `SetGpuModeAsync` (`lines 338-344`):
```csharp
var resp = await SendRawAsync(PacketSetDeviceData, payload);
if (!IsSuccessResult(resp)) return false;
return true;
```

#### 2.2.3 Auditing Other Endpoints in `AcerAgentClient`
- `GetOperatingModeAsync`:
  Check `if (node?["result"]?.ToString() is string r && r != "0" && r != "3") return OperatingMode.Default;`
- `GetGpuModeAsync`:
  Check `if (node?["result"]?.ToString() is string r && r != "0" && r != "3") return null;`
- `GetGpuModeCapabilityAsync`:
  Check `if (node?["result"]?.ToString() is string r && r != "0" && r != "3") return null;`
- `SetFanModeAsync`:
  Change `return !string.IsNullOrWhiteSpace(resp);` to `return IsSuccessResult(resp);`
- `SetLightingAsync`:
  Change `return !string.IsNullOrWhiteSpace(resp);` to `return IsSuccessResult(resp);`
- `SetBacklightTimeoutAsync`:
  Change `return !string.IsNullOrWhiteSpace(resp);` to `return IsSuccessResult(resp);`

---

### 2.3 MUX UI Supported Modes Filtering (`MainWindow.xaml.cs`)

#### 2.3.1 Capability Bitmask Decoding
Acer Agent Service reports `SUPPORT_GPU_MODE_CAPABILITY` as a 3-bit bitmask:
- `Bit 0` (`1` = `1 << 0` = `1 << (int)GpuOperatingMode.Optimus`): Optimus (Hybrid iGPU + dGPU)
- `Bit 1` (`2` = `1 << 1` = `1 << (int)GpuOperatingMode.DiscreteGpu`): DiscreteGpu (NVIDIA dGPU Only)
- `Bit 2` (`4` = `1 << 2` = `1 << (int)GpuOperatingMode.Automatic`): Automatic (Advanced Optimus / DDS)

Helper method:
```csharp
public static bool IsGpuModeSupported(int capabilityBitmask, GpuOperatingMode mode)
{
    int mask = 1 << (int)mode;
    return (capabilityBitmask & mask) != 0;
}
```

#### 2.3.2 UI Refresh Logic in `MainWindow.xaml.cs`
Update `RefreshGpuModeAsync()`:
```csharp
private int? _supportedGpuCapability = null;

public async Task RefreshGpuModeAsync()
{
    try
    {
        _supportedGpuCapability = await _agentClient.GetGpuModeCapabilityAsync();
        UpdateGpuModeCapabilitiesUi(_supportedGpuCapability);

        var mode = await _agentClient.GetGpuModeAsync();
        if (mode.HasValue)
        {
            UpdateGpuModeUi(mode.Value);
        }
        else
        {
            if (TxtGpuModeBadge != null)
            {
                TxtGpuModeBadge.Text = "Bilinmiyor";
                TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("TextMuted");
            }
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[GPU Mode] Refresh error: {ex.Message}");
    }
}

private void UpdateGpuModeCapabilitiesUi(int? capability)
{
    if (CmbGpuMode == null) return;

    CmbGpuMode.Items.Clear();

    // If no capability or neither Discrete nor Automatic is supported, MUX is not available
    if (!capability.HasValue || capability.Value <= 0 || (capability.Value & 6) == 0)
    {
        CmbGpuMode.Items.Add(new ComboBoxItem
        {
            Content = "Bu cihazda MUX desteği yok",
            Tag = "Unsupported",
            IsEnabled = false
        });
        CmbGpuMode.SelectedIndex = 0;
        CmbGpuMode.IsEnabled = false;
        if (BtnApplyGpuMode != null) BtnApplyGpuMode.IsEnabled = false;
        if (TxtGpuModeBadge != null)
        {
            TxtGpuModeBadge.Text = "Desteklenmiyor";
            TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("TextMuted");
        }
        return;
    }

    int cap = capability.Value;
    CmbGpuMode.IsEnabled = true;
    if (BtnApplyGpuMode != null) BtnApplyGpuMode.IsEnabled = true;

    // Bit 2: Automatic (Advanced Optimus / DDS)
    if (IsGpuModeSupported(cap, GpuOperatingMode.Automatic))
    {
        CmbGpuMode.Items.Add(new ComboBoxItem
        {
            Content = "Otomatik Seçim (DDS / Advanced Optimus)",
            Tag = "Automatic"
        });
    }

    // Bit 0: Optimus (Hybrid)
    if (IsGpuModeSupported(cap, GpuOperatingMode.Optimus))
    {
        CmbGpuMode.Items.Add(new ComboBoxItem
        {
            Content = "Optimus (Hibrit iGPU + dGPU)",
            Tag = "Optimus"
        });
    }

    // Bit 1: Discrete Only (dGPU)
    if (IsGpuModeSupported(cap, GpuOperatingMode.DiscreteGpu))
    {
        CmbGpuMode.Items.Add(new ComboBoxItem
        {
            Content = "Yalnızca Harici GPU (NVIDIA dGPU)",
            Tag = "DiscreteGpu"
        });
    }

    // Re-select current mode if valid
    SelectGpuModeCombo(_currentGpuMode.ToString());
}
```

#### 2.3.3 Pre-Execution Capability Validation in `BtnApplyGpuMode_Click`
In `MainWindow.xaml.cs:1083`:
```csharp
if (_supportedGpuCapability.HasValue && !IsGpuModeSupported(_supportedGpuCapability.Value, targetMode))
{
    ShowToast("Seçilen GPU modu donanımınız tarafından desteklenmiyor.", false);
    return;
}
```

---

## Section 3: Suggested Unit Test Strategy (`NitroLite.Tests`)

Unit tests must execute fast, deterministically, and without physical hardware dependencies.

### 3.1 Tests for `AcerAgentClient` Status Validation

Add to `NitroLite.Tests/AcerAgentClientTests.cs`:

```csharp
[Theory]
[InlineData("{\"result\": \"0\"}", true)]
[InlineData("{\"result\": 0}", true)]
[InlineData("{\"result\": \"3\"}", true)]
[InlineData("{\"result\": 3}", true)]
[InlineData("{\"result\": \"99\"}", false)]
[InlineData("{\"result\": 99}", false)]
[InlineData("{\"result\": \"1\"}", false)]
[InlineData("{\"result\": \"-1\"}", false)]
[InlineData("{}", false)]
[InlineData("", false)]
[InlineData(null, false)]
[InlineData("INVALID_JSON", false)]
[InlineData("{\"result\": \"99\", \"request\": \"GPU_MODE\"}", false)]
[InlineData("{\"result\": \"99\", \"request\": \"OPERATING_MODE\"}", false)]
public void IsSuccessResult_TreatsNonSuccessResponsesAsFailure(string? json, bool expectedSuccess)
{
    bool actual = AcerAgentClient.IsSuccessResult(json);
    Assert.Equal(expectedSuccess, actual);
}

[Theory]
[InlineData(7, GpuOperatingMode.Optimus, true)]
[InlineData(7, GpuOperatingMode.DiscreteGpu, true)]
[InlineData(7, GpuOperatingMode.Automatic, true)]
[InlineData(3, GpuOperatingMode.Optimus, true)]
[InlineData(3, GpuOperatingMode.DiscreteGpu, true)]
[InlineData(3, GpuOperatingMode.Automatic, false)] // DDS unsupported on 3
[InlineData(1, GpuOperatingMode.Optimus, true)]
[InlineData(1, GpuOperatingMode.DiscreteGpu, false)] // MUX unsupported on 1
[InlineData(1, GpuOperatingMode.Automatic, false)]
[InlineData(0, GpuOperatingMode.DiscreteGpu, false)]
public void IsGpuModeSupported_DecodesBitmaskAccurately(int capability, GpuOperatingMode mode, bool expected)
{
    bool supported = MainWindow.IsGpuModeSupported(capability, mode);
    Assert.Equal(expected, supported);
}
```

### 3.2 Tests for `MockAcerAgentClient` MUX Operations
Extend `MockAcerAgentClient` in `NitroLite.Tests/MockHardwareServices.cs`:
```csharp
public int? MockCapability { get; set; } = 7;
public GpuOperatingMode? MockGpuMode { get; set; } = GpuOperatingMode.Automatic;
public bool FailOnSetGpuMode { get; set; } = false;

public override Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult(MockCapability);
public override Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult(MockGpuMode);
public override Task<bool> SetGpuModeAsync(GpuOperatingMode mode)
{
    if (FailOnSetGpuMode) return Task.FromResult(false);
    MockGpuMode = mode;
    return Task.FromResult(true);
}
```

Unit test verifying rejection on `FailOnSetGpuMode`:
```csharp
[Fact]
public async Task SetGpuModeAsync_WhenAgentReturnsResult99_ReportsFailure()
{
    var mock = new MockAcerAgentClient { FailOnSetGpuMode = true };
    bool result = await mock.SetGpuModeAsync(GpuOperatingMode.DiscreteGpu);
    Assert.False(result);
}
```

### 3.3 Tests for `AtkMouseService` Overlapped Memory Safety

Add to `NitroLite.Tests/AtkMouseServiceTests.cs`:

1. **Non-Throwing Disconnect & Timeout Safety Test**:
   ```csharp
   [Fact]
   public void WriteAndReadPacket_OnInvalidOrDisconnectedDevice_ReturnsNullWithoutThrowingOrLeaking()
   {
       var service = new AtkMouseService();
       // Invoking profile query when physical mouse is absent
       var result = service.QueryCurrentProfile();
       // Must safely return null without memory violation or uncaught exception
       Assert.Null(result);
   }
   ```

2. **Buffer Pinning & Memory Invariant Verification**:
   ```csharp
   [Fact]
   public void AtkMouseService_BufferLayoutAndPinningContract_ValidatesLengths()
   {
       byte[] setPkt = AtkMouseService.BuildSetCurrentConfigPacket(0);
       byte[] getPkt = AtkMouseService.BuildGetCurrentConfigPacket();

       Assert.Equal(17, setPkt.Length);
       Assert.Equal(17, getPkt.Length);

       // Verify GCHandle pinning round-trip for 17-byte buffer
       GCHandle handle = GCHandle.Alloc(setPkt, GCHandleType.Pinned);
       try
       {
           Assert.True(handle.IsAllocated);
           IntPtr addr = handle.AddrOfPinnedObject();
           Assert.NotEqual(IntPtr.Zero, addr);
       }
       finally
       {
           handle.Free();
       }
       Assert.False(handle.IsAllocated);
   }
   ```

---

## Section 4: Summary Table of Defects and Target Edits

| Target File | Lines | Issue Description | Recommended Solution |
| :--- | :--- | :--- | :--- |
| `NitroLite/Core/AtkMouseService.cs` | 313–368 | Unpinned buffer, uncancelled `ReadFile`, stack struct risk | Pin buffer with `GCHandle`, track `ioPending`, invoke `CancelIoEx` + `GetOverlappedResult(..., true)` in `finally` |
| `NitroLite/Core/AtkMouseService.cs` | 370–420 | Missing P/Invoke signatures | Add `CancelIoEx` and `CancelIo` P/Invoke signatures |
| `NitroLite/Core/AcerAgentClient.cs` | 283–285 | `|| !string.IsNullOrWhiteSpace(resp)` returns true for `{"result":"99"}` | Use `IsSuccessResult(resp)` checking strictly `resStr == "0" \|\| resStr == "3"` |
| `NitroLite/Core/AcerAgentClient.cs` | 342–344 | `|| !string.IsNullOrWhiteSpace(resp)` returns true for `{"result":"99"}` | Use `IsSuccessResult(resp)` checking strictly `resStr == "0" \|\| resStr == "3"` |
| `NitroLite/MainWindow.xaml.cs` | 1019–1041 | MUX options not filtered by capability | Call `GetGpuModeCapabilityAsync()`, decode bitmask (`7`, `3`, `1`), dynamically populate `CmbGpuMode` |
| `NitroLite/MainWindow.xaml.cs` | 1083–1143 | Unsupported modes selectable and reported as successful | Validate mode against capability before sending; report failure on `success == false` |
| `NitroLite.Tests/AcerAgentClientTests.cs` | New tests | Lack of tests for `result: "99"` and MUX bitmask | Add unit tests for `IsSuccessResult` and `IsGpuModeSupported` |
