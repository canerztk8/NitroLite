using System.ComponentModel;
using System.Diagnostics;

namespace NitroLite.Core.Hardware.Wmi;

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
    private readonly Func<string, string, TimeSpan, CancellationToken, Task<WmiWorkerClient>> _connect;
    private readonly Func<bool> _isElevated;
    private readonly JobObjectGuard? _job;
    private bool _disposed;

    /// <summary>
    /// <paramref name="start"/> and <paramref name="connect"/> are injectable so the UAC-cancelled path can be tested
    /// without a UAC prompt and the pipe path without an elevated process. <paramref name="isElevated"/> decides
    /// whether the helper needs a prompt at all.
    /// </summary>
    public WmiWorkerLauncher(
        string? executablePath = null,
        Func<ProcessStartInfo, Process?>? start = null,
        Func<string, string, TimeSpan, CancellationToken, Task<WmiWorkerClient>>? connect = null,
        Func<bool>? isElevated = null)
    {
        _executablePath = executablePath ?? Environment.ProcessPath ?? "NitroLite.exe";
        _start = start ?? (psi => Process.Start(psi));
        _connect = connect ?? ((pipe, nonce, timeout, ct) => WmiWorkerClient.ConnectAsync(pipe, nonce, timeout, ct));
        _isElevated = isElevated ?? MemoryCleanerService.IsRunningAsAdministrator;
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

    /// <summary>How many times a helper was started. Used by the tests and the log.</summary>
    public int Attempts { get; private set; }

    /// <summary>
    /// The command line the helper is started with. Exposed so the argument shape can be asserted against
    /// <see cref="StartupMode.Parse"/> without launching anything.
    /// </summary>
    public static string BuildArguments(string pipeName, int parentProcessId, string nonce) =>
        $"--wmi-worker --pipe \"{pipeName}\" --parent-pid {parentProcessId} --nonce {nonce}";

    /// <summary>
    /// Starts the helper and connects to it. Returns null when the user declined, the process could not be started,
    /// or the handshake failed; <see cref="LastError"/> says which.
    /// </summary>
    public async Task<WmiWorkerClient?> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;
        if (UserDeclined)
        {
            LastError = "the user declined elevation earlier in this session";
            return null;
        }

        Attempts++;

        var pipeName = "nitrolite-wmi-" + Guid.NewGuid().ToString("N");
        var nonce = WmiWorkerClient.GenerateNonce();
        // An already-elevated parent passes its token to the child, so asking for runas would raise a prompt for a
        // privilege the process already holds. That is the whole point of the optional Task Scheduler entry: the
        // user answers UAC once, at the moment they turn it on, and never again.
        bool needsPrompt = !IsElevated;

        var startInfo = new ProcessStartInfo(_executablePath, BuildArguments(pipeName, Environment.ProcessId, nonce))
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
            LastError = "the user declined the elevation prompt";
            AppLog.Info("Doğrudan WMI için yönetici izni verilmedi",
                "Kullanıcı UAC penceresinde Hayır'ı seçti; bu oturumda bir daha sorulmayacak.");
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            AppLog.Warn("--wmi-worker başlatılamadı", LastError);
            return null;
        }

        if (process == null)
        {
            LastError = "the helper process did not start";
            return null;
        }

        // Best effort: this fails with access denied for an elevated child, which is exactly the case the worker's
        // own parent watchdog covers.
        _job?.Assign(process);

        try
        {
            var client = await _connect(pipeName, nonce, ConnectTimeout, cancellationToken).ConfigureAwait(false);
            LastError = "";
            AppLog.Info("--wmi-worker bağlandı", $"pipe='{pipeName}' pid={process.Id}");
            return client;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            AppLog.Warn("--wmi-worker el sıkışması başarısız", LastError);
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _job?.Dispose();
    }
}
