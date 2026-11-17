using System.Diagnostics;
using System.Runtime.InteropServices;
using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// Watches for NitroSense coming to the front, which is what the Nitro key does on a model whose button the Acer
/// service stack owns.
/// </summary>
/// <remarks>
/// <para>
/// The AN16-41 is one of those models: the button produces no WMI event NitroLite can subscribe to and no raw-input
/// report it can read, but pressing it brings NitroSense forward. That transition is the signal, and it is a reliable
/// one — nothing else on the machine opens that window.
/// </para>
/// <para>
/// The watcher polls the foreground window rather than hooking anything. A hook would need to run inside every
/// process on the desktop, and the question "which window is in front" is cheap enough to ask four times a second.
/// </para>
/// <para>
/// It only reports. Closing NitroSense and bringing NitroLite forward is the shell's decision, because the setting
/// lives there and so does the window.
/// </para>
/// </remarks>
public sealed class NitroSenseWatcher : INitroSenseWatcher
{
    /// <summary>The process name NitroSense runs under.</summary>
    public const string ProcessName = "NitroSense";

    /// <summary>How often the foreground window is checked. Fast enough to feel immediate, cheap enough to ignore.</summary>
    public const int PollMilliseconds = 400;

    /// <summary>
    /// How long the watcher stays quiet after closing NitroSense. The Acer service can relaunch it, and reacting to
    /// that would be a loop the user sees as a flickering window.
    /// </summary>
    public static readonly TimeSpan CloseCooldown = TimeSpan.FromSeconds(5);

    private readonly Func<int> _foregroundProcessId;
    private readonly Func<bool>? _shouldReact;
    private readonly Func<int, bool> _isNitroSense;
    private readonly object _gate = new();

    private Timer? _timer;
    private bool _started;
    private bool _disposed;

    /// <summary>The process last seen in front, so one activation is one event.</summary>
    private int _lastProcessId;

    private long _quietUntilTicks;

    /// <param name="foregroundProcessId">
    /// The process owning the foreground window. Injectable so the decision can be tested without a desktop.
    /// </param>
    /// <param name="shouldReact">
    /// Asked before every poll. The shell uses it to stay out of the way while NitroLite is already in front.
    /// </param>
    /// <param name="isNitroSense">
    /// Answers whether a process id is NitroSense. Injectable so the decision can be tested without the program.
    /// </param>
    public NitroSenseWatcher(
        Func<int>? foregroundProcessId = null,
        Func<bool>? shouldReact = null,
        Func<int, bool>? isNitroSense = null)
    {
        _foregroundProcessId = foregroundProcessId ?? ForegroundProcessId;
        _shouldReact = shouldReact;
        _isNitroSense = isNitroSense ?? IsNitroSense;
    }

    public event EventHandler? NitroSenseActivated;

    public bool IsSupported => OperatingSystem.IsWindows();

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        _timer = new Timer(_ => Poll(), null, PollMilliseconds, PollMilliseconds);
        AppLog.Info("NitroSense izleyicisi başladı", $"aralık={PollMilliseconds} ms");
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
        }

        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// One poll. Internal so a test can drive the whole decision without a timer or a desktop.
    /// </summary>
    internal void Poll()
    {
        if (_disposed) return;
        if (_shouldReact != null && !_shouldReact()) return;

        int processId = _foregroundProcessId();
        if (processId <= 0) return;

        // The same window staying in front is not a second press.
        if (processId == _lastProcessId) return;

        if (!_isNitroSense(processId))
        {
            _lastProcessId = 0;
            return;
        }

        _lastProcessId = processId;

        if (Stopwatch.GetTimestamp() < _quietUntilTicks) return;

        try
        {
            NitroSenseActivated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A subscriber that throws must not take the timer thread down with it.
            AppLog.Warn("NitroSense abonesi hata verdi", ex.Message);
        }
    }

    /// <summary>True when the process with this id is NitroSense. A process that has already exited is not.</summary>
    internal static bool IsNitroSense(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // The process ended between the foreground read and this call, or it is not ours to open.
            return false;
        }
    }

    public bool Close()
    {
        bool closed = false;

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ProcessName);
        }
        catch (Exception ex)
        {
            AppLog.Warn("NitroSense süreçleri listelenemedi", ex.Message);
            return false;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    // Asked first: a window that closes itself saves whatever the user had open in it.
                    if (process.CloseMainWindow() && process.WaitForExit(1500)) closed = true;
                    else
                    {
                        process.Kill();
                        closed = true;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("NitroSense kapatılamadı", ex.Message);
                }
            }
        }

        if (!closed) return false;

        SuppressFor(CloseCooldown);
        AppLog.Info("NitroSense kapatıldı", "Nitro tuşu NitroLite'a yönlendirildi.");

        return true;
    }

    /// <summary>
    /// Stays quiet for a while. Called after closing NitroSense: the Acer service can relaunch it, and reacting to
    /// that would be a loop the user sees as a flickering window.
    /// </summary>
    internal void SuppressFor(TimeSpan window) =>
        _quietUntilTicks = Stopwatch.GetTimestamp() + (long)(window.TotalSeconds * Stopwatch.Frequency);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }

    // -------------------------------------------------------------- interop

    private static int ForegroundProcessId()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return 0;

            GetWindowThreadProcessId(window, out uint processId);
            return (int)processId;
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
