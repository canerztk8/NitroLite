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

    private readonly Mutex? _mutex;
    private readonly FileStream? _lockFile;
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeServerStream? _server;
    private Task? _listener;
    private bool _disposed;

    private SingleInstanceGuard(Mutex? mutex, FileStream? lockFile, bool isFirst)
    {
        _mutex = mutex;
        _lockFile = lockFile;
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
        if (OperatingSystem.IsWindows())
        {
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
                    // The mutex exists but belongs to another user's session: that is still "already running".
                    return new SingleInstanceGuard(null, null, isFirst: false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Tek örnek kilidi alınamadı", $"{name}: {ex.Message}");
                }
            }

            return new SingleInstanceGuard(null, null, isFirst: true);
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
            AppLog.Warn("Tek örnek kilidi alınamadı", ex.Message);
            return new SingleInstanceGuard(null, null, isFirst: true);
        }
    }

    /// <summary>
    /// Starts listening for a second launch. Only the first instance calls this; the second one calls
    /// <see cref="SignalExistingInstance"/> and exits.
    /// </summary>
    public void Listen()
    {
        if (!IsFirstInstance || _listener != null) return;

        _listener = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    _server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await _server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);

                    // One byte is the whole protocol: the message is the connection.
                    var buffer = new byte[1];
                    await _server.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                    _server.Dispose();
                    _server = null;

                    ActivationRequested?.Invoke();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Tek örnek dinleyicisi durdu", ex.Message);
                    return;
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
            AppLog.Warn("Çalışan pencereye ulaşılamadı", ex.Message);
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
