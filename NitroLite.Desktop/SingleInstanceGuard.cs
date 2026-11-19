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
    /// Set to <c>1</c> to start without taking the machine's slot. Only <c>scripts/screenshot.ps1</c> uses it: the
    /// harness runs against a throwaway data folder while the user's own NitroLite sits in the tray, and the guard
    /// would otherwise make it exit before the first window appears.
    /// </summary>
    public const string AllowSecondInstanceVariable = "NITROLITE_ALLOW_SECOND_INSTANCE";

    private readonly Mutex? _mutex;
    private readonly FileStream? _lockFile;
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _standalone;
    private NamedPipeServerStream? _server;
    private Task? _listener;
    private bool _disposed;

    private SingleInstanceGuard(Mutex? mutex, FileStream? lockFile, bool isFirst, bool standalone = false)
    {
        _mutex = mutex;
        _lockFile = lockFile;
        _standalone = standalone;
        IsFirstInstance = isFirst;
    }

    /// <summary>True when this process is the one that owns the machine's NitroLite.</summary>
    public bool IsFirstInstance { get; }

    /// <summary>Raised on a background thread when a second launch asked to be shown.</summary>
    public event Action? ActivationRequested;

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
            return new SingleInstanceGuard(null, null, isFirst: true, standalone: true);
        }

        if (OperatingSystem.IsWindows())
        {
            bool refusedByAnotherSession = false;

            foreach (var name in new[] { MutexName, MutexNameFallback })
            {
                try
                {
                    var mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
                    if (createdNew) return new SingleInstanceGuard(mutex, null, isFirst: true);

                    // Somebody else owns it. This process is the second launch.
                    mutex.Dispose();
                    return new SingleInstanceGuard(null, null, isFirst: false);
                }
                catch (UnauthorizedAccessException)
                {
                    // The mutex exists but belongs to another user's session. That is not this session's answer:
                    // the per-session name is tried next, so two users logged on at once each get their own
                    // window instead of the second one silently refusing to start.
                    refusedByAnotherSession = true;
                }
                catch (Exception ex)
                {
                    AppLog.Warn(Loc.T("Tek örnek kilidi alınamadı"), $"{name}: {ex.Message}");
                }
            }

            // Both names were refused, so another session owns the machine-wide slot and the per-session one too.
            return new SingleInstanceGuard(null, null, isFirst: !refusedByAnotherSession);
        }

        try
        {
            var path = AppPaths.GetDataFile("nitrolite.lock");
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstanceGuard(null, stream, isFirst: true);
        }
        catch (IOException)
        {
            // The lock file is held by a running instance.
            return new SingleInstanceGuard(null, null, isFirst: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Tek örnek kilidi alınamadı"), ex.Message);
            return new SingleInstanceGuard(null, null, isFirst: true);
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

        _listener = Task.Run(async () =>
        {
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

                    if (read == 1) ActivationRequested?.Invoke();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Warn(Loc.T("Tek örnek dinleyicisi durdu"), ex.Message);
                    return;
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

    /// <summary>
    /// Tells the running instance to come forward. Returns false when nobody answered, which is what a stale lock
    /// looks like.
    /// </summary>
    public static bool SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            client.WriteByte(1);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Çalışan pencereye ulaşılamadı"), ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stop.Cancel();
        try { _server?.Dispose(); } catch { /* the listener is already gone */ }
        _server = null;

        try { _mutex?.ReleaseMutex(); } catch { /* not owned, or already released */ }
        _mutex?.Dispose();
        _lockFile?.Dispose();
        _stop.Dispose();
    }
}
