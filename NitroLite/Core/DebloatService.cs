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
        AppPaths.GetDataFile("debloat_backup_state.json");

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
                        errorMessage = Loc.T("Önceki PowerShell işlemi hâlâ devam ediyor (durum belirsiz). Lütfen işlemin tamamlanmasını bekleyin.");
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
                    var dispName = CleanDisplayName(key.GetValue("DisplayName")?.ToString(), name);
                    var startVal = key.GetValue("Start");
                    var startInt = startVal is int i ? i : 2;
                    var startTypeStr = startInt switch
                    {
                        2 => "Automatic",
                        3 => "Manual",
                        4 => "Disabled",
                        _ => "Unknown"
                    };

                    list.Add(new ServiceStatusInfo
                    {
                        Name = name,
                        DisplayName = dispName,
                        Status = DescribeStatus(QueryServiceStatusDirect(name), startTypeStr),
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
                        Status = Loc.T("Kurulu değil"),
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
                    Status = Loc.T("Bilinmiyor"),
                    StartType = "Disabled",
                    IsDisabled = true
                });
            }
        }
        return list;
    }

    /// <summary>Text for the status list: the start type matters as much as the live state.</summary>
    public static string DescribeStatus(string? liveStatus, string startType)
    {
        if (string.Equals(startType, "Disabled", StringComparison.OrdinalIgnoreCase)) return Loc.T("Devre dışı");
        if (liveStatus == "Running") return Loc.T("Çalışıyor");
        if (liveStatus == "Stopped")
            return string.Equals(startType, "Manual", StringComparison.OrdinalIgnoreCase) ? Loc.T("Durduruldu (elle başlar)") : Loc.T("Durduruldu");
        return "Bilinmiyor";
    }

    /// <summary>
    /// The registry DisplayName may be an indirect string ("@oem72.inf,%NAME%;Acer Service Component Service");
    /// the text after the last ';' is the fallback name Windows shows.
    /// </summary>
    public static string CleanDisplayName(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var text = raw.Trim();
        if (text.StartsWith("@"))
        {
            int semi = text.LastIndexOf(';');
            text = semi >= 0 && semi < text.Length - 1 ? text[(semi + 1)..].Trim() : fallback;
        }
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    /// <summary>
    /// Start type Acer installs each service with. Every one of these driver packages declares
    /// StartType = 2 (SERVICE_AUTO_START) in its INF, and the System event log on the AN16-41 shows each
    /// being changed "from auto start to disabled". Used to restore when no backup of the earlier state exists.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> FactoryStartTypes =
        Tier1Services.ToDictionary(s => s, _ => "Automatic", StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a backup with at least one valid recorded service exists.</summary>
    public bool HasRestorableBackup()
    {
        var backup = LoadBackupState();
        return backup.Count > 0 && Tier1Services.Any(s => backup.ContainsKey(s) && IsValidStartupType(backup[s]));
    }

    /// <summary>True when every installed Tier 1 service is already disabled (nothing for the debloat to do).</summary>
    public bool AreAllInstalledServicesDisabled(Dictionary<string, string>? startTypes = null)
    {
        startTypes ??= RecordCurrentStartupTypes();
        return startTypes.Count > 0 && startTypes.Values.All(t => string.Equals(t, "Disabled", StringComparison.OrdinalIgnoreCase));
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
                    FileName = AppPaths.SystemTool("sc.exe"),
                    Arguments = $"query \"{name}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p != null)
                {
                    var output = ReadOutputWithTimeout(p, 2000);
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

    /// <summary>
    /// Reads a child process' stdout with a hard timeout. A synchronous ReadToEnd() blocked forever when the tool
    /// hung, so the WaitForExit() that followed it never ran and the caller froze.
    /// </summary>
    private static string ReadOutputWithTimeout(Process process, int timeoutMs)
    {
        try
        {
            var readTask = process.StandardOutput.ReadToEndAsync();
            if (!readTask.Wait(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                // Killing the process closes the pipe; give the read a moment to return what it already has.
                readTask.Wait(500);
                return readTask.IsCompletedSuccessfully ? readTask.Result : string.Empty;
            }
            process.WaitForExit(500);
            return readTask.Result;
        }
        catch
        {
            return string.Empty;
        }
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
                FileName = AppPaths.SystemTool("schtasks.exe"),
                Arguments = $"/query /tn \"{taskName}\" /fo csv",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p != null)
            {
                var output = ReadOutputWithTimeout(p, 2000);
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
            LastError = Loc.T($"Servis yedek dosyası bozuk: {ex.Message}");
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
            LastError = Loc.T($"Servis yedeği kaydedilemedi: {ex.Message}");
            return false;
        }
    }

    public string GenerateDebloatScript(Dictionary<string, string>? recordedState = null, string? errorLogPath = null)
    {
        errorLogPath ??= AppPaths.UniqueTempFile("nitrolite_debloat_err", ".log");
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
                                 "    if ($vTask.State -ne 'Disabled') { throw '" + Loc.Ps("NitroSenseLauncher görevi devre dışı bırakılamadı.") + "' }; " +
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
               "      if ($vSvc.Status -ne 'Stopped') { throw '" + Loc.Ps("Servis durdurulamadı veya hâlâ çalışıyor.") + "' }; " +
               "      if ($vSvc.StartType -ne 'Disabled') { throw '" + Loc.Ps("Servis başlangıç türü devre dışı bırakılamadı.") + "' }; " +
               "    } " +
               "  } catch { " +
               "    $errs += ($s + ': ' + $_.Exception.Message) " +
               "  } " +
               "}; " +
               taskDisableSnippet +
               "if ($errs.Count -gt 0) { $errs -join \"`n\" | Out-File -FilePath $errLog -Encoding utf8; exit 1 } else { exit 0 }";
    }

    // Source text, kept as constants so the PowerShell builder and the tests can compare against it; the public
    // members translate it for the user.
    internal const string NoBackupSource = "Geri yükleme iptal: kayıtlı servis yedeği yok.";
    internal const string AlreadyDisabledSource = "Servislerin hepsi zaten devre dışı; değişiklik yapılmadı.";

    public static string NoBackupMessage => Loc.T(NoBackupSource);
    public static string AlreadyDisabledMessage => Loc.T(AlreadyDisabledSource);

    /// <summary>
    /// Restores Acer's install-time configuration (<see cref="FactoryStartTypes"/>) when no backup exists.
    /// Services that are not installed are skipped; a service that is set to Automatic but does not stay
    /// running is not an error (Windows starts it again at the next boot). The NitroSenseLauncher task is left alone.
    /// </summary>
    public string GenerateFactoryRestoreScript(string? errorLogPath = null)
    {
        errorLogPath ??= AppPaths.UniqueTempFile("nitrolite_restore_err", ".log");
        var logEscaped = errorLogPath.Replace("'", "''");
        var sb = new System.Text.StringBuilder();
        sb.Append($"$errLog = '{logEscaped}'; Remove-Item -Path $errLog -Force -ErrorAction SilentlyContinue; ");
        sb.Append("$errs = @(); ");
        foreach (var s in Tier1Services)
        {
            string canonicalType = ValidateStartupType(FactoryStartTypes[s], s);
            sb.Append("try { ");
            sb.Append($"$svc = Get-Service -Name '{s}' -ErrorAction SilentlyContinue; ");
            sb.Append("if ($svc) { ");
            sb.Append($"Set-Service -Name '{s}' -StartupType {canonicalType} -ErrorAction Stop; ");
            sb.Append($"Start-Service -Name '{s}' -ErrorAction SilentlyContinue; ");
            sb.Append($"$vSvc = Get-Service -Name '{s}' -ErrorAction Stop; ");
            sb.Append($"if ($vSvc.StartType -ne '{canonicalType}') {{ throw '{Loc.Ps($"Servis başlangıç türü doğrulanamadı: {s}")}' }}; ");
            sb.Append("} ");
            sb.Append("} catch { ");
            sb.Append($"$errs += ('{s}: ' + $_.Exception.Message); ");
            sb.Append("}; ");
        }
        sb.Append("if ($errs.Count -gt 0) { $errs -join \"`n\" | Out-File -FilePath $errLog -Encoding utf8; exit 1 } else { exit 0 }");
        return sb.ToString();
    }

    public async Task<bool> RestoreFactoryDefaultsAsync()
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
            var errLog = AppPaths.UniqueTempFile("nitrolite_restore_err", ".log");
            return await RunElevatedPowerShellAsync(GenerateFactoryRestoreScript(errLog), errLog);
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

    public string GenerateRestoreScript(Dictionary<string, string>? backupState = null, string? errorLogPath = null)
    {
        errorLogPath ??= AppPaths.UniqueTempFile("nitrolite_restore_err", ".log");
        var logEscaped = errorLogPath.Replace("'", "''");
        backupState ??= LoadBackupState();

        var sb = new System.Text.StringBuilder();
        sb.Append($"$errLog = '{logEscaped}'; Remove-Item -Path $errLog -Force -ErrorAction SilentlyContinue; ");
        sb.Append("$errs = @(); ");

        if (backupState == null || backupState.Count == 0 || !(Tier1Services.Any(s => backupState.ContainsKey(s) && !string.IsNullOrWhiteSpace(backupState[s])) || (backupState.TryGetValue("Task:NitroSenseLauncher", out var onlyTask) && (IsActiveTaskState(onlyTask) || IsDisabledTaskState(onlyTask)))))
        {
            sb.Append("$errs += '" + Loc.Ps(NoBackupSource) + "'; ");
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
            sb.Append($"if (-not $svc) {{ throw '{Loc.Ps($"Servis bulunamadı: {s}")}' }}; ");
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
            sb.Append($"if ($vSvc.StartType -ne '{canonicalType}') {{ throw '{Loc.Ps($"Servis başlangıç türü doğrulanamadı: {s}")}' }}; ");
            if (shouldStart)
            {
                sb.Append($"if ($vSvc.Status -ne 'Running') {{ throw '{Loc.Ps($"Servis çalışma durumu doğrulanamadı: {s}")}' }}; ");
            }
            else
            {
                sb.Append($"if ($vSvc.Status -ne 'Stopped') {{ throw '{Loc.Ps($"Servis çalışma durumu doğrulanamadı: {s}")}' }}; ");
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
                sb.Append("  if (-not $task) { throw '" + Loc.Ps("NitroSenseLauncher görevi bulunamadı.") + "' }; ");
                sb.Append("  Enable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
                sb.Append("  $vTask = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
                sb.Append("  if ($vTask.State -eq 'Disabled') { throw '" + Loc.Ps("NitroSenseLauncher görevi etkinleştirilemedi veya doğrulanamadı.") + "' }; ");
                sb.Append("} catch { ");
                sb.Append("  $errs += ('Task:NitroSenseLauncher: ' + $_.Exception.Message); ");
                sb.Append("}; ");
            }
            else if (IsDisabledTaskState(taskState))
            {
                sb.Append("try { ");
                sb.Append("  $task = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
                sb.Append("  if (-not $task) { throw '" + Loc.Ps("NitroSenseLauncher görevi bulunamadı.") + "' }; ");
                sb.Append("  Disable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
                sb.Append("  $vTask = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop; ");
                sb.Append("  if ($vTask.State -ne 'Disabled') { throw '" + Loc.Ps("NitroSenseLauncher görevi devre dışı bırakılamadı veya doğrulanamadı.") + "' }; ");
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
                    LastError = Loc.T($"PowerShell işlemi {runnerExit} çıkış koduyla bitti.");
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
                    FileName = AppPaths.WindowsPowerShell,
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                p = Process.Start(psi);
                if (p == null)
                {
                    LastError = Loc.T("PowerShell işlemi başlatılamadı.");
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
                    LastError = Loc.T($"PowerShell işlemi {PowerShellTimeoutMs / 1000} saniye içinde tamamlanamadı ve arka planda devam ediyor. Süreç sonlanana kadar yeni işlem başlatılamaz.");
                    return false;
                }

                int exitCode = -1;
                try
                {
                    exitCode = p.ExitCode;
                }
                catch (Exception ex)
                {
                    LastError = Loc.T($"PowerShell çıkış kodu okunamadı: {ex.Message}");
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
                    LastError = Loc.T($"PowerShell işlemi {exitCode} çıkış koduyla bitti.");
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
                    LastError = Loc.T($"İşlem iptal: '{BackupStateFilePath}' konumundaki servis yedeği geçersiz veya bozuk; üzerine yazılmadı. Dosyayı kontrol edin.");
                    return false;
                }

                // Clean baseline: record current startup types, running states, and scheduled task state before modifying
                var currentTypes = RecordCurrentStartupTypes();

                // Recording an all-disabled state as the baseline would make "restore" keep everything disabled.
                if (AreAllInstalledServicesDisabled(currentTypes))
                {
                    LastError = AlreadyDisabledMessage;
                    return false;
                }

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
                    LastError = Loc.T($"İşlem iptal: servis yedeği diske yazılamadı. {LastError}").Trim();
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
                        LastError = Loc.T($"İşlem iptal: '{svc}' servisinin yedeğinde başlangıç türü eksik.");
                        return false;
                    }
                    if (!existingBackup.ContainsKey(svc + ":Status") || string.IsNullOrWhiteSpace(existingBackup[svc + ":Status"]))
                    {
                        LastError = Loc.T($"İşlem iptal: '{svc}' servisinin yedeğinde çalışma durumu eksik.");
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
                        LastError = Loc.T($"İşlem iptal: mevcut yedekte '{svc}' servisinin kaydı geçersiz.");
                        return false;
                    }
                }
            }

            // 2. Generate and run debloat script with error logging
            var errLog = AppPaths.UniqueTempFile("nitrolite_debloat_err", ".log");
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
                    LastError = NoBackupMessage;
                }
                return false;
            }

            // Validate all recorded services in backup state
            foreach (var svc in Tier1Services.Where(s => backup.ContainsKey(s)))
            {
                if (!IsValidStartupType(backup[svc]))
                {
                    LastError = Loc.T($"Geri yükleme iptal: yedekte '{svc}' için geçersiz başlangıç türü '{backup[svc]}'.");
                    return false;
                }
            }

            var errLog = AppPaths.UniqueTempFile("nitrolite_restore_err", ".log");
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

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "NitroLite";

    internal Func<string?>? AutoStartReadSeam { get; set; }
    internal Func<string?, bool>? AutoStartWriteSeam { get; set; }
    internal Func<string, bool>? FileExistsSeam { get; set; }

    public bool IsNitroLiteAutoStartEnabled() => !string.IsNullOrWhiteSpace(ReadAutoStartCommand());

    private string? ReadAutoStartCommand()
    {
        if (AutoStartReadSeam != null) return AutoStartReadSeam();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(RunValueName)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    // null deletes the value. Returns false when the registry could not be written.
    private bool WriteAutoStartCommand(string? command)
    {
        if (AutoStartWriteSeam != null) return AutoStartWriteSeam(command);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key == null) return false;
            if (command == null) key.DeleteValue(RunValueName, false);
            else key.SetValue(RunValueName, command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="exePath"/> lies under the user's or Windows' temp folder, e.g. an exe started
    /// straight out of a zip in Explorer. Such a copy disappears, so it must not be registered for autostart.
    /// </summary>
    public static bool IsTemporaryLocation(string? exePath, IEnumerable<string>? tempRoots = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        try
        {
            var full = Path.GetFullPath(exePath);
            tempRoots ??= new[]
            {
                Path.GetTempPath(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
            };
            foreach (var root in tempRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    public static string BuildAutoStartCommand(string exePath, bool startMinimized) =>
        startMinimized ? $"\"{exePath}\" --autostart --minimized" : $"\"{exePath}\" --autostart";

    /// <summary>Executable path from a Run value ("\"C:\...\NitroLite.exe\" --autostart" or an unquoted path).</summary>
    public static string? ParseAutoStartExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();
        if (c.StartsWith("\""))
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }

    /// <summary>Writes or removes the Run entry. Returns false when Windows refused the change.</summary>
    public bool SetNitroLiteAutoStart(bool enable, bool startMinimized = false)
    {
        if (!enable) return WriteAutoStartCommand(null);
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;
        return WriteAutoStartCommand(BuildAutoStartCommand(exePath, startMinimized));
    }

    /// <summary>
    /// Keeps an existing Run entry working: when the executable it points to no longer exists (folder moved or
    /// deleted), it is pointed at this executable; when only the --minimized flag disagrees with the setting, the
    /// flag is fixed and the path kept. Does nothing when autostart is off. Returns true when the entry was changed.
    /// </summary>
    public bool RepairNitroLiteAutoStart(bool startMinimized, string? currentExePath = null)
    {
        var command = ReadAutoStartCommand();
        if (string.IsNullOrWhiteSpace(command)) return false;

        currentExePath ??= Environment.ProcessPath;
        var registeredPath = ParseAutoStartExePath(command);
        var fileExists = FileExistsSeam ?? File.Exists;
        var targetPath = registeredPath != null && fileExists(registeredPath) ? registeredPath : currentExePath;
        if (string.IsNullOrEmpty(targetPath)) return false;

        var desired = BuildAutoStartCommand(targetPath, startMinimized);
        if (string.Equals(command.Trim(), desired, StringComparison.OrdinalIgnoreCase)) return false;
        return WriteAutoStartCommand(desired);
    }

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

    /// <summary>
    /// Closes NitroSense (an Electron app: one main process plus helpers, so the whole tree is killed).
    /// Returns true when no NitroSense process is left afterwards.
    /// </summary>
    public bool KillNitroSenseProcesses()
    {
        var procs = Process.GetProcessesByName("NitroSense");
        foreach (var p in procs)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(true);
                    p.WaitForExit(2000);
                }
            }
            catch { }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
        return !IsNitroSenseRunning();
    }

    /// <summary>
    /// State of the "NitroSenseLauncher" scheduled task, which starts NitroSense at sign-in:
    /// true = enabled, false = disabled, null = not found / unreadable.
    /// </summary>
    public bool? IsNitroSenseLauncherEnabled()
    {
        var state = RecordCurrentScheduledTaskState("NitroSenseLauncher");
        if (IsActiveTaskState(state)) return true;
        if (IsDisabledTaskState(state)) return false;
        return null;
    }
}
