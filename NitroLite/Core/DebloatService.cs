using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NitroLite.Core;

public class ServiceStatusInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "";
    public string StartType { get; set; } = "";
    public bool IsDisabled { get; set; }
}

public class DebloatService
{
    public static readonly string[] Tier1Services = new[]
    {
        "AcerServiceSvc",
        "ASMSvc",
        "AcerDIAgentSvis",
        "AcerEZSvc",
        "AcerDeviceInfoAgentService",
        "AcerDeviceEnablingServiceV2"
    };

    public static readonly HashSet<string> AllowedStartupTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Automatic",
        "Manual",
        "Disabled"
    };

    #region Win32 P/Invoke for Service Status
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_STOPPED = 1;
    private const uint SERVICE_START_PENDING = 2;
    private const uint SERVICE_STOP_PENDING = 3;
    private const uint SERVICE_RUNNING = 4;
    private const uint SERVICE_CONTINUE_PENDING = 5;
    private const uint SERVICE_PAUSE_PENDING = 6;
    private const uint SERVICE_PAUSED = 7;
    #endregion

    public static bool IsValidStartupType(string? startupType)
    {
        return !string.IsNullOrWhiteSpace(startupType) && AllowedStartupTypes.Contains(startupType.Trim());
    }

    public static string ValidateStartupType(string? input, string? serviceName = null)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            var msg = serviceName != null
                ? $"Startup type for service '{serviceName}' cannot be null or empty."
                : "Startup type cannot be null or empty.";
            throw new ArgumentException(msg, nameof(input));
        }

        string trimmed = input.Trim();
        if (AllowedStartupTypes.TryGetValue(trimmed, out var canonical))
            return canonical;

        var errorMsg = serviceName != null
            ? $"Invalid startup type '{input}' for service '{serviceName}'. Allowed values: Automatic, Manual, Disabled."
            : $"Invalid service startup type: '{input}'. Allowed values: Automatic, Manual, Disabled.";
        throw new ArgumentException(errorMsg, nameof(input));
    }

    public string? LastError { get; internal set; }

    public string BackupStateFilePath { get; set; } = 
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debloat_backup_state.json");

    private Process? _activePowerShellProcess;
    private readonly object _processLock = new();

    internal Func<string, bool, Task<int>>? PowerShellRunner { get; set; }
    internal int PowerShellTimeoutMs { get; set; } = 20000;

    internal Process? ActivePowerShellProcess
    {
        get
        {
            lock (_processLock) { return _activePowerShellProcess; }
        }
        set
        {
            lock (_processLock) { _activePowerShellProcess = value; }
        }
    }

    public bool IsOperationInProgress
    {
        get
        {
            lock (_processLock)
            {
                if (_activePowerShellProcess != null)
                {
                    try
                    {
                        if (!_activePowerShellProcess.HasExited)
                        {
                            return true;
                        }
                        _activePowerShellProcess.Dispose();
                        _activePowerShellProcess = null;
                    }
                    catch
                    {
                        _activePowerShellProcess = null;
                    }
                }
                return false;
            }
        }
    }

    internal Func<string, string?>? ServiceStatusQuerySeam { get; set; }
    internal Func<string, string?>? ServiceStartupTypeQuerySeam { get; set; }
    internal Func<string, string>? ScheduledTaskStateQuerySeam { get; set; }

    private bool IsPreviousProcessRunning(out string? errorMessage)
    {
        errorMessage = null;
        lock (_processLock)
        {
            if (_activePowerShellProcess != null)
            {
                try
                {
                    if (!_activePowerShellProcess.HasExited)
                    {
                        errorMessage = "Önceki PowerShell işlemi hâlâ devam ediyor (durum belirsiz). Lütfen işlemin tamamlanmasını bekleyin.";
                        return true;
                    }
                    _activePowerShellProcess.Dispose();
                    _activePowerShellProcess = null;
                }
                catch
                {
                    try { _activePowerShellProcess?.Dispose(); } catch { }
                    _activePowerShellProcess = null;
                }
            }
        }
        return false;
    }

    public List<ServiceStatusInfo> GetServiceStatuses()
    {
        var list = new List<ServiceStatusInfo>();
        foreach (var name in Tier1Services)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", false);
                if (key != null)
                {
                    var dispName = key.GetValue("DisplayName")?.ToString() ?? name;
                    var startVal = key.GetValue("Start");
                    var startInt = startVal is int i ? i : 2;
                    var startTypeStr = startInt switch
                    {
                        2 => "Automatic",
                        3 => "Manual",
                        4 => "Disabled",
                        _ => "Unknown"
                    };

                    var liveStatus = QueryServiceStatusDirect(name);
                    string statusDisplay;
                    if (liveStatus == "Running")
                    {
                        statusDisplay = "Çalışıyor";
                    }
                    else if (liveStatus == "Stopped")
                    {
                        statusDisplay = "Durduruldu";
                    }
                    else
                    {
                        statusDisplay = startInt == 4 ? "Durduruldu" : "Bilinmiyor";
                    }

                    list.Add(new ServiceStatusInfo
                    {
                        Name = name,
                        DisplayName = dispName,
                        Status = statusDisplay,
                        StartType = startTypeStr,
                        IsDisabled = startInt == 4
                    });
                }
                else
                {
                    list.Add(new ServiceStatusInfo
                    {
                        Name = name,
                        DisplayName = name,
                        Status = "Kayıtlı Değil",
                        StartType = "Disabled",
                        IsDisabled = true
                    });
                }
            }
            catch
            {
                list.Add(new ServiceStatusInfo
                {
                    Name = name,
                    DisplayName = name,
                    Status = "Bilinmiyor",
                    StartType = "Disabled",
                    IsDisabled = true
                });
            }
        }
        return list;
    }

    public Dictionary<string, string> RecordCurrentStartupTypes()
    {
        var currentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Tier1Services)
        {
            if (ServiceStartupTypeQuerySeam != null)
            {
                var seamType = ServiceStartupTypeQuerySeam(name);
                if (!string.IsNullOrEmpty(seamType))
                {
                    currentMap[name] = seamType;
                    continue;
                }
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", false);
                if (key != null)
                {
                    var startVal = key.GetValue("Start");
                    var startInt = startVal is int i ? i : 2;
                    var startTypeStr = startInt switch
                    {
                        2 => "Automatic",
                        3 => "Manual",
                        4 => "Disabled",
                        _ => "Automatic"
                    };
                    currentMap[name] = startTypeStr;
                }
            }
            catch { }
        }
        return currentMap;
    }

    public static string? QueryServiceStatusDirect(string serviceName)
    {
        try
        {
            var hScm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (hScm == IntPtr.Zero) return null;
            try
            {
                var hSvc = OpenService(hScm, serviceName, SERVICE_QUERY_STATUS);
                if (hSvc == IntPtr.Zero) return null;
                try
                {
                    if (QueryServiceStatus(hSvc, out var status))
                    {
                        if (status.dwCurrentState == SERVICE_RUNNING || status.dwCurrentState == SERVICE_START_PENDING)
                            return "Running";
                        if (status.dwCurrentState == SERVICE_STOPPED || status.dwCurrentState == SERVICE_STOP_PENDING || 
                            status.dwCurrentState == SERVICE_PAUSED || status.dwCurrentState == SERVICE_CONTINUE_PENDING || 
                            status.dwCurrentState == SERVICE_PAUSE_PENDING)
                            return "Stopped";
                    }
                }
                finally
                {
                    CloseServiceHandle(hSvc);
                }
            }
            finally
            {
                CloseServiceHandle(hScm);
            }
        }
        catch { }
        return null;
    }

    public static string? ParseScQueryOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        // 1. Check for labeled state code across known Windows language headers
        var m = Regex.Match(output, @"\b(?:STATE|STATUS|ETAT|STATUT|DURUM|ESTADO|STATO|STASIS|STAN|STAV|ZUSTAND|TILSTAND|TILLSTÅND|СОСТОЯНИЕ|SOSTOYANIE|状態|状态|상태)\b[^\r\n:]*:\s*(\d+)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int stateCode))
        {
            if (stateCode == 4 || stateCode == 2) return "Running";
            if (stateCode == 1 || stateCode == 3 || stateCode == 5 || stateCode == 6 || stateCode == 7) return "Stopped";
        }

        // 2. Generic line scan: find single-digit state code 1-7 (excluding metadata lines like TYPE, EXIT_CODE, etc.)
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Contains("EXIT_CODE", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("TYPE", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("CHECKPOINT", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("WAIT_HINT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lineMatch = Regex.Match(line, @":\s*([1-7])(?:\s+|$)");
            if (lineMatch.Success && int.TryParse(lineMatch.Groups[1].Value, out int code))
            {
                if (code == 4 || code == 2) return "Running";
                if (code == 1 || code == 3 || code == 5 || code == 6 || code == 7) return "Stopped";
            }
        }

        // 3. Multilingual keywords check (English, Turkish, German, French, Spanish, Italian, Russian, Polish, Portuguese, Japanese, Chinese, Korean)
        if (Regex.IsMatch(output, @"\b(?:RUNNING|CALISIYOR|ÇALIŞIYOR|LAUFEND|EN_COURS|EN COURS|EJECUCION|EJECUCIÓN|IN ESECUZIONE|РАБОТАЕТ|URUCHOMIONA|EM EXECUÇÃO|EXECUTANDO|EXECUCAO|実行中|正在运行|실행\s*중)\b", RegexOptions.IgnoreCase))
            return "Running";
        if (Regex.IsMatch(output, @"\b(?:STOPPED|DURDURULDU|BEENDET|ANGEHALTEN|ARRETE|ARRÊTÉ|DETENIDO|ARRESTO|ARRESTATA|ОСТАНОВЛЕНА|ZATRZYMANA|PARADO|停止|已停止|중지됨)\b", RegexOptions.IgnoreCase))
            return "Stopped";

        return null;
    }

    public Dictionary<string, string> RecordCurrentServiceRunningStates()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Tier1Services)
        {
            if (ServiceStatusQuerySeam != null)
            {
                var seamStatus = ServiceStatusQuerySeam(name);
                if (!string.IsNullOrEmpty(seamStatus))
                {
                    map[name] = seamStatus;
                    continue;
                }
            }

            try
            {
                var directStatus = QueryServiceStatusDirect(name);
                if (!string.IsNullOrEmpty(directStatus))
                {
                    map[name] = directStatus;
                    continue;
                }

                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"query \"{name}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p != null)
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    var parsed = ParseScQueryOutput(output);
                    if (!string.IsNullOrEmpty(parsed))
                    {
                        map[name] = parsed;
                    }
                }
            }
            catch { }
        }
        return map;
    }

    public static bool IsActiveTaskState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return false;
        var s = state.Trim();
        return string.Equals(s, "Ready", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Enabled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Running", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Hazır", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Çalışıyor", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Bereit", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Prêt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Listo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Pronto", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Готово", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Выполняется", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDisabledTaskState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return false;
        var s = state.Trim();
        return string.Equals(s, "Disabled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Devre Dışı", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Deaktiviert", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Désactivé", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Deshabilitado", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(s, "Отключено", StringComparison.OrdinalIgnoreCase);
    }

    public string RecordCurrentScheduledTaskState(string taskName = "NitroSenseLauncher")
    {
        if (ScheduledTaskStateQuerySeam != null)
        {
            return ScheduledTaskStateQuerySeam(taskName);
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/query /tn \"{taskName}\" /fo csv",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p != null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                if (output.Contains("Ready", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Enabled", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Hazır", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Bereit", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Prêt", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Listo", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Готово", StringComparison.OrdinalIgnoreCase))
                {
                    return "Ready";
                }
                if (output.Contains("Disabled", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Devre Dışı", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Deaktiviert", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Désactivé", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Deshabilitado", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Отключено", StringComparison.OrdinalIgnoreCase))
                {
                    return "Disabled";
                }
            }
        }
        catch { }
        return "Unknown";
    }

    public Dictionary<string, string> LoadBackupState()
    {
        try
        {
            if (File.Exists(BackupStateFilePath))
            {
                var json = File.ReadAllText(BackupStateFilePath);
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (map != null) return new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            LastError = $"Debloat backup state file is corrupted: {ex.Message}";
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public bool SaveBackupState(Dictionary<string, string> state)
    {
        try
        {
            var dir = Path.GetDirectoryName(BackupStateFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            var tmp = BackupStateFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, BackupStateFilePath, overwrite: true);
            return File.Exists(BackupStateFilePath);
        }
        catch (Exception ex)
        {
            LastError = $"Failed to save backup state: {ex.Message}";
            return false;
        }
    }

    public string GenerateDebloatScript(Dictionary<string, string>? recordedState = null, string? errorLogPath = null)
    {
        errorLogPath ??= Path.Combine(Path.GetTempPath(), "nitrolite_debloat_err.log");
        var logEscaped = errorLogPath.Replace("'", "''");
        var targetServices = recordedState != null
            ? Tier1Services.Where(s => recordedState.ContainsKey(s)).ToArray()
            : Tier1Services;

        var svcs = string.Join(",", targetServices.Select(s => $"'{s}'"));

        string taskDisableSnippet = "";
        bool shouldDisableTask = false;
        if (recordedState != null)
        {
            if (recordedState.TryGetValue("Task:NitroSenseLauncher", out var taskStateBeforeDebloat))
            {
                shouldDisableTask = IsActiveTaskState(taskStateBeforeDebloat);
            }
        }
        else
        {
            shouldDisableTask = IsActiveTaskState(RecordCurrentScheduledTaskState("NitroSenseLauncher"));
        }

        if (shouldDisableTask)
        {
            taskDisableSnippet = "try { " +
                                 "  $task = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; " +
                                 "  if ($task) { " +
                                 "    Disable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; " +
                                 "    $vTask = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; " +
                                 "    if ($vTask.State -ne 'Disabled') { throw 'NitroSenseLauncher görevi devre dışı bırakılamadı.' }; " +
                                 "  } " +
                                 "} catch { $errs += ('Task:NitroSenseLauncher: ' + $_.Exception.Message) }; ";
        }

        return $"$errLog = '{logEscaped}'; " +
               "Remove-Item -Path $errLog -Force -ErrorAction SilentlyContinue; " +
               $"$errs = @(); $svcs = @({svcs}); " +
               "foreach ($s in $svcs) { " +
               "  try { " +
               "    $svc = Get-Service -Name $s -ErrorAction SilentlyContinue; " +
               "    if ($svc) { " +
               "      if ($svc.Status -eq 'Running') { Stop-Service -Name $s -Force -ErrorAction Stop }; " +
               "      if ($svc.StartType -ne 'Disabled') { Set-Service -Name $s -StartupType Disabled -ErrorAction Stop }; " +
               "      $vSvc = Get-Service -Name $s -ErrorAction Stop; " +
               "      if ($vSvc.Status -ne 'Stopped') { throw 'Servis durdurulamadı veya hâlâ çalışıyor.' }; " +
               "      if ($vSvc.StartType -ne 'Disabled') { throw 'Servis başlangıç türü devre dışı bırakılamadı.' }; " +
               "    } " +
               "  } catch { " +
               "    $errs += ($s + ': ' + $_.Exception.Message) " +
               "  } " +
               "}; " +
               taskDisableSnippet +
               "if ($errs.Count -gt 0) { $errs -join \"`n\" | Out-File -FilePath $errLog -Encoding utf8; exit 1 } else { exit 0 }";
    }

    public string GenerateRestoreScript(Dictionary<string, string>? backupState = null, string? errorLogPath = null)
    {
        errorLogPath ??= Path.Combine(Path.GetTempPath(), "nitrolite_restore_err.log");
        var logEscaped = errorLogPath.Replace("'", "''");
        backupState ??= LoadBackupState();

        var sb = new System.Text.StringBuilder();
        sb.Append($"$errLog = '{logEscaped}'; Remove-Item -Path $errLog -Force -ErrorAction SilentlyContinue; ");
        sb.Append("$errs = @(); ");

        if (backupState == null || backupState.Count == 0 || !(Tier1Services.Any(s => backupState.ContainsKey(s) && !string.IsNullOrWhiteSpace(backupState[s])) || (backupState.TryGetValue("Task:NitroSenseLauncher", out var onlyTask) && (IsActiveTaskState(onlyTask) || IsDisabledTaskState(onlyTask)))))
        {
            sb.Append("$errs += 'Restore aborted: backup state is empty or contains no recorded services.'; ");
            sb.Append("if ($errs.Count -gt 0) { $errs -join \"`n\" | Out-File -FilePath $errLog -Encoding utf8; exit 1 } else { exit 0 }");
            return sb.ToString();
        }

        foreach (var s in Tier1Services)
        {
            // Do NOT force missing backup services to Automatic! Skip if unrecorded.
            if (backupState == null || !backupState.TryGetValue(s, out var recordedType) || string.IsNullOrWhiteSpace(recordedType))
            {
                continue;
            }

            // Validate startup type to prevent command injection
            string canonicalType = ValidateStartupType(recordedType, s);

            bool shouldStart = false;
            if (backupState.TryGetValue(s + ":Status", out var recordedStatus))
            {
                shouldStart = string.Equals(recordedStatus, "Running", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                shouldStart = string.Equals(canonicalType, "Automatic", StringComparison.OrdinalIgnoreCase);
            }

            sb.Append("try { ");
            sb.Append($"$svc = Get-Service -Name '{s}' -ErrorAction Stop; ");
            sb.Append($"if (-not $svc) {{ throw 'Servis bulunamadı: {s}' }}; ");
            if (canonicalType.Equals("Disabled", StringComparison.OrdinalIgnoreCase) && shouldStart)
            {
                // Windows SCM prohibits starting a Disabled service (Error 1058).
                // To restore a service recorded as Disabled + Running, temporarily set to Manual,
                // start the service, and then switch startup type back to Disabled.
                sb.Append($"Set-Service -Name '{s}' -StartupType Manual -ErrorAction Stop; ");
                sb.Append($"Start-Service -Name '{s}' -ErrorAction Stop; ");
                sb.Append($"Set-Service -Name '{s}' -StartupType Disabled -ErrorAction Stop; ");
            }
            else
            {
                sb.Append($"Set-Service -Name '{s}' -StartupType {canonicalType} -ErrorAction Stop; ");
                if (shouldStart)
                {
                    sb.Append($"Start-Service -Name '{s}' -ErrorAction Stop; ");
                }
                else
                {
                    sb.Append($"Stop-Service -Name '{s}' -Force -ErrorAction Stop; ");
                }
            }
            sb.Append($"$vSvc = Get-Service -Name '{s}' -ErrorAction Stop; ");
            sb.Append($"if ($vSvc.StartType -ne '{canonicalType}') {{ throw 'Servis başlangıç türü doğrulanamadı: {s}' }}; ");
            if (shouldStart)
            {
                sb.Append($"if ($vSvc.Status -ne 'Running') {{ throw 'Servis çalışma durumu doğrulanamadı: {s}' }}; ");
            }
            else
            {
                sb.Append($"if ($vSvc.Status -ne 'Stopped') {{ throw 'Servis çalışma durumu doğrulanamadı: {s}' }}; ");
            }
            sb.Append("} catch { ");
            sb.Append($"$errs += ('{s}: ' + $_.Exception.Message); ");
            sb.Append("}; ");
        }

        if (backupState != null && backupState.TryGetValue("Task:NitroSenseLauncher", out var taskState))
        {
            if (IsActiveTaskState(taskState))
            {
                sb.Append("try { ");
                sb.Append("  $task = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
                sb.Append("  if (-not $task) { throw 'NitroSenseLauncher görevi bulunamadı.' }; ");
                sb.Append("  Enable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
                sb.Append("  $vTask = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
                sb.Append("  if ($vTask.State -eq 'Disabled') { throw 'NitroSenseLauncher görevi etkinleştirilemedi veya doğrulanamadı.' }; ");
                sb.Append("} catch { ");
                sb.Append("  $errs += ('Task:NitroSenseLauncher: ' + $_.Exception.Message); ");
                sb.Append("}; ");
            }
            else if (IsDisabledTaskState(taskState))
            {
                sb.Append("try { ");
                sb.Append("  $task = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
                sb.Append("  if (-not $task) { throw 'NitroSenseLauncher görevi bulunamadı.' }; ");
                sb.Append("  Disable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
                sb.Append("  $vTask = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
                sb.Append("  if ($vTask.State -ne 'Disabled') { throw 'NitroSenseLauncher görevi devre dışı bırakılamadı veya doğrulanamadı.' }; ");
                sb.Append("} catch { ");
                sb.Append("  $errs += ('Task:NitroSenseLauncher: ' + $_.Exception.Message); ");
                sb.Append("}; ");
            }
        }

        sb.Append("if ($errs.Count -gt 0) { $errs -join \"`n\" | Out-File -FilePath $errLog -Encoding utf8; exit 1 } else { exit 0 }");

        return sb.ToString();
    }

    internal async Task<bool> RunElevatedPowerShellAsync(string script, string? errorLogPath = null)
    {
        LastError = null;

        if (PowerShellRunner != null)
        {
            try
            {
                int runnerExit = await PowerShellRunner(script, true);
                if (errorLogPath != null && File.Exists(errorLogPath))
                {
                    try
                    {
                        var errText = File.ReadAllText(errorLogPath).Trim();
                        if (!string.IsNullOrEmpty(errText))
                        {
                            LastError = errText;
                        }
                        File.Delete(errorLogPath);
                    }
                    catch { }
                }

                if (runnerExit != 0 && string.IsNullOrEmpty(LastError))
                {
                    LastError = $"PowerShell process exited with code {runnerExit}";
                }

                return runnerExit == 0;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        return await Task.Run(() =>
        {
            Process? p = null;
            try
            {
                var bytes = System.Text.Encoding.Unicode.GetBytes(script);
                var encoded = Convert.ToBase64String(bytes);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                p = Process.Start(psi);
                if (p == null)
                {
                    LastError = "PowerShell işlemi başlatılamadı.";
                    return false;
                }

                lock (_processLock)
                {
                    _activePowerShellProcess = p;
                }

                bool exited = p.WaitForExit(PowerShellTimeoutMs);
                if (!exited)
                {
                    // Timed out: do NOT access p.ExitCode to avoid InvalidOperationException.
                    // Retain p in _activePowerShellProcess to block subsequent operations until it exits.
                    LastError = "PowerShell işlemi 20 saniye içinde tamamlanamadı ve arka planda devam ediyor. Süreç sonlanana kadar yeni işlem başlatılamaz.";
                    return false;
                }

                int exitCode = -1;
                try
                {
                    exitCode = p.ExitCode;
                }
                catch (Exception ex)
                {
                    LastError = $"PowerShell çıkış kodu okunamadı: {ex.Message}";
                }

                lock (_processLock)
                {
                    p.Dispose();
                    _activePowerShellProcess = null;
                    p = null;
                }

                if (errorLogPath != null && File.Exists(errorLogPath))
                {
                    try
                    {
                        var errText = File.ReadAllText(errorLogPath).Trim();
                        if (!string.IsNullOrEmpty(errText))
                        {
                            LastError = errText;
                        }
                        File.Delete(errorLogPath);
                    }
                    catch { }
                }

                if (exitCode != 0 && string.IsNullOrEmpty(LastError))
                {
                    LastError = $"PowerShell process exited with code {exitCode}";
                }

                return exitCode == 0;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                lock (_processLock)
                {
                    if (p != null)
                    {
                        try
                        {
                            if (p.HasExited)
                            {
                                p.Dispose();
                                _activePowerShellProcess = null;
                            }
                        }
                        catch
                        {
                            _activePowerShellProcess = null;
                        }
                    }
                }
                return false;
            }
        });
    }

    private readonly SemaphoreSlim _debloatLock = new(1, 1);

    public async Task<bool> ApplyTier1DebloatAsync()
    {
        if (IsPreviousProcessRunning(out var runningMsg))
        {
            LastError = runningMsg;
            return false;
        }

        await _debloatLock.WaitAsync();
        try
        {
            if (IsPreviousProcessRunning(out var runningMsgLocked))
            {
                LastError = runningMsgLocked;
                return false;
            }

            var existingBackup = LoadBackupState();
            bool hasExistingValidBackup = File.Exists(BackupStateFilePath) &&
                existingBackup.Count > 0 &&
                Tier1Services.Any(s => existingBackup.ContainsKey(s) && IsValidStartupType(existingBackup[s]));

            if (!hasExistingValidBackup)
            {
                // If backup file exists on disk but is invalid / corrupt, do NOT overwrite it!
                if (File.Exists(BackupStateFilePath))
                {
                    LastError = $"Apply debloat aborted: an existing backup file exists at '{BackupStateFilePath}' but its content is invalid or corrupted. Please inspect or restore it before applying debloat.";
                    return false;
                }

                // Clean baseline: record current startup types, running states, and scheduled task state before modifying
                var currentTypes = RecordCurrentStartupTypes();
                var currentStatuses = RecordCurrentServiceRunningStates();
                var taskState = RecordCurrentScheduledTaskState("NitroSenseLauncher");

                foreach (var kvp in currentTypes)
                {
                    existingBackup[kvp.Key] = kvp.Value;
                }
                foreach (var kvp in currentStatuses)
                {
                    existingBackup[kvp.Key + ":Status"] = kvp.Value;
                }
                if (!string.IsNullOrEmpty(taskState) && !taskState.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    existingBackup["Task:NitroSenseLauncher"] = taskState;
                }

                // Abort service shutdown if backup state cannot be written
                if (!SaveBackupState(existingBackup))
                {
                    LastError = $"Debloat aborted: failed to persist service backup state to disk. {LastError}".Trim();
                    return false;
                }

                // Completeness verification:
                // Ensure that any discovered/installed service has complete backup (both startup type and running status)
                var discoveredServices = new HashSet<string>(currentTypes.Keys, StringComparer.OrdinalIgnoreCase);
                discoveredServices.UnionWith(currentStatuses.Keys);

                foreach (var svc in discoveredServices)
                {
                    if (!existingBackup.ContainsKey(svc) || !IsValidStartupType(existingBackup[svc]))
                    {
                        LastError = $"Debloat aborted: incomplete backup state for installed service '{svc}' (missing startup type).";
                        return false;
                    }
                    if (!existingBackup.ContainsKey(svc + ":Status") || string.IsNullOrWhiteSpace(existingBackup[svc + ":Status"]))
                    {
                        LastError = $"Debloat aborted: incomplete backup state for installed service '{svc}' (missing running status).";
                        return false;
                    }
                }
            }
            else
            {
                // Existing valid backup exists. Protect baseline from being overwritten by partially debloated state.
                foreach (var svc in Tier1Services.Where(s => existingBackup.ContainsKey(s)))
                {
                    if (!IsValidStartupType(existingBackup[svc]) ||
                        !existingBackup.TryGetValue(svc + ":Status", out var savedStatus) ||
                        (savedStatus != "Running" && savedStatus != "Stopped"))
                    {
                        LastError = $"Debloat aborted: invalid startup type for service '{svc}' in existing backup.";
                        return false;
                    }
                }
            }

            // 2. Generate and run debloat script with error logging
            var errLog = Path.Combine(Path.GetTempPath(), "nitrolite_debloat_err.log");
            var script = GenerateDebloatScript(existingBackup, errLog);
            return await RunElevatedPowerShellAsync(script, errLog);
        }
        finally
        {
            _debloatLock.Release();
        }
    }

    public async Task<bool> RestoreTier1ServicesAsync()
    {
        if (IsPreviousProcessRunning(out var runningMsg))
        {
            LastError = runningMsg;
            return false;
        }

        await _debloatLock.WaitAsync();
        try
        {
            if (IsPreviousProcessRunning(out var runningMsgLocked))
            {
                LastError = runningMsgLocked;
                return false;
            }

            var backup = LoadBackupState();
            if (backup == null || backup.Count == 0 || !Tier1Services.Any(s => backup.ContainsKey(s) && IsValidStartupType(backup[s])))
            {
                if (string.IsNullOrEmpty(LastError))
                {
                    LastError = "Restore aborted: backup state is empty or contains no recorded services.";
                }
                return false;
            }

            // Validate all recorded services in backup state
            foreach (var svc in Tier1Services.Where(s => backup.ContainsKey(s)))
            {
                if (!IsValidStartupType(backup[svc]))
                {
                    LastError = $"Restore aborted: invalid startup type '{backup[svc]}' for service '{svc}' in backup state.";
                    return false;
                }
            }

            var errLog = Path.Combine(Path.GetTempPath(), "nitrolite_restore_err.log");
            var script = GenerateRestoreScript(backup, errLog);
            bool success = await RunElevatedPowerShellAsync(script, errLog);
            if (success)
            {
                try
                {
                    if (File.Exists(BackupStateFilePath))
                    {
                        File.Delete(BackupStateFilePath);
                    }
                    var bak = BackupStateFilePath + ".bak";
                    if (File.Exists(bak))
                    {
                        File.Delete(bak);
                    }
                }
                catch { }
            }
            return success;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
        finally
        {
            _debloatLock.Release();
        }
    }

    public bool IsNitroLiteAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("NitroLite") != null;
        }
        catch
        {
            return false;
        }
    }

    public void SetNitroLiteAutoStart(bool enable, bool startMinimized = false)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var cmd = startMinimized ? $"\"{exePath}\" --autostart --minimized" : $"\"{exePath}\" --autostart";
                    key.SetValue("NitroLite", cmd);
                }
            }
            else
            {
                key.DeleteValue("NitroLite", false);
            }
        }
        catch
        {
            // Ignore
        }
    }

    public const string NitroSensePackageFamily = "ULICTekInc.NitroSenseforNotebook_nt9dgb7efx6bt";

    public bool IsNitroSenseRunning()
    {
        var procs = Process.GetProcessesByName("NitroSense");
        try
        {
            return procs.Length > 0;
        }
        finally
        {
            foreach (var p in procs)
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    public int KillNitroSenseProcesses()
    {
        var procs = Process.GetProcessesByName("NitroSense");
        int count = procs.Length;
        foreach (var p in procs)
        {
            try
            {
                p.Kill(true);
            }
            catch { }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
        return count;
    }

    public void DisableNitroSenseBackgroundExecution()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\{NitroSensePackageFamily}", true);
            if (key != null)
            {
                key.SetValue("Disabled", 1, RegistryValueKind.DWord);
                key.SetValue("DisabledByUser", 1, RegistryValueKind.DWord);
            }
        }
        catch { }
    }
}
