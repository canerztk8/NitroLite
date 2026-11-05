# Project: NitroLite 7 Verified Defects Remediation & Hardware Safety

## Architecture
NitroLite is a lightweight, high-performance Windows desktop hardware monitoring and control utility for Acer Nitro gaming laptops built with .NET 10 WPF.
- **Core Automation & Debloat Subsystem**:
  - `DebloatService.cs`: OEM telemetry service management, safe debloat state persistence (`debloat_backup_state.json`), and elevated PowerShell restoration script generation.
  - `SettingsManager.cs`: Atomic JSON settings persistence (`settings.json`), corrupted file recovery, and backup preservation (`settings.json.bak`).
- **Hardware Interop & Communication Subsystem**:
  - `AtkMouseService.cs`: Native Win32 HID communication with ATK wireless gaming mice using overlapped I/O (`ReadFile`, `HidD_SetOutputReport`, `CancelIoEx`, `GetOverlappedResult`, `WaitForSingleObject`).
  - `AcerAgentClient.cs`: IPC client communicating with Acer Agent Service (`AASSvc`) over localhost sockets for MUX switch and performance operating mode selection.
- **Presentation & Game Subsystem**:
  - `MainWindow.xaml` / `MainWindow.xaml.cs`: Primary desktop interface, hardware mode selectors (dynamic MUX ComboBox based on device capability bitmask), and debloat confirmation dialogs.
  - `GameDetectionService.cs`: Active process monitoring, automatic game mode profile activation/deactivation, and system restore state machine.
- **Unit & Regression Test Suite**:
  - `NitroLite.Tests`: xUnit test suite validating all service logic, error handling, state recovery, and protocol parsing in isolation without physical hardware dependencies.

## Feature Inventory
| # | Feature | Description | Milestone | Source | Status |
|---|---------|-------------|-----------|--------|--------|
| 1 | Debloat Startup Type Whitelist Validation | Validate startup types in `debloat_backup_state.json` against whitelist (`Automatic`, `Manual`, `Disabled`) with normalization before generating elevated PowerShell restore commands (prevent command injection). | M1 | explorer_survey_r4_1 | DONE |
| 2 | AcerLightingService Debloat Exclusion | Exclude `AcerLightingService` from `Tier1Services` to preserve keyboard RGB controls and timeout management, aligning with UI prompt ("Altı adet OEM telemetri servisi ... Donanım kontrolleri etkilenmeyecektir") and 214 MB memory reduction. | M1 | explorer_survey_r4_1 | DONE |
| 3 | Resilient Debloat Backup & Restoration | Abort debloat if `SaveBackupState` fails; do not force unrecorded services to `Automatic`; record and restore service running states (`Running`/`Stopped`) and scheduled task states (`NitroSenseLauncher`). | M1 | explorer_survey_r4_1 | DONE |
| 4 | Settings Backup Preservation on Corrupted Recovery | Validate settings file integrity (`IsValidSettingsFile`) before copying to `settings.json.bak` in `SaveSettingsAtomic`, preventing corrupted settings from destroying valid backups. | M1 | explorer_survey_r4_1 | DONE |
| 5 | Baseline Privacy Cleanliness (.gitignore) | Add `battery-report.html` to `.gitignore` to resolve privacy test failure and achieve 100% baseline pass rate. | M1 | explorer_survey_r4_3 | DONE |
| 6 | Native HID Overlapped Memory Safety | In `AtkMouseService.cs`, pin `rxBuf` with `GCHandle`, import and invoke Win32 `CancelIoEx`, and await `GetOverlappedResult(..., true)` on timeouts (`waitRes != 0`) or output report failure before closing handles or exiting. | M2 | explorer_survey_r4_2 | DONE |
| 7 | AcerAgentClient Response Validation | Remove `|| !string.IsNullOrWhiteSpace(resp)` from `AcerAgentClient.cs` lines 285 and 343; strictly require `result == "0" || result == "3"` so error responses like `{"result":"99"}` are treated as failures. | M2 | explorer_survey_r4_2 | DONE |
| 8 | Dynamic Hardware MUX Mode Presentation | Query `GetGpuModeCapabilityAsync()` in `MainWindow.xaml.cs`, decode bitmask (`Bit 0: Optimus`, `Bit 1: dGPU`, `Bit 2: DDS / Dynamic Display Switch`), and dynamically populate `CmbGpuMode` to only display hardware-supported modes. | M2 | explorer_survey_r4_2 | DONE |
| 9 | GameDetectionService Active Toggle Teardown | In `GameDetectionService.cs`, position active PID fast-path exit detection before checking `AutoGameModeEnabled`, and teardown active session / invoke `GameEnded` immediately when `AutoGameModeEnabled` is toggled off during active gameplay. | M2 | explorer_survey_r4_3 | DONE |
| 10 | Automated Regression Unit Test Suite | Author comprehensive unit tests in `NitroLite.Tests` covering all 7 verified defects without requiring physical hardware modifications. | M3 | explorer_survey_r4_3 | DONE |
| 11 | Release Build & Full Solution Regression | Compile `NitroLite.sln` in Release configuration (`dotnet build NitroLite.sln -c Release`) with 0 errors and 0 warnings, and execute all tests (`dotnet test NitroLite.sln`) with 100% pass rate. | M4 | all explorers | DONE |
| 12 | Independent Multi-Agent Verification Gate | Independent gate verification by 2 Reviewers, 2 Challengers, and 1 Forensic Auditor ensuring strict correctness, memory safety, and integrity compliance. | M4 | orchestrator | DONE |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | Debloat Resilience, Settings Protection & Privacy | Features 1–5: `DebloatService.cs`, `SettingsManager.cs`, `.gitignore` | Survey complete | DONE |
| M2 | Hardware Interop Safety, MUX UI & Game State | Features 6–9: `AtkMouseService.cs`, `AcerAgentClient.cs`, `MainWindow.xaml.cs`, `GameDetectionService.cs` | M1 | DONE |
| M3 | Automated Regression Test Suite | Feature 10: `NitroLite.Tests` regression tests covering all 7 fixes | M2 | DONE |
| M4 | Release Compilation & Verification Gate | Features 11–12: Clean Release build, 100% green test suite, multi-agent review, challenge, and forensic audit gate | M3 | DONE |

## Interface Contracts
### `DebloatService` Startup Type Validation Contract
```csharp
private static readonly HashSet<string> AllowedStartupTypes = new(StringComparer.OrdinalIgnoreCase)
{
    "Automatic", "Manual", "Disabled"
};

public static string ValidateStartupType(string? input)
{
    if (string.IsNullOrWhiteSpace(input))
        throw new ArgumentException("Startup type cannot be null or empty.");

    string trimmed = input.Trim();
    if (AllowedStartupTypes.TryGetValue(trimmed, out var canonical))
        return canonical;

    throw new ArgumentException($"Invalid service startup type: '{input}'. Allowed values: Automatic, Manual, Disabled.");
}
```

### `AtkMouseService` Overlapped I/O Memory Safety Contract
```csharp
[DllImport("kernel32.dll", SetLastError = true)]
private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

// Memory safety rules:
// 1. rxBuf MUST be pinned with GCHandle.Alloc(rxBuf, GCHandleType.Pinned).
// 2. On waitRes != 0 (timeout) or !txOk:
//    - Call CancelIoEx(handle, pOverlapped).
//    - Call GetOverlappedResult(handle, ref ov, out _, true) to synchronously await kernel IRP completion.
// 3. Unpin GCHandle and CloseHandle(hEvent) only in finally block after I/O completion is guaranteed.
```

### `AcerAgentClient` Response Contract
```csharp
// Response validation contract:
// resStr == "0" || resStr == "3" indicates success.
// Any other code (e.g. "99") or null/empty indicates failure.
// Under no circumstances should !string.IsNullOrWhiteSpace(resp) grant success.
```

### `MainWindow` MUX Capability Contract
```csharp
// Capability bitmask contract:
// Bit 0 (1): Optimus (Hybrid iGPU + dGPU)
// Bit 1 (2): Discrete Only (dGPU)
// Bit 2 (4): Dynamic Display Switch (DDS / Advanced Optimus)
// If cap == 0 or API unavailable -> fallback to existing default items.
// Otherwise -> clear CmbGpuMode.Items and add ONLY items whose corresponding bit is set.
```

### `GameDetectionService` State Transition Contract
```csharp
// On AutoGameModeEnabled = false:
// If _activeGameProfile != null:
//   Save reference, set _activeGameProfile = null, _activeProcessId = 0.
//   Invoke GameEnded event with saved profile so MainWindow restores power, fans, and mouse settings.
// In ScanProcesses():
//   Evaluate active PID fast-path before returning if !AutoGameModeEnabled.
```

## Code Layout
- `NitroLite/Core/DebloatService.cs`: Startup type validation, `Tier1Services` array, `SaveBackupState`, running/task state capture.
- `NitroLite/Core/SettingsManager.cs`: `IsValidSettingsFile` pre-copy validation in `SaveSettingsAtomic`.
- `NitroLite/Core/AtkMouseService.cs`: `CancelIoEx` import, buffer pinning, safe overlapped cancellation and drain.
- `NitroLite/Core/AcerAgentClient.cs`: Strict JSON response parsing for operating mode and GPU mode.
- `NitroLite/MainWindow.xaml.cs`: Dynamic MUX ComboBox population based on `GetGpuModeCapabilityAsync()`.
- `NitroLite/Core/GameDetectionService.cs`: Active PID fast-path reordering and `AutoGameModeEnabled` setter teardown.
- `.gitignore`: Ignore `battery-report.html`.
- `NitroLite.Tests/DebloatServiceTests.cs` (or `DebloatRemediationTests.cs`): Unit tests for startup type validation, backup resilience, and service list.
- `NitroLite.Tests/SettingsManagerTests.cs` (or `SettingsRemediationTests.cs`): Unit tests for backup file preservation.
- `NitroLite.Tests/AtkMouseServiceTests.cs`: Unit tests for overlapped I/O safety and cancellation logic.
- `NitroLite.Tests/AcerAgentClientTests.cs`: Unit tests for response code validation (`result: 99`) and MUX capability decoding.
- `NitroLite.Tests/GameDetectionServiceTests.cs`: Unit tests for auto game mode toggling during gameplay and exit restoration.
