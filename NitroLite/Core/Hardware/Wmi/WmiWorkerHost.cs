using System.Diagnostics;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// Watches the process that started the worker and reports when it disappears.
/// <para>
/// This is the guarantee that actually holds for an elevated child. A job object cannot be assigned across the
/// integrity boundary — a medium-integrity parent has no <c>PROCESS_SET_QUOTA</c> on a high-integrity child — so the
/// worker has to notice its own orphanhood. One second of latency is the cost; an administrator process left running
/// after the window closed is the thing being avoided.
/// </para>
/// </summary>
public sealed class ParentWatchdog : IDisposable
{
    /// <summary>How often the parent is checked. One second is invisible to a user and cheap.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private ParentWatchdog(int parentProcessId, Action onParentGone, TimeSpan interval, Func<int, bool> isAlive)
    {
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(interval, _cts.Token).ConfigureAwait(false);
                    if (_cts.IsCancellationRequested) return;

                    if (!isAlive(parentProcessId))
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
    /// process and killing it.
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
            isAlive ?? IsProcessAlive);
    }

    /// <summary>True when a process with this id exists and has not exited.</summary>
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
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
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
            AppLog.Warn("--wmi-worker reddedildi: eksik veya geçersiz argüman",
                $"pipe='{mode?.PipeName}' parent={mode?.ParentProcessId} nonce={mode?.Nonce.Length ?? 0} karakter");
            return ExitInvalidArguments;
        }

        using var cts = new CancellationTokenSource();
        var parentGone = 0;

        using var watchdog = ParentWatchdog.Start(mode.ParentProcessId, () =>
        {
            Interlocked.Exchange(ref parentGone, 1);
            try { cts.Cancel(); } catch { }
        });

        try
        {
            using var executor = new AcerWmiExecutor();
            using var server = new WmiWorkerServer(mode.PipeName, mode.Nonce, executor);

            AppLog.Info("--wmi-worker başladı", $"pipe='{mode.PipeName}' parent={mode.ParentProcessId}");

            var served = server.RunAsync(AcceptTimeout, cts.Token).GetAwaiter().GetResult();

            if (Volatile.Read(ref parentGone) == 1)
            {
                AppLog.Info("--wmi-worker ebeveyn kapandığı için çıkıyor");
                return ExitParentGone;
            }

            AppLog.Info(served ? "--wmi-worker istemciyi tamamladı" : "--wmi-worker istemci bulamadı");
            return served ? ExitOk : ExitNoClient;
        }
        catch (Exception ex)
        {
            AppLog.Error("--wmi-worker beklenmedik hata", $"{ex.GetType().Name}: {ex.Message}");
            return ExitNoClient;
        }
    }
}
