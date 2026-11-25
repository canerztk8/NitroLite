using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// Watches the process that started the worker and reports when it disappears.
/// <para>
/// This is the guarantee that actually holds for an elevated child. A job object cannot be assigned across the
/// integrity boundary — a medium-integrity parent has no <c>PROCESS_SET_QUOTA</c> on a high-integrity child — so the
/// worker has to notice its own orphanhood. One second of latency is the cost; an administrator process left running
/// after the window closed is the thing being avoided.
/// </para>
/// <para>
/// The parent is tracked by an open kernel handle, not by its process id. A pid is only unique among the processes
/// that exist right now: once the parent exits, Windows is free to hand the same number to something else, and a
/// watchdog that re-resolved the id would happily watch a stranger and stay alive. A handle opened while the parent
/// was still running keeps referring to that process and becomes signalled the moment it exits, whatever the id is
/// reused for.
/// </para>
/// </summary>
public sealed class ParentWatchdog : IDisposable
{
    /// <summary>How often the parent is checked. One second is invisible to a user and cheap.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary><c>SYNCHRONIZE</c>: the only right needed to wait on a process handle.</summary>
    private const uint Synchronize = 0x00100000;

    private const uint WaitTimeout = 0x00000102;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly SafeProcessHandle? _handle;

    private ParentWatchdog(int parentProcessId, Action onParentGone, TimeSpan interval, Func<int, bool>? isAlive)
    {

        // Handle mode: opened once, held for the watchdog's life. A handle that cannot be opened means the parent is
        // already gone, which the first tick reports.
        if (isAlive == null) _handle = TryOpenParentHandle(parentProcessId, out var handle) ? handle : null;

        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(interval, _cts.Token).ConfigureAwait(false);
                    if (_cts.IsCancellationRequested) return;

                    bool alive = isAlive != null ? isAlive(parentProcessId) : IsHandleAlive(_handle);
                    if (!alive)
                    {
                        onParentGone();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Disposing the watchdog is the normal way this loop ends.
            }
        });
    }

    /// <summary>
    /// Starts watching. <paramref name="isAlive"/> is injectable so the loop can be tested without spawning a
    /// process and killing it; when it is null the watchdog uses a held handle on the real parent.
    /// </summary>
    public static ParentWatchdog Start(
        int parentProcessId,
        Action onParentGone,
        TimeSpan? interval = null,
        Func<int, bool>? isAlive = null)
    {
        ArgumentNullException.ThrowIfNull(onParentGone);
        return new ParentWatchdog(
            parentProcessId,
            onParentGone,
            interval ?? DefaultInterval,
            isAlive);
    }

    /// <summary>
    /// Opens a <c>SYNCHRONIZE</c> handle on the parent. Returns false when the process is already gone or when this
    /// process is not allowed to open it, both of which mean "do not wait for it".
    /// </summary>
    public static bool TryOpenParentHandle(int processId, out SafeProcessHandle handle)
    {
        handle = new SafeProcessHandle(IntPtr.Zero, ownsHandle: true);
        if (processId <= 0) return false;

        try
        {
            var raw = OpenProcess(Synchronize, bInheritHandle: false, processId);
            if (raw == IntPtr.Zero) return false;

            handle = new SafeProcessHandle(raw, ownsHandle: true);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// True while the held handle is not signalled. A missing handle, a failed wait or a signalled handle all mean
    /// the parent is not there any more.
    /// </summary>
    public static bool IsHandleAlive(SafeProcessHandle? handle)
    {
        if (handle == null || handle.IsInvalid || handle.IsClosed) return false;

        try
        {
            var result = WaitForSingleObject(handle, 0);
            return result == WaitTimeout;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>True when a process with this id exists and has not exited. Used only by the injected path.</summary>
    public static bool IsProcessAlive(int processId)
    {
        if (processId <= 0) return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById throws when nothing has that id.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _handle?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}

/// <summary>
/// The body of the <c>--wmi-worker</c> process: build the executor, serve one client, and leave.
/// <para>
/// Nothing here touches the UI, the tray or the telemetry loop. The worker is a short-lived, single-purpose process
/// that exists only while the window needs an elevated WMI call path, and it exits as soon as its parent does.
/// </para>
/// </summary>
public static class WmiWorkerHost
{
    /// <summary>
    /// How long the worker waits for its parent to connect. Generous on purpose: the parent starts this process with
    /// <c>Verb = "runas"</c>, so a UAC prompt sits between the two, and a person may take a while to click Yes.
    /// </summary>
    public static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Arguments were missing or malformed; nothing was started.</summary>
    public const int ExitInvalidArguments = 2;

    /// <summary>The worker served its client and finished normally.</summary>
    public const int ExitOk = 0;

    /// <summary>Nobody connected in time, or the handshake was refused.</summary>
    public const int ExitNoClient = 4;

    /// <summary>The parent went away while the worker was running.</summary>
    public const int ExitParentGone = 5;

    /// <summary>Runs the worker to completion and returns the process exit code.</summary>
    public static int Run(StartupMode mode)
    {
        if (mode == null || !mode.IsValidWorkerRequest)
        {
            // The pipe name is only echoed when it passed the allow-list; otherwise just its length is logged, so a
            // crafted argument cannot write arbitrary text into the log.
            var pipeText = StartupMode.IsValidPipeName(mode?.PipeName) ? mode!.PipeName : $"<rejected, {mode?.PipeName?.Length ?? 0} chars>";
            AppLog.Warn("--wmi-worker rejected: missing or invalid argument",
                $"pipe='{pipeText}' parent={mode?.ParentProcessId}");
            return ExitInvalidArguments;
        }

        using var cts = new CancellationTokenSource();
        var parentGone = 0;

        using var watchdog = ParentWatchdog.Start(mode.ParentProcessId, () =>
        {
            Interlocked.Exchange(ref parentGone, 1);
            try { cts.Cancel(); } catch { }

            // The worker is not in a kill-on-close job (it must outlive a killed window long enough to hand the fans
            // back), so this watchdog is what ends it. Cancelling cannot interrupt a firmware call that hangs, so a
            // deadline ends the process regardless: the hand-back has had its time by then.
            _ = Task.Run(async () =>
            {
                await Task.Delay(OrphanExitDeadline).ConfigureAwait(false);
                AppLog.Warn("--wmi-worker did not finish after the parent closed; exiting");
                Environment.Exit(ExitParentGone);
            });
        });

        AcerWmiExecutor? executor = null;
        WmiWorkerServer? server = null;
        try
        {
            executor = new AcerWmiExecutor();
            // The parent id is the authentication gate: every connection is checked against it, so a process that
            // merely runs as the same user cannot take the helper over.
            server = new WmiWorkerServer(mode.PipeName, executor, mode.ParentProcessId);

            AppLog.Info("--wmi-worker started", $"pipe='{mode.PipeName}' parent={mode.ParentProcessId}");

            var (served, executorBusy) = ServeAndHandBackAsync(server, AcceptTimeout, cts.Token)
                .GetAwaiter().GetResult();

            // A hand-back that did not finish in time leaves the executor busy; it is then not disposed, because
            // disposing would wait on the same lock forever.
            if (executorBusy) executor = null;

            if (Volatile.Read(ref parentGone) == 1)
            {
                AppLog.Info("--wmi-worker exiting because the parent closed");
                return ExitParentGone;
            }

            AppLog.Info(served ? "--wmi-worker served the client" : "--wmi-worker found no client");
            return served ? ExitOk : ExitNoClient;
        }
        catch (Exception ex)
        {
            AppLog.Error("--wmi-worker unexpected error", $"{ex.GetType().Name}: {ex.Message}");
            return ExitNoClient;
        }
        finally
        {
            server?.Dispose();
            executor?.Dispose();
        }
    }

    /// <summary>How long the exit path waits for the "fans to Auto" call before leaving anyway.</summary>
    public static readonly TimeSpan FanReleaseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a worker whose parent is gone may still run. Longer than the hand-back's own limit, so a normal exit
    /// always comes first; this only ends a worker stuck in a firmware call.
    /// </summary>
    public static readonly TimeSpan OrphanExitDeadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Serves one client, then decides whether the fans go back to the firmware. Returns whether a client was served,
    /// and whether the hand-back call was still running when its time ran out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hand-back mirrors the Linux helper's "manual owner": a fixed speed is only safe while the window's curve is
    /// watching the temperature, so when the window dies with one in place (killed, crashed, out of memory) the worker
    /// writes Auto once, through the same call the window uses for Auto, before it leaves.
    /// </para>
    /// <para>
    /// It runs whenever the pipe is lost, whatever the reason: Auto is always the thermally safe direction. It is
    /// skipped only when the last fan write was not a fixed speed, or when the client declared with
    /// <see cref="WmiWorkerCommand.SetFanHandBack"/> that the speed is to stay (the one-shot <c>--fan</c> command). A
    /// window that dropped this worker on purpose (a timed-out call) re-sends its last manual fan command to the new
    /// worker it connects to, so a late Auto from here does not decide where the fans end.
    /// </para>
    /// </remarks>
    internal static async Task<(bool Served, bool ExecutorBusy)> ServeAndHandBackAsync(
        WmiWorkerServer server,
        TimeSpan acceptTimeout,
        CancellationToken cancellationToken)
    {
        bool served = false;
        bool busy = false;
        try
        {
            served = await server.RunAsync(acceptTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (server.HoldsManualFans && server.HandBackOnDisconnect)
            {
                busy = !server.ReleaseManualFans(FanReleaseTimeout);
            }
        }

        return (served, busy);
    }
}
