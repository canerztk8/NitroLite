using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NitroLite.Core;

public enum StandbyPurgeTaskState
{
    /// <summary>The helper is not next to NitroLite.exe (broken build/copy); setup is impossible.</summary>
    Unavailable,
    NotInstalled,
    /// <summary>Installed, but the installed helper or the task no longer matches this NitroLite version.</summary>
    NeedsUpdate,
    Ready
}

/// <summary>
/// One-time setup that lets the standby purge run without a UAC prompt each time.
/// <para>
/// Setup (one UAC prompt) copies the NitroLite.StandbyPurge helper to C:\Program Files\NitroLite\StandbyPurge, which
/// only administrators can write, and registers the scheduled task \NitroLite\StandbyPurge that runs it with highest
/// privileges for the current user. NitroLite then starts the task on demand. The task has no trigger, takes no
/// arguments and the helper only empties the standby list. Pointing the task at the copy in Program Files, not at
/// the build folder under Documents, matters: a user-writable target would let any program swap the exe and get
/// administrator rights silently.
/// </para>
/// </summary>
public class StandbyPurgeTask
{
    public const string TaskFolder = @"\NitroLite";
    public const string TaskName = "StandbyPurge";
    public const string HelperExeName = "NitroLite.StandbyPurge.exe";
    /// <summary>
    /// Files the helper may consist of. A normal build is framework-dependent (exe + dll + runtimeconfig, needs the
    /// .NET runtime); the release package ships a Native AOT build that is the exe alone and needs no runtime.
    /// </summary>
    public static readonly string[] CandidateFiles =
    {
        "NitroLite.StandbyPurge.exe",
        "NitroLite.StandbyPurge.dll",
        "NitroLite.StandbyPurge.runtimeconfig.json"
    };

    /// <summary>The helper files actually present next to NitroLite.exe (exe first); empty when the exe is missing.</summary>
    public string[] BundledHelperFiles()
    {
        if (!File.Exists(Path.Combine(BundledDirectory, HelperExeName))) return Array.Empty<string>();
        var files = CandidateFiles.Where(f => File.Exists(Path.Combine(BundledDirectory, f))).ToArray();
        // A framework-dependent exe without its dll/runtimeconfig would not start: treat as missing.
        bool frameworkDependent = files.Length > 1;
        if (frameworkDependent && files.Length != CandidateFiles.Length) return Array.Empty<string>();
        return files;
    }

    public string InstallDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NitroLite", "StandbyPurge");

    public string BundledDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "StandbyPurge");

    public string InstalledExePath => Path.Combine(InstallDirectory, HelperExeName);

    public string? LastError { get; private set; }

    internal int ElevatedTimeoutMs { get; set; } = 60000;

    // Seams: tests replace the Task Scheduler and the elevated PowerShell.
    internal Func<RegisteredTaskInfo?>? TaskReader { get; set; }
    internal Func<string, Task<int>>? ElevatedRunner { get; set; }

    internal sealed record RegisteredTaskInfo(string ActionPath, string? Arguments, bool RunsHighest);

    // ---------------------------------------------------------------- state

    public StandbyPurgeTaskState GetState()
    {
        var files = BundledHelperFiles();
        if (files.Length == 0) return StandbyPurgeTaskState.Unavailable;

        var task = ReadTask();
        if (task == null) return StandbyPurgeTaskState.NotInstalled;

        bool taskOk = task.RunsHighest
                      && string.IsNullOrWhiteSpace(task.Arguments)
                      && string.Equals(Path.GetFullPath(task.ActionPath.Trim('"')), Path.GetFullPath(InstalledExePath), StringComparison.OrdinalIgnoreCase);
        if (!taskOk) return StandbyPurgeTaskState.NeedsUpdate;

        // Switching between a normal build and the release package changes the exe, so this also catches that.
        var bundled = ComputeHashes(BundledDirectory, files);
        var installed = ComputeHashes(InstallDirectory, files);
        if (bundled == null || installed == null) return StandbyPurgeTaskState.NeedsUpdate;
        return bundled.SequenceEqual(installed) ? StandbyPurgeTaskState.Ready : StandbyPurgeTaskState.NeedsUpdate;
    }

    /// <summary>SHA-256 of each of <paramref name="files"/> in <paramref name="directory"/>; null when one is missing.</summary>
    public static string[]? ComputeHashes(string directory, IReadOnlyList<string> files)
    {
        try
        {
            return files.Select(f =>
            {
                using var s = File.OpenRead(Path.Combine(directory, f));
                return Convert.ToHexString(SHA256.HashData(s));
            }).ToArray();
        }
        catch
        {
            return null;
        }
    }

    private RegisteredTaskInfo? ReadTask()
    {
        if (TaskReader != null) return TaskReader();
        try
        {
            dynamic? service = CreateScheduler();
            if (service == null) return null;
            dynamic task = service.GetFolder(TaskFolder).GetTask(TaskName);
            dynamic definition = task.Definition;
            dynamic action = definition.Actions.Item(1);       // IExecAction (1-based collection)
            int runLevel = definition.Principal.RunLevel;       // 1 = TASK_RUNLEVEL_HIGHEST
            return new RegisteredTaskInfo((string)action.Path, (string?)action.Arguments, runLevel == 1);
        }
        catch
        {
            return null;    // folder or task missing, or Task Scheduler unavailable
        }
    }

    private static dynamic? CreateScheduler()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type == null) return null;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    // ---------------------------------------------------------------- run

    /// <summary>
    /// Starts the task and waits for the helper to finish. Returns its exit code
    /// (0 = purged, see NitroLite.StandbyPurge), or null when the task could not be started or did not finish in time.
    /// </summary>
    public async Task<int?> RunAsync(int timeoutMs = 15000)
    {
        LastError = null;
        return await Task.Run(() =>
        {
            try
            {
                dynamic? service = CreateScheduler();
                if (service == null) { LastError = "Görev Zamanlayıcı kullanılamıyor."; return (int?)null; }
                dynamic task = service.GetFolder(TaskFolder).GetTask(TaskName);
                DateTime before = DateTime.Now.AddSeconds(-1);
                task.Run(null);

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    Thread.Sleep(100);
                    int state = task.State;                     // 4 = TASK_STATE_RUNNING
                    DateTime lastRun = task.LastRunTime;
                    if (state != 4 && lastRun >= before)
                        return (int?)(int)task.LastTaskResult;
                }
                LastError = "Temizleme görevi zamanında bitmedi.";
                return null;
            }
            catch (Exception ex)
            {
                LastError = $"Temizleme görevi başlatılamadı: {ex.Message}";
                return null;
            }
        });
    }

    // ---------------------------------------------------------------- setup / removal

    private static readonly Regex SafeUserName = new(@"^[\p{L}\p{N} ._\-]+(\\[\p{L}\p{N} ._\-$]+)?$", RegexOptions.Compiled);

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// Elevated script: copy the helper to Program Files, check every copied file against the hash NitroLite
    /// computed just before (the source folder is user-writable), then register the task for <paramref name="userId"/>.
    /// </summary>
    public string GenerateInstallScript(string userId, IReadOnlyList<string> files, string[] expectedHashes, string errorLogPath)
    {
        if (!SafeUserName.IsMatch(userId)) throw new ArgumentException($"Geçersiz kullanıcı adı: {userId}", nameof(userId));
        if (files.Count == 0 || files[0] != HelperExeName || files.Any(f => !CandidateFiles.Contains(f)))
            throw new ArgumentException("Geçersiz yardımcı dosya listesi.", nameof(files));
        if (expectedHashes.Length != files.Count || expectedHashes.Any(h => !Regex.IsMatch(h, "^[0-9A-F]{64}$")))
            throw new ArgumentException("Geçersiz dosya özeti.", nameof(expectedHashes));

        var sb = new System.Text.StringBuilder();
        sb.Append($"$errLog = {Quote(errorLogPath)}; Remove-Item -Path $errLog -Force -ErrorAction SilentlyContinue; ");
        sb.Append("try { ");
        sb.Append($"$src = {Quote(BundledDirectory)}; $dst = {Quote(InstallDirectory)}; ");
        sb.Append("if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force -ErrorAction Stop }; ");
        sb.Append("New-Item -ItemType Directory -Path $dst -Force -ErrorAction Stop | Out-Null; ");
        for (int i = 0; i < files.Count; i++)
        {
            var f = Quote(files[i]);
            sb.Append($"Copy-Item -LiteralPath (Join-Path $src {f}) -Destination (Join-Path $dst {f}) -Force -ErrorAction Stop; ");
            sb.Append($"if ((Get-FileHash -LiteralPath (Join-Path $dst {f}) -Algorithm SHA256).Hash -ne '{expectedHashes[i]}') " +
                      $"{{ Remove-Item -LiteralPath $dst -Recurse -Force; throw ('Kopyalanan dosya doğrulanamadı: ' + {f}) }}; ");
        }
        sb.Append($"$exe = Join-Path $dst {Quote(HelperExeName)}; ");
        sb.Append("$action = New-ScheduledTaskAction -Execute $exe; ");
        sb.Append($"$principal = New-ScheduledTaskPrincipal -UserId {Quote(userId)} -LogonType Interactive -RunLevel Highest; ");
        sb.Append("$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
                  "-ExecutionTimeLimit (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable:$false; ");
        sb.Append($"Register-ScheduledTask -TaskPath '{TaskFolder}\\' -TaskName '{TaskName}' -Action $action -Principal $principal " +
                  "-Settings $settings -Description 'NitroLite: bekleme belleğini boşaltır (yalnızca NitroLite tarafından elle veya otomatik başlatılır).' -Force -ErrorAction Stop | Out-Null; ");
        sb.Append("exit 0 ");
        sb.Append("} catch { $_.Exception.Message | Out-File -FilePath $errLog -Encoding utf8; exit 1 }");
        return sb.ToString();
    }

    public string GenerateUninstallScript(string errorLogPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"$errLog = {Quote(errorLogPath)}; Remove-Item -Path $errLog -Force -ErrorAction SilentlyContinue; $errs = @(); ");
        sb.Append($"try {{ $t = Get-ScheduledTask -TaskPath '{TaskFolder}\\' -TaskName '{TaskName}' -ErrorAction SilentlyContinue; " +
                  $"if ($t) {{ Unregister-ScheduledTask -TaskPath '{TaskFolder}\\' -TaskName '{TaskName}' -Confirm:$false -ErrorAction Stop }} }} " +
                  "catch { $errs += $_.Exception.Message }; ");
        sb.Append($"try {{ $s = New-Object -ComObject Schedule.Service; $s.Connect(); $s.GetFolder('\\').DeleteFolder('{TaskFolder.TrimStart('\\')}', 0) }} catch {{ }}; ");
        sb.Append($"$dst = {Quote(InstallDirectory)}; ");
        sb.Append("try { if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force -ErrorAction Stop }; " +
                  "$parent = Split-Path $dst -Parent; if ((Test-Path -LiteralPath $parent) -and -not (Get-ChildItem -LiteralPath $parent -Force)) { Remove-Item -LiteralPath $parent -Force } } " +
                  "catch { $errs += $_.Exception.Message }; ");
        sb.Append("if ($errs.Count -gt 0) { $errs -join \"`n\" | Out-File -FilePath $errLog -Encoding utf8; exit 1 } else { exit 0 }");
        return sb.ToString();
    }

    public async Task<bool> InstallAsync()
    {
        LastError = null;
        var files = BundledHelperFiles();
        var hashes = files.Length == 0 ? null : ComputeHashes(BundledDirectory, files);
        if (hashes == null)
        {
            LastError = "Temizleme yardımcısı NitroLite klasöründe bulunamadı (StandbyPurge).";
            return false;
        }
        string userId;
        try { userId = System.Security.Principal.WindowsIdentity.GetCurrent().Name; }
        catch (Exception ex) { LastError = ex.Message; return false; }

        var errLog = AppPaths.UniqueTempFile("nitrolite_purge_setup_err", ".log");
        string script;
        try { script = GenerateInstallScript(userId, files, hashes, errLog); }
        catch (Exception ex) { LastError = ex.Message; return false; }

        bool ok = await RunElevatedAsync(script, errLog);
        if (ok && GetState() != StandbyPurgeTaskState.Ready)
        {
            LastError = "Kurulum bitti ama görev doğrulanamadı.";
            return false;
        }
        return ok;
    }

    public async Task<bool> UninstallAsync()
    {
        LastError = null;
        var errLog = AppPaths.UniqueTempFile("nitrolite_purge_remove_err", ".log");
        return await RunElevatedAsync(GenerateUninstallScript(errLog), errLog);
    }

    private async Task<bool> RunElevatedAsync(string script, string errLog)
    {
        int exit;
        if (ElevatedRunner != null)
        {
            exit = await ElevatedRunner(script);
        }
        else
        {
            exit = await Task.Run(() =>
            {
                try
                {
                    var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                    using var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = AppPaths.WindowsPowerShell,
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    if (p == null) return -1;
                    if (!p.WaitForExit(ElevatedTimeoutMs)) return -2;
                    return p.ExitCode;
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return -3;  // the user answered "No" to the UAC prompt
                }
                catch
                {
                    return -1;
                }
            });
        }

        try
        {
            if (File.Exists(errLog))
            {
                var text = File.ReadAllText(errLog).Trim();
                if (text.Length > 0) LastError = text;
                File.Delete(errLog);
            }
        }
        catch { }

        if (exit != 0 && LastError == null)
        {
            LastError = exit switch
            {
                -3 => "Yönetici izni verilmedi.",
                -2 => "İşlem zamanında bitmedi.",
                _ => $"İşlem başarısız oldu (kod {exit})."
            };
        }
        return exit == 0;
    }
}
