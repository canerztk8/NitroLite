using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// What a foreground window turned out to be, as far as the Nitro key is concerned.
/// </summary>
public enum NitroSenseWindowKind
{
    /// <summary>Not NitroSense. The common answer, and the one that must cost nothing.</summary>
    None = 0,

    /// <summary>A window owned by a process the Acer stack runs under.</summary>
    Process = 1,

    /// <summary>A packaged app's window: the process is <c>ApplicationFrameHost</c> and only the title says which app.</summary>
    ApplicationFrameHost = 2,

    /// <summary>A <c>#32770</c> dialog belonging to NitroSense — the permission prompt the key can raise.</summary>
    ErrorDialog = 3
}

/// <summary>
/// The foreground window, flattened to the four facts the decision needs.
/// </summary>
/// <remarks>
/// <see cref="Content"/> is the text of the window's child labels, and it is only filled for a dialog. A permission
/// prompt's own title is usually just the app name; the sentence that identifies it as the prompt is in a child
/// static, so a check that read only the title would miss the case this exists for.
/// </remarks>
public readonly record struct ForegroundWindowInfo(
    int ProcessId,
    string ProcessName,
    string ClassName,
    string Title,
    string Content,
    IntPtr Handle)
{
    /// <summary>Nothing in front, or a window that could not be read.</summary>
    public static ForegroundWindowInfo None => new(0, "", "", "", "", IntPtr.Zero);

    /// <summary>
    /// A window known only by its process, for a caller that injects the process id and nothing else. The class and
    /// title are empty, so only the process rule can match — which is exactly what such a caller is testing.
    /// </summary>
    public static ForegroundWindowInfo ForProcess(int processId) =>
        new(processId, "", "", "", "", IntPtr.Zero);
}

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
/// Three shapes of window count as the key, because the same button produces all three depending on how NitroSense
/// was installed: the app's own process, a packaged build whose window belongs to <c>ApplicationFrameHost</c>, and the
/// permission dialog the Acer service raises when it cannot reach its driver. The first two are reported; the dialog
/// is closed here, because it is a window rather than the app and one <c>WM_CLOSE</c> is the whole fix.
/// </para>
/// <para>
/// It only reports the app cases. Closing NitroSense and bringing NitroLite forward is the shell's decision, because
/// the setting lives there and so does the window.
/// </para>
/// </remarks>
public sealed class NitroSenseWatcher : INitroSenseWatcher
{
    /// <summary>
    /// The processes the Acer Nitro key stack runs under, in the order they have to be closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>NitroSenseLauncher</c> comes first because it is what the Acer service starts on the key press, and it is
    /// what spawns everything else. Closing it first stops the window before it exists.
    /// </para>
    /// <para>
    /// <c>SenseOverlay</c> is the NitroSense window on this generation of the app: the name on the box is not the name
    /// of the executable. <c>NitroSense</c> is the packaged app the launcher also tries to open.
    /// </para>
    /// </remarks>
    public static readonly string[] ProcessNames = { "NitroSenseLauncher", "SenseOverlay", "NitroSense" };

    /// <summary>
    /// The prefix every process in the Acer stack starts with. A build that renames the executable to
    /// <c>NitroSenseSomething</c> is still the app, and the prefix is what catches it without the list above having to
    /// learn the new name.
    /// </summary>
    public const string NitroSenseProcessPrefix = "NitroSense";

    /// <summary>The host process a packaged (MSIX) app's window belongs to.</summary>
    public const string ApplicationFrameHostProcess = "ApplicationFrameHost";

    /// <summary>The window class every Windows dialog uses, including the Acer permission prompt.</summary>
    public const string DialogClassName = "#32770";

    /// <summary>The package name the Acer permission prompt carries in its text.</summary>
    public const string NitroSensePackageName = "NitroSenseforNotebook";

    /// <summary>The message that asks a window to close, which is what the user's own click on the X sends.</summary>
    private const uint WmClose = 0x0010;

    /// <summary>How often the foreground window is checked. Fast enough to feel immediate, cheap enough to ignore.</summary>
    public const int PollMilliseconds = 400;

    /// <summary>
    /// How often the close sweep looks for the launcher. Much faster than the foreground poll: the launcher lives for
    /// about a tenth of a second before it opens the window, so a slow sweep would always arrive after the fact.
    /// </summary>
    public const int SweepMilliseconds = 50;

    /// <summary>
    /// How long the watcher stays quiet after closing NitroSense. The Acer service can relaunch it, and reacting to
    /// that would be a loop the user sees as a flickering window. Short enough that a deliberate second press is not
    /// swallowed: the relaunch it guards against happens within a tenth of a second, not within seconds.
    /// </summary>
    public static readonly TimeSpan CloseCooldown = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How many times one window may close the stack before the loop gives up. The Acer service can relaunch it
    /// faster than this loop can close it, and a fight the user sees as a flickering window is worse than losing.
    /// </summary>
    public const int MaxClosesPerWindow = 20;

    /// <summary>
    /// How many child labels of a dialog are read before the enumeration stops. A permission prompt has a handful; the
    /// bound is there so a window with a pathological child count cannot spend the poll inside the loop.
    /// </summary>
    private const int MaxDialogChildren = 24;

    private readonly Func<ForegroundWindowInfo> _foregroundWindow;
    private readonly Func<bool>? _shouldReact;
    private readonly Func<int, bool> _isNitroSense;
    private readonly Action<IntPtr> _closeWindow;
    private readonly object _gate = new();

    private Timer? _timer;
    private bool _started;
    private bool _disposed;

    /// <summary>The process last seen in front, so one activation is one event.</summary>
    private int _lastProcessId;

    /// <summary>The window handle the cached <see cref="_lastWindow"/> belongs to.</summary>
    private IntPtr _lastHwnd;

    /// <summary>The last foreground window read, kept so an unchanged handle costs one call instead of six.</summary>
    private ForegroundWindowInfo? _lastWindow;

    private long _quietUntilTicks;

    /// <summary>How long the close loop keeps sweeping. Extended by every press, so a burst is one sweep.</summary>
    private long _closeUntilTicks;

    /// <summary>1 while the close loop is running, so repeated presses do not stack workers.</summary>
    private int _closeLoopRunning;

    /// <param name="foregroundProcessId">
    /// The process owning the foreground window. Injectable so the decision can be tested without a desktop. A caller
    /// that passes this and not <paramref name="foregroundWindow"/> gets a window with no class and no title, so only
    /// the process rule can match.
    /// </param>
    /// <param name="shouldReact">
    /// Asked before every poll. The shell uses it to stay out of the way while NitroLite is already in front.
    /// </param>
    /// <param name="isNitroSense">
    /// Answers whether a process id is NitroSense. Injectable so the decision can be tested without the program.
    /// </param>
    /// <param name="foregroundWindow">
    /// The whole foreground window, for a caller that needs the class and title rules as well. Injectable for the same
    /// reason as the two above.
    /// </param>
    /// <param name="closeWindow">
    /// Asks a window to close. Injectable so the dialog rule can be asserted without a dialog on screen.
    /// </param>
    public NitroSenseWatcher(
        Func<int>? foregroundProcessId = null,
        Func<bool>? shouldReact = null,
        Func<int, bool>? isNitroSense = null,
        Func<ForegroundWindowInfo>? foregroundWindow = null,
        Action<IntPtr>? closeWindow = null)
    {
        _shouldReact = shouldReact;
        _isNitroSense = isNitroSense ?? IsNitroSense;
        _closeWindow = closeWindow ?? PostClose;

        _foregroundWindow = foregroundWindow
            ?? (foregroundProcessId == null
                ? () => CachedForegroundWindow(GetForegroundWindow(), ReadForegroundWindow)
                : () => ForegroundWindowInfo.ForProcess(foregroundProcessId()));
    }

    public event EventHandler? NitroSenseActivated;

    public bool IsSupported => OperatingSystem.IsWindows();

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;

            // Created inside the lock, not after it. The old order set the flag, released the lock and only then
            // built the timer, so a Stop that landed in between saw _started true, cleared it, disposed a null
            // timer and returned — and the timer this method went on to create was never disposed by anyone.
            _timer = new Timer(_ => Poll(), null, PollMilliseconds, PollMilliseconds);
        }

        AppLog.Info(Loc.T("NitroSense izleyicisi başladı"), $"aralık={PollMilliseconds} ms");
    }

    public void Stop()
    {
        Timer? timer;

        lock (_gate)
        {
            if (!_started) return;
            _started = false;

            // Taken out of the field under the lock so a concurrent Start can only ever build a new timer, never
            // adopt the one being disposed here.
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    /// <summary>
    /// One poll. Internal so a test can drive the whole decision without a timer or a desktop.
    /// </summary>
    internal void Poll()
    {
        if (_disposed) return;
        if (_shouldReact != null && !_shouldReact()) return;

        var window = _foregroundWindow();
        if (window.ProcessId <= 0) return;

        // The same window staying in front is not a second press.
        if (window.ProcessId == _lastProcessId) return;

        var kind = Classify(window, _isNitroSense(window.ProcessId));
        if (kind == NitroSenseWindowKind.None)
        {
            _lastProcessId = 0;
            return;
        }

        // The cooldown is checked before the window is remembered. Remembering it first meant a press that landed
        // inside the cooldown was dropped for good: the same window stayed in front, matched the dedupe on every later
        // poll, and NitroSense was never closed. Checking first lets the next poll after the cooldown act on it.
        if (Stopwatch.GetTimestamp() < _quietUntilTicks) return;

        _lastProcessId = window.ProcessId;

        // The permission dialog is a window, not the app: one WM_CLOSE is the whole fix, and it is done here because
        // this is where the handle is. The event is still raised, because the press has to bring NitroLite forward
        // whether the key opened the app or the prompt.
        if (kind == NitroSenseWindowKind.ErrorDialog) _closeWindow(window.Handle);

        // Logged on every accepted press, with the shape that matched. Without this line the only evidence that the
        // key works is the window moving, which is exactly what a user reporting "it does nothing" cannot show.
        AppLog.Info(Loc.T("Nitro tuşu algılandı"), Describe(kind, window));

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

    /// <summary>
    /// Which of the three shapes a foreground window is, as a pure function so every rule can be asserted without a
    /// desktop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order matters, and the dialog comes first. A permission prompt is owned by the NitroSense process, so the
    /// process rule would match it too — and matching there would close the whole process tree when one
    /// <c>WM_CLOSE</c> on the dialog is the whole fix. The dialog rule is narrow enough to lead: it needs the
    /// <c>#32770</c> class <em>and</em> NitroSense's name in the window's text.
    /// </para>
    /// <para>
    /// The process rule is next because it is the cheapest and the most common. The packaged-app rule is last because
    /// its process name says nothing at all: <c>ApplicationFrameHost</c> hosts every MSIX app on the machine, so the
    /// title is the only evidence there is.
    /// </para>
    /// </remarks>
    public static NitroSenseWindowKind Classify(ForegroundWindowInfo window, bool processIsNitroSense)
    {
        if (window.ProcessId <= 0) return NitroSenseWindowKind.None;

        if (IsDialogClass(window.ClassName) &&
            (MentionsNitroSense(window.Title) || MentionsNitroSense(window.Content)))
        {
            return NitroSenseWindowKind.ErrorDialog;
        }

        if (processIsNitroSense || IsNitroSenseProcessName(window.ProcessName))
        {
            return NitroSenseWindowKind.Process;
        }

        if (IsApplicationFrameHost(window.ProcessName) && MentionsNitroSense(window.Title))
        {
            return NitroSenseWindowKind.ApplicationFrameHost;
        }

        return NitroSenseWindowKind.None;
    }

    /// <summary>True when a process name belongs to the Acer stack by prefix, which covers a renamed executable.</summary>
    public static bool IsNitroSenseProcessName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        name.StartsWith(NitroSenseProcessPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the process is the host a packaged app's window belongs to.</summary>
    public static bool IsApplicationFrameHost(string? name) =>
        string.Equals(name, ApplicationFrameHostProcess, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the window class is the one every Windows dialog uses.</summary>
    public static bool IsDialogClass(string? className) =>
        string.Equals(className, DialogClassName, StringComparison.Ordinal);

    /// <summary>
    /// True when a piece of window text names NitroSense. The package name is checked as well as the product name so
    /// the intent is readable, even though the second contains the first.
    /// </summary>
    public static bool MentionsNitroSense(string? text) =>
        !string.IsNullOrEmpty(text) &&
        (text.Contains(NitroSensePackageName, StringComparison.OrdinalIgnoreCase) ||
         text.Contains(NitroSenseProcessPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The log line's detail: which shape matched, and the evidence for it.</summary>
    private static string Describe(NitroSenseWindowKind kind, ForegroundWindowInfo window) => kind switch
    {
        NitroSenseWindowKind.ApplicationFrameHost =>
            $"ApplicationFrameHost (paketli uygulama) başlık='{window.Title}'",
        NitroSenseWindowKind.ErrorDialog =>
            $"izin penceresi sınıf='{window.ClassName}' başlık='{window.Title}' içerik='{window.Content}'",
        _ => $"süreç='{window.ProcessName}'"
    };

    /// <summary>True when the process with this id is NitroSense. A process that has already exited is not.</summary>
    internal static bool IsNitroSense(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return IsNitroSenseName(process.ProcessName);
        }
        catch
        {
            // The process ended between the foreground read and this call, or it is not ours to open.
            return false;
        }
    }

    /// <summary>True when this process name belongs to the NitroSense stack.</summary>
    internal static bool IsNitroSenseName(string name)
    {
        foreach (var candidate in ProcessNames)
        {
            if (candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public bool Close() => CloseCore(log: true);

    public bool CloseFor(TimeSpan window)
    {
        long until = Stopwatch.GetTimestamp() + (long)(window.TotalSeconds * Stopwatch.Frequency);

        // Extended rather than restarted: a burst of presses must not stack workers.
        lock (_gate)
        {
            if (until > _closeUntilTicks) _closeUntilTicks = until;
        }

        if (Interlocked.CompareExchange(ref _closeLoopRunning, 1, 0) != 0) return false;

        try
        {
            bool closed = false;
            int closes = 0;

            while (true)
            {
                long deadline;
                lock (_gate) deadline = _closeUntilTicks;

                if (Stopwatch.GetTimestamp() >= deadline) break;

                if (CloseCore(log: false))
                {
                    closed = true;
                    closes++;
                }

                // The Acer service can relaunch the stack faster than this loop can close it. Enough passes to prove
                // that is what is happening; more would be a fight the user sees as a flickering window.
                if (closes >= MaxClosesPerWindow)
                {
                    AppLog.Warn(Loc.T("NitroSense tekrar tekrar açılıyor"),
                        $"pencere içinde {closes} kez kapatıldı; Acer servisi yeniden başlatıyor olabilir.");
                    break;
                }

                Thread.Sleep(SweepMilliseconds);
            }

            if (closed) AppLog.Info(Loc.T("NitroSense kapatıldı"), "Nitro tuşu NitroLite'a yönlendirildi.");

            return closed;
        }
        finally
        {
            Interlocked.Exchange(ref _closeLoopRunning, 0);
        }
    }

    private bool CloseCore(bool log)
    {
        bool closed = false;

        // Launcher first: it is the process that opens the window, so closing it before the others means the window
        // never gets a chance to appear.
        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex)
            {
                AppLog.Warn(Loc.T("NitroSense süreçleri listelenemedi"), $"{name}: {ex.Message}");
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;

                        // Killed outright rather than asked to close. NitroSense has no document to save, and a polite
                        // close that waits on the window would spend seconds per process — longer than the sweep it
                        // runs inside, which would leave the window it was meant to remove.
                        process.Kill(entireProcessTree: true);
                        closed = true;
                        AppLog.Info(Loc.T("NitroSense süreci kapatıldı"), name);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn(Loc.T("NitroSense kapatılamadı"), $"{name}: {ex.Message}");
                    }
                }
            }
        }

        if (!closed) return false;

        SuppressFor(CloseCooldown);
        if (log) AppLog.Info(Loc.T("NitroSense kapatıldı"), "Nitro tuşu NitroLite'a yönlendirildi.");

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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
    }

    // -------------------------------------------------------------- interop

    /// <summary>
    /// The foreground window, re-read only when the handle changed.
    /// </summary>
    /// <remarks>
    /// The poll runs four times a second and the window in front usually does not change between two of them. Reading
    /// it whole costs a class name, a title, a child enumeration for a dialog and — the expensive one — a
    /// <see cref="Process.GetProcessById"/> handle. Comparing the handle first turns the common case into a single
    /// <c>GetForegroundWindow</c> call, which is what stops the watcher from opening thousands of process handles a
    /// minute for an answer it already had.
    /// </remarks>
    internal ForegroundWindowInfo CachedForegroundWindow(IntPtr handle, Func<IntPtr, ForegroundWindowInfo> read)
    {
        if (handle == IntPtr.Zero) return ForegroundWindowInfo.None;
        if (handle == _lastHwnd && _lastWindow is { } cached) return cached;

        var info = read(handle);
        _lastHwnd = handle;
        _lastWindow = info;
        return info;
    }

    /// <summary>
    /// The foreground window, read once per poll. Every failure answers <see cref="ForegroundWindowInfo.None"/>: a
    /// window that closed between the read and the query is not an error, and a poll that threw would stop the timer.
    /// </summary>
    private static ForegroundWindowInfo ReadForegroundWindow(IntPtr window)
    {
        try
        {
            if (window == IntPtr.Zero) return ForegroundWindowInfo.None;

            // The thread id is not needed; the process id is the whole answer.
            _ = GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0) return ForegroundWindowInfo.None;

            var className = ReadClassName(window);
            var title = ReadWindowText(window);

            // A dialog's own title is often just the app name; the sentence that says what went wrong is in a child
            // label. Reading the children is what makes the permission prompt identifiable at all.
            var content = IsDialogClass(className) ? ReadChildText(window) : "";

            return new ForegroundWindowInfo(
                (int)processId, ReadProcessName((int)processId), className, title, content, window);
        }
        catch
        {
            return ForegroundWindowInfo.None;
        }
    }

    private static string ReadClassName(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private static string ReadWindowText(IntPtr window)
    {
        var buffer = new StringBuilder(512);
        return GetWindowText(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    /// <summary>
    /// The text of a dialog's child labels, joined by a space. Bounded by <see cref="MaxDialogChildren"/> so a window
    /// with a pathological child count cannot spend the poll inside the enumeration.
    /// </summary>
    private static string ReadChildText(IntPtr window)
    {
        var parts = new List<string>();
        var buffer = new StringBuilder(512);

        try
        {
            EnumChildWindows(window, (child, _) =>
            {
                if (parts.Count >= MaxDialogChildren) return false;

                buffer.Clear();
                if (GetWindowText(child, buffer, buffer.Capacity) > 0 && buffer.Length > 0)
                {
                    parts.Add(buffer.ToString());
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // A dialog that closed mid-enumeration leaves what was read so far, which is enough to classify it.
        }

        return string.Join(' ', parts);
    }

    private static string ReadProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            // The process ended between the foreground read and this call, or it is not ours to open.
            return "";
        }
    }

    /// <summary>
    /// Asks a window to close. <c>WM_CLOSE</c> rather than a process kill: the dialog is a window, and asking it to
    /// close is what the user's own click on the X does. Killing the process would take NitroSense's tray icon with it.
    /// </summary>
    private static void PostClose(IntPtr window)
    {
        if (window == IntPtr.Zero) return;

        try
        {
            PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("NitroSense izin penceresi kapatılamadı"), ex.Message);
        }
    }

    private delegate bool EnumChildProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
