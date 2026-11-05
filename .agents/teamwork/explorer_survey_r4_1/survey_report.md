# Technical Survey Report: DebloatService & SettingsManager Audit and Remediation

**Target System**: NitroLite (.NET 10.0 WPF Acer Nitro Control Center)  
**Investigating Agent**: `explorer_survey_r4_1`  
**Working Directory**: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_r4_1`  
**Date**: 2026-11-04  
**Classification**: Read-Only Investigation & Remediation Architecture Specification  

---

## Executive Summary

This survey report provides a forensic code analysis, root-cause investigation, and concrete remediation architecture for four critical security, state-resilience, and consistency defects in `NitroLite/Core/DebloatService.cs`, `NitroLite/Core/SettingsManager.cs`, and `NitroLite/MainWindow.xaml.cs`:

1. **Startup Type Validation in DebloatService (`DebloatService.cs:188`)**: Elevated PowerShell script generation directly interpolates unvalidated startup type strings deserialized from `debloat_backup_state.json`, presenting an arbitrary command execution vulnerability with Administrator privileges (`runas`).
2. **AcerLightingService Debloat Discrepancy (`DebloatService.cs:19`, `MainWindow.xaml.cs:2571`)**: The UI confirmation dialog promises to disable 6 OEM telemetry services (~214 MB) with zero hardware control impact, while `Tier1Services` contains 7 services including `AcerLightingService`. Disabling `AcerLightingService` disables keyboard RGB lighting controls and dynamic animations.
3. **Resilient Service Restoration in DebloatService (`DebloatService.cs:271`)**: Backup persistence failures silently proceed to stop and disable services; unrecorded services in the backup state dictionary are unconditionally forced to `Automatic` and started; and neither pre-debloat service running states (`Running` vs `Stopped`) nor scheduled task states (`NitroSenseLauncher`) are captured.
4. **SettingsManager Backup Corruption (`SettingsManager.cs:62`)**: When recovering from a malformed or corrupted `settings.json`, `SaveSettingsAtomic` unconditionally copies the corrupt primary file over `settings.json.bak`, permanently destroying valid historical backup settings.

---

## 1. Startup Type Validation in DebloatService

### 1.1 Affected Code Location
- **File**: `NitroLite/Core/DebloatService.cs`
- **Lines**: 178–217 (specifically lines 188–208)

```csharp
188: foreach (var s in Tier1Services)
189: {
190:     string targetType = "Automatic";
191:     if (backupState != null && backupState.TryGetValue(s, out var recordedType) && !string.IsNullOrWhiteSpace(recordedType))
192:     {
193:         targetType = recordedType;
194:     }
195: 
196:     sb.Append("try { ");
197:     sb.Append($"$svc = Get-Service -Name '{s}' -ErrorAction SilentlyContinue; ");
198:     sb.Append("if ($svc) { ");
199:     sb.Append($"Set-Service -Name '{s}' -StartupType {targetType} -ErrorAction Stop; ");
200:     if (targetType.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
201:     {
202:         sb.Append($"Start-Service -Name '{s}' -ErrorAction Stop; ");
203:     }
204:     sb.Append("} ");
205:     sb.Append("} catch { ");
206:     sb.Append($"$errs += ('{s}: ' + $_.Exception.Message); ");
207:     sb.Append("}; ");
208: }
```

### 1.2 Root Cause & Attack Vector Analysis
- **Data Flow**: `LoadBackupState()` reads `debloat_backup_state.json` (or accepts a caller-supplied dictionary) and deserializes it into `Dictionary<string, string>`. In `GenerateRestoreScript`, `recordedType` is extracted and directly interpolated into the PowerShell string at line 199: `Set-Service -Name '{s}' -StartupType {targetType} -ErrorAction Stop;`.
- **Privilege Escalation & Arbitrary Code Execution**: In `RunElevatedPowerShellAsync` (lines 219–268), the script is Base64-encoded (`-EncodedCommand`) and executed via `powershell.exe` with `Verb = "runas"` (elevated Administrator privileges).
- **Vulnerability Scenario**: If `debloat_backup_state.json` is modified by an attacker, non-privileged user, or compromised subprocess to contain:
  ```json
  {
    "AcerServiceSvc": "Automatic; Start-Process cmd.exe -ArgumentList '/c net localgroup administrators Attacker /add' #"
  }
  ```
  The generated elevated script executes:
  ```powershell
  Set-Service -Name 'AcerServiceSvc' -StartupType Automatic; Start-Process cmd.exe -ArgumentList '/c net localgroup administrators Attacker /add' # -ErrorAction Stop;
  ```
  This immediately grants full Administrator rights to the attacker without user consent beyond the initial standard UAC prompt.
- **Corrupted Input Failure**: Even without malicious intent, any corrupted or unrecognized string (e.g., `"Disabled; Foo"`, `"Unknown"`, `"Delayed"`, or whitespace) causes PowerShell's `Set-Service -StartupType` parameter binding to fail with an unhandled command error.

### 1.3 Valid Startup Types Specification
Under Windows Service Control Manager (SCM) and the PowerShell `Microsoft.PowerShell.Commands.ServiceStartupType` enumeration, the valid startup types are:
- `Automatic`
- `Manual`
- `Disabled`

In Windows SCM registry keys (`HKLM\SYSTEM\CurrentControlSet\Services\<Name>\Start`), these correspond directly to integer values:
- `2` = `Automatic`
- `3` = `Manual`
- `4` = `Disabled`

### 1.4 Recommended Implementation Strategy
1. **Strict Whitelist Definition**:
   Define an immutable, case-insensitive whitelist of permissible startup types:
   ```csharp
   public static readonly HashSet<string> AllowedStartupTypes = new(StringComparer.OrdinalIgnoreCase)
   {
       "Automatic",
       "Manual",
       "Disabled"
   };

   public static bool IsValidStartupType(string? startupType)
   {
       return !string.IsNullOrWhiteSpace(startupType) && AllowedStartupTypes.Contains(startupType.Trim());
   }
   ```
2. **Rejection Contract**:
   If an entry in `backupState` contains an invalid, unverified, or malformed startup type string, `GenerateRestoreScript` must reject it immediately by throwing an `ArgumentException`. This completely prevents script generation and ensures malicious or corrupt tokens are never passed to the elevated PowerShell runner.
3. **Canonical Normalization**:
   To eliminate any possibility of injection via whitespace, casing, or escape sequences, map the verified value to its canonical enum string representation (`"Automatic"`, `"Manual"`, or `"Disabled"`).

---

## 2. AcerLightingService Debloat Discrepancy

### 2.1 Affected Code Locations
- **File**: `NitroLite/Core/DebloatService.cs` (lines 19–28)
- **File**: `NitroLite/MainWindow.xaml.cs` (lines 2571–2573)

```csharp
// DebloatService.cs:19-28
public static readonly string[] Tier1Services = new[]
{
    "AcerServiceSvc",
    "ASMSvc",
    "AcerDIAgentSvis",
    "AcerEZSvc",
    "AcerDeviceInfoAgentService",
    "AcerDeviceEnablingServiceV2",
    "AcerLightingService"
};
```

```csharp
// MainWindow.xaml.cs:2571-2573
var confirm = System.Windows.MessageBox.Show(this, 
    "Altı adet OEM telemetri servisi devre dışı bırakılacak ve yaklaşık 214 MB bellek tasarrufu sağlanacaktır.\n\nDonanım kontrolleri etkilenmeyecektir. Devam edilsin mi?",
    "Servis Yapılandırması", MessageBoxButton.YesNo, MessageBoxImage.Question);
```

### 2.2 Forensic Architectural Evidence
Ground truth in `scripts/research/nitro_sense_analysis.md` documents the exact service breakdown and memory footprint:

| Metric / Item | Hardware Analysis Specification (`nitro_sense_analysis.md`) | Current Code Implementation |
|---|---|---|
| **Tier 1 Services Count** | **6 Services** (Section 1.3, Line 131) | **7 Services** (including `AcerLightingService`) |
| **Tier 1 Services List** | `AcerServiceSvc`, `ASMSvc`, `AcerDIAgentSvis`, `AcerEZSvc`, `AcerDeviceInfoAgentService`, `AcerDeviceEnablingServiceV2` | Above 6 services **PLUS** `AcerLightingService` |
| **Physical Working Set Savings** | **213.51 MB** (rounds to **214 MB**) | **~252.62 MB** (213.51 MB + 39.11 MB from `AcerLightingService`) |
| **Hardware Feature Impact** | Section 1.4: "Keyboard Lighting / 4-Zone RGB: 4-zone static colors, dynamic lighting animations, and backlight idle timeouts remain completely intact via `AcerLightingService` and `OpenRGB.exe`." | Disabling `AcerLightingService` halts the RGB lighting daemon and breaks dynamic lighting/timeouts. |
| **Subsystem Classification** | Section 4.3 (Line 485): "`AcerLightingService`: **CRITICAL: Keyboard RGB Lighting Engine**. Listens on port 6742. Communicates directly with keyboard EC and USB HID controller (`SunrexUSBKeyboard.dll`)." | Mislabeled as a "telemetry service" in `Tier1Services`. |
| **Authoritative Warning** | Section 4.3 (Line 728): "**CRITICAL WARNING**: In `services.msc`, leave `Acer Agent Service` (`AASSvc`) and `Acer Lighting Service` (`AcerLightingService`) set to **Automatic / Running**. Do not modify them." | Unconditionally disabled when applying debloat. |

### 2.3 Evaluation of Approaches

#### Approach A: Exclude `AcerLightingService` from Tier 1 Debloat (RECOMMENDED)
- **Mechanism**: Remove `"AcerLightingService"` from `Tier1Services` in `DebloatService.cs:19-28`.
- **Consistency**:
  - The service array count matches the UI confirmation dialog: exactly 6 services ("Altı adet").
  - The memory savings estimate is exact: 213.51 MB (~214 MB).
  - The UI guarantee ("Donanım kontrolleri etkilenmeyecektir" / "Hardware controls will not be affected") is 100% truthful, as 4-zone keyboard RGB backlighting, dynamic lighting effects, and idle timeout management remain fully operational.
- **Lines to Change**:
  - `NitroLite/Core/DebloatService.cs`: lines 19–28.

#### Approach B: Retain `AcerLightingService` in Debloat and Update UI/Documentation
- **Mechanism**: Keep `AcerLightingService` in `Tier1Services`, but rewrite `MainWindow.xaml.cs` lines 2571–2573, line 2581, and associated documentation:
  - Update dialog: "Yedi adet OEM servisi devre dışı bırakılacak ve yaklaşık 253 MB bellek tasarrufu sağlanacaktır.\n\nUYARI: Klavye RGB aydınlatma kontrolü devre dışı bırakılacaktır. Devam edilsin mi?"
  - Update toast: `Telemetri ve aydınlatma servisleri devre dışı bırakıldı (~253 MB).`
- **Drawbacks**: Disabling `AcerLightingService` impairs core hardware functionality expected by users of an Acer Nitro gaming notebook, violating NitroLite's mission as a seamless NitroSense replacement.

### 2.4 Conclusion & Recommendation
Approach A is the strictly correct architectural choice. `AcerLightingService` was erroneously grouped with OEM telemetry bloatware, despite being an essential hardware service for keyboard lighting.

---

## 3. Resilient Service Restoration in DebloatService

### 3.1 Affected Code Locations
- **File**: `NitroLite/Core/DebloatService.cs`
- **Lines**: 134–149 (`SaveBackupState`), 188–217 (`GenerateRestoreScript`), 271–291 (`ApplyTier1DebloatAsync`)

```csharp
134: public void SaveBackupState(Dictionary<string, string> state)
135: {
136:     try
137:     {
138:         var dir = Path.GetDirectoryName(BackupStateFilePath);
139:         if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
140:         {
141:             Directory.CreateDirectory(dir);
142:         }
143:         var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
144:         var tmp = BackupStateFilePath + ".tmp";
145:         File.WriteAllText(tmp, json);
146:         File.Move(tmp, BackupStateFilePath, overwrite: true);
147:     }
148:     catch { }
149: }
```

```csharp
271: public async Task<bool> ApplyTier1DebloatAsync()
272: {
273:     // 1. Record current startup types before modifying
274:     var currentTypes = RecordCurrentStartupTypes();
275:     var existingBackup = LoadBackupState();
276: 
277:     // Merge: keep original pre-debloat types if already saved, add newly detected
278:     foreach (var kvp in currentTypes)
279:     {
280:         if (!existingBackup.ContainsKey(kvp.Key))
281:         {
282:             existingBackup[kvp.Key] = kvp.Value;
283:         }
284:     }
285:     SaveBackupState(existingBackup);
286: 
287:     // 2. Generate and run debloat script with error logging
288:     var errLog = Path.Combine(Path.GetTempPath(), "nitrolite_debloat_err.log");
289:     var script = GenerateDebloatScript(existingBackup, errLog);
290:     return await RunElevatedPowerShellAsync(script, errLog);
291: }
```

### 3.2 Detailed Root Cause Analysis

#### Flaw 3A: Silent Persistence Failure & Unchecked Service Shutdown
- In `SaveBackupState`, any exception during JSON serialization, directory creation, or file writing/moving is caught and ignored (`catch { }`).
- `SaveBackupState` returns `void`. `ApplyTier1DebloatAsync` has no visibility into whether backup persistence succeeded.
- If the backup write fails (e.g. disk full, permission denied, locked file), `ApplyTier1DebloatAsync` proceeds directly to line 290 and shuts down/disables all services. The user's prior configuration is permanently lost, making restoration impossible.

#### Flaw 3B: Unrecorded Services Unconditionally Forced to Automatic
- In `GenerateRestoreScript` (lines 190–204):
  ```csharp
  string targetType = "Automatic";
  if (backupState != null && backupState.TryGetValue(s, out var recordedType) && !string.IsNullOrWhiteSpace(recordedType))
  {
      targetType = recordedType;
  }
  ...
  Set-Service -Name '{s}' -StartupType {targetType} -ErrorAction Stop;
  if (targetType.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
  {
      Start-Service -Name '{s}' -ErrorAction Stop;
  }
  ```
- If a service in `Tier1Services` is not present in `backupState` (or if `backupState` is missing/empty), `targetType` defaults to `"Automatic"`.
- As a result, services that were never installed, services that were disabled by the OEM/user before NitroLite was installed, or services omitted from the backup are unconditionally forced to `Automatic` and started.

#### Flaw 3C: Failure to Record Running States and Scheduled Task States
- `RecordCurrentStartupTypes` only queries the registry `Start` value (`2`, `3`, `4`). It records startup type, but does NOT capture:
  1. **Service Running State**: Whether the service was actively `Running`, `Stopped`, or `Paused` prior to debloat. On restore, any service restored to `Automatic` is unconditionally started (`Start-Service`), even if it was stopped prior to debloat.
  2. **Scheduled Task State**: `GenerateDebloatScript` unconditionally disables the `NitroSenseLauncher` task (`Disable-ScheduledTask -TaskName 'NitroSenseLauncher'`), and `GenerateRestoreScript` unconditionally re-enables it (`Enable-ScheduledTask -TaskName 'NitroSenseLauncher'`), completely ignoring whether the task was originally `Ready` or `Disabled` by the user prior to debloat.

### 3.3 Recommended Implementation Strategy

1. **Abort Service Shutdown on Backup Write Failure**:
   - Change `SaveBackupState` return type to `bool`.
   - In `ApplyTier1DebloatAsync`:
     ```csharp
     if (!SaveBackupState(existingBackup))
     {
         LastError = "Debloat aborted: failed to persist service backup state to disk.";
         return false;
     }
     ```
   - If persistence fails, zero services are touched, and an informative error is returned.

2. **Omit Unrecorded Services from Restoration**:
   - In `GenerateRestoreScript`, if a service `s` is not present in `backupState`, skip it entirely:
     ```csharp
     if (backupState == null || !backupState.TryGetValue(s, out var recordedType) || string.IsNullOrWhiteSpace(recordedType))
     {
         continue; // Do not force missing backup services to Automatic
     }
     ```

3. **Capture Service Running States & Scheduled Task States**:
   - Add `RecordCurrentServiceRunningStates()` using native `sc.exe query "<name>"` or `System.Management` (`Win32_Service.State`).
   - Add `RecordCurrentScheduledTaskState(string taskName)` using native `schtasks.exe /query /tn "<taskName>" /fo csv`.
   - Store these in `debloat_backup_state.json` using backward-compatible dictionary keys:
     - Service startup type: `"<ServiceName>"` -> `"Automatic" | "Manual" | "Disabled"`
     - Service running state: `"<ServiceName>:Status"` -> `"Running" | "Stopped"`
     - Scheduled task state: `"Task:NitroSenseLauncher"` -> `"Ready" | "Disabled"`
   - In `GenerateRestoreScript`:
     - Only start services if `backupState` indicates the service was `Running` prior to debloat (or legacy fallback where status key is absent and startup type is `Automatic`).
     - Only re-enable `NitroSenseLauncher` if `backupState["Task:NitroSenseLauncher"]` was `Ready`/`Enabled`.

---

## 4. SettingsManager Backup Corruption

### 4.1 Affected Code Location
- **File**: `NitroLite/Core/SettingsManager.cs`
- **Lines**: 62–87 (`SaveSettingsAtomic`)

```csharp
62: public static void SaveSettingsAtomic(string filePath, AppSettings settings)
63: {
64:     var dir = Path.GetDirectoryName(filePath);
65:     if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
66:     {
67:         Directory.CreateDirectory(dir);
68:     }
69: 
70:     var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
71:     string tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
72:     string backupPath = filePath + ".bak";
73: 
74:     File.WriteAllText(tempPath, json);
75: 
76:     // Maintain backup of previous valid settings
77:     if (File.Exists(filePath))
78:     {
79:         try
80:         {
81:             File.Copy(filePath, backupPath, overwrite: true);
82:         }
83:         catch { }
84:     }
85: 
86:     File.Move(tempPath, filePath, overwrite: true);
87: }
```

### 4.2 Root Cause Analysis: The Corrupted Backup Cascading Failure
1. **Initial Fault**: `settings.json` is corrupted (e.g. truncated write, zero bytes, invalid JSON syntax: `"{ INVALID JSON !!!"`).
2. **Fallback Loading**: Application calls `LoadSettingsWithFallback("settings.json")`.
   - Step 1: Deserializing `settings.json` throws `JsonException`. Caught silently.
   - Step 2: Deserializing `settings.json.bak` succeeds, loading the user's previously valid settings (or step 3 loads `config.json`).
3. **Trigger**: Application modifies a setting or saves upon startup/shutdown, calling `SaveSettingsAtomic("settings.json", loadedSettings)`.
4. **Catastrophic Backup Destruction (Line 77–84)**:
   - `if (File.Exists(filePath))` evaluates to `true` because `settings.json` is on disk.
   - Line 81 executes: `File.Copy(filePath, backupPath, overwrite: true);`.
   - **`File.Copy` overwrites `settings.json.bak` with the corrupt content of `settings.json`!**
   - The comment at line 76 explicitly claims: `// Maintain backup of previous valid settings`, but the implementation never checks if `filePath` is valid.
   - The only valid historical backup that saved the user during step 2 is now permanently overwritten with corrupt data.

### 4.3 Recommended Implementation Strategy
1. **Pre-Copy Validation**:
   Introduce a fast, non-allocating validation check `IsValidSettingsFile(string path)`:
   ```csharp
   public static bool IsValidSettingsFile(string path)
   {
       try
       {
           if (!File.Exists(path)) return false;
           var text = File.ReadAllText(path);
           if (string.IsNullOrWhiteSpace(text)) return false;
           var parsed = JsonSerializer.Deserialize<AppSettings>(text);
           return parsed != null;
       }
       catch
       {
           return false;
       }
   }
   ```
2. **Guarded Backup Copy**:
   In `SaveSettingsAtomic`:
   ```csharp
   // Maintain backup of previous valid settings (never overwrite with corrupt data)
   if (File.Exists(filePath) && IsValidSettingsFile(filePath))
   {
       try
       {
           File.Copy(filePath, backupPath, overwrite: true);
       }
       catch { }
   }
   ```
   If `filePath` is malformed, `File.Copy` is skipped. The valid `backupPath` (.bak) remains untouched, while `tempPath` safely replaces the corrupted `filePath` with freshly serialized valid settings.

---

## 5. Concrete Proposed Code Modifications

### 5.1 Proposed Diff for `NitroLite/Core/DebloatService.cs`

```diff
--- a/NitroLite/Core/DebloatService.cs
+++ b/NitroLite/Core/DebloatService.cs
@@ -19,8 +19,7 @@ public class DebloatService
     {
         "AcerServiceSvc",
         "ASMSvc",
         "AcerDIAgentSvis",
         "AcerEZSvc",
         "AcerDeviceInfoAgentService",
-        "AcerDeviceEnablingServiceV2",
-        "AcerLightingService"
+        "AcerDeviceEnablingServiceV2"
     };
 
+    public static readonly HashSet<string> AllowedStartupTypes = new(StringComparer.OrdinalIgnoreCase)
+    {
+        "Automatic",
+        "Manual",
+        "Disabled"
+    };
+
+    public static bool IsValidStartupType(string? startupType)
+    {
+        return !string.IsNullOrWhiteSpace(startupType) && AllowedStartupTypes.Contains(startupType.Trim());
+    }
+
     public Dictionary<string, string> RecordCurrentStartupTypes()
@@ -118,6 +134,57 @@ public class DebloatService
         return currentMap;
     }
 
+    public Dictionary<string, string> RecordCurrentServiceRunningStates()
+    {
+        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
+        foreach (var name in Tier1Services)
+        {
+            try
+            {
+                using var p = Process.Start(new ProcessStartInfo
+                {
+                    FileName = "sc.exe",
+                    Arguments = $"query \"{name}\"",
+                    RedirectStandardOutput = true,
+                    UseShellExecute = false,
+                    CreateNoWindow = true
+                });
+                if (p != null)
+                {
+                    var output = p.StandardOutput.ReadToEnd();
+                    p.WaitForExit(2000);
+                    if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
+                        map[name] = "Running";
+                    else if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
+                        map[name] = "Stopped";
+                }
+            }
+            catch { }
+        }
+        return map;
+    }
+
+    public string RecordCurrentScheduledTaskState(string taskName = "NitroSenseLauncher")
+    {
+        try
+        {
+            using var p = Process.Start(new ProcessStartInfo
+            {
+                FileName = "schtasks.exe",
+                Arguments = $"/query /tn \"{taskName}\" /fo csv",
+                RedirectStandardOutput = true,
+                UseShellExecute = false,
+                CreateNoWindow = true
+            });
+            if (p != null)
+            {
+                var output = p.StandardOutput.ReadToEnd();
+                p.WaitForExit(2000);
+                if (output.Contains("Ready", StringComparison.OrdinalIgnoreCase)) return "Ready";
+                if (output.Contains("Disabled", StringComparison.OrdinalIgnoreCase)) return "Disabled";
+            }
+        }
+        catch { }
+        return "Unknown";
+    }
+
     public Dictionary<string, string> LoadBackupState()
@@ -134,7 +201,7 @@ public class DebloatService
-    public void SaveBackupState(Dictionary<string, string> state)
+    public bool SaveBackupState(Dictionary<string, string> state)
     {
         try
         {
@@ -146,8 +213,14 @@ public class DebloatService
             var tmp = BackupStateFilePath + ".tmp";
             File.WriteAllText(tmp, json);
             File.Move(tmp, BackupStateFilePath, overwrite: true);
+            return File.Exists(BackupStateFilePath);
         }
-        catch { }
+        catch (Exception ex)
+        {
+            LastError = $"Failed to save backup state: {ex.Message}";
+            return false;
+        }
     }
 
@@ -188,14 +261,31 @@ public class DebloatService
         foreach (var s in Tier1Services)
         {
-            string targetType = "Automatic";
-            if (backupState != null && backupState.TryGetValue(s, out var recordedType) && !string.IsNullOrWhiteSpace(recordedType))
+            // Do NOT force missing backup services to Automatic! Skip if unrecorded.
+            if (backupState == null || !backupState.TryGetValue(s, out var recordedType) || string.IsNullOrWhiteSpace(recordedType))
             {
-                targetType = recordedType;
+                continue;
+            }
+
+            // Validate startup type to prevent command injection
+            if (!IsValidStartupType(recordedType))
+            {
+                throw new ArgumentException($"Invalid or unverified startup type '{recordedType}' for service '{s}'.", nameof(backupState));
+            }
+            string canonicalType = AllowedStartupTypes.First(t => string.Equals(t, recordedType.Trim(), StringComparison.OrdinalIgnoreCase));
+
+            bool shouldStart = false;
+            if (backupState.TryGetValue(s + ":Status", out var recordedStatus))
+            {
+                shouldStart = string.Equals(recordedStatus, "Running", StringComparison.OrdinalIgnoreCase);
+            }
+            else
+            {
+                shouldStart = string.Equals(canonicalType, "Automatic", StringComparison.OrdinalIgnoreCase);
             }
 
             sb.Append("try { ");
             sb.Append($"$svc = Get-Service -Name '{s}' -ErrorAction SilentlyContinue; ");
             sb.Append("if ($svc) { ");
-            sb.Append($"Set-Service -Name '{s}' -StartupType {targetType} -ErrorAction Stop; ");
-            if (targetType.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
+            sb.Append($"Set-Service -Name '{s}' -StartupType {canonicalType} -ErrorAction Stop; ");
+            if (shouldStart)
             {
                 sb.Append($"Start-Service -Name '{s}' -ErrorAction Stop; ");
             }
@@ -210,10 +300,16 @@ public class DebloatService
-        sb.Append("try { ");
-        sb.Append("  $task = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction SilentlyContinue; ");
-        sb.Append("  if ($task) { Enable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction SilentlyContinue } ");
-        sb.Append("} catch {}; ");
+        if (backupState != null && backupState.TryGetValue("Task:NitroSenseLauncher", out var taskState))
+        {
+            if (string.Equals(taskState, "Ready", StringComparison.OrdinalIgnoreCase) || string.Equals(taskState, "Enabled", StringComparison.OrdinalIgnoreCase))
+            {
+                sb.Append("try { ");
+                sb.Append("  $task = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction SilentlyContinue; ");
+                sb.Append("  if ($task) { Enable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction SilentlyContinue } ");
+                sb.Append("} catch {}; ");
+            }
+        }
 
@@ -271,10 +367,29 @@ public class DebloatService
     public async Task<bool> ApplyTier1DebloatAsync()
     {
-        // 1. Record current startup types before modifying
+        // 1. Record current startup types, running states, and scheduled task state before modifying
         var currentTypes = RecordCurrentStartupTypes();
+        var currentStatuses = RecordCurrentServiceRunningStates();
+        var taskState = RecordCurrentScheduledTaskState("NitroSenseLauncher");
+
         var existingBackup = LoadBackupState();
 
         // Merge: keep original pre-debloat types if already saved, add newly detected
         foreach (var kvp in currentTypes)
         {
             if (!existingBackup.ContainsKey(kvp.Key))
             {
                 existingBackup[kvp.Key] = kvp.Value;
             }
         }
+        foreach (var kvp in currentStatuses)
+        {
+            var key = kvp.Key + ":Status";
+            if (!existingBackup.ContainsKey(key))
+            {
+                existingBackup[key] = kvp.Value;
+            }
+        }
+        if (!string.IsNullOrEmpty(taskState) && !existingBackup.ContainsKey("Task:NitroSenseLauncher"))
+        {
+            existingBackup["Task:NitroSenseLauncher"] = taskState;
+        }
 
-        SaveBackupState(existingBackup);
+        // Abort service shutdown if backup state cannot be written
+        if (!SaveBackupState(existingBackup))
+        {
+            LastError = "Debloat aborted: failed to persist service backup state to disk.";
+            return false;
+        }
```

### 5.2 Proposed Diff for `NitroLite/Core/SettingsManager.cs`

```diff
--- a/NitroLite/Core/SettingsManager.cs
+++ b/NitroLite/Core/SettingsManager.cs
@@ -62,6 +62,19 @@ public static class SettingsManager
     public static void SaveSettingsAtomic(string filePath, AppSettings settings)
     {
+        var dir = Path.GetDirectoryName(filePath);
+        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
+        {
+            Directory.CreateDirectory(dir);
+        }
+
+        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
+        string tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
+        string backupPath = filePath + ".bak";
+
+        File.WriteAllText(tempPath, json);
+
+        // Maintain backup of previous valid settings (only copy if filePath contains valid deserializable settings)
+        if (File.Exists(filePath) && IsValidSettingsFile(filePath))
+        {
+            try
+            {
+                File.Copy(filePath, backupPath, overwrite: true);
+            }
+            catch { }
+        }
+
+        File.Move(tempPath, filePath, overwrite: true);
+    }
+
+    public static bool IsValidSettingsFile(string path)
+    {
+        try
+        {
+            if (!File.Exists(path)) return false;
+            var text = File.ReadAllText(path);
+            if (string.IsNullOrWhiteSpace(text)) return false;
+            var parsed = JsonSerializer.Deserialize<AppSettings>(text);
+            return parsed != null;
+        }
+        catch
+        {
+            return false;
+        }
+    }
```

---

## 6. Suggested Unit Test Strategy (`NitroLite.Tests`)

To achieve complete automated regression coverage in `NitroLite.Tests`, add the following targeted unit tests:

### Test 1: Startup Type Validation & Injection Defense
```csharp
[Fact]
public void DebloatService_GenerateRestoreScript_RejectsInvalidStartupTypes()
{
    var debloat = new DebloatService();
    var maliciousState = new Dictionary<string, string>
    {
        { "AcerServiceSvc", "Automatic; Start-Process calc.exe #" }
    };

    var ex = Assert.Throws<ArgumentException>(() => debloat.GenerateRestoreScript(maliciousState));
    Assert.Contains("AcerServiceSvc", ex.Message);

    var invalidState = new Dictionary<string, string>
    {
        { "AcerServiceSvc", "BogusStartupType" }
    };
    Assert.Throws<ArgumentException>(() => debloat.GenerateRestoreScript(invalidState));
}
```

### Test 2: Missing Backup Services Not Forced to Automatic
```csharp
[Fact]
public void DebloatService_GenerateRestoreScript_MissingBackupServices_NotForcedToAutomatic()
{
    var debloat = new DebloatService();
    var partialBackup = new Dictionary<string, string>
    {
        { "AcerServiceSvc", "Manual" }
    };

    var restoreScript = debloat.GenerateRestoreScript(partialBackup);

    // Explicitly recorded service is restored
    Assert.Contains("Set-Service -Name 'AcerServiceSvc' -StartupType Manual", restoreScript);

    // Unrecorded services must NOT be forced to Automatic
    Assert.DoesNotContain("Set-Service -Name 'ASMSvc'", restoreScript);
    Assert.DoesNotContain("Set-Service -Name 'AcerEZSvc'", restoreScript);
    Assert.DoesNotContain("Set-Service -Name 'AcerDIAgentSvis'", restoreScript);
}
```

### Test 3: Abort Service Shutdown on Backup Write Failure
```csharp
[Fact]
public async Task DebloatService_ApplyTier1DebloatAsync_AbortsWhenBackupWriteFails()
{
    var debloat = new DebloatService();
    // Configure invalid path that cannot be written
    debloat.BackupStateFilePath = "Z:\\NonExistentDrive\\forbidden\\backup.json";

    bool result = await debloat.ApplyTier1DebloatAsync();

    Assert.False(result);
    Assert.NotNull(debloat.LastError);
    Assert.Contains("failed to", debloat.LastError, StringComparison.OrdinalIgnoreCase);
}
```

### Test 4: Running State & Scheduled Task State Preservation
```csharp
[Fact]
public void DebloatService_RestoreScript_RespectsRecordedRunningAndTaskStates()
{
    var debloat = new DebloatService();
    var backup = new Dictionary<string, string>
    {
        { "AcerServiceSvc", "Automatic" },
        { "AcerServiceSvc:Status", "Stopped" },
        { "ASMSvc", "Automatic" },
        { "ASMSvc:Status", "Running" },
        { "Task:NitroSenseLauncher", "Disabled" }
    };

    var script = debloat.GenerateRestoreScript(backup);

    // AcerServiceSvc was Stopped: set to Automatic but do NOT Start
    Assert.Contains("Set-Service -Name 'AcerServiceSvc' -StartupType Automatic", script);
    Assert.DoesNotContain("Start-Service -Name 'AcerServiceSvc'", script);

    // ASMSvc was Running: set to Automatic and Start
    Assert.Contains("Set-Service -Name 'ASMSvc' -StartupType Automatic", script);
    Assert.Contains("Start-Service -Name 'ASMSvc'", script);

    // Task was Disabled: do NOT Enable
    Assert.DoesNotContain("Enable-ScheduledTask -TaskName 'NitroSenseLauncher'", script);
}
```

### Test 5: AcerLightingService Excluded from Debloat
```csharp
[Fact]
public void DebloatService_Tier1Services_ExcludesAcerLightingService_PreservingKeyboardRgb()
{
    Assert.DoesNotContain("AcerLightingService", DebloatService.Tier1Services);
    Assert.Equal(6, DebloatService.Tier1Services.Length);
}
```

### Test 6: SettingsManager Preserves Valid Backup on Corrupted Settings Recovery
```csharp
[Fact]
public void SettingsManager_SaveAtomic_PreservesValidBackup_WhenRecoveringFromCorruptSettings()
{
    string tempDir = Path.Combine(Path.GetTempPath(), $"settings_corrupt_test_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);
    string primaryPath = Path.Combine(tempDir, "settings.json");
    string backupPath = primaryPath + ".bak";

    try
    {
        // 1. Establish valid historical backup
        var validPreviousSettings = new AppSettings { SelectedChartMetric = "GPU_POWER", AutoRamCleanerEnabled = true };
        File.WriteAllText(backupPath, JsonSerializer.Serialize(validPreviousSettings));

        // 2. Simulate primary file corruption
        File.WriteAllText(primaryPath, "{ CORRUPTED_NON_PARSABLE_JSON_DATA !@#$%^&*() ");

        // 3. Application loads with fallback (recovers from backupPath)
        var loaded = SettingsManager.LoadSettingsWithFallback(primaryPath, backupPath);
        Assert.Equal("GPU_POWER", loaded.SelectedChartMetric);
        Assert.True(loaded.AutoRamCleanerEnabled);

        // 4. Application saves settings
        loaded.SelectedChartMetric = "CPU_TEMP";
        SettingsManager.SaveSettingsAtomic(primaryPath, loaded);

        // 5. Verify backup file was NOT overwritten with the corrupted data
        Assert.True(File.Exists(backupPath));
        string backupContent = File.ReadAllText(backupPath);
        Assert.DoesNotContain("CORRUPTED_NON_PARSABLE_JSON_DATA", backupContent);

        var bakLoaded = SettingsManager.LoadSettingsWithFallback(backupPath);
        Assert.Equal("GPU_POWER", bakLoaded.SelectedChartMetric);
        Assert.True(bakLoaded.AutoRamCleanerEnabled);

        // 6. Verify primary file now contains newly saved valid settings
        var currLoaded = SettingsManager.LoadSettingsWithFallback(primaryPath);
        Assert.Equal("CPU_TEMP", currLoaded.SelectedChartMetric);
    }
    finally
    {
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
}
```
