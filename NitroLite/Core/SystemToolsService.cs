using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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

    /// <summary>
    /// Finds the DriverStore folder containing Acer service executables (predatorservice.inf_amd64_*).
    /// Dynamically selects the latest directory containing the required binaries.
    /// </summary>
    public static string? FindDriverStoreFolder(string[] requiredBinaries)
    {
        try
        {
            var repo = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
            if (!Directory.Exists(repo)) return null;

            var dirs = Directory.GetDirectories(repo, "predatorservice.inf_amd64_*")
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.LastWriteTimeUtc);

            foreach (var d in dirs)
            {
                bool allFound = requiredBinaries.All(b => File.Exists(Path.Combine(d.FullName, b)));
                if (allFound) return d.FullName;
            }
        }
        catch { }
        return null;
    }

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
$dirs = Get-ChildItem -Path $repo -Filter 'predatorservice.inf_amd64_*' -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
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
        var (ok, err) = await RunElevatedScriptAsync(script, "nl_restore_mux");
        return new ToolExecutionResult
        {
            Success = ok,
            Message = ok
                ? Loc.T("MUX switch desteği kayıt defterinde etkinleştirildi ve eski görevler temizlendi.")
                : Loc.T($"MUX ayarı onarılamadı: {err}")
        };
    }

    /// <summary>Where the previous values of every setting OptimizePcAsync changes are stored.</summary>
    public static string OptimizeBackupPath => AppPaths.GetDataFile("optimize_backup.json");

    /// <summary>
    /// Applies elevated performance, telemetry debloat and system tuning optimizations. Every value that is
    /// changed is written to <see cref="OptimizeBackupPath"/> first, so <see cref="RestoreOptimizePcAsync"/> can
    /// put the machine back the way it was.
    /// </summary>
    public static async Task<ToolExecutionResult> OptimizePcAsync()
    {
        string backupPath = OptimizeBackupPath;
        string script = @"
$ErrorActionPreference = 'Stop'
$errs = @()
$backupPath = '" + backupPath.Replace("'", "''") + @"'

# The undo record is read first and written before the first change. A second run must not replace the original
# state with the already-optimized one, and a run that is cut short must still be undoable.
$services = @{}
$tasks = @()
$hags = -1
if (Test-Path -LiteralPath $backupPath) {
    try { $existing = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json }
    catch { throw '" + Loc.Ps("Optimizasyon yedeği okunamadı; üzerine yazılmadı.") + @"' }
    if ($null -ne $existing.services) {
        foreach ($p in $existing.services.PSObject.Properties) { $services[$p.Name] = [string]$p.Value }
    }
    if ($null -ne $existing.tasks) { $tasks = @($existing.tasks) }
    if ($null -ne $existing.hags) { $hags = [int]$existing.hags }
}

function Save-Backup {
    [ordered]@{ services = $services; hags = $hags; tasks = $tasks } |
        ConvertTo-Json -Depth 5 | Out-File -FilePath $backupPath -Encoding utf8 -Force
}

$gfxPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
$ceip = '\Microsoft\Windows\Customer Experience Improvement Program\'
$idleServices = 'ClickToRunSvc', 'Plastic Server 6'

# 1. Record the current state. Only values that are not in the backup yet are taken, so the first run keeps
#    the machine's real starting point. -1 means HwSchMode did not exist and must be removed on restore.
$svc = Get-Service -Name 'SysMain' -ErrorAction SilentlyContinue
if ($svc -and -not $services.ContainsKey('SysMain')) { $services['SysMain'] = [string]$svc.StartType }

if ($hags -eq -1 -and (Test-Path -LiteralPath $gfxPath)) {
    $existingHags = Get-ItemProperty -Path $gfxPath -Name 'HwSchMode' -ErrorAction SilentlyContinue
    if ($null -ne $existingHags) { $hags = [int]$existingHags.HwSchMode }
}

foreach ($t in 'Consolidator', 'UsbCeip') {
    $task = Get-ScheduledTask -TaskName $t -TaskPath $ceip -ErrorAction SilentlyContinue
    if ($task -and -not ($tasks | Where-Object { $_.name -eq $t })) {
        $tasks += @{ name = $t; path = $ceip; state = [string]$task.State }
    }
}

foreach ($s in $idleServices) {
    $idle = Get-Service -Name $s -ErrorAction SilentlyContinue
    if ($idle -and -not $services.ContainsKey($s)) { $services[$s] = [string]$idle.StartType }
}

# 2. Write the undo record before anything is changed.
try { Save-Backup }
catch { throw ('" + Loc.Ps("Optimizasyon yedeği yazılamadı; hiçbir ayar değiştirilmedi.") + @" ' + $_.Exception.Message) }

# 3. Apply.
try {
    $svc = Get-Service -Name 'SysMain' -ErrorAction Stop
    Set-Service -Name 'SysMain' -StartupType Disabled -ErrorAction Stop
    Stop-Service -Name 'SysMain' -Force -ErrorAction Stop
} catch { $errs += ('SysMain: ' + $_.Exception.Message) }

try {
    if (Test-Path -LiteralPath $gfxPath) {
        Set-ItemProperty -Path $gfxPath -Name 'HwSchMode' -Value 2 -Type DWord -Force -ErrorAction Stop
    }
} catch { $errs += ('HAGS: ' + $_.Exception.Message) }

foreach ($t in 'Consolidator', 'UsbCeip') {
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

if ($errs.Count -gt 0) { $errs | Out-File -FilePath $errLog -Encoding utf8; exit 1 } else { exit 0 }
";
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
    /// Puts back every value <see cref="OptimizePcAsync"/> changed, using the backup it wrote.
    /// </summary>
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

        string script = @"
$ErrorActionPreference = 'Stop'
$errs = @()
$backupPath = '" + backupPath.Replace("'", "''") + @"'
if (-not (Test-Path -LiteralPath $backupPath)) { throw '" + Loc.Ps("Optimizasyon yedegi bulunamadi.") + @"' }
$backup = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json

if ($null -ne $backup.services) {
    foreach ($prop in $backup.services.PSObject.Properties) {
        try {
            $startType = [string]$prop.Value
            if ($startType) {
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
        try {
            if ([string]$t.state -eq 'Ready') {
                Enable-ScheduledTask -TaskName $t.name -TaskPath $t.path -ErrorAction Stop | Out-Null
            }
        } catch { $errs += ($t.name + ': ' + $_.Exception.Message) }
    }
}

if ($errs.Count -gt 0) { $errs | Out-File -FilePath $errLog -Encoding utf8; exit 1 } else { exit 0 }
";
        var (ok, err) = await RunElevatedScriptAsync(script, "nl_restore_optimize");
        if (ok)
        {
            // The machine is back to its pre-optimization state; keeping the file would let a second restore
            // re-apply values that are no longer the current ones.
            try { File.Delete(backupPath); } catch { }
        }
        return new ToolExecutionResult
        {
            Success = ok,
            Message = ok
                ? Loc.T("Optimizasyon öncesi ayarlar geri yüklendi.")
                : Loc.T($"Ayarlar geri yüklenemedi: {err}")
        };
    }

    private static async Task<(bool ok, string? error)> RunElevatedScriptAsync(string script, string prefix)
    {
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

        string errorLog = AppPaths.UniqueTempFile(prefix + "_err", ".log");
        string wrappedScript = $"$errLog = '{errorLog.Replace("'", "''")}'; Remove-Item -Path $errLog -Force -ErrorAction SilentlyContinue; try {{ {script} }} catch {{ $_.Exception.Message | Out-File -FilePath $errLog -Encoding utf8; exit 1 }}";
        // -EncodedCommand keeps the multi-line script out of the command line: there are no quoting rules to
        // escape and no character in the script text can break out of the argument.
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(wrappedScript));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.WindowsPowerShell,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var p = Process.Start(psi);
            if (p == null) return (false, Loc.T("Süreç başlatılamadı."));

            bool finished = await Task.Run(() => p.WaitForExit(35000));
            if (!finished)
            {
                // An unelevated caller cannot terminate an elevated process. Only claim it was stopped when the
                // exit is actually observed.
                bool killed = false;
                try { p.Kill(); killed = p.WaitForExit(2000); } catch { }
                return (false, killed
                    ? Loc.T("İşlem 35 saniye içinde tamamlanamadı ve durduruldu.")
                    : Loc.T("İşlem 35 saniye içinde tamamlanamadı; yönetici işlemi arka planda devam ediyor."));
            }

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
