using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace NitroLite.Core;

public class ToolExecutionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Unified maintenance, repair and optimization tools for Acer Nitro and Windows systems.
/// Replaces fragmented external batch and PowerShell scripts with safe, native, asynchronous actions.
/// </summary>
public static class SystemToolsService
{
    public static Func<string, Task<int>>? ElevatedRunnerSeam { get; set; }

    /// <summary>The line breaks an elevated script's error log is split on.</summary>
    private static readonly char[] ErrorLineBreaks = { '\r', '\n' };

    /// <summary>
    /// Fixes keyboard RGB backlight service (AcerLightingService) registry path and startup type,
    /// then restarts AcerLightingService and AASSvc.
    /// </summary>
    public static async Task<ToolExecutionResult> FixKeyboardLightingAsync()
    {
        string script = @"
$ErrorActionPreference = 'Stop'
$repo = Join-Path $env:windir 'System32\DriverStore\FileRepository'
function Test-AcerSigned([string]$path) {
    try {
        $sig = Get-AuthenticodeSignature -LiteralPath $path -ErrorAction Stop
        if ($sig.Status -ne 'Valid') { return $false }
        if ($null -eq $sig.SignerCertificate) { return $false }
        return ($sig.SignerCertificate.Subject -match 'Acer')
    } catch { return $false }
}
$dirs = Get-ChildItem -Path $repo -Filter 'predatorservice.inf_amd64_*' -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
$lightingExe = $null
foreach ($d in $dirs) {
    $c = Join-Path $d.FullName 'AcerLightingService.exe'
    # The path is written into a SYSTEM service, so only an Acer-signed binary may be used.
    if ((Test-Path -LiteralPath $c -PathType Leaf) -and (Test-AcerSigned $c)) { $lightingExe = $c; break }
}
if (-not $lightingExe) { throw '" + Loc.Ps("AcerLightingService.exe surucu deposunda bulunamadi veya imzasi gecerli degil.") + @"' }

# The previous registry values are kept so a repair that does not end in a running service can be undone.
$svcKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\AcerLightingService'
$oldImage = $null
$oldStart = $null
try {
    $oldImage = (Get-ItemProperty -LiteralPath $svcKey -Name 'ImagePath' -ErrorAction Stop).ImagePath
    $oldStart = (Get-ItemProperty -LiteralPath $svcKey -Name 'Start' -ErrorAction Stop).Start
} catch { }

try {
    Stop-Service -Name 'AASSvc' -Force -ErrorAction SilentlyContinue
    Stop-Service -Name 'AcerLightingService' -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600

    Set-ItemProperty -LiteralPath $svcKey -Name 'ImagePath' -Value $lightingExe -Force
    Set-ItemProperty -LiteralPath $svcKey -Name 'Start' -Value 2 -Type DWord -Force
    Set-Service -Name 'AcerLightingService' -StartupType Automatic -ErrorAction SilentlyContinue

    Start-Service -Name 'AcerLightingService' -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600

    $svc = Get-Service -Name 'AcerLightingService' -ErrorAction SilentlyContinue
    if ($null -eq $svc -or $svc.Status -ne 'Running') {
        throw '" + Loc.Ps("AcerLightingService baslatilamadi.") + @"'
    }
} catch {
    if ($null -ne $oldImage) { try { Set-ItemProperty -LiteralPath $svcKey -Name 'ImagePath' -Value $oldImage -Force } catch { } }
    if ($null -ne $oldStart) { try { Set-ItemProperty -LiteralPath $svcKey -Name 'Start' -Value ([int]$oldStart) -Type DWord -Force } catch { } }
    throw
} finally {
    # AASSvc was stopped above; it must not be left down when the repair fails.
    Start-Service -Name 'AASSvc' -ErrorAction SilentlyContinue
}
exit 0
";
        WriteRepairBackup("fix-lighting", LightingRepairServices, includeMux: false);
        var (ok, err) = await RunElevatedScriptAsync(script, "nl_fix_lighting");
        return new ToolExecutionResult
        {
            Success = ok,
            Message = ok
                ? Loc.T("Klavye aydınlatma servisi (AcerLightingService) başarıyla onarıldı ve başlatıldı.")
                : Loc.T($"Klavye servisi onarılamadı: {err}")
        };
    }

    /// <summary>
    /// Fixes Acer backend services (AASSvc &amp; AcerLightingService) path mismatch in registry,
    /// stops conflicting older instances, and starts updated services.
    /// </summary>
    public static async Task<ToolExecutionResult> FixAcerServicesAsync()
    {
        string script = @"
$ErrorActionPreference = 'Stop'
$repo = Join-Path $env:windir 'System32\DriverStore\FileRepository'
function Test-AcerSigned([string]$path) {
    try {
        $sig = Get-AuthenticodeSignature -LiteralPath $path -ErrorAction Stop
        if ($sig.Status -ne 'Valid') { return $false }
        if ($null -eq $sig.SignerCertificate) { return $false }
        return ($sig.SignerCertificate.Subject -match 'Acer')
    } catch { return $false }
}
$dirs = @(Get-ChildItem -Path $repo -Filter 'predatorservice.inf_amd64_*' -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
# The package the services already run from comes first. The newest folder by date is only a guess — a driver store
# can hold an older package that was touched last — and replacing a working image path with it is the repair that
# breaks a machine that was fine.
function Get-ImageFolder([string]$key) {
    try {
        $p = [Environment]::ExpandEnvironmentVariables([string](Get-ItemProperty -LiteralPath $key -Name 'ImagePath' -ErrorAction Stop).ImagePath).Trim()
        if ($p.StartsWith('""')) { $end = $p.IndexOf('""', 1); if ($end -gt 1) { $p = $p.Substring(1, $end - 1) } else { return $null } }
        elseif ($p -match '^(.+?\.exe)') { $p = $Matches[1] }
        if ($p.StartsWith('\??\')) { $p = $p.Substring(4) }
        return [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($p))
    } catch { return $null }
}
$repoFull = [IO.Path]::GetFullPath($repo).TrimEnd('\')
$preferred = @()
foreach ($k in 'HKLM:\SYSTEM\CurrentControlSet\Services\AASSvc', 'HKLM:\SYSTEM\CurrentControlSet\Services\AcerLightingService') {
    $folder = Get-ImageFolder $k
    if (-not $folder) { continue }
    $match = $dirs | Where-Object { $_.FullName.TrimEnd('\') -ieq $folder.TrimEnd('\') -and $_.Parent.FullName.TrimEnd('\') -ieq $repoFull }
    if ($match -and -not ($preferred | Where-Object { $_.FullName -ieq $match[0].FullName })) { $preferred += $match[0] }
}
$dirs = @($preferred) + @($dirs | Where-Object { $d = $_; -not ($preferred | Where-Object { $_.FullName -ieq $d.FullName }) })
$centralExe = $null
$lightingExe = $null
foreach ($d in $dirs) {
    $c1 = Join-Path $d.FullName 'AcerCentralService.exe'
    $c2 = Join-Path $d.FullName 'AcerLightingService.exe'
    # These paths become SYSTEM service images, so only Acer-signed binaries may be used.
    if ((Test-Path -LiteralPath $c1 -PathType Leaf) -and (Test-Path -LiteralPath $c2 -PathType Leaf) -and
        (Test-AcerSigned $c1) -and (Test-AcerSigned $c2)) {
        $centralExe = $c1
        $lightingExe = $c2
        break
    }
}
if (-not $centralExe) { throw '" + Loc.Ps("Gerekli Acer servis dosyalari (AcerCentralService / AcerLightingService) surucu deposunda bulunamadi veya imzalari gecerli degil.") + @"' }

# Both service images are SYSTEM binaries; the previous values are kept so a failed repair can be undone.
$agentKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\AASSvc'
$lightingKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\AcerLightingService'
$oldAgentImage = $null
$oldAgentStart = $null
$oldLightingImage = $null
$oldLightingStart = $null
try {
    $oldAgentImage = (Get-ItemProperty -LiteralPath $agentKey -Name 'ImagePath' -ErrorAction Stop).ImagePath
    $oldAgentStart = (Get-ItemProperty -LiteralPath $agentKey -Name 'Start' -ErrorAction Stop).Start
} catch { }
try {
    $oldLightingImage = (Get-ItemProperty -LiteralPath $lightingKey -Name 'ImagePath' -ErrorAction Stop).ImagePath
    $oldLightingStart = (Get-ItemProperty -LiteralPath $lightingKey -Name 'Start' -ErrorAction Stop).Start
} catch { }

try {
    Stop-Service -Name 'AASSvc' -Force -ErrorAction SilentlyContinue
    Stop-Service -Name 'AcerLightingService' -Force -ErrorAction SilentlyContinue
    Stop-Process -Name 'AcerAgentService','AcerHardwareService','AcerCentralService','NitroSense' -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600

    Set-ItemProperty -LiteralPath $agentKey -Name 'ImagePath' -Value $centralExe -Force
    Set-ItemProperty -LiteralPath $agentKey -Name 'Start' -Value 2 -Type DWord -Force
    Set-ItemProperty -LiteralPath $lightingKey -Name 'ImagePath' -Value $lightingExe -Force
    Set-ItemProperty -LiteralPath $lightingKey -Name 'Start' -Value 2 -Type DWord -Force

    Set-Service -Name 'AASSvc' -StartupType Automatic -ErrorAction SilentlyContinue
    Set-Service -Name 'AcerLightingService' -StartupType Automatic -ErrorAction SilentlyContinue

    Start-Service -Name 'AASSvc' -ErrorAction Stop
    Start-Sleep -Milliseconds 600
    Start-Service -Name 'AcerLightingService' -ErrorAction Stop
    Start-Sleep -Milliseconds 800

    $agent = Get-Service -Name 'AASSvc' -ErrorAction SilentlyContinue
    if ($null -eq $agent -or $agent.Status -ne 'Running') { throw '" + Loc.Ps("AASSvc baslatilamadi.") + @"' }
    $lighting = Get-Service -Name 'AcerLightingService' -ErrorAction SilentlyContinue
    if ($null -eq $lighting -or $lighting.Status -ne 'Running') { throw '" + Loc.Ps("AcerLightingService baslatilamadi.") + @"' }
} catch {
    if ($null -ne $oldAgentImage) { try { Set-ItemProperty -LiteralPath $agentKey -Name 'ImagePath' -Value $oldAgentImage -Force } catch { } }
    if ($null -ne $oldAgentStart) { try { Set-ItemProperty -LiteralPath $agentKey -Name 'Start' -Value ([int]$oldAgentStart) -Type DWord -Force } catch { } }
    if ($null -ne $oldLightingImage) { try { Set-ItemProperty -LiteralPath $lightingKey -Name 'ImagePath' -Value $oldLightingImage -Force } catch { } }
    if ($null -ne $oldLightingStart) { try { Set-ItemProperty -LiteralPath $lightingKey -Name 'Start' -Value ([int]$oldLightingStart) -Type DWord -Force } catch { } }
    throw
} finally {
    # Both services were stopped above; neither may be left down when the repair fails.
    Start-Service -Name 'AASSvc' -ErrorAction SilentlyContinue
    Start-Service -Name 'AcerLightingService' -ErrorAction SilentlyContinue
}
exit 0
";
        WriteRepairBackup("fix-services", RepairedServices, includeMux: false);
        var (ok, err) = await RunElevatedScriptAsync(script, "nl_fix_services");
        return new ToolExecutionResult
        {
            Success = ok,
            Message = ok
                ? Loc.T("Acer & NitroSense arka plan servisleri başarıyla onarıldı ve güncellendi.")
                : Loc.T($"Acer servisleri onarılamadı: {err}")
        };
    }

    /// <summary>
    /// Restores discrete_gpu_support MUX registry setting and cleans legacy logon tasks.
    /// </summary>
    public static async Task<ToolExecutionResult> RestoreMuxSwitchAsync()
    {
        string script = @"
$ErrorActionPreference = 'Stop'
$regPath = 'HKLM:\SOFTWARE\OEM\AcerAgentService\AdvanceSettings'
# Only an existing Acer Agent key is repaired. Creating it on a machine without a MUX switch would leave a
# setting behind that nothing reads and that no uninstall removes.
if (-not (Test-Path -LiteralPath $regPath)) {
    throw '" + Loc.Ps("Acer Agent ayar anahtari bulunamadi; bu bilgisayarda MUX switch kaydi yok.") + @"'
}
$old = $null
try { $old = (Get-ItemProperty -LiteralPath $regPath -Name 'discrete_gpu_support' -ErrorAction Stop).discrete_gpu_support } catch { }

Set-ItemProperty -LiteralPath $regPath -Name 'discrete_gpu_support' -Value 1 -Type DWord -Force
$written = (Get-ItemProperty -LiteralPath $regPath -Name 'discrete_gpu_support' -ErrorAction Stop).discrete_gpu_support
if ($written -ne 1) {
    if ($null -ne $old) { try { Set-ItemProperty -LiteralPath $regPath -Name 'discrete_gpu_support' -Value ([int]$old) -Type DWord -Force } catch { } }
    throw '" + Loc.Ps("discrete_gpu_support yazilamadi.") + @"'
}

$oldTask = Get-ScheduledTask -TaskName 'AcerNitroSenseMuxRestore' -ErrorAction SilentlyContinue
if ($null -ne $oldTask) {
    Unregister-ScheduledTask -TaskName 'AcerNitroSenseMuxRestore' -Confirm:$false -ErrorAction SilentlyContinue
}

Start-Service -Name 'AcerQAAgentSvis' -ErrorAction SilentlyContinue
Stop-Process -Name 'NitroSense' -Force -ErrorAction SilentlyContinue
exit 0
";
        WriteRepairBackup("restore-mux", Array.Empty<string>(), includeMux: true);
        var (ok, err) = await RunElevatedScriptAsync(script, "nl_restore_mux");
        return new ToolExecutionResult
        {
            Success = ok,
            Message = ok
                ? Loc.T("MUX switch desteği kayıt defterinde etkinleştirildi ve eski görevler temizlendi.")
                : Loc.T($"MUX ayarı onarılamadı: {err}")
        };
    }

    /// <summary>
    /// Where the values the last repair replaced are kept: the service image paths and start types
    /// (<see cref="FixKeyboardLightingAsync"/>, <see cref="FixAcerServicesAsync"/>) and the MUX registry value
    /// (<see cref="RestoreMuxSwitchAsync"/>). <see cref="UndoLastRepairAsync"/> puts them back.
    /// </summary>
    public static string RepairBackupPath => AppPaths.GetDataFile("repair_backup.json");

    /// <summary>The services a repair rewrites, and the only ones the undo may touch.</summary>
    internal static readonly string[] RepairedServices = { "AASSvc", "AcerLightingService" };

    /// <summary>The one service <see cref="FixKeyboardLightingAsync"/> rewrites.</summary>
    private static readonly string[] LightingRepairServices = { "AcerLightingService" };

    private const string MuxRegistryKey = @"SOFTWARE\OEM\AcerAgentService\AdvanceSettings";
    private const string MuxRegistryValue = "discrete_gpu_support";

    /// <summary>Test seam for the registry read behind the repair record: service name to (raw ImagePath, Start).</summary>
    internal static Func<string, (string? ImagePath, int? Start)>? RepairServiceStateSeam { get; set; }

    /// <summary>Test seam for the MUX value read: (key exists, value or null when absent).</summary>
    internal static Func<(bool KeyExists, int? Value)>? RepairMuxStateSeam { get; set; }

    /// <summary>
    /// Records, unelevated and before the prompt, what a repair is about to replace. Written through
    /// <see cref="AtomicFile"/> by this process for the same reason as the optimization record: the elevated script must
    /// not write into a folder the user can redirect. Best effort — a repair is what a broken machine needs, so a record
    /// that cannot be written is logged and the repair still runs. The record is kept when the repair fails: the
    /// script's own rollback leaves the machine in exactly the recorded state, and a script that outlived its timeout
    /// may still finish.
    /// </summary>
    internal static void WriteRepairBackup(string repair, IEnumerable<string> services, bool includeMux)
    {
        try
        {
            var root = new JsonObject
            {
                ["repair"] = repair,
                ["createdUtc"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            };

            var serviceNodes = new JsonObject();
            foreach (var name in services)
            {
                if (!RepairedServices.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                var (imagePath, start) = (RepairServiceStateSeam ?? ReadServiceState)(name);
                if (string.IsNullOrWhiteSpace(imagePath) || start is not int startValue) continue;
                serviceNodes[name] = new JsonObject { ["imagePath"] = imagePath, ["start"] = startValue };
            }

            if (serviceNodes.Count > 0) root["services"] = serviceNodes;

            if (includeMux)
            {
                var (keyExists, value) = (RepairMuxStateSeam ?? ReadMuxState)();
                if (keyExists)
                {
                    root["mux"] = value is int v
                        ? new JsonObject { ["present"] = true, ["value"] = v }
                        : new JsonObject { ["present"] = false };
                }
            }

            if (root["services"] == null && root["mux"] == null) return;

            var path = RepairBackupPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            AtomicFile.WriteAllText(path, root.ToJsonString(JsonDefaults.Indented));
        }
        catch (Exception ex)
        {
            AppLog.Warn("The repair undo record could not be written", ex.Message);
        }
    }

    /// <summary>The raw (unexpanded) ImagePath and the Start value of a service, read without administrator rights.</summary>
    private static (string? ImagePath, int? Start) ReadServiceState(string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            if (key == null) return (null, null);
            var image = key.GetValue("ImagePath", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var start = key.GetValue("Start") is int s ? (int?)s : null;
            return (image, start);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (null, null);
        }
    }

    private static (bool KeyExists, int? Value) ReadMuxState()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MuxRegistryKey);
            if (key == null) return (false, null);
            return (true, key.GetValue(MuxRegistryValue) is int v ? (int?)v : null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Reads a repair record and keeps only what the undo may act on: the two known services with a non-empty image
    /// path and a start type of 2, 3 or 4, and a MUX value of 0 or 1 (or "absent"). Throws when the text is not a JSON
    /// object. The elevated script checks every value again; this only keeps anything else out of its text.
    /// </summary>
    internal static JsonObject ParseRepairBackup(string json)
    {
        var root = JsonNode.Parse(json.TrimStart('﻿')) as JsonObject
                   ?? throw new InvalidDataException("The repair backup is not a JSON object.");

        var clean = new JsonObject();
        if (root["services"] is JsonObject services)
        {
            var kept = new JsonObject();
            foreach (var (name, node) in services)
            {
                if (!RepairedServices.Contains(name, StringComparer.Ordinal)) continue;
                if (node is not JsonObject entry) continue;
                var image = ReadString(entry["imagePath"]);
                int? start = entry["start"] is JsonValue sv && sv.TryGetValue<int>(out var s) ? s : null;
                if (string.IsNullOrWhiteSpace(image) || start is not (2 or 3 or 4)) continue;
                kept[name] = new JsonObject { ["imagePath"] = image, ["start"] = start.Value };
            }

            if (kept.Count > 0) clean["services"] = kept;
        }

        if (root["mux"] is JsonObject mux && mux["present"] is JsonValue pv && pv.TryGetValue<bool>(out var present))
        {
            if (!present) clean["mux"] = new JsonObject { ["present"] = false };
            else if (mux["value"] is JsonValue vv && vv.TryGetValue<int>(out var value) && value is 0 or 1)
                clean["mux"] = new JsonObject { ["present"] = true, ["value"] = value };
        }

        return clean;
    }

    /// <summary>
    /// Puts back what the last repair replaced, from <see cref="RepairBackupPath"/>. An image path is only written back
    /// when it names an existing, Acer-signed executable under the driver store or Program Files — the same bar the
    /// repair itself applies — because the record sits in a folder any same-user process can edit, and the value
    /// becomes a SYSTEM service image.
    /// </summary>
    public static async Task<ToolExecutionResult> UndoLastRepairAsync()
    {
        string path = RepairBackupPath;
        if (!File.Exists(path))
        {
            return new ToolExecutionResult { Success = false, Message = Loc.T($"Geri alınacak onarım kaydı yok: {path}") };
        }

        string data;
        try
        {
            var clean = ParseRepairBackup(BoundedTextFile.ReadAllText(path));
            if (clean.Count == 0)
            {
                return new ToolExecutionResult { Success = false, Message = Loc.T($"Geri alınacak onarım kaydı yok: {path}") };
            }

            data = clean.ToJsonString();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The repair undo record could not be read", $"{path}: {ex.Message}");
            string reason = ex.Message;
            return new ToolExecutionResult { Success = false, Message = Loc.T($"Onarım geri alınamadı: {reason}") };
        }

        var (ok, err) = await RunElevatedScriptAsync(BuildUndoRepairScript(data), "nl_undo_repair");
        if (ok)
        {
            try { File.Delete(path); } catch { }
        }

        return new ToolExecutionResult
        {
            Success = ok,
            Message = ok
                ? Loc.T("Onarım öncesi ayarlar geri yüklendi.")
                : Loc.T($"Onarım geri alınamadı: {err}")
        };
    }

    /// <summary>The elevated undo script. <paramref name="cleanJson"/> is the output of <see cref="ParseRepairBackup"/>.</summary>
    internal static string BuildUndoRepairScript(string cleanJson) => @"
$ErrorActionPreference = 'Stop'
$errs = @()
$backup = '" + Loc.PsQuote(cleanJson) + @"' | ConvertFrom-Json
$knownServices = 'AASSvc', 'AcerLightingService'
# The roots come from the known-folder API, not from $env: — a user's HKCU\Environment can redefine those variables
# for an elevated process, and the roots are what keeps the record from naming any other executable.
$trustedRoots = @((Join-Path ([Environment]::GetFolderPath('Windows')) 'System32\DriverStore\FileRepository'), [Environment]::GetFolderPath('ProgramFiles'), [Environment]::GetFolderPath('ProgramFilesX86')) | Where-Object { $_ }
function Test-AcerSigned([string]$path) {
    try {
        $sig = Get-AuthenticodeSignature -LiteralPath $path -ErrorAction Stop
        if ($sig.Status -ne 'Valid') { return $false }
        if ($null -eq $sig.SignerCertificate) { return $false }
        return ($sig.SignerCertificate.Subject -match 'Acer')
    } catch { return $false }
}
# The record is a file in the user's data folder, so its image path is not trusted: only a plain path (no arguments)
# to an existing, Acer-signed .exe under the driver store or Program Files is accepted.
function Resolve-TrustedImage([string]$image) {
    if ([string]::IsNullOrWhiteSpace($image)) { return $null }
    $p = $image.Trim()
    if ($p.StartsWith('""')) {
        if ($p.Length -lt 3 -or -not $p.EndsWith('""')) { return $null }
        $p = $p.Substring(1, $p.Length - 2)
    }
    if ($p.IndexOf('""') -ge 0) { return $null }
    $p = [Environment]::ExpandEnvironmentVariables($p)
    if (-not [IO.Path]::IsPathRooted($p)) { return $null }
    $full = [IO.Path]::GetFullPath($p)
    if (-not $full.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { return $null }
    $inside = $false
    foreach ($r in $trustedRoots) {
        $root = [IO.Path]::GetFullPath($r).TrimEnd('\') + '\'
        if ($full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $inside = $true }
    }
    if (-not $inside) { return $null }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { return $null }
    if (-not (Test-AcerSigned $full)) { return $null }
    return $full
}

if ($null -ne $backup.services) {
    foreach ($prop in $backup.services.PSObject.Properties) {
        $name = [string]$prop.Name
        if ($knownServices -notcontains $name) { $errs += ($name + ': ' + 'unknown service'); continue }
        try {
            $full = Resolve-TrustedImage ([string]$prop.Value.imagePath)
            if ($null -eq $full) { throw 'untrusted image path' }
            $start = [int]$prop.Value.start
            if (@(2, 3, 4) -notcontains $start) { throw 'unknown start type' }
            $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $name
            if (-not (Test-Path -LiteralPath $key)) { throw 'service not installed' }
            Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
            # Quoted, so a path with spaces cannot be read as a shorter executable plus arguments.
            Set-ItemProperty -LiteralPath $key -Name 'ImagePath' -Value ('""' + $full + '""') -Force
            Set-ItemProperty -LiteralPath $key -Name 'Start' -Value $start -Type DWord -Force
            if ($start -eq 2) { Start-Service -Name $name -ErrorAction SilentlyContinue }
        } catch { $errs += ($name + ': ' + $_.Exception.Message) }
    }
}

if ($null -ne $backup.mux) {
    try {
        $regPath = 'HKLM:\SOFTWARE\OEM\AcerAgentService\AdvanceSettings'
        if (Test-Path -LiteralPath $regPath) {
            if ($backup.mux.present -eq $true) {
                $v = [int]$backup.mux.value
                if (@(0, 1) -notcontains $v) { throw 'unknown value' }
                Set-ItemProperty -LiteralPath $regPath -Name 'discrete_gpu_support' -Value $v -Type DWord -Force
            } else {
                Remove-ItemProperty -LiteralPath $regPath -Name 'discrete_gpu_support' -ErrorAction SilentlyContinue
            }
        }
    } catch { $errs += ('MUX: ' + $_.Exception.Message) }
}

" + ElevatedErrorLog.WriteStatement + @"
";

    /// <summary>Where the previous values of every setting OptimizePcAsync changes are stored.</summary>
    public static string OptimizeBackupPath => AppPaths.GetDataFile("optimize_backup.json");

    /// <summary>The services <see cref="OptimizePcAsync"/> changes, and the only ones the restore acts on.</summary>
    internal static readonly string[] OptimizedServices = { "SysMain", "ClickToRunSvc", "Plastic Server 6" };

    /// <summary>The CEIP tasks <see cref="OptimizePcAsync"/> disables.</summary>
    internal static readonly string[] OptimizedTasks = { "Consolidator", "UsbCeip" };

    /// <summary>The Task Scheduler folder of <see cref="OptimizedTasks"/>.</summary>
    internal const string CeipTaskPath = @"\Microsoft\Windows\Customer Experience Improvement Program\";

    /// <summary>
    /// The machine's state before an optimization: each service's start type (by name), the HwSchMode value (-1 when it
    /// does not exist) and each task's state ("Ready" or "Disabled").
    /// </summary>
    /// <remarks>
    /// <see cref="HagsKnown"/> separates "HwSchMode does not exist" (<c>Hags = -1</c>, known) from "it could not be
    /// read" (unknown). Only a known value may become the undo record: recording an unreadable value as "absent" would
    /// make the undo delete a setting the user had.
    /// </remarks>
    internal sealed record OptimizeSnapshot(
        IReadOnlyDictionary<string, string> Services,
        int Hags,
        IReadOnlyDictionary<string, string> Tasks,
        bool HagsKnown = true);

    /// <summary>The undo record, in the shape <c>optimize_backup.json</c> has always had.</summary>
    internal sealed class OptimizeBackupRecord
    {
        public Dictionary<string, string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>HwSchMode before the first optimization; -1 when it did not exist. Meaningful only when <see cref="HagsRecorded"/>.</summary>
        public int Hags { get; set; } = -1;

        /// <summary>
        /// True once <see cref="Hags"/> holds the machine's real starting value. Kept apart from the value because -1
        /// used to mean both "not recorded yet" and "did not exist": a second run then replaced the original "absent"
        /// with the optimized 2, and the undo wrote 2 instead of deleting the value. In the file, the presence of the
        /// <c>hags</c> field is this flag.
        /// </summary>
        public bool HagsRecorded { get; set; }

        public List<(string Name, string Path, string State)> Tasks { get; } = new();
    }

    /// <summary>Test seam for the unelevated state read, so a test never depends on this machine's services.</summary>
    internal static Func<OptimizeSnapshot>? CurrentStateSeam { get; set; }

    /// <summary>
    /// Applies elevated performance, telemetry debloat and system tuning optimizations. Every value that is
    /// changed is written to <see cref="OptimizeBackupPath"/> first, so <see cref="RestoreOptimizePcAsync"/> can
    /// put the machine back the way it was.
    /// </summary>
    /// <remarks>
    /// The undo record is written here, unelevated, through <see cref="AtomicFile"/>, before the elevation prompt is
    /// shown. The elevated script used to write it itself with <c>Out-File -Force</c> to a fixed path in the user's
    /// data folder — a folder any same-user process can replace with a junction or a link, which turned the write
    /// into "overwrite any file on the machine with administrator rights". The elevated script now writes nothing
    /// into a folder the user can write to; it only receives the list of tasks it may disable.
    /// <para>
    /// Everything the record needs is readable without administrator rights: a service's start type and HwSchMode
    /// from HKLM, a task's state from <c>schtasks /query /xml</c>. A task whose state cannot be read is not recorded,
    /// and is therefore not disabled either: the tool never changes what it could not undo.
    /// </para>
    /// </remarks>
    public static async Task<ToolExecutionResult> OptimizePcAsync()
    {
        string backupPath = OptimizeBackupPath;

        // The undo record is read first and written before the first change. A second run must not replace the
        // original state with the already-optimized one, and a run that is cut short must still be undoable.
        OptimizeBackupRecord record;
        try
        {
            record = File.Exists(backupPath)
                ? ParseOptimizeBackup(BoundedTextFile.ReadAllText(backupPath))
                : new OptimizeBackupRecord();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The optimization backup could not be read", $"{backupPath}: {ex.Message}");
            string reason = Loc.T("Optimizasyon yedeği okunamadı; üzerine yazılmadı.");
            return new ToolExecutionResult
            {
                Success = false,
                Message = Loc.T($"Optimizasyon sırasında hata: {reason}")
            };
        }

        OptimizeSnapshot current;
        try
        {
            // Off the caller's thread: two schtasks queries can take a few seconds on a slow machine.
            current = await Task.Run(CurrentStateSeam ?? ReadCurrentOptimizeState);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The state before the optimization could not be read", ex.Message);
            current = new OptimizeSnapshot(
                new Dictionary<string, string>(), -1, new Dictionary<string, string>(), HagsKnown: false);
        }

        MergeMissing(record, current);

        try
        {
            var directory = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            AtomicFile.WriteAllText(backupPath, SerializeOptimizeBackup(record, indented: true));
        }
        catch (Exception ex)
        {
            AppLog.Warn("The optimization backup could not be written", $"{backupPath}: {ex.Message}");
            string reason = Loc.T("Optimizasyon yedeği yazılamadı; hiçbir ayar değiştirilmedi.") + " " + ex.Message;
            return new ToolExecutionResult
            {
                Success = false,
                Message = Loc.T($"Optimizasyon sırasında hata: {reason}")
            };
        }

        // HAGS is only switched on when its previous value is in the record: the tool never changes what it could not
        // undo.
        string script = BuildOptimizeScript(record.Tasks.Select(t => t.Name), setHags: record.HagsRecorded);
        var (ok, err) = await RunElevatedScriptAsync(script, "nl_optimize_pc");
        return new ToolExecutionResult
        {
            Success = ok,
            Message = ok
                ? Loc.T($"Sistem ve arka plan optimizasyonları başarıyla uygulandı. Önceki ayarlar yedeklendi: {backupPath}")
                : Loc.T($"Optimizasyon sırasında hata: {err}")
        };
    }

    /// <summary>
    /// The elevated optimization script. <paramref name="tasksToDisable"/> is filtered against
    /// <see cref="OptimizedTasks"/>, so nothing but those two constant names can reach the script text.
    /// </summary>
    internal static string BuildOptimizeScript(IEnumerable<string> tasksToDisable, bool setHags = true)
    {
        var wanted = new HashSet<string>(tasksToDisable, StringComparer.OrdinalIgnoreCase);
        var tasks = OptimizedTasks
            .Where(wanted.Contains)
            .Select(name => "'" + Loc.PsQuote(name) + "'");
        string taskList = "@(" + string.Join(", ", tasks) + ")";

        return @"
$ErrorActionPreference = 'Stop'
$errs = @()

# The undo record was written by the unelevated app before this script was started. This script writes nothing
# into the user's data folder.
$gfxPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
$ceip = '" + Loc.PsQuote(CeipTaskPath) + @"'
$idleServices = 'ClickToRunSvc', 'Plastic Server 6'
$tasksToDisable = " + taskList + @"

try {
    $svc = Get-Service -Name 'SysMain' -ErrorAction Stop
    Set-Service -Name 'SysMain' -StartupType Disabled -ErrorAction Stop
    Stop-Service -Name 'SysMain' -Force -ErrorAction Stop
} catch { $errs += ('SysMain: ' + $_.Exception.Message) }

$setHags = " + (setHags ? "$true" : "$false") + @"
try {
    if ($setHags -and (Test-Path -LiteralPath $gfxPath)) {
        Set-ItemProperty -Path $gfxPath -Name 'HwSchMode' -Value 2 -Type DWord -Force -ErrorAction Stop
    }
} catch { $errs += ('HAGS: ' + $_.Exception.Message) }

foreach ($t in $tasksToDisable) {
    try { Disable-ScheduledTask -TaskName $t -TaskPath $ceip -ErrorAction Stop | Out-Null }
    catch { $errs += ($t + ': ' + $_.Exception.Message) }
}

try { Optimize-Volume -DriveLetter C -ReTrim -ErrorAction Stop | Out-Null } catch { $errs += ('TRIM C: ' + $_.Exception.Message) }
if (Test-Path 'D:\') {
    try { Optimize-Volume -DriveLetter D -ReTrim -ErrorAction Stop | Out-Null } catch { $errs += ('TRIM D: ' + $_.Exception.Message) }
}

# Windows Temp cache: top-level files only. Recursing would follow junctions that any user can create
# under C:\Windows\Temp and delete files elsewhere with administrator rights.
try {
    Get-ChildItem -LiteralPath (Join-Path $env:windir 'Temp') -Force -ErrorAction Stop |
        Where-Object { -not $_.PSIsContainer -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
        ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop } catch { } }
} catch { $errs += ('Temp: ' + $_.Exception.Message) }

foreach ($s in $idleServices) {
    try {
        $idle = Get-Service -Name $s -ErrorAction SilentlyContinue
        if ($idle) {
            Stop-Service -Name $s -Force -ErrorAction Stop
            Set-Service -Name $s -StartupType Manual -ErrorAction Stop
        }
    } catch { $errs += ($s + ': ' + $_.Exception.Message) }
}

" + ElevatedErrorLog.WriteStatement + @"
";
    }

    /// <summary>
    /// Adds to <paramref name="record"/> every value it does not hold yet. Values already in it are the machine's
    /// real starting point from an earlier run and are never replaced.
    /// </summary>
    internal static void MergeMissing(OptimizeBackupRecord record, OptimizeSnapshot current)
    {
        foreach (var (name, startType) in current.Services)
        {
            if (!OptimizedServices.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            record.Services.TryAdd(name, startType);
        }

        // Recorded once, from a value that was actually read, and never updated afterwards.
        if (!record.HagsRecorded && current.HagsKnown)
        {
            record.Hags = current.Hags;
            record.HagsRecorded = true;
        }

        foreach (var (name, state) in current.Tasks)
        {
            if (!OptimizedTasks.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (record.Tasks.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            record.Tasks.Add((name, CeipTaskPath, state));
        }
    }

    /// <summary>
    /// Reads an undo record. Accepts both what this class writes and what the elevated script used to write
    /// (<c>ConvertTo-Json</c> may emit a one-element task list as a bare object). Throws when the text is not a
    /// JSON object.
    /// </summary>
    internal static OptimizeBackupRecord ParseOptimizeBackup(string json)
    {
        var root = JsonNode.Parse(json.TrimStart('\uFEFF')) as JsonObject
                   ?? throw new InvalidDataException("The optimization backup is not a JSON object.");

        var record = new OptimizeBackupRecord();

        if (root["services"] is JsonObject services)
        {
            foreach (var (name, value) in services)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var startType))
                    record.Services[name] = startType;
            }
        }

        // A record that carries the field has its value; -1 there is "did not exist", which is also what a file from
        // an older version meant by it.
        if (root["hags"] is JsonValue hags)
        {
            if (hags.TryGetValue<int>(out var number)) { record.Hags = number; record.HagsRecorded = true; }
            else if (hags.TryGetValue<string>(out var text)
                     && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            { record.Hags = parsed; record.HagsRecorded = true; }
        }

        IEnumerable<JsonNode?> taskNodes = root["tasks"] switch
        {
            JsonArray array => array,
            JsonObject single => new[] { single },
            _ => Array.Empty<JsonNode?>()
        };
        foreach (var node in taskNodes)
        {
            if (node is not JsonObject task) continue;
            string? name = ReadString(task["name"]);
            string? path = ReadString(task["path"]);
            string? state = ReadString(task["state"]);
            if (string.IsNullOrEmpty(name)) continue;
            record.Tasks.Add((name, path ?? "", state ?? ""));
        }

        return record;
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Writes an undo record in the shape the restore script reads.</summary>
    internal static string SerializeOptimizeBackup(OptimizeBackupRecord record, bool indented)
    {
        var services = new JsonObject();
        foreach (var (name, startType) in record.Services) services[name] = startType;

        var tasks = new JsonArray();
        foreach (var (name, path, state) in record.Tasks)
        {
            tasks.Add(new JsonObject
            {
                ["name"] = name,
                ["path"] = path,
                ["state"] = state
            });
        }

        var root = new JsonObject { ["services"] = services };

        // Without a recorded value the field is left out, so the restore script does not touch HwSchMode at all.
        if (record.HagsRecorded) root["hags"] = record.Hags;
        root["tasks"] = tasks;
        return indented ? root.ToJsonString(JsonDefaults.Indented) : root.ToJsonString();
    }

    /// <summary>
    /// The current state of everything <see cref="OptimizePcAsync"/> changes, read without administrator rights.
    /// </summary>
    private static OptimizeSnapshot ReadCurrentOptimizeState()
    {
        var services = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in OptimizedServices)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + name);
                // The names Get-Service reports as StartType in Windows PowerShell 5.1, which is what the restore
                // script validates and passes back to Set-Service.
                string? startType = key?.GetValue("Start") switch
                {
                    0 => "Boot",
                    1 => "System",
                    2 => "Automatic",
                    3 => "Manual",
                    4 => "Disabled",
                    _ => null
                };
                if (startType != null) services[name] = startType;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not installed or not readable: not recorded, and the script's own Get-Service check skips it.
            }
        }

        int hags = -1;
        bool hagsKnown = true;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            object? raw = key?.GetValue("HwSchMode");
            if (raw is int value) hags = value;
            else if (raw != null) hagsKnown = false; // present but not a DWORD: not something the undo can put back
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            hagsKnown = false;
        }

        var tasks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in OptimizedTasks)
        {
            var enabled = QueryTaskEnabled(CeipTaskPath + name);
            if (enabled.HasValue) tasks[name] = enabled.Value ? "Ready" : "Disabled";
        }

        return new OptimizeSnapshot(services, hags, tasks, hagsKnown);
    }

    /// <summary>
    /// Whether a scheduled task is enabled, from <c>schtasks /query /xml</c> (the XML is the same in every display
    /// language). Null when the task does not exist, cannot be read or schtasks does not answer in time.
    /// </summary>
    private static bool? QueryTaskEnabled(string taskPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = WindowsPaths.SystemTool("schtasks.exe"),
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }.WithArguments("/query", "/tn", taskPath, "/xml"));
            if (process == null) return null;

            var read = process.StandardOutput.ReadToEndAsync();
            if (!read.Wait(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (!process.WaitForExit(500)) return null;
            return ParseTaskEnabled(read.Result, process.ExitCode);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the enabled flag out of <c>schtasks /query /xml</c> output. Null when schtasks failed or the output is not
    /// a task definition.
    /// </summary>
    /// <remarks>
    /// schtasks <em>omits</em> <c>&lt;Enabled&gt;</c> for an enabled task — the Task Scheduler schema default is true —
    /// and only writes <c>&lt;Enabled&gt;false&lt;/Enabled&gt;</c> for a disabled one. Treating a missing element as
    /// "unknown" made every enabled CEIP task unrecordable, so PC Optimize silently stopped disabling them.
    /// </remarks>
    internal static bool? ParseTaskEnabled(string? xml, int exitCode)
    {
        if (exitCode != 0 || string.IsNullOrWhiteSpace(xml)) return null;

        var explicitFlag = DebloatService.ParseTaskEnabledFromXml(xml);
        if (explicitFlag.HasValue) return explicitFlag;

        // A task definition with no <Enabled> element is an enabled task.
        return System.Text.RegularExpressions.Regex.IsMatch(xml, @"<Task[\s>]") ? true : null;
    }

    /// <summary>
    /// Puts back every value <see cref="OptimizePcAsync"/> changed, using the backup it wrote.
    /// </summary>
    /// <remarks>
    /// The record is read here, unelevated, and handed to the script as data. The elevated script no longer opens a
    /// path in the user's data folder at all, so a link planted there cannot point it at another file.
    /// </remarks>
    public static async Task<ToolExecutionResult> RestoreOptimizePcAsync()
    {
        string backupPath = OptimizeBackupPath;
        if (!File.Exists(backupPath))
        {
            return new ToolExecutionResult
            {
                Success = false,
                Message = Loc.T($"Geri alınacak optimizasyon yedeği yok: {backupPath}")
            };
        }

        string backupJson;
        try
        {
            // Parsed and written out again, so only the three fields the script reads reach it.
            backupJson = SerializeOptimizeBackup(
                ParseOptimizeBackup(BoundedTextFile.ReadAllText(backupPath)), indented: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The optimization backup could not be read", $"{backupPath}: {ex.Message}");
            string reason = ex.Message;
            return new ToolExecutionResult
            {
                Success = false,
                Message = Loc.T($"Ayarlar geri yüklenemedi: {reason}")
            };
        }

        string script = @"
$ErrorActionPreference = 'Stop'
$errs = @()
$backup = '" + Loc.PsQuote(backupJson) + @"' | ConvertFrom-Json
if ($null -eq $backup) { throw '" + Loc.Ps("Optimizasyon yedegi bulunamadi.") + @"' }

# The undo record is a file in the user's data folder, so its contents are not trusted. Only the names this tool
# itself records are acted on, and only with a start type Windows accepts: without that, editing the file would make
# an elevated script reconfigure any service on the machine.
$knownServices = 'SysMain', 'ClickToRunSvc', 'Plastic Server 6'
$knownStartTypes = 'Automatic', 'Manual', 'Disabled', 'Boot', 'System'
$knownTasks = 'Consolidator', 'UsbCeip'
$ceipPath = '\Microsoft\Windows\Customer Experience Improvement Program\'

if ($null -ne $backup.services) {
    foreach ($prop in $backup.services.PSObject.Properties) {
        if ($knownServices -notcontains $prop.Name) { $errs += ($prop.Name + ': ' + 'unknown service'); continue }
        # A service that is gone (Office or Plastic uninstalled since) has nothing left to restore. Failing on it made
        # the undo fail on every attempt for as long as the record existed.
        if (-not (Get-Service -Name $prop.Name -ErrorAction SilentlyContinue)) { continue }
        try {
            $startType = [string]$prop.Value
            if ($startType) {
                if ($knownStartTypes -notcontains $startType) { $errs += ($prop.Name + ': ' + 'unknown start type'); continue }
                Set-Service -Name $prop.Name -StartupType $startType -ErrorAction Stop
                if ($startType -eq 'Automatic') { Start-Service -Name $prop.Name -ErrorAction SilentlyContinue }
            }
        } catch { $errs += ($prop.Name + ': ' + $_.Exception.Message) }
    }
}

if ($null -ne $backup.hags) {
    try {
        $hagsValue = [int]$backup.hags
        if ($hagsValue -lt 0 -or $hagsValue -gt 2) {
            # -1 means the value did not exist before the optimization; anything above 2 is not a value Windows
            # accepts. Both are undone by removing the value, which returns HAGS to its default (off).
            Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name 'HwSchMode' -ErrorAction SilentlyContinue
        } else {
            Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name 'HwSchMode' `
                -Value $hagsValue -Type DWord -Force -ErrorAction Stop
        }
    } catch { $errs += ('HAGS: ' + $_.Exception.Message) }
}

if ($null -ne $backup.tasks) {
    foreach ($t in $backup.tasks) {
        if ($knownTasks -notcontains [string]$t.name) { $errs += ([string]$t.name + ': ' + 'unknown task'); continue }
        if ([string]$t.path -ne $ceipPath) { $errs += ([string]$t.name + ': ' + 'unknown task path'); continue }
        try {
            if ([string]$t.state -eq 'Ready') {
                Enable-ScheduledTask -TaskName $t.name -TaskPath $t.path -ErrorAction Stop | Out-Null
            }
        } catch { $errs += ($t.name + ': ' + $_.Exception.Message) }
    }
}

" + ElevatedErrorLog.WriteStatement + @"
";
        var (ok, err) = await RunElevatedScriptAsync(script, "nl_restore_optimize");
        if (ok)
        {
            // The machine is back to its pre-optimization state; keeping the file would let a second restore
            // re-apply values that are no longer the current ones.
            try { File.Delete(backupPath); } catch { }
        }
        else if (PruneOptimizeRecordToFailures(ParseOptimizeBackup(backupJson), err) is { } remaining)
        {
            // Only what still failed stays in the record, so the next attempt retries exactly that instead of failing
            // again on values that were already put back.
            try { AtomicFile.WriteAllText(backupPath, SerializeOptimizeBackup(remaining, indented: true)); }
            catch (Exception ex) { AppLog.Warn("The optimization backup could not be narrowed", ex.Message); }
        }
        return new ToolExecutionResult
        {
            Success = ok,
            Message = ok
                ? Loc.T("Optimizasyon öncesi ayarlar geri yüklendi.")
                : Loc.T($"Ayarlar geri yüklenemedi: {err}")
        };
    }

    /// <summary>
    /// The part of <paramref name="record"/> the restore script reported as failed, read from its error lines
    /// (<c>"SysMain: …"</c>, <c>"HAGS: …"</c>, <c>"Consolidator: …"</c>). Entries the script refused as unknown are
    /// dropped — they are never acted on, so keeping them would only fail every later attempt. Null when no line names
    /// an entry (a refused prompt, a timeout): the record is then kept as it is.
    /// </summary>
    internal static OptimizeBackupRecord? PruneOptimizeRecordToFailures(OptimizeBackupRecord record, string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText)) return null;

        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool identified = false;
        foreach (var raw in errorText.Split(ErrorLineBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0) continue;

            var name = line[..colon].Trim();
            bool known = string.Equals(name, "HAGS", StringComparison.OrdinalIgnoreCase)
                         || record.Services.ContainsKey(name)
                         || record.Tasks.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (!known) continue;

            identified = true;
            var detail = line[(colon + 2)..].Trim();
            if (detail.StartsWith("unknown ", StringComparison.Ordinal)) continue;
            failed.Add(name);
        }

        if (!identified) return null;

        var remaining = new OptimizeBackupRecord();
        foreach (var (name, startType) in record.Services)
        {
            if (failed.Contains(name)) remaining.Services[name] = startType;
        }

        if (record.HagsRecorded && failed.Contains("HAGS"))
        {
            remaining.Hags = record.Hags;
            remaining.HagsRecorded = true;
        }

        foreach (var task in record.Tasks)
        {
            if (failed.Contains(task.Name)) remaining.Tasks.Add(task);
        }

        return remaining;
    }

    /// <summary>
    /// The cleanup a user is expected to run before deleting the folder. NitroLite has no installer, so nothing
    /// calls this automatically: without it, "PC Optimize" leaves SysMain disabled and HAGS on after the app is
    /// gone, and the autostart entry keeps pointing at an exe that no longer exists.
    /// </summary>
    public static async Task<ToolExecutionResult> UninstallCleanupAsync()
    {
        var parts = new List<string>();
        bool ok = true;

        if (File.Exists(OptimizeBackupPath))
        {
            var restore = await RestoreOptimizePcAsync();
            ok &= restore.Success;
            parts.Add(restore.Message);
        }

        bool autoStartRemoved = RemoveAutoStartEntry();
        ok &= autoStartRemoved;
        parts.Add(autoStartRemoved
            ? Loc.T("Otomatik başlatma kaydı kaldırıldı.")
            : Loc.T("Otomatik başlatma kaydı kaldırılamadı."));

        return new ToolExecutionResult { Success = ok, Message = string.Join(" ", parts) };
    }

    /// <summary>Deletes the HKCU Run value NitroLite writes when autostart is enabled. True when it is gone.</summary>
    internal static bool RemoveAutoStartEntry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return true;
            key.DeleteValue("NitroLite", throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// How long the caller waits for an elevated script before it reports the script as still running.
    /// </summary>
    internal const int ElevatedTimeoutSeconds = 35;

    /// <summary>How long one service is given to reach Running or Stopped inside an elevated script.</summary>
    internal const int ServiceWaitSeconds = 20;

    /// <summary>
    /// Bounded replacements for <c>Start-Service</c> and <c>Stop-Service</c>, prepended to every script.
    /// <para>
    /// The cmdlets wait without a limit for a service that sits in StartPending or StopPending. An elevated script
    /// stuck there never reaches its <c>finally</c> block, so a repair that stopped AASSvc and changed its image path
    /// could neither finish nor put either back. A PowerShell function takes precedence over a cmdlet of the same
    /// name, so every existing call — including the ones in the <c>finally</c> blocks — becomes a call with a
    /// <see cref="ServiceWaitSeconds"/>-second limit without each site having to remember it. <c>-ErrorAction</c>
    /// keeps its meaning: <c>Stop</c> (or the scripts' default) throws, <c>SilentlyContinue</c> swallows.
    /// </para>
    /// </summary>
    internal static readonly string BoundedServiceCommands = @"
Add-Type -AssemblyName System.ServiceProcess
function Start-Service {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true, Position = 0)][string[]]$Name)
    foreach ($n in $Name) {
        $sc = $null
        try {
            $sc = New-Object System.ServiceProcess.ServiceController -ArgumentList $n
            if ($sc.Status -ne 'Running') {
                if ($sc.Status -ne 'StartPending') { $sc.Start() }
                $sc.WaitForStatus('Running', [TimeSpan]::FromSeconds(" + ServiceWaitSeconds + @"))
            }
        } catch {
            if ($ErrorActionPreference -eq 'Stop') { throw } else { Write-Error -ErrorRecord $_ }
        } finally {
            if ($null -ne $sc) { $sc.Dispose(); $sc = $null }
        }
    }
}
function Stop-Service {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true, Position = 0)][string[]]$Name, [switch]$Force)
    foreach ($n in $Name) {
        $sc = $null
        try {
            $sc = New-Object System.ServiceProcess.ServiceController -ArgumentList $n
            if ($sc.Status -ne 'Stopped') {
                # ServiceController.Stop() stops running dependents first and waits for them without a limit, so
                # they are stopped here, bounded, beforehand. The list comes back in stop order.
                foreach ($d in $sc.DependentServices) {
                    if ($d.Status -ne 'Stopped') {
                        if ($d.Status -ne 'StopPending') { $d.Stop() }
                        $d.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(" + ServiceWaitSeconds + @"))
                    }
                }
                if ($sc.Status -ne 'StopPending') { $sc.Stop() }
                $sc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(" + ServiceWaitSeconds + @"))
            }
        } catch {
            if ($ErrorActionPreference -eq 'Stop') { throw } else { Write-Error -ErrorRecord $_ }
        } finally {
            if ($null -ne $sc) { $sc.Dispose(); $sc = $null }
        }
    }
}
";

    private static readonly object ActiveScriptLock = new();

    /// <summary>
    /// The elevated script that outlived <see cref="ElevatedTimeoutSeconds"/>. It is left to finish rather than
    /// killed, and no other script starts while it runs: two repairs editing the same service keys at once is how a
    /// machine ends up with neither the old nor the new image path.
    /// </summary>
    private static Process? _activeScript;

    private static async Task<(bool ok, string? error)> RunElevatedScriptAsync(string script, string prefix)
    {
        script = BoundedServiceCommands + script;

        if (ElevatedRunnerSeam != null)
        {
            try
            {
                int exit = await ElevatedRunnerSeam(script);
                return (exit == 0, exit == 0 ? null : Loc.T($"İşlem kodu: {exit}"));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        lock (ActiveScriptLock)
        {
            if (_activeScript != null)
            {
                bool stillRunning;
                try { stillRunning = !_activeScript.HasExited; }
                catch { stillRunning = false; }

                if (stillRunning)
                {
                    return (false, Loc.T($"PowerShell işlemi {ElevatedTimeoutSeconds} saniye içinde tamamlanamadı ve arka planda devam ediyor. Süreç sonlanana kadar yeni işlem başlatılamaz."));
                }

                _activeScript.Dispose();
                _activeScript = null;
            }
        }

        string errorLog = AppPaths.UniqueTempFile(prefix + "_err", ".log");
        string wrappedScript = ElevatedErrorLog.Function + " " +
            $"$errLog = '{Loc.PsQuote(errorLog)}'; Remove-Item -Path $errLog -Force -ErrorAction SilentlyContinue; " +
            $"try {{ {script} }} catch {{ {ElevatedErrorLog.WriteException}; exit 1 }}";
        // -EncodedCommand keeps the multi-line script out of the command line: there are no quoting rules to
        // escape and no character in the script text can break out of the argument.
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(wrappedScript));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = WindowsPaths.WindowsPowerShell,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var started = Process.Start(psi);
            if (started == null) return (false, Loc.T("Süreç başlatılamadı."));

            bool finished = await Task.Run(() => started.WaitForExit(ElevatedTimeoutSeconds * 1000));
            if (!finished)
            {
                // Not killed. Terminating the script half-way is the one outcome worse than waiting: FixAcerServices
                // would leave AASSvc stopped with its image path already rewritten, and a TRIM on a large drive
                // legitimately takes longer than this. The script's own catch/finally blocks are what put things
                // back, and they only run if it is allowed to finish. The process is remembered so the next request
                // is refused until it has.
                lock (ActiveScriptLock) { _activeScript = started; }
                return (false, Loc.T("İşlem 35 saniye içinde tamamlanamadı; yönetici işlemi arka planda devam ediyor."));
            }

            using var p = started;

            if (p.ExitCode == 0)
            {
                try { if (File.Exists(errorLog)) File.Delete(errorLog); } catch { }
                return (true, null);
            }

            string errDetail = string.Empty;
            if (File.Exists(errorLog))
            {
                try { errDetail = (await File.ReadAllTextAsync(errorLog)).Trim(); } catch { }
                try { File.Delete(errorLog); } catch { }
            }
            if (string.IsNullOrWhiteSpace(errDetail)) errDetail = Loc.T($"Çıkış kodu {p.ExitCode}");
            return (false, errDetail);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, Loc.T("Yönetici izni verilmedi (UAC reddedildi)."));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
