using System.Globalization;
using System.IO;
using System.IO.Pipes;
using NitroLite.Core;

namespace NitroLite.Desktop;

/// <summary>
/// Keeps one NitroLite on the desktop, and lets a second launch bring the first one forward.
/// </summary>
/// <remarks>
/// <para>
/// The WPF window used a named mutex. A mutex alone is not enough here: the second process has to tell the first one
/// to show itself, and a mutex carries no message. The signal is a named pipe, which is the one primitive that behaves
/// the same on Windows and on Linux, so the same code path runs on both.
/// </para>
/// <para>
/// The mutex is still used on Windows because it is released by the kernel when the process dies, including a crash.
/// A lock file is the Linux equivalent, and it is opened with <see cref="FileShare.None"/> so the operating system
/// enforces the exclusion rather than a flag inside the file.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>The mutex the WPF window used, kept so an upgrade does not run two copies side by side.</summary>
    public const string MutexName = @"Global\NitroLite_SingleInstance_Mutex_AN16";

    /// <summary>Used when the global namespace is not available, which happens in a restricted session.</summary>
    public const string MutexNameFallback = @"Local\NitroLite_SingleInstance_Mutex_AN16";

    /// <summary>The pipe a second launch writes one byte to.</summary>
    public const string PipeName = "NitroLite.SingleInstance.AN16";

    /// <summary>
    /// The file a second launch drops a timestamp into when the pipe refuses it. An elevated NitroLite (the Task
    /// Scheduler auto-start) creates its pipe with a high integrity label, so a normal launch cannot open it for
    /// writing and the window would never come forward: "Access is denied" every time, for a reason the user cannot
    /// see. A file in the data folder has no such label, and the first instance polls it once a second.
    /// </summary>
    public const string WakeFileName = "nitrolite.wake";

    /// <summary>How often the first instance looks for a wake request the pipe could not deliver.</summary>
    private static readonly TimeSpan WakePollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Set to <c>1</c> to start without taking the machine's slot. Only <c>scripts/screenshot.ps1</c> uses it: the
    /// harness runs against a throwaway data folder while the user's own NitroLite sits in the tray, and the guard
    /// would otherwise make it exit before the first window appears.
    /// </summary>
    public const string AllowSecondInstanceVariable = "NITROLITE_ALLOW_SECOND_INSTANCE";

    private readonly Mutex? _mutex;
    private readonly FileStream? _lockFile;
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _standalone;
    private readonly long _startedAtTicks = DateTime.UtcNow.Ticks;
    private NamedPipeServerStream? _server;
    private Task? _listener;
    private Timer? _wakeWatch;
    private long _wakeSeen;
    private bool _disposed;

    private SingleInstanceGuard(Mutex? mutex, FileStream? lockFile, bool isFirst, bool standalone = false,
        bool ownsMachineWide = false)
    {
        _mutex = mutex;
        _lockFile = lockFile;
        _standalone = standalone;
        IsFirstInstance = isFirst;
        OwnsMachineWide = ownsMachineWide;
    }

    /// <summary>True when this process is the one that owns the machine's NitroLite.</summary>
    public bool IsFirstInstance { get; }

    /// <summary>
    /// False only for a copy that runs on the per-session name because another user's session holds the machine-wide
    /// (<c>Global\</c>) slot (fast user switching): that copy shares the same fans and EC with the first session's
    /// copy, so hardware writes should be left to the owner. True for the <c>Global\</c> owner, for the only copy on a
    /// machine whose global namespace is unavailable, for a harness copy (<see cref="AllowSecondInstanceVariable"/>),
    /// and on Linux for the copy that holds the per-user lock file.
    /// </summary>
    public bool OwnsMachineWide { get; }

    private readonly object _activationGate = new();
    private Action? _activationRequested;
    private bool _activationPending;

    /// <summary>
    /// Raised on a background thread when a second launch asked to be shown. A request that arrives before anything
    /// has subscribed (the listener starts before the window exists) is kept and raised once for the first handler.
    /// </summary>
    public event Action? ActivationRequested
    {
        add
        {
            if (value == null) return;
            bool raisePending;
            lock (_activationGate)
            {
                raisePending = _activationPending && _activationRequested == null;
                _activationRequested += value;
                if (raisePending) _activationPending = false;
            }

            if (raisePending) value();
        }
        remove
        {
            lock (_activationGate) _activationRequested -= value;
        }
    }

    /// <summary>Raises <see cref="ActivationRequested"/>, or remembers the request when nobody is listening yet.</summary>
    private void RaiseActivation()
    {
        Action? handlers;
        lock (_activationGate)
        {
            handlers = _activationRequested;
            if (handlers == null)
            {
                _activationPending = true;
                return;
            }
        }

        handlers();
    }

    /// <summary>
    /// Takes the machine's slot. Never throws: a platform that refuses the mutex still gets a window, because refusing
    /// to start is worse than starting twice.
    /// </summary>
    public static SingleInstanceGuard Acquire()
    {
        // The screenshot harness asks for this explicitly. It is not a way to run two copies by accident: the variable
        // has to be set by hand, and the harness pairs it with a throwaway data folder so the second copy never reads
        // or writes the user's settings.
        if (IsSecondInstanceAllowed())
        {
            // A harness copy runs on its own data folder and is allowed to write, as it always was; reporting it as
            // "another session's copy" would make the screenshots and the trailer recordings read-only.
            return new SingleInstanceGuard(null, null, isFirst: true, standalone: true, ownsMachineWide: true);
        }

        if (OperatingSystem.IsWindows())
        {
            bool refusedByAnotherSession = false;

            foreach (var name in new[] { MutexName, MutexNameFallback })
            {
                try
                {
                    var mutex = CreateMutex(name, out bool createdNew);
                    if (createdNew)
                    {
                        return new SingleInstanceGuard(mutex, null, isFirst: true,
                            ownsMachineWide: OwnsMachineWideFor(name, refusedByAnotherSession));
                    }

                    // Somebody else owns it. This process is the second launch.
                    mutex.Dispose();
                    return new SingleInstanceGuard(null, null, isFirst: false);
                }
                catch (UnauthorizedAccessException)
                {
                    // Refused on the machine-wide name. Two very different owners look identical here: an ELEVATED
                    // NitroLite in this same session (the auto-start task; its mutex carries the administrator DACL a
                    // normal launch cannot open), or a copy in another user's session. The first must be treated as
                    // "already running" — otherwise this launch takes the per-session name and becomes a second full
                    // instance with a second fan loop on the same desktop — and the wake file brings it forward.
                    if (string.Equals(name, MutexName, StringComparison.Ordinal) && IsAnotherCopyInThisSession())
                    {
                        return new SingleInstanceGuard(null, null, isFirst: false);
                    }

                    // Another user's session: the per-session name is tried next, so two users logged on at once each
                    // get their own window instead of the second one silently refusing to start.
                    refusedByAnotherSession = true;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("The single-instance lock could not be acquired", $"{name}: {ex.Message}");
                }
            }

            // Both names were refused, so another session owns the machine-wide slot and the per-session one too.
            // When neither name could be used for another reason nobody else is known to own the machine, so this copy
            // is treated as the owner.
            return new SingleInstanceGuard(null, null, isFirst: !refusedByAnotherSession,
                ownsMachineWide: !refusedByAnotherSession);
        }

        try
        {
            var path = AppPaths.GetDataFile("nitrolite.lock");
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstanceGuard(null, stream, isFirst: true, ownsMachineWide: true);
        }
        catch (IOException)
        {
            // The lock file is held by a running instance.
            return new SingleInstanceGuard(null, null, isFirst: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The single-instance lock could not be acquired", ex.Message);
            return new SingleInstanceGuard(null, null, isFirst: true, ownsMachineWide: true);
        }
    }

    /// <summary>
    /// Takes the machine's slot, waiting for a copy that is on its way out.
    /// </summary>
    /// <remarks>
    /// Only the restart path uses this. A restart starts the replacement <em>before</em> the old process has let go of
    /// the slot — starting it afterwards would mean nothing is left to start it — so the first few attempts are
    /// expected to fail. The wait is bounded: a copy that never lets go is a copy that is still running, and the
    /// caller falls back to signalling it rather than leaving a second window on the desktop.
    /// </remarks>
    public static SingleInstanceGuard AcquireWhenFree(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var guard = Acquire();
            if (guard.IsFirstInstance) return guard;

            guard.Dispose();
            if (DateTime.UtcNow >= deadline) return new SingleInstanceGuard(null, null, isFirst: false);

            // Short enough that the window appears the moment the old one is gone, long enough that the loop is not a
            // spin. The old process spends most of its exit handing the fans back, which is bounded at two seconds.
            Thread.Sleep(120);
        }
    }

    /// <summary>
    /// Opens the named mutex, or creates it with a DACL that grants the current user <c>Synchronize | Modify</c> and
    /// nobody else anything. The user SID is the same in the elevated and the normal token, so another copy of the same
    /// user can open it.
    /// </summary>
    /// <remarks>
    /// An existing mutex is opened first, asking only for the two rights the DACL grants. <see cref="MutexAcl.Create"/>
    /// on an existing name asks for full control, which this DACL deliberately does not give, so calling it first made
    /// every second launch fail with "access denied" and fall through to the per-session name. A creation that loses a
    /// race to another launch is answered the same way: open again. An elevated owner still refuses a normal launch
    /// (its High integrity label blocks the write right); that surfaces as <see cref="UnauthorizedAccessException"/> and
    /// the caller decides with <see cref="DecideRefusedGlobal"/>. Falls back to the default DACL if the explicit one
    /// cannot be built.
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Mutex CreateMutex(string name, out bool createdNew)
    {
        // The rights the current user gets on the mutex, and the rights an existing one is opened with.
        const System.Security.AccessControl.MutexRights UserMutexRights =
            System.Security.AccessControl.MutexRights.Synchronize | System.Security.AccessControl.MutexRights.Modify;

        System.Security.AccessControl.MutexSecurity? security = null;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (identity.User is { } user)
            {
                security = new System.Security.AccessControl.MutexSecurity();
                security.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(
                    user, UserMutexRights, System.Security.AccessControl.AccessControlType.Allow));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                      or InvalidOperationException or ArgumentException)
        {
            security = null;
        }

        if (security == null) return new Mutex(initiallyOwned: true, name, out createdNew);

        for (int attempt = 0; ; attempt++)
        {
            // Throws UnauthorizedAccessException when the mutex exists and this process may not open it.
            if (MutexAcl.TryOpenExisting(name, UserMutexRights, out var existing))
            {
                createdNew = false;
                return existing;
            }

            try
            {
                return MutexAcl.Create(initiallyOwned: true, name, out createdNew, security);
            }
            catch (UnauthorizedAccessException) when (attempt == 0)
            {
                // Another launch created it between the open and the create; open it on the next pass.
            }
        }
    }

    /// <summary>What a refused machine-wide name means for this launch.</summary>
    public enum RefusalDecision
    {
        /// <summary>A NitroLite window of this user runs in this session (elevated): this launch is the second one.</summary>
        AlreadyRunning,

        /// <summary>Nothing of ours runs here, so another user's session owns the name: take the per-session one.</summary>
        OtherSession
    }

    /// <summary>One process of this executable's name, as far as the refusal decision needs it.</summary>
    public readonly record struct SessionProcess(int Id, int SessionId, string? CommandLine);

    /// <summary>
    /// Flags that mark a process of this executable as something other than a window: the elevated helper, a one-shot
    /// command, the report. Such a process holds no slot, so it is not "a NitroLite already running".
    /// </summary>
    private static readonly string[] NonWindowFlags =
    [
        "--cli", "--wmi-worker", "--mode", "--fan", "--profile", "--hardware-report", "--pssvc-sensors",
        "--fix-lighting", "--fix-services", "--restore-mux", "--optimize-pc", "--restore-optimize", "--uninstall-cleanup"
    ];

    /// <summary>
    /// Decides what a refused <c>Global\</c> name means, from the process list. Pure, so every rule is testable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A process counts as a running window when it is in this session, is not this process and not its parent (a
    /// Scoop or WinGet shim carries the same name and waits for its child), and its command line does not carry a
    /// non-window flag. A command line that could not be read (an elevated process seen from a normal one) counts: the
    /// elevated auto-start window is exactly the case this exists for.
    /// </para>
    /// <para>
    /// It fails safe. A list that could not be read (<paramref name="processes"/> null) answers
    /// <see cref="RefusalDecision.AlreadyRunning"/>: a launch that brings nothing forward is a click the user repeats,
    /// a second full instance is a second fan loop writing the same EC.
    /// </para>
    /// </remarks>
    public static RefusalDecision DecideRefusedGlobal(
        IReadOnlyList<SessionProcess>? processes, int selfId, int selfSessionId, int parentId)
    {
        if (processes == null) return RefusalDecision.AlreadyRunning;

        foreach (var p in processes)
        {
            if (p.Id == selfId || p.Id == parentId || p.SessionId != selfSessionId) continue;
            if (IsNonWindowCommandLine(p.CommandLine)) continue;
            return RefusalDecision.AlreadyRunning;
        }

        return RefusalDecision.OtherSession;
    }

    /// <summary>True when a command line carries one of <see cref="NonWindowFlags"/> as an argument.</summary>
    public static bool IsNonWindowCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;

        foreach (var token in commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = token.Trim('"').Split('=', 2)[0];
            if (NonWindowFlags.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>The refusal decision for the real machine.</summary>
    private static bool IsAnotherCopyInThisSession()
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var (processes, parentId) = ReadSessionProcesses(self.Id, self.ProcessName);
            return DecideRefusedGlobal(processes, self.Id, self.SessionId, parentId) == RefusalDecision.AlreadyRunning;
        }
        catch (Exception ex)
        {
            // Fail safe: see DecideRefusedGlobal.
            AppLog.Warn("The process list could not be read for the single-instance check", ex.Message);
            return true;
        }
    }

    /// <summary>
    /// The processes of this executable's name with their command lines (WMI), and this process's parent id. Falls back
    /// to the plain process list without command lines when WMI does not answer; null when neither can be read.
    /// </summary>
    private static (IReadOnlyList<SessionProcess>? Processes, int ParentId) ReadSessionProcesses(int selfId, string processName)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var list = new List<SessionProcess>();
                int parentId = 0;
                var name = processName.Replace("'", "", StringComparison.Ordinal) + ".exe";
                using var searcher = new System.Management.ManagementObjectSearcher(
                    @"root\cimv2",
                    $"SELECT ProcessId, ParentProcessId, SessionId, CommandLine FROM Win32_Process WHERE Name = '{name}'",
                    new System.Management.EnumerationOptions { ReturnImmediately = true, Timeout = TimeSpan.FromSeconds(3) });
                using var results = searcher.Get();
                foreach (System.Management.ManagementObject item in results)
                {
                    using (item)
                    {
                        int id = Convert.ToInt32(item["ProcessId"], System.Globalization.CultureInfo.InvariantCulture);
                        int session = Convert.ToInt32(item["SessionId"], System.Globalization.CultureInfo.InvariantCulture);
                        if (id == selfId)
                            parentId = Convert.ToInt32(item["ParentProcessId"], System.Globalization.CultureInfo.InvariantCulture);
                        list.Add(new SessionProcess(id, session, item["CommandLine"] as string));
                    }
                }

                return (list, parentId);
            }
            catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException
                                          or UnauthorizedAccessException or InvalidCastException or FormatException
                                          or OverflowException)
            {
                AppLog.Warn("The process command lines could not be read for the single-instance check", ex.Message);
            }
        }
#endif

        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(processName);
            try
            {
                return (processes.Select(p => new SessionProcess(p.Id, p.SessionId, null)).ToList(), 0);
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                      or NotSupportedException or UnauthorizedAccessException)
        {
            return (null, 0);
        }
    }

    /// <summary>
    /// True when a NitroLite that owns a slot (the machine-wide or this session's) is running. Takes nothing: used by
    /// the one-shot hardware commands to warn that a running window will overwrite their write on its next tick.
    /// </summary>
    public static bool IsAnotherInstanceRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { MutexName, MutexNameFallback })
            {
                try
                {
                    if (Mutex.TryOpenExisting(name, out var existing))
                    {
                        existing.Dispose();
                        return true;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // It exists; this process just may not open it (elevated owner or another session).
                    if (string.Equals(name, MutexName, StringComparison.Ordinal) && IsAnotherCopyInThisSession())
                        return true;
                }
                catch (Exception ex) when (ex is IOException or WaitHandleCannotBeOpenedException)
                {
                    // Not there, or the name is unusable here.
                }
            }

            return false;
        }

        try
        {
            var path = AppPaths.GetDataFile("nitrolite.lock");
            if (!File.Exists(path)) return false;
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a copy that created <paramref name="acquiredName"/> owns the machine. The per-session name means "not
    /// the owner" only when the machine-wide name was refused because another session really holds it; when it failed
    /// for any other reason (a restricted session without the global namespace) this copy is the only one and must be
    /// allowed to write.
    /// </summary>
    public static bool OwnsMachineWideFor(string acquiredName, bool refusedByAnotherSession) =>
        string.Equals(acquiredName, MutexName, StringComparison.Ordinal) || !refusedByAnotherSession;

    /// <summary>True when <see cref="AllowSecondInstanceVariable"/> asks for a copy that owns no slot.</summary>
    private static bool IsSecondInstanceAllowed()
    {
        var value = Environment.GetEnvironmentVariable(AllowSecondInstanceVariable);
        return !string.IsNullOrWhiteSpace(value)
            && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Starts listening for a second launch. Only the first instance calls this; the second one calls
    /// <see cref="SignalExistingInstance"/> and exits.
    /// </summary>
    public void Listen()
    {
        // A standalone copy owns no slot, so it has nothing to listen on: binding the pipe would fail against the
        // running instance and log a warning for a condition the caller asked for.
        if (!IsFirstInstance || _standalone || _listener != null) return;

        StartWakeWatch();

        _listener = Task.Run(async () =>
        {
            int failures = 0;

            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    // CurrentUserOnly: the signal is a wake-up, not a message, but a pipe any local account can
                    // write to is still a way to make the window jump to the front at will.
                    _server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await _server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);

                    // One byte is the whole protocol: the message is the connection. The count is checked rather
                    // than assumed — a client that connects and closes without writing has not asked for anything,
                    // and raising the event for it would be a free way to yank the window forward.
                    var buffer = new byte[1];
                    int read = await _server.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);

                    _server.Dispose();
                    _server = null;

                    failures = 0;
                    if (read == 1) RaiseActivation();
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Only a stop ends the listener. A client that broke the pipe mid-write, or a pipe instance the
                    // previous iteration had not quite released yet, used to end it for good, and from then on a
                    // second launch could no longer bring the window forward. The wait grows on repeated failures so a
                    // pipe that keeps refusing costs a log line now and then rather than one a second.
                    if (_stop.IsCancellationRequested) return;

                    failures++;
                    if (failures == 1 || failures % 30 == 0)
                        AppLog.Warn("The single-instance listener failed and will retry", ex.Message);

                    // The broken instance is released before the pause, not after it, so the name is free again
                    // when the next iteration opens it.
                    try { _server?.Dispose(); } catch { /* already gone */ }
                    _server = null;

                    try
                    {
                        await Task.Delay(ListenerRetryDelay(failures), _stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception delayEx) when (delayEx is OperationCanceledException or ObjectDisposedException)
                    {
                        // Stopped, or disposed, while waiting.
                        return;
                    }
                }
                finally
                {
                    // A throw between the accept and the dispose would otherwise leave the handle open and the
                    // next iteration would fail on a pipe name that is still in use.
                    try { _server?.Dispose(); } catch { /* already gone */ }
                    _server = null;
                }
            }
        });
    }

    /// <summary>The pause before the listener opens the pipe again after a failure: 250 ms, doubling, at most 10 s.</summary>
    private static TimeSpan ListenerRetryDelay(int failures) =>
        TimeSpan.FromMilliseconds(Math.Min(10_000, 250 * Math.Pow(2, Math.Min(failures - 1, 6))));

    /// <summary>
    /// Tells the running instance to come forward. Returns false when nobody answered, which is what a stale lock
    /// looks like.
    /// </summary>
    /// <remarks>
    /// The pipe is tried first because it is instant and touches no disk. When it is refused — the running copy is
    /// elevated, or another account owns it — the request is left in <see cref="WakeFileName"/> instead, which the
    /// first instance picks up within a second. A launch that does nothing at all is the one outcome worth avoiding:
    /// the user clicked the icon and has to be given a window.
    /// </remarks>
    public static bool SignalExistingInstance()
    {
        var piped = false;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            client.WriteByte(1);
            client.Flush();
            piped = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The running window could not be reached through the pipe", ex.Message);
        }

        // Both are sent when the pipe worked: the wake file is one tick behind, so a request that lands just as the
        // pipe closes still arrives, and a duplicate wake is a window that was already coming forward.
        return PostWakeRequest() || piped;
    }

    /// <summary>
    /// Leaves a wake request for the first instance. Returns false when even that failed, which means the data folder
    /// is not writable and there is no way to reach the running copy at all.
    /// </summary>
    public static bool PostWakeRequest()
    {
        try
        {
            File.WriteAllText(WakeFilePath, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The running window could not be asked to come forward", ex.Message);
            return false;
        }
    }

    /// <summary>Where a wake request is left.</summary>
    public static string WakeFilePath => AppPaths.GetDataFile(WakeFileName);

    /// <summary>
    /// The pending wake request, or null when there is none newer than <paramref name="sinceTicks"/>. A request from
    /// before this instance started is ignored: it belongs to a launch that has already been answered, or to a copy
    /// that is no longer running.
    /// </summary>
    public static long? PendingWakeRequest(long sinceTicks)
    {
        try
        {
            var path = WakeFilePath;
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path).Trim();
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long stamp)) return null;

            return stamp > sinceTicks ? stamp : null;
        }
        catch (IOException)
        {
            // The second instance is halfway through writing it. The next poll a second later gets a whole file.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Starts looking for wake requests. <see cref="Listen"/> calls this; the tests call it directly.</summary>
    /// <remarks>
    /// Split out because a developer machine already runs a NitroLite that owns the slot and the pipe name, so the
    /// tests cannot start a real listener: they would be skipped on exactly the machines that would catch a regression.
    /// </remarks>
    public void WatchForWakeRequests() => StartWakeWatch();

    /// <summary>Starts looking for wake requests the pipe could not deliver.</summary>
    private void StartWakeWatch()
    {
        if (_wakeWatch != null) return;

        // A leftover request from the previous run is answered at once, otherwise the timer would keep re-reading it.
        _wakeSeen = PendingWakeRequest(_startedAtTicks) ?? 0;

        _wakeWatch = new Timer(_ => CheckWakeRequest(), null, WakePollInterval, WakePollInterval);
    }

    /// <summary>One poll: raises the activation event when a request newer than the last one answered is waiting.</summary>
    private void CheckWakeRequest()
    {
        if (_disposed) return;

        var stamp = PendingWakeRequest(Math.Max(_startedAtTicks, _wakeSeen));
        if (stamp == null) return;

        _wakeSeen = stamp.Value;
        RaiseActivation();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stop.Cancel();

        try { _wakeWatch?.Dispose(); } catch { /* the poll has already stopped */ }
        _wakeWatch = null;

        try { _server?.Dispose(); } catch { /* the listener is already gone */ }
        _server = null;

        try { _mutex?.ReleaseMutex(); } catch { /* not owned, or already released */ }
        _mutex?.Dispose();
        _lockFile?.Dispose();
        _stop.Dispose();
    }
}
