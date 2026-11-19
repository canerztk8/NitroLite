using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// Why the helper was not started. The log carries the English detail in <see cref="WmiWorkerLauncher.LastError"/>;
/// this is what lets the window say the reason in the user's language instead of pasting that detail into a sentence.
/// </summary>
public enum WmiWorkerRefusal
{
    /// <summary>Nothing has been refused. Either the helper is running or nobody has asked for it yet.</summary>
    None,

    /// <summary>The user said no to the UAC prompt earlier in this session.</summary>
    UserDeclined,

    /// <summary>The executable sits where another local account could replace it, and the launch would elevate.</summary>
    UntrustedLocation,

    /// <summary>The process could not be started at all.</summary>
    StartFailed,

    /// <summary>The helper started but the pipe handshake did not complete.</summary>
    HandshakeFailed
}

/// <summary>
/// Starts the elevated <c>--wmi-worker</c> helper, at most once per session, and only when the user asked for it.
/// <para>
/// The window runs <c>asInvoker</c> and stays that way. Elevation is requested at the moment a hardware write is
/// actually needed, not at launch: a UAC prompt on startup, before the user has asked for anything, is the fastest
/// way to get an application uninstalled. If the prompt is declined the answer is remembered for the rest of the
/// session — asking again on every fan click would be harassment, and the user already said no.
/// </para>
/// <para>
/// The helper is tied to this process twice over: a job object where the OS allows it, and the worker's own
/// <c>--parent-pid</c> watchdog where it does not (a medium-integrity parent cannot assign a high-integrity child).
/// The same process id is what the worker checks every incoming connection against, so the handshake secret never
/// has to appear on a command line.
/// </para>
/// </summary>
public sealed class WmiWorkerLauncher : IDisposable
{
    /// <summary><c>ERROR_CANCELLED</c>: the user clicked No on the UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>How long the helper has to connect back after the prompt is accepted.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly string _executablePath;
    private readonly Func<ProcessStartInfo, Process?> _start;
    private readonly Func<string, string, int?, TimeSpan, CancellationToken, Task<WmiWorkerClient>> _connect;
    private readonly Func<bool> _isElevated;
    private readonly Func<string, bool> _isTrustedLocation;
    private readonly JobObjectGuard? _job;
    private bool _disposed;

    /// <summary>
    /// True once the executable's location has been refused. The answer cannot change while this process runs — the
    /// file does not move — so the second attempt only has to remember it instead of reading the ACLs again and
    /// printing the same warning on every telemetry tick.
    /// </summary>
    private bool _locationRefused;

    /// <summary>
    /// <paramref name="start"/> and <paramref name="connect"/> are injectable so the UAC-cancelled path can be tested
    /// without a UAC prompt and the pipe path without an elevated process. <paramref name="isElevated"/> decides
    /// whether the helper needs a prompt at all, and <paramref name="isTrustedLocation"/> decides whether the
    /// executable is somewhere another account could have replaced it.
    /// </summary>
    public WmiWorkerLauncher(
        string? executablePath = null,
        Func<ProcessStartInfo, Process?>? start = null,
        Func<string, string, int?, TimeSpan, CancellationToken, Task<WmiWorkerClient>>? connect = null,
        Func<bool>? isElevated = null,
        Func<string, bool>? isTrustedLocation = null)
    {
        _executablePath = executablePath ?? Environment.ProcessPath ?? "NitroLite.exe";
        _start = start ?? (psi => Process.Start(psi));
        _connect = connect ?? ((pipe, nonce, pid, timeout, ct) => WmiWorkerClient.ConnectAsync(pipe, nonce, timeout, pid, ct));
        _isElevated = isElevated ?? MemoryCleanerService.IsRunningAsAdministrator;
        _isTrustedLocation = isTrustedLocation ?? IsTrustedHelperLocation;
        _job = JobObjectGuard.Create();
    }

    /// <summary>
    /// True when this process already holds an elevated token, so the helper inherits it and no prompt is needed.
    /// That is the case when NitroLite was started by the optional Task Scheduler entry.
    /// </summary>
    public bool IsElevated => _isElevated();

    /// <summary>True once the user has declined the prompt. No further attempt is made this session.</summary>
    public bool UserDeclined { get; private set; }

    /// <summary>Why the last attempt failed, for the log and the settings page. Empty after a success.</summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// Which kind of failure <see cref="LastError"/> describes, so a localised status line can name it without
    /// parsing an English sentence.
    /// </summary>
    public WmiWorkerRefusal Refusal { get; private set; }

    /// <summary>How many times a helper was started. Used by the tests and the log.</summary>
    public int Attempts { get; private set; }

    /// <summary>
    /// The command line the helper is started with. Exposed so the argument shape can be asserted against
    /// <see cref="StartupMode.Parse"/> without launching anything.
    /// <para>
    /// There is no nonce here on purpose. A command line is readable through <c>Win32_Process.CommandLine</c> by any
    /// process running as the same user, and a secret that any same-user process can read is not a secret. The
    /// handshake value is generated by the window and sent over the pipe; the worker authenticates the caller by
    /// process id, which cannot be forged.
    /// </para>
    /// </summary>
    public static string BuildArguments(string pipeName, int parentProcessId) =>
        $"--wmi-worker --pipe \"{pipeName}\" --parent-pid {parentProcessId}";

    /// <summary>
    /// True when the helper executable cannot be replaced by a principal wider than the signed-in user: anything
    /// under the profile, and Program Files, which only an administrator can write to.
    /// <para>
    /// A folder created directly under a drive root (<c>C:\WinTools</c>) is <em>not</em> trusted on a stock Windows.
    /// The drive root grants <c>Authenticated Users</c> modify to everything created beneath it, so a file in such a
    /// folder is writable — and replaceable — by any local account. The README and the refusal message therefore
    /// send the user to the profile or to Program Files, where that grant does not apply.
    /// </para>
    /// </summary>
    public static bool IsTrustedHelperLocation(string executablePath)
    {
        try
        {
            return DirectoryTrust.IsExecutableTrusted(executablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Starts the helper and connects to it. Returns null when the user declined, the process could not be started,
    /// or the handshake failed; <see cref="LastError"/> says which.
    /// </summary>
    public async Task<WmiWorkerClient?> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;
        if (UserDeclined)
        {
            Refusal = WmiWorkerRefusal.UserDeclined;
            LastError = "the user declined elevation earlier in this session";
            return null;
        }

        // An already-elevated parent passes its token to the child, so asking for runas would raise a prompt for a
        // privilege the process already holds. That is the whole point of the optional Task Scheduler entry: the
        // user answers UAC once, at the moment they turn it on, and never again.
        bool needsPrompt = !IsElevated;

        // The folder check guards privilege that is about to be *granted*, and only the runas launch grants any:
        // the helper would start with an administrator token that the user just handed to whatever file happens to
        // be at that path. A helper started by an already-elevated parent inherits the token the process is running
        // under; refusing it protects nothing the user has not already given away, and it took away the one recovery
        // a user has. The AN16-61 report is the measurement: NitroLite was installed under C:\WinTools, every launch
        // was refused, and "restart it as administrator" changed nothing because the refusal came before the
        // elevation question was even asked.
        //
        // The connection is asked for again on every hardware read, so a refusal that is re-evaluated and re-logged
        // each time turned one line into a warning every second and buried the rest of the report it appeared in.
        // The path is refused once; the reason stays in LastError for the settings page.
        if (needsPrompt && (_locationRefused || !_isTrustedLocation(_executablePath)))
        {
            Refusal = WmiWorkerRefusal.UntrustedLocation;
            LastError = "the helper executable sits in a folder any local account can write to; "
                        + "start NitroLite as administrator, or move it under your own profile or into Program Files";
            if (!_locationRefused)
            {
                _locationRefused = true;
                AppLog.Warn("--wmi-worker was not started", $"{LastError}: {_executablePath}");
            }

            return null;
        }

        Attempts++;

        var pipeName = "nitrolite-wmi-" + Guid.NewGuid().ToString("N");
        var nonce = WmiWorkerClient.GenerateNonce();

        var startInfo = new ProcessStartInfo(_executablePath, BuildArguments(pipeName, Environment.ProcessId))
        {
            // runas is what raises the prompt. UseShellExecute has to be true for a verb to be honoured.
            UseShellExecute = needsPrompt,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = !needsPrompt
        };

        if (needsPrompt) startInfo.Verb = "runas";

        Process? process;
        try
        {
            process = _start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            UserDeclined = true;
            Refusal = WmiWorkerRefusal.UserDeclined;
            LastError = "the user declined the elevation prompt";
            AppLog.Info("Administrator permission was not granted for direct WMI",
                "The user chose No in the UAC prompt; it will not be asked again this session.");
            return null;
        }
        catch (Exception ex)
        {
            Refusal = WmiWorkerRefusal.StartFailed;
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            AppLog.Warn("--wmi-worker could not be started", LastError);
            return null;
        }

        if (process == null)
        {
            Refusal = WmiWorkerRefusal.StartFailed;
            LastError = "the helper process did not start";
            return null;
        }

        // The handle is only needed to assign the job and to read the id; the process itself outlives it.
        using (process)
        {
            // Best effort: this fails with access denied for an elevated child, which is exactly the case the
            // worker's own parent watchdog covers.
            _job?.Assign(process);

            try
            {
                var client = await _connect(pipeName, nonce, process.Id, ConnectTimeout, cancellationToken).ConfigureAwait(false);
                Refusal = WmiWorkerRefusal.None;
                LastError = "";
                AppLog.Info("--wmi-worker connected", $"pipe='{pipeName}' pid={process.Id}");
                return client;
            }
            catch (Exception ex)
            {
                Refusal = WmiWorkerRefusal.HandshakeFailed;
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                AppLog.Warn("--wmi-worker handshake failed", LastError);
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _job?.Dispose();
    }
}
