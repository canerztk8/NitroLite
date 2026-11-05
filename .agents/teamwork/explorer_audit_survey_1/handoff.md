# Handoff Report: Survey and Architecture Proposal for Requirements R1 and R2

**Agent**: explorer_audit_survey_1  
**Working Directory**: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_audit_survey_1\`  
**Scope**: 
- **R1: Debloat Service Restoration & Error Propagation**
- **R2: Memory Cleaner Game Guard & Default State**  
**Date**: 2026-11-04  

---

## 1. Observation

### 1.1 Existing Codebase & Environment Context
- **Solution & Test Status**:
  - `dotnet build NitroLite.sln` compiles cleanly with 0 errors and 0 warnings.
  - `dotnet test NitroLite.Tests` passes all 322 current unit and regression tests.
- **Project Structure**:
  - Main Project: `NitroLite/` targeting `net10.0-windows` (WPF, C# 13).
  - Test Project: `NitroLite.Tests/` targeting `net10.0-windows` (xUnit 2.9.3 + custom runner `Program.cs`).

---

### 1.2 Observations on Requirement R1 (Debloat Service Restoration & Error Propagation)

#### Observation 1.1: Missing State Recording in `ApplyTier1DebloatAsync`
File: `NitroLite/Core/DebloatService.cs`, Lines 113–118:
```csharp
    public async Task<bool> ApplyTier1DebloatAsync()
    {
        var svcs = string.Join(",", Tier1Services.Select(s => $"'{s}'"));
        var script = $"$svcs = @({svcs}); foreach ($s in $svcs) {{ Stop-Service -Name $s -Force -ErrorAction SilentlyContinue; Set-Service -Name $s -StartupType Disabled -ErrorAction SilentlyContinue }}; Disable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction SilentlyContinue";
        return await RunElevatedPowerShellAsync(script);
    }
```
- The method immediately disables all services without querying or saving the existing startup type (`Automatic`, `Manual`, etc.) of each service.
- If a service was previously configured as `Manual` (or `Automatic (Delayed Start)`), that configuration is completely discarded.

#### Observation 1.2: Unconditional Restoration to `Automatic`
File: `NitroLite/Core/DebloatService.cs`, Lines 120–125:
```csharp
    public async Task<bool> RestoreTier1ServicesAsync()
    {
        var svcs = string.Join(",", Tier1Services.Select(s => $"'{s}'"));
        var script = $"$svcs = @({svcs}); foreach ($s in $svcs) {{ Set-Service -Name $s -StartupType Automatic -ErrorAction SilentlyContinue; Start-Service -Name $s -ErrorAction SilentlyContinue }}; Enable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction SilentlyContinue";
        return await RunElevatedPowerShellAsync(script);
    }
```
- Every service is hardcoded to `Set-Service -Name $s -StartupType Automatic`.
- Acer OEM telemetry services (such as `ASMSvc` or `AcerDIAgentSvis`, which are typically configured as demand-start `Manual`) are forcibly changed to `Automatic` and started upon system boot, altering the operating system's original configuration.

#### Observation 1.3: Error Suppression via `-ErrorAction SilentlyContinue`
Files: `NitroLite/Core/DebloatService.cs`, Lines 116 & 123:
- In both debloat and restore scripts, `-ErrorAction SilentlyContinue` is passed to `Stop-Service`, `Set-Service`, `Start-Service`, and `Disable-ScheduledTask`/`Enable-ScheduledTask`.
- In Windows PowerShell, `-ErrorAction SilentlyContinue` swallows terminating and non-terminating errors (including permissions failures, missing services, locked services), allowing `powershell.exe` to exit with code `0`.

#### Observation 1.4: Elevated Process Execution Lacks Error Redirection & Diagnostics
File: `NitroLite/Core/DebloatService.cs`, Lines 87–111:
```csharp
    internal async Task<bool> RunElevatedPowerShellAsync(string script)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var p = Process.Start(psi);
                p?.WaitForExit(10000);
                return p?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        });
    }
```
- `UseShellExecute = true` is required for elevation via `Verb = "runas"`. Under .NET, standard error redirection cannot be combined with `UseShellExecute = true`.
- When an error occurs (or when the user clicks "No" on UAC, throwing `Win32Exception: The operation was canceled by the user`), the method catches all exceptions and silently returns `false` with no diagnostics.
- The method returns a bare `bool`, preventing callers from receiving error details or diagnostic output.

#### Observation 1.5: UI Swallows Failures and Reports Inaccurate Status
File: `NitroLite/MainWindow.xaml.cs`, Lines 2257–2296:
```csharp
    private async void BtnApplyDebloat_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ...
            if (confirm == MessageBoxResult.Yes)
            {
                var ok = await _debloatService.ApplyTier1DebloatAsync();
                RefreshServiceList();
                if (ok)
                {
                    ShowToast("Telemetri servisleri devre dışı bırakıldı (~214 MB).");
                }
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Servisler yapılandırılamadı: {ex.Message}", false);
        }
    }

    private async void BtnRestoreServices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await _debloatService.RestoreTier1ServicesAsync();
            RefreshServiceList();
            if (ok)
            {
                ShowToast("Varsayılan servis yapılandırması geri yüklendi.");
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Servisler geri yüklenemedi: {ex.Message}", false);
        }
    }
```
- If `ApplyTier1DebloatAsync()` or `RestoreTier1ServicesAsync()` returns `false` (e.g. UAC rejected, script timeout, or non-zero exit code), **no message or error toast is shown to the user at all**; the UI remains completely silent.
- If PowerShell suppressed internal errors via `-ErrorAction SilentlyContinue`, `ok` returns `true`, triggering a **false success toast** even though services failed to change state.

#### Observation 1.6: Zero Unit Test Coverage for Debloat Service
- `find_by_name` and `grep_search` across `NitroLite.Tests` show no test file exists for `DebloatService` (no `DebloatServiceTests.cs`). Only a basic command-line autostart string check exists in `StartupOptionsTests.cs:64`.

---

### 1.3 Observations on Requirement R2 (Memory Cleaner Game Guard & Default State)

#### Observation 2.1: `AutoRamCleanerEnabled` Defaults to `true`
File: `NitroLite/Core/Models.cs`, Lines 176–195:
```csharp
public class AppSettings
{
    public bool StartMinimized { get; set; } = false;
    public bool OsdEnabled { get; set; } = false;
    public string SelectedChartMetric { get; set; } = "CPU_TEMP";
    public bool AutoRamCleanerEnabled { get; set; } = true;
    ...
}
```
- `AutoRamCleanerEnabled` is initialized to `true` by default.
- There is no property named `AutoCleanMemory` on `AppSettings` or `MemoryCleanerService`.

#### Observation 2.2: Standby Memory Purged on Game Launch (Game Start Thrashing)
File: `NitroLite/MainWindow.xaml.cs`, Lines 1218–1227:
```csharp
    // Automatically purge standby memory when entering game for smooth zero-stutter FPS (async offload)
    if (_appSettings.AutoRamCleanerEnabled)
    {
        var memRes = await _memoryService.CleanMemoryAsync();
        if (memRes.FreedMegabytes > 150)
        {
            string freedStr = memRes.FreedMegabytes >= 1024 ? $"{memRes.FreedMegabytes / 1024.0:F1} GB" : $"{memRes.FreedMegabytes:F0} MB";
            ShowToast($"{profile.DisplayName}: {freedStr} bellek önbelleği temizlendi.");
        }
    }
```
- Right as a game profile is detected and applied (game start), `CleanMemoryAsync()` is explicitly called.
- `CleanMemory` calls `NtSetSystemInformation` with `MemoryPurgeStandbyList` and iterates over all running processes calling `EmptyWorkingSet`. This empties the OS file cache and trims working sets while the game is loading assets into RAM, causing heavy disk I/O thrashing and frame stutters.

#### Observation 2.3: Periodic Telemetry Check Ignores Active Game State
File: `NitroLite/MainWindow.xaml.cs`, Lines 612–626:
```csharp
    // Optimal Auto RAM Cleaner Check (32 GB RAM optimized)
    if (_appSettings.AutoRamCleanerEnabled && _memoryService.ShouldAutoClean(data.RamUsage, data.RamTotalMB))
    {
        var res = await _memoryService.CleanMemoryAsync();
        ...
    }
```
- Line 613 calls `_memoryService.ShouldAutoClean(data.RamUsage, data.RamTotalMB)`.
- The signature of `ShouldAutoClean` is `ShouldAutoClean(double ramUsagePercent, double ramTotalMB, bool isGameActive = false)` (`MemoryCleanerService.cs:101`).
- Because `_gameService.IsGameActive` (or `IsGameRunning`) is omitted, `isGameActive` defaults to `false`. Periodic automatic memory purging runs continuously during gaming sessions if RAM usage crosses 88%.

#### Observation 2.4: Neither Service Exposes `IsGameRunning`
- `GameDetectionService.cs:49` exposes `public bool IsGameActive`, but does NOT expose `IsGameRunning`.
- `MemoryCleanerService.cs` has neither `AutoCleanMemory` nor `IsGameRunning` properties.

---

## 2. Logic Chain

### 2.1 Logic Chain for Requirement R1
1. **Observation 1.1 & 1.2** show that `ApplyTier1DebloatAsync` disables services without recording their prior startup type, and `RestoreTier1ServicesAsync` forces all services to `Automatic`.
2. Windows services have diverse intended startup configurations: core background daemons default to `Automatic`, while auxiliary or diagnostic agents (like `ASMSvc` or `AcerDIAgentSvis`) default to `Manual` (started only when triggered by specific events).
3. Forcing `Manual` services to `Automatic` creates persistent system alterations, unnecessary startup overhead, and violates the principle of non-destructive debloating.
4. **Observation 1.3 & 1.4** show that errors are suppressed inside PowerShell scripts via `-ErrorAction SilentlyContinue`, and standard error output is uncaptured due to `UseShellExecute = true`.
5. When PowerShell fails (e.g. access denied, registry permissions, script syntax, service locked), `powershell.exe` exits with code 0 due to `SilentlyContinue`, and `RunElevatedPowerShellAsync` returns `true`.
6. **Observation 1.5** demonstrates that `MainWindow.xaml.cs` reports false success to the user, and if an execution failure occurs (`ok == false`), it reports nothing.
7. **Therefore**, `DebloatService` must:
   - Record each service's actual startup type from `SYSTEM\CurrentControlSet\Services\{name}` into a persistent configuration file (`debloat_service_states.json`) before setting them to `Disabled`.
   - Restore services specifically to their recorded startup type (`Manual` -> `Manual`, `Automatic` -> `Automatic`).
   - Remove `-ErrorAction SilentlyContinue`, enforce `$ErrorActionPreference = 'Stop'`, catch script-level exceptions, and write error diagnostics to a secure temporary error log.
   - Return a rich `DebloatResult` object (with `Success`, `ErrorMessage`, `Errors`, and implicit `bool` conversion).
   - Provide injectable delegate hooks (`ScriptExecutor` and `StartupTypeReader`) to enable deterministic unit testing without requiring UAC elevation.
   - Update `MainWindow.xaml.cs` to surface failure toasts (`ShowToast($"...", false)`) upon non-success.

### 2.2 Logic Chain for Requirement R2
1. **Observation 2.1** shows `AppSettings.AutoRamCleanerEnabled` initializes to `true`.
2. On 32GB RAM systems (like the Acer Nitro 16 AN16-41), background memory cleaning is rarely needed and can cause premature working set trimming. Defaulting to enabled is sub-optimal.
3. **Observation 2.2** shows `MainWindow.xaml.cs:1218-1227` calls `CleanMemoryAsync()` directly upon game start, and **Observation 2.3** shows periodic checks call `ShouldAutoClean` without passing active game state.
4. Calling `EmptyWorkingSet` and purging the standby list while a game is initializing or actively rendering strips memory pages from RAM into swap/disk. When the game immediately demands those textures or meshes, page faults spike, resulting in severe micro-stutter and frame drops.
5. **Therefore**, to satisfy R2:
   - `AppSettings` must default `AutoRamCleanerEnabled = false` and expose `AutoCleanMemory = false`.
   - `MemoryCleanerService` must expose `AutoCleanMemory = false` and `IsGameRunning = false`.
   - `GameDetectionService` must expose `IsGameRunning => IsGameActive`.
   - `CleanMemory` / `CleanMemoryAsync` in `MemoryCleanerService` must check `IsGameRunning` (or an explicit parameter); if a game is running, working set trimming and standby cache purging must be completely bypassed.
   - `MainWindow.xaml.cs` must remove the aggressive `CleanMemoryAsync()` call from game profile detection (`OnGameStarted`), link game lifecycle events to update `_memoryService.IsGameRunning`, and pass `_gameService.IsGameRunning` during periodic checks.

---

## 3. Caveats

1. **Non-Elevated Unit Testing**:
   - Unit tests running under `dotnet test` cannot execute `powershell.exe -Verb runas` on arbitrary systems due to UAC prompts and environment restrictions.
   - To make R1 fully unit-testable in `NitroLite.Tests`, `DebloatService` must expose clean abstraction hooks (`ScriptExecutor` and `StartupTypeReader`). Production defaults will invoke elevated PowerShell and registry queries, while unit tests can simulate all success, failure, UAC cancellation, and script syntax scenarios.
2. **Missing OEM Services on Non-Acer Test Machines**:
   - On clean Windows installs or CI build agents, Acer services (`AcerServiceSvc`, etc.) will not exist in the registry.
   - The recording logic must handle nonexistent services gracefully, falling back to predefined OEM defaults (`ASMSvc` -> `Automatic`, `AcerDIAgentSvis` -> `Manual`) and verifying that the restore script targets those types.
3. **Standby Memory Purging Privileges**:
   - `TryPurgeStandbyList` in `MemoryCleanerService` requires `SeProfileSingleProcessPrivilege`. When running non-elevated, it returns `false` gracefully, which is handled properly in the existing codebase.
4. **Settings Backward Compatibility**:
   - Existing user configuration files may contain `"AutoRamCleanerEnabled": true` or `"AutoRamCleanerEnabled": false`. By maintaining both `AutoRamCleanerEnabled` and `AutoCleanMemory` in `AppSettings` referencing the same backing field, complete backward and forward serialization compatibility is preserved.

---

## 4. Conclusion & Technical Implementation Plan

### 4.1 Interface & Model Additions

#### `DebloatResult` & `DebloatExecutionResult`
In `NitroLite/Core/DebloatService.cs`:
```csharp
public class DebloatExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string? ErrorOutput { get; set; }
}

public class DebloatResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Errors { get; set; } = new();
    public Dictionary<string, string> RecordedStartupTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static implicit operator bool(DebloatResult? res) => res?.Success ?? false;
}
```

#### `AppSettings` & `MemoryCleanerService` Properties
In `NitroLite/Core/Models.cs`:
```csharp
    private bool _autoRamCleanerEnabled = false;

    public bool AutoRamCleanerEnabled
    {
        get => _autoRamCleanerEnabled;
        set => _autoRamCleanerEnabled = value;
    }

    public bool AutoCleanMemory
    {
        get => _autoRamCleanerEnabled;
        set => _autoRamCleanerEnabled = value;
    }
```

In `NitroLite/Core/MemoryCleanerService.cs`:
```csharp
    public bool AutoCleanMemory { get; set; } = false;
    public bool IsGameRunning { get; set; } = false;
```

In `NitroLite/Core/GameDetectionService.cs`:
```csharp
    public bool IsGameRunning => IsGameActive;
```

---

### 4.2 Proposed Code Diffs

#### Diff 1: `NitroLite/Core/DebloatService.cs`
```csharp
<<<<
    internal async Task<bool> RunElevatedPowerShellAsync(string script)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var p = Process.Start(psi);
                p?.WaitForExit(10000);
                return p?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        });
    }

    public async Task<bool> ApplyTier1DebloatAsync()
    {
        var svcs = string.Join(",", Tier1Services.Select(s => $"'{s}'"));
        var script = $"$svcs = @({svcs}); foreach ($s in $svcs) {{ Stop-Service -Name $s -Force -ErrorAction SilentlyContinue; Set-Service -Name $s -StartupType Disabled -ErrorAction SilentlyContinue }}; Disable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction SilentlyContinue";
        return await RunElevatedPowerShellAsync(script);
    }

    public async Task<bool> RestoreTier1ServicesAsync()
    {
        var svcs = string.Join(",", Tier1Services.Select(s => $"'{s}'"));
        var script = $"$svcs = @({svcs}); foreach ($s in $svcs) {{ Set-Service -Name $s -StartupType Automatic -ErrorAction SilentlyContinue; Start-Service -Name $s -ErrorAction SilentlyContinue }}; Enable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction SilentlyContinue";
        return await RunElevatedPowerShellAsync(script);
    }
====
    public static readonly Dictionary<string, string> DefaultFallbackStartupTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AcerServiceSvc"] = "Automatic",
        ["ASMSvc"] = "Manual",
        ["AcerDIAgentSvis"] = "Manual",
        ["AcerEZSvc"] = "Automatic",
        ["AcerDeviceInfoAgentService"] = "Manual",
        ["AcerDeviceEnablingServiceV2"] = "Automatic"
    };

    public string StateFilePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debloat_service_states.json");

    public Func<string, string?>? StartupTypeReader { get; set; }
    public Func<string, Task<DebloatExecutionResult>>? ScriptExecutor { get; set; }

    public virtual string? GetServiceStartupType(string name)
    {
        if (StartupTypeReader != null) return StartupTypeReader(name);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", false);
            if (key != null)
            {
                var startVal = key.GetValue("Start");
                var startInt = startVal is int i ? i : 2;
                return startInt switch
                {
                    2 => "Automatic",
                    3 => "Manual",
                    4 => "Disabled",
                    _ => "Automatic"
                };
            }
        }
        catch { }
        return null;
    }

    public Dictionary<string, string> LoadRecordedStartupTypes()
    {
        try
        {
            if (File.Exists(StateFilePath))
            {
                var json = File.ReadAllText(StateFilePath);
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null) return new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public void SaveRecordedStartupTypes(Dictionary<string, string> states)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(states, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var tmp = StateFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, StateFilePath, overwrite: true);
        }
        catch { }
    }

    public Dictionary<string, string> RecordPreDebloatStartupTypes()
    {
        var recorded = LoadRecordedStartupTypes();
        foreach (var name in Tier1Services)
        {
            var currentType = GetServiceStartupType(name);
            if (!string.IsNullOrEmpty(currentType) && !currentType.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                if (currentType.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (!recorded.ContainsKey(name) || recorded[name].Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        recorded[name] = DefaultFallbackStartupTypes.TryGetValue(name, out var fb) ? fb : "Automatic";
                    }
                }
                else
                {
                    recorded[name] = currentType;
                }
            }
            else if (!recorded.ContainsKey(name))
            {
                recorded[name] = DefaultFallbackStartupTypes.TryGetValue(name, out var fb) ? fb : "Automatic";
            }
        }
        SaveRecordedStartupTypes(recorded);
        return recorded;
    }

    internal async Task<DebloatExecutionResult> ExecutePowerShellScriptAsync(string script)
    {
        if (ScriptExecutor != null) return await ScriptExecutor(script);

        return await Task.Run(() =>
        {
            string errFile = Path.Combine(Path.GetTempPath(), $"nitrolite_err_{Guid.NewGuid():N}.log");
            try
            {
                string wrappedScript = $@"
$ErrorActionPreference = 'Stop'
$errLog = '{errFile.Replace("'", "''")}'
try {{
    {script}
}} catch {{
    $_.Exception.ToString() | Out-File -FilePath $errLog -Encoding utf8
    exit 1
}}
";
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"& {{ {wrappedScript} }}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var p = Process.Start(psi);
                if (p == null)
                {
                    return new DebloatExecutionResult { Success = false, ExitCode = -1, ErrorOutput = "PowerShell işlemi başlatılamadı." };
                }

                bool exited = p.WaitForExit(30000);
                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    return new DebloatExecutionResult { Success = false, ExitCode = -2, ErrorOutput = "İşlem zaman aşımına uğradı (30s)." };
                }

                int exitCode = p.ExitCode;
                string? errOutput = null;
                if (File.Exists(errFile))
                {
                    try { errOutput = File.ReadAllText(errFile).Trim(); } catch { }
                }

                return new DebloatExecutionResult
                {
                    Success = exitCode == 0 && string.IsNullOrEmpty(errOutput),
                    ExitCode = exitCode,
                    ErrorOutput = !string.IsNullOrEmpty(errOutput) ? errOutput : (exitCode != 0 ? $"PowerShell işlemi başarısız (Kod: {exitCode})." : null)
                };
            }
            catch (System.ComponentModel.Win32Exception wEx)
            {
                return new DebloatExecutionResult
                {
                    Success = false,
                    ExitCode = wEx.NativeErrorCode,
                    ErrorOutput = wEx.NativeErrorCode == 1223 ? "İşlem iptal edildi (UAC reddedildi)." : $"UAC hatası: {wEx.Message}"
                };
            }
            catch (Exception ex)
            {
                return new DebloatExecutionResult { Success = false, ExitCode = -1, ErrorOutput = ex.Message };
            }
            finally
            {
                try { if (File.Exists(errFile)) File.Delete(errFile); } catch { }
            }
        });
    }

    public async Task<DebloatResult> ApplyTier1DebloatAsync()
    {
        var recorded = RecordPreDebloatStartupTypes();
        var svcs = string.Join(",", Tier1Services.Select(s => $"'{s}'"));
        var script = $@"
$svcs = @({svcs})
$errors = @()
foreach ($s in $svcs) {{
    if (Get-Service -Name $s -ErrorAction SilentlyContinue) {{
        try {{ Stop-Service -Name $s -Force -ErrorAction Stop }} catch {{ $errors += ""Stop $s: $($_.Exception.Message)"" }}
        try {{ Set-Service -Name $s -StartupType Disabled -ErrorAction Stop }} catch {{ $errors += ""Disable $s: $($_.Exception.Message)"" }}
    }}
}}
if (Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction SilentlyContinue) {{
    try {{ Disable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop }} catch {{ $errors += ""DisableTask NitroSenseLauncher: $($_.Exception.Message)"" }}
}}
if ($errors.Count -gt 0) {{
    throw ($errors -join [Environment]::NewLine)
}}
";
        var execResult = await ExecutePowerShellScriptAsync(script);
        return new DebloatResult
        {
            Success = execResult.Success,
            ErrorMessage = execResult.ErrorOutput,
            RecordedStartupTypes = recorded
        };
    }

    public async Task<DebloatResult> RestoreTier1ServicesAsync()
    {
        var recorded = LoadRecordedStartupTypes();
        var pairs = new List<string>();
        foreach (var s in Tier1Services)
        {
            string type = recorded.TryGetValue(s, out var t) ? t : (DefaultFallbackStartupTypes.TryGetValue(s, out var fb) ? fb : "Automatic");
            pairs.Add($"'{s}' = '{type}'");
        }
        var dictScript = string.Join("; ", pairs);

        var script = $@"
$targets = @{{ {dictScript} }}
$errors = @()
foreach ($name in $targets.Keys) {{
    $targetType = $targets[$name]
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) {{
        try {{ Set-Service -Name $name -StartupType $targetType -ErrorAction Stop }} catch {{ $errors += ""Set $name to $targetType: $($_.Exception.Message)"" }}
        if ($targetType -eq 'Automatic') {{
            try {{ Start-Service -Name $name -ErrorAction Stop }} catch {{ $errors += ""Start $name: $($_.Exception.Message)"" }}
        }}
    }}
}}
if (Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction SilentlyContinue) {{
    try {{ Enable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop }} catch {{ $errors += ""EnableTask NitroSenseLauncher: $($_.Exception.Message)"" }}
}}
if ($errors.Count -gt 0) {{
    throw ($errors -join [Environment]::NewLine)
}}
";
        var execResult = await ExecutePowerShellScriptAsync(script);
        return new DebloatResult
        {
            Success = execResult.Success,
            ErrorMessage = execResult.ErrorOutput,
            RecordedStartupTypes = recorded
        };
    }
>>>>
```

---

#### Diff 2: `NitroLite/Core/MemoryCleanerService.cs`
```csharp
<<<<
    public bool ShouldAutoClean(double ramUsagePercent, double ramTotalMB, bool isGameActive = false)
    {
        // Total RAM must be legitimate
        if (ramTotalMB <= 0) return false;

        // Optional game active suppression
        if (isGameActive) return false;

        // Rate-limit auto cleaner to at most once per 120 seconds
        if ((DateTime.Now - _lastAutoCleanTime).TotalSeconds < 120) return false;

        double freeMB = ramTotalMB * (1.0 - (ramUsagePercent / 100.0));
        // Auto-clean only under genuine memory pressure
        return ramUsagePercent >= 88.0 || freeMB <= 2048.0;
    }

    public async Task<MemoryCleanerResult> CleanMemoryAsync(int? excludePid = null, string? excludeProcessName = null)
    {
        return await Task.Run(() => CleanMemory(excludePid, excludeProcessName));
    }

    public MemoryCleanerResult CleanMemory(int? excludePid = null, string? excludeProcessName = null)
    {
        _lastAutoCleanTime = DateTime.Now;
====
    public bool AutoCleanMemory { get; set; } = false;
    public bool IsGameRunning { get; set; } = false;

    public bool ShouldAutoClean(double ramUsagePercent, double ramTotalMB, bool isGameActive = false)
    {
        // Total RAM must be legitimate
        if (ramTotalMB <= 0) return false;

        // Suppress memory cleaning whenever a game is running
        if (isGameActive || IsGameRunning) return false;

        // Rate-limit auto cleaner to at most once per 120 seconds
        if ((DateTime.Now - _lastAutoCleanTime).TotalSeconds < 120) return false;

        double freeMB = ramTotalMB * (1.0 - (ramUsagePercent / 100.0));
        // Auto-clean only under genuine memory pressure
        return ramUsagePercent >= 88.0 || freeMB <= 2048.0;
    }

    public async Task<MemoryCleanerResult> CleanMemoryAsync(int? excludePid = null, string? excludeProcessName = null, bool? isGameRunning = null)
    {
        return await Task.Run(() => CleanMemory(excludePid, excludeProcessName, isGameRunning));
    }

    public MemoryCleanerResult CleanMemory(int? excludePid = null, string? excludeProcessName = null, bool? isGameRunning = null)
    {
        bool gameActive = isGameRunning.GetValueOrDefault(IsGameRunning);
        if (gameActive)
        {
            // Bypassed: Prevent working set trimming and standby cache purging while games load or execute
            var currentMem = new MEMORYSTATUSEX();
            GlobalMemoryStatusEx(currentMem);
            return new MemoryCleanerResult
            {
                FreedMegabytes = 0,
                AvailableGigabytes = Math.Round(currentMem.ullAvailPhys / (1024.0 * 1024.0), 2),
                ProcessesOptimized = 0,
                StandbyPurged = false
            };
        }

        _lastAutoCleanTime = DateTime.Now;
>>>>
```

---

#### Diff 3: `NitroLite/Core/Models.cs`
```csharp
<<<<
public class AppSettings
{
    public bool StartMinimized { get; set; } = false;
    public bool OsdEnabled { get; set; } = false;
    public string SelectedChartMetric { get; set; } = "CPU_TEMP";
    public bool AutoRamCleanerEnabled { get; set; } = true;
====
public class AppSettings
{
    public bool StartMinimized { get; set; } = false;
    public bool OsdEnabled { get; set; } = false;
    public string SelectedChartMetric { get; set; } = "CPU_TEMP";
    
    private bool _autoRamCleanerEnabled = false;

    public bool AutoRamCleanerEnabled
    {
        get => _autoRamCleanerEnabled;
        set => _autoRamCleanerEnabled = value;
    }

    public bool AutoCleanMemory
    {
        get => _autoRamCleanerEnabled;
        set => _autoRamCleanerEnabled = value;
    }
>>>>
```

---

#### Diff 4: `NitroLite/Core/GameDetectionService.cs`
```csharp
<<<<
    public bool IsGameActive
    {
        get
        {
            lock (_stateLock)
            {
                return _activeGameProfile != null;
            }
        }
    }
====
    public bool IsGameActive
    {
        get
        {
            lock (_stateLock)
            {
                return _activeGameProfile != null;
            }
        }
    }

    public bool IsGameRunning => IsGameActive;
>>>>
```

---

#### Diff 5: `NitroLite/MainWindow.xaml.cs`
1. Hook Game Events to sync memory service state:
```csharp
<<<<
        _gameService.GameStarted += OnGameStarted;
        _gameService.GameEnded += OnGameEnded;
====
        _gameService.GameStarted += profile => { _memoryService.IsGameRunning = true; };
        _gameService.GameEnded += profile => { _memoryService.IsGameRunning = false; };
        _gameService.GameStarted += OnGameStarted;
        _gameService.GameEnded += OnGameEnded;
>>>>
```

2. Periodic check:
```csharp
<<<<
            // Optimal Auto RAM Cleaner Check (32 GB RAM optimized)
            if (_appSettings.AutoRamCleanerEnabled && _memoryService.ShouldAutoClean(data.RamUsage, data.RamTotalMB))
            {
                var res = await _memoryService.CleanMemoryAsync();
====
            // Optimal Auto RAM Cleaner Check (32 GB RAM optimized)
            bool isGameRunning = _gameService.IsGameRunning;
            _memoryService.IsGameRunning = isGameRunning;
            if (_appSettings.AutoRamCleanerEnabled && !isGameRunning && _memoryService.ShouldAutoClean(data.RamUsage, data.RamTotalMB, isGameRunning))
            {
                var res = await _memoryService.CleanMemoryAsync(isGameRunning: isGameRunning);
>>>>
```

3. Game Start (lines 1218–1227):
```csharp
<<<<
                    // Automatically purge standby memory when entering game for smooth zero-stutter FPS (async offload)
                    if (_appSettings.AutoRamCleanerEnabled)
                    {
                        var memRes = await _memoryService.CleanMemoryAsync();
                        if (memRes.FreedMegabytes > 150)
                        {
                            string freedStr = memRes.FreedMegabytes >= 1024 ? $"{memRes.FreedMegabytes / 1024.0:F1} GB" : $"{memRes.FreedMegabytes:F0} MB";
                            ShowToast($"{profile.DisplayName}: {freedStr} bellek önbelleği temizlendi.");
                        }
                    }
====
                    // Suppress memory cleaning on game start to prevent working set trimming and standby cache purging while games load or execute
                    _memoryService.IsGameRunning = true;
>>>>
```

4. Surface Debloat & Restore failures:
```csharp
<<<<
            if (confirm == MessageBoxResult.Yes)
            {
                var ok = await _debloatService.ApplyTier1DebloatAsync();
                RefreshServiceList();
                if (ok)
                {
                    ShowToast("Telemetri servisleri devre dışı bırakıldı (~214 MB).");
                }
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Servisler yapılandırılamadı: {ex.Message}", false);
        }
====
            if (confirm == MessageBoxResult.Yes)
            {
                var res = await _debloatService.ApplyTier1DebloatAsync();
                RefreshServiceList();
                if (res.Success)
                {
                    ShowToast("Telemetri servisleri devre dışı bırakıldı (~214 MB).");
                }
                else
                {
                    ShowToast($"Servisler devre dışı bırakılamadı: {res.ErrorMessage ?? "Bilinmeyen hata"}", false);
                }
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Servisler yapılandırılamadı: {ex.Message}", false);
        }
>>>>
```

```csharp
<<<<
        try
        {
            var ok = await _debloatService.RestoreTier1ServicesAsync();
            RefreshServiceList();
            if (ok)
            {
                ShowToast("Varsayılan servis yapılandırması geri yüklendi.");
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Servisler geri yüklenemedi: {ex.Message}", false);
        }
====
        try
        {
            var res = await _debloatService.RestoreTier1ServicesAsync();
            RefreshServiceList();
            if (res.Success)
            {
                ShowToast("Varsayılan servis yapılandırması geri yüklendi.");
            }
            else
            {
                ShowToast($"Servisler geri yüklenemedi: {res.ErrorMessage ?? "Bilinmeyen hata"}", false);
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Servisler geri yüklenemedi: {ex.Message}", false);
        }
>>>>
```

---

### 4.3 Proposed Unit Test Strategy (`NitroLite.Tests`)

#### Test Suite 1: `NitroLite.Tests/DebloatServiceTests.cs` (New File)
Covers all acceptance criteria for R1:
1. `PreDebloatServiceStartupTypes_AreRecordedAccurately`:
   - Configures mock startup types (`AcerServiceSvc` = "Automatic", `ASMSvc` = "Manual", `AcerDIAgentSvis` = "Manual").
   - Runs `ApplyTier1DebloatAsync()`.
   - Reads `debloat_service_states.json` and asserts startup types are recorded verbatim.
2. `RestoreTier1Services_RestoresServicesToRecordedStartupTypesRatherThanUnconditionallyAutomatic`:
   - Pre-seeds recorded types (`ASMSvc` = "Manual", `AcerServiceSvc` = "Automatic").
   - Captures generated PowerShell script via `ScriptExecutor`.
   - Asserts the script sets `ASMSvc` to `Manual` (`Set-Service -Name ASMSvc -StartupType Manual`).
   - Asserts `ASMSvc` is **not** set to `Automatic`.
3. `DebloatOperations_DoNotSuppressPowerShellErrors_AndSurfaceFailures`:
   - Configures `ScriptExecutor` to simulate script failure (`ExitCode = 1`, `ErrorOutput = "Access is denied"`).
   - Executes `ApplyTier1DebloatAsync()`.
   - Asserts `result.Success == false` and `result.ErrorMessage` contains `"Access is denied"`.
   - Configures `ScriptExecutor` to simulate failure on `RestoreTier1ServicesAsync()`.
   - Asserts `result.Success == false` and failure is surfaced.
4. `DebloatPowerShellScript_DoesNotContainSilentlyContinue`:
   - Inspects the captured script strings.
   - Asserts `Stop-Service`, `Set-Service`, and `Disable-ScheduledTask` do not contain `-ErrorAction SilentlyContinue`.

#### Test Suite 2: `NitroLite.Tests/MemoryCleanerServiceTests.cs` (Augmented)
Covers all acceptance criteria for R2:
1. `AutoCleanMemory_DefaultsToFalse_OnBothServiceAndSettings`:
   - `Assert.False(new MemoryCleanerService().AutoCleanMemory);`
   - `Assert.False(new AppSettings().AutoCleanMemory);`
   - `Assert.False(new AppSettings().AutoRamCleanerEnabled);`
2. `ShouldAutoClean_WhenIsGameRunningIsTrue_ReturnsFalse`:
   - Sets `service.IsGameRunning = true`.
   - Under 95% RAM pressure, `Assert.False(service.ShouldAutoClean(95.0, 32768.0));`.
3. `CleanMemory_WhenGameIsRunning_BypassesWorkingSetTrimmingAndStandbyPurging`:
   - Sets `service.IsGameRunning = true`.
   - Calls `service.CleanMemory()`.
   - Asserts `result.FreedMegabytes == 0.0`.
   - Asserts `result.ProcessesOptimized == 0`.
   - Asserts `result.StandbyPurged == false`.
4. `CleanMemoryAsync_WhenGameIsRunning_BypassesExecution`:
   - Calls `await service.CleanMemoryAsync(isGameRunning: true)`.
   - Asserts `result.FreedMegabytes == 0.0 && !result.StandbyPurged`.
5. `GameDetectionService_ExposesIsGameRunning`:
   - `Assert.False(new GameDetectionService().IsGameRunning);`

---

## 5. Verification Method

To independently verify the implementation and findings:

1. **Build Verification**:
   ```powershell
   dotnet build NitroLite.sln
   ```
   *Expected Output*: Build succeeds with 0 errors and 0 warnings.

2. **Automated Unit Test Verification**:
   ```powershell
   dotnet test NitroLite.Tests --filter "FullyQualifiedName~DebloatServiceTests|FullyQualifiedName~MemoryCleanerServiceTests"
   ```
   *Expected Output*: All tests in `DebloatServiceTests` and `MemoryCleanerServiceTests` pass.

3. **Full Regression Suite Verification**:
   ```powershell
   dotnet test NitroLite.Tests
   ```
   *Expected Output*: All existing 322 tests plus newly added R1/R2 tests execute cleanly.

4. **Invalidation Conditions**:
   - If `RestoreTier1ServicesAsync` sets `ASMSvc` or other manual services to `Automatic`, the restoration verification fails.
   - If PowerShell errors are caught and swallowed with `return false` or `ShowToast` is omitted on error, error propagation verification fails.
   - If `new AppSettings().AutoCleanMemory` or `new MemoryCleanerService().AutoCleanMemory` returns `true`, the default state verification fails.
   - If `CleanMemory` executes `EmptyWorkingSet` or `NtSetSystemInformation` when `IsGameRunning = true`, the game guard verification fails.
