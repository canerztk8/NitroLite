using System.IO;
using System.Security.Principal;
using System.Text;

namespace NitroLite.Core;

/// <summary>How an elevated-auto-start change ended.</summary>
public enum ElevatedStartupOutcome
{
    /// <summary>The scheduled task or the Run entry now matches what was asked for.</summary>
    Applied = 0,

    /// <summary>The user said No to the elevation prompt. Nothing was changed.</summary>
    Cancelled = 1,

    /// <summary>Windows refused, or the command failed. Nothing was changed.</summary>
    Failed = 2
}

/// <summary>The result of one change, with the reason when it did not apply.</summary>
public sealed record ElevatedStartupResult(ElevatedStartupOutcome Outcome, string? Detail)
{
    public bool Applied => Outcome == ElevatedStartupOutcome.Applied;

    public static ElevatedStartupResult Applied_() => new(ElevatedStartupOutcome.Applied, null);
    public static ElevatedStartupResult Cancelled(string? detail = null) => new(ElevatedStartupOutcome.Cancelled, detail);
    public static ElevatedStartupResult Failed(string? detail = null) => new(ElevatedStartupOutcome.Failed, detail);
}

/// <summary>
/// Optional elevated auto-start through Windows Task Scheduler.
/// <para>
/// The window is <c>asInvoker</c> and stays that way. On a machine where Acer's services have been removed, the
/// direct-WMI backend needs an elevated helper, and without this the user would answer a UAC prompt at the start of
/// every session. A scheduled task with <c>RunLevel HighestAvailable</c> starts NitroLite with the elevated token
/// once, at logon, so the helper inherits it and no prompt appears.
/// </para>
/// <para>
/// It is off by default and costs one UAC prompt when it is turned on and one when it is turned off. That is the
/// whole trade: two prompts ever, instead of one per session.
/// </para>
/// <para>
/// The task is registered from an XML file rather than from <c>schtasks</c> flags because three of the defaults are
/// wrong for a tray application: Windows would kill it after 72 hours (<c>ExecutionTimeLimit</c>), refuse to start it
/// on battery, and stop it when the machine unplugs. All three are turned off in the template below.
/// </para>
/// </summary>
public sealed class ElevatedStartupService
{
    /// <summary>The scheduled task's name. Fixed, so a second install replaces the first instead of adding one.</summary>
    public const string TaskName = "NitroLite_ElevatedStartup";

    /// <summary><c>ERROR_CANCELLED</c>: the user clicked No on the UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>The flags the task passes, matching what the standard Run entry uses.</summary>
    public const string AutostartFlag = "--autostart";

    /// <summary>
    /// The executable the task must launch. The published package ships the Avalonia front end under this name, which
    /// is deliberately the name the retired WPF build used: the in-place updater swaps <c>NitroLite.exe</c> and nothing
    /// else, so a package that renamed the binary would leave every existing user unable to update, and every task the
    /// old build registered pointing at a file that no longer exists.
    /// </summary>
    public const string ExpectedExecutableName = "NitroLite.exe";

    /// <summary>
    /// The name the project builds under. A development build is <c>NitroLite.Desktop.exe</c> and is accepted so the
    /// feature can be exercised without a package; the release script renames the binary before zipping it.
    /// </summary>
    public const string DevelopmentExecutableName = "NitroLite.Desktop.exe";

    /// <summary>True when <paramref name="path"/> is a NitroLite front end, packaged or from a development build.</summary>
    public static bool IsDesktopExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileName(Unquote(path));
        return string.Equals(name, ExpectedExecutableName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, DevelopmentExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips the quotes the task engine's <c>&lt;Command&gt;</c> element carries. <see cref="BuildTaskXml"/> quotes a
    /// path with a space in it, so the value read back out of a registered task is <c>"C:\…\NitroLite.exe"</c> and
    /// <see cref="Path.GetFileName(string)"/> would answer <c>NitroLite.exe"</c> — a name that matches nothing.
    /// </summary>
    private static string Unquote(string path) => path.Trim().Trim('"');

    private readonly IProcessRunner _runner;
    private readonly Func<bool> _isElevated;
    private readonly Func<string?> _executablePath;
    private readonly Func<string?> _userId;
    private readonly Func<string> _tempFilePath;

    /// <summary>
    /// Every dependency is injectable so the XML, the mutual exclusion and the cancelled prompt can be tested without
    /// registering a real task, reading the real registry or raising a real UAC prompt.
    /// </summary>
    public ElevatedStartupService(
        IProcessRunner? runner = null,
        Func<bool>? isElevated = null,
        Func<string?>? executablePath = null,
        Func<string?>? userId = null,
        Func<string>? tempFilePath = null)
    {
        _runner = runner ?? new ProcessRunner();
        _isElevated = isElevated ?? MemoryCleanerService.IsRunningAsAdministrator;
        _executablePath = executablePath ?? (() => Environment.ProcessPath);
        _userId = userId ?? CurrentUserSid;
        _tempFilePath = tempFilePath ?? (() => Path.Combine(Path.GetTempPath(), $"nitrolite-task-{Guid.NewGuid():N}.xml"));
    }

    /// <summary>The current user's SID, which is what the task's principal and logon trigger are keyed on.</summary>
    public static string? CurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes the standard <c>HKCU\...\Run</c> entry. Set by the window, which owns the debloat service. Null leaves
    /// the Run key alone, which is what the tests use.
    /// </summary>
    public Func<bool>? RemoveStandardAutoStart { get; set; }

    /// <summary>Writes the standard <c>HKCU\...\Run</c> entry. Takes the "start minimized" flag.</summary>
    public Func<bool, bool>? SetStandardAutoStart { get; set; }

    /// <summary>True when the standard <c>HKCU\...\Run</c> entry exists.</summary>
    public Func<bool>? IsStandardAutoStartEnabled { get; set; }

    /// <summary>Why the last change failed, for the log and the settings row.</summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// The task's arguments, matching <see cref="DebloatService.BuildAutoStartCommand"/> so the two registrations
    /// start the application the same way.
    /// </summary>
    public static string BuildTaskArguments(bool startMinimized) =>
        startMinimized ? $"{AutostartFlag} --minimized" : AutostartFlag;

    /// <summary>
    /// The task definition. Pure, so the three Windows defaults that would break a tray application can be asserted
    /// in a unit test: no execution time limit, start on battery, keep running when unplugged.
    /// </summary>
    public static string BuildTaskXml(string executablePath, string arguments, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // The command is quoted because a path with a space would otherwise be split by the task engine.
        string command = executablePath.StartsWith('"') ? executablePath : $"\"{executablePath}\"";

        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        xml.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
        xml.AppendLine("  <RegistrationInfo>");
        xml.AppendLine("    <Author>NitroLite</Author>");
        xml.AppendLine("    <Description>NitroLite elevated auto-start (optional)</Description>");
        xml.AppendLine($"    <URI>\\{TaskName}</URI>");
        xml.AppendLine("  </RegistrationInfo>");
        xml.AppendLine("  <Triggers>");
        xml.AppendLine("    <LogonTrigger>");
        xml.AppendLine("      <Enabled>true</Enabled>");
        xml.AppendLine($"      <UserId>{Escape(userId)}</UserId>");
        xml.AppendLine("    </LogonTrigger>");
        xml.AppendLine("  </Triggers>");
        xml.AppendLine("  <Principals>");
        xml.AppendLine("    <Principal id=\"Author\">");
        xml.AppendLine($"      <UserId>{Escape(userId)}</UserId>");
        xml.AppendLine("      <LogonType>InteractiveToken</LogonType>");
        xml.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
        xml.AppendLine("    </Principal>");
        xml.AppendLine("  </Principals>");
        xml.AppendLine("  <Settings>");
        xml.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        xml.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        xml.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        xml.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
        xml.AppendLine("    <StartWhenAvailable>false</StartWhenAvailable>");
        xml.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
        xml.AppendLine("    <IdleSettings>");
        xml.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
        xml.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
        xml.AppendLine("    </IdleSettings>");
        xml.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
        xml.AppendLine("    <Enabled>true</Enabled>");
        xml.AppendLine("    <Hidden>false</Hidden>");
        xml.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
        xml.AppendLine("    <WakeToRun>false</WakeToRun>");
        // PT0S is "no limit". Without it Windows terminates the task after 72 hours of runtime.
        xml.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
        xml.AppendLine("    <Priority>7</Priority>");
        xml.AppendLine("  </Settings>");
        xml.AppendLine("  <Actions Context=\"Author\">");
        xml.AppendLine("    <Exec>");
        xml.AppendLine($"      <Command>{Escape(command)}</Command>");
        xml.AppendLine($"      <Arguments>{Escape(arguments)}</Arguments>");
        xml.AppendLine("    </Exec>");
        xml.AppendLine("  </Actions>");
        xml.AppendLine("</Task>");
        return xml.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// True when the task is registered. Asked of Windows rather than of a settings file: a task the user deleted
    /// from Task Scheduler must show as off, and a settings flag would keep claiming it is on.
    /// </summary>
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            "schtasks.exe", $"/Query /TN \"{TaskName}\"", elevate: false, cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    /// <summary>
    /// The <c>&lt;Command&gt;</c> element of a task definition, or an empty string when the XML has none. Read with a
    /// substring rather than an XML parser: the document carries a namespace and a schema location, and the one value
    /// this needs is a single element.
    /// </summary>
    public static string ReadCommandFromTaskXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return "";

        const string open = "<Command>";
        const string close = "</Command>";

        int start = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";

        start += open.Length;
        int end = xml.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return "";

        return xml[start..end].Trim();
    }

    /// <summary>
    /// Registers the task and removes the standard Run entry, so logon starts NitroLite once. Returns
    /// <see cref="ElevatedStartupOutcome.Cancelled"/> when the user declined the prompt, having changed nothing.
    /// </summary>
    public async Task<ElevatedStartupResult> EnableAsync(bool startMinimized, CancellationToken cancellationToken = default)
    {
        string? exePath = _executablePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return Fail("the executable path is unknown");
        }

        // A dev build runs under dotnet.exe or a test host, so this is a warning rather than a refusal: the shipped
        // app always resolves to NitroLite.exe, and refusing would make the feature untestable.
        if (!IsDesktopExecutable(exePath))
        {
            AppLog.Warn("Otomatik başlatma hedefi beklenen çalıştırılabilir değil",
                $"{exePath} (beklenen: {ExpectedExecutableName})");
        }

        string? userId = _userId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Fail("the current user's SID could not be read");
        }

        string xml = BuildTaskXml(exePath, BuildTaskArguments(startMinimized), userId);
        string tempPath = _tempFilePath();

        try
        {
            // UTF-16 with a BOM: schtasks reads the declaration and refuses a file whose encoding disagrees with it.
            await File.WriteAllTextAsync(tempPath, xml, Encoding.Unicode, cancellationToken).ConfigureAwait(false);

            var result = await _runner.RunAsync(
                "schtasks.exe",
                $"/Create /TN \"{TaskName}\" /XML \"{tempPath}\" /F",
                elevate: !_isElevated(),
                cancellationToken).ConfigureAwait(false);

            if (result.Cancelled)
            {
                LastError = "the user declined the elevation prompt";
                AppLog.Info("Yönetici yetkisiyle otomatik başlatma için izin verilmedi",
                    "Kullanıcı UAC penceresinde Hayır'ı seçti; görev oluşturulmadı.");
                return ElevatedStartupResult.Cancelled(LastError);
            }

            if (!result.Success)
            {
                return Fail($"schtasks /Create exited with {result.ExitCode}: {FirstLine(result.StandardError)}");
            }
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // The XML is a temporary file, not a record: leaving it behind would litter the temp folder with a
            // definition that no longer matches the registered task.
            try { File.Delete(tempPath); } catch { }
        }

        // Two registrations would start NitroLite twice at logon. The task wins because the user just chose it.
        if (RemoveStandardAutoStart?.Invoke() == false)
        {
            AppLog.Warn("Standart başlangıç kaydı kaldırılamadı",
                "Yönetici görevi oluşturuldu; HKCU Run kaydı silinemedi.");
        }

        LastError = "";
        AppLog.Info("Yönetici yetkisiyle otomatik başlatma etkin", $"görev='{TaskName}'");
        return ElevatedStartupResult.Applied_();
    }

    /// <summary>Deletes the task. Returns <see cref="ElevatedStartupOutcome.Cancelled"/> when the prompt was declined.</summary>
    public async Task<ElevatedStartupResult> DisableAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            "schtasks.exe",
            $"/Delete /TN \"{TaskName}\" /F",
            elevate: !_isElevated(),
            cancellationToken).ConfigureAwait(false);

        if (result.Cancelled)
        {
            LastError = "the user declined the elevation prompt";
            AppLog.Info("Yönetici görevi kaldırılmadı", "Kullanıcı UAC penceresinde Hayır'ı seçti.");
            return ElevatedStartupResult.Cancelled(LastError);
        }

        // A task that is not there is the state the caller asked for, so a "cannot find" answer is a success.
        if (!result.Success && await IsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return Fail($"schtasks /Delete exited with {result.ExitCode}: {FirstLine(result.StandardError)}");
        }

        LastError = "";
        AppLog.Info("Yönetici yetkisiyle otomatik başlatma kapatıldı", $"görev='{TaskName}'");
        return ElevatedStartupResult.Applied_();
    }

    /// <summary>
    /// Turns the standard Run entry on, removing the elevated task first. The order matters: if the task cannot be
    /// removed — the user declined the prompt, or Windows refused — the Run entry is left alone, because two
    /// registrations would start NitroLite twice at logon.
    /// </summary>
    public async Task<ElevatedStartupResult> EnableStandardAsync(bool startMinimized, CancellationToken cancellationToken = default)
    {
        if (await IsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            var removed = await DisableAsync(cancellationToken).ConfigureAwait(false);
            if (!removed.Applied) return removed;
        }

        if (SetStandardAutoStart?.Invoke(startMinimized) == false)
        {
            return Fail("the Run entry could not be written");
        }

        LastError = "";
        return ElevatedStartupResult.Applied_();
    }

    /// <summary>True when either registration exists, which is what the settings row reports.</summary>
    public async Task<bool> IsAnyAutoStartEnabledAsync(CancellationToken cancellationToken = default) =>
        await IsEnabledAsync(cancellationToken).ConfigureAwait(false)
        || IsStandardAutoStartEnabled?.Invoke() == true;

    private ElevatedStartupResult Fail(string detail)
    {
        LastError = detail;
        AppLog.Warn("Yönetici yetkisiyle otomatik başlatma değiştirilemedi", detail);
        return ElevatedStartupResult.Failed(detail);
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        int newline = text.IndexOfAny(['\r', '\n']);
        return (newline < 0 ? text : text[..newline]).Trim();
    }
}
