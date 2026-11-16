using System.Management;
using System.Runtime.InteropServices;
using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The Nitro key on Windows, heard through both routes the machine offers.
/// </summary>
/// <remarks>
/// <para>
/// The button is not a keyboard key. On a Nitro or Predator laptop the embedded controller raises an ACPI event, the
/// Acer WMI hotkey driver turns it into a <c>root\WMI</c> event, and NitroSense is what listens for it. That is the
/// primary route here, and it is the one that keeps working when the Acer services are stopped, because the event
/// comes from the driver and the firmware rather than from the service.
/// </para>
/// <para>
/// The second route is raw input. Some models map the same button to <c>VK_LAUNCH_APP1</c> or
/// <c>VK_LAUNCH_APP2</c>, and a few expose it as a vendor or consumer HID collection. A message-only window
/// (<c>HWND_MESSAGE</c>) registered with <c>RIDEV_INPUTSINK</c> receives those reports without ever taking focus, so
/// the app can be in the tray and still hear the key.
/// </para>
/// <para>
/// Both routes can report one physical press, which is what <see cref="NitroKeyDebounceGuard"/> is for. Neither route
/// may throw into the caller: a machine without the key, a driver that refuses the subscription and a device that
/// disappears mid-read all end the same way, with <see cref="IsSupported"/> false and nothing else changed.
/// </para>
/// </remarks>
public sealed class WindowsNitroKeyListener : INitroKeyListener
{
    /// <summary>
    /// The <c>EventType</c> values the embedded controller reports for the Nitro key.
    /// </summary>
    /// <remarks>
    /// A set rather than a single value because the code is per model, and a set rather than "any event" because the
    /// same class also carries the fan-mode and lighting keys: opening the window for those would be a bug the user
    /// could not turn off. Every code that arrives is logged, so a machine that reports something else here is a
    /// one-line correction rather than a mystery.
    /// </remarks>
    public static readonly IReadOnlySet<uint> NitroKeyEventCodes = new HashSet<uint> { 0x04 };

    /// <summary>The virtual-key codes some models use for the same button.</summary>
    public static readonly IReadOnlySet<ushort> NitroKeyVirtualKeys = new HashSet<ushort> { 0xB6, 0xB7 };

    /// <summary>
    /// The HID consumer usages behind <c>VK_LAUNCH_APP1</c> and <c>VK_LAUNCH_APP2</c>. A consumer report that carries
    /// either of these is the button.
    /// </summary>
    public static readonly ushort[] NitroKeyUsages = { 0x0194, 0x0195 };

    /// <summary>The WMI classes that carry the Acer hotkey event, most specific first.</summary>
    private static readonly string[] EventClassNames = { "AcerGamingEvent", "AcerWmiEvent" };

    private readonly NitroKeyDebounceGuard _debounce;
    private readonly object _gate = new();

    private ManagementEventWatcher? _watcher;
    private RawInputWindow? _rawInput;
    private bool _probed;
    private bool _started;
    private bool _disposed;

    /// <summary>How many distinct HID reports have been logged, so a live run can be diagnosed without a flood.</summary>
    private int _loggedReports;

    public WindowsNitroKeyListener(NitroKeyDebounceGuard? debounce = null) =>
        _debounce = debounce ?? new NitroKeyDebounceGuard();

    public event EventHandler? NitroKeyPressed;

    /// <summary>
    /// True when the Acer hotkey event class is registered on this machine. Raw input alone is not evidence: every
    /// Windows machine accepts a keyboard registration, so treating that as support would put the settings row in front
    /// of users whose laptop has no such button.
    /// </summary>
    /// <remarks>
    /// Asking is what runs the probe, because the settings row has to decide whether to exist before the listener is
    /// started. The probe is a metadata query, not a subscription: it answers "is the class there" without listening.
    /// </remarks>
    public bool IsSupported
    {
        get
        {
            EnsureProbed();
            return EventClassName != null;
        }
    }

    /// <summary>The WMI class that answered, for the log and the hardware report.</summary>
    public string? EventClassName { get; private set; }

    public void Start()
    {
        EnsureProbed();

        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        StartWmi();
        StartRawInput();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
        }

        try
        {
            if (_watcher != null)
            {
                _watcher.EventArrived -= OnWmiEvent;
                _watcher.Stop();
                _watcher.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Acer WMI olay izleyicisi kapatılamadı", ex.Message);
        }
        finally
        {
            _watcher = null;
        }

        _rawInput?.Dispose();
        _rawInput = null;
    }

    // ------------------------------------------------------------------ the WMI route

    /// <summary>
    /// Finds the Acer event class once. A metadata query is the only way to ask about an event class: it has no
    /// instances to enumerate, so <c>SELECT * FROM AcerGamingEvent</c> would wait for an event rather than answer.
    /// </summary>
    private void EnsureProbed()
    {
        lock (_gate)
        {
            if (_probed || _disposed) return;
            _probed = true;

            try
            {
                var scope = new ManagementScope(@"\\.\root\WMI");
                scope.Connect();

                foreach (var className in EventClassNames)
                {
                    if (!ClassExists(scope, className)) continue;

                    EventClassName = className;
                    AppLog.Info("Nitro tuşu WMI olay sınıfı bulundu", $"sınıf='{className}'");
                    return;
                }

                AppLog.Info("Nitro tuşu WMI üzerinden bulunamadı", "Acer olay sınıfı kayıtlı değil.");
            }
            catch (Exception ex)
            {
                // A machine without the Acer hotkey driver, or a WMI service that is not answering.
                AppLog.Info("Acer WMI olay sınıfı sorgulanamadı", ex.Message);
            }
        }
    }

    private static bool ClassExists(ManagementScope scope, string className)
    {
        using var searcher = new ManagementObjectSearcher(
            scope, new ObjectQuery($"SELECT * FROM meta_class WHERE __CLASS = '{className}'"));
        using var results = searcher.Get();
        return results.Count > 0;
    }

    private void StartWmi()
    {
        string? className = EventClassName;
        if (className == null) return;

        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();

            var watcher = new ManagementEventWatcher(scope, new WqlEventQuery($"SELECT * FROM {className}"));
            watcher.EventArrived += OnWmiEvent;
            watcher.Start();

            _watcher = watcher;
            AppLog.Info("Nitro tuşu WMI olay izleyicisi bağlandı", $"sınıf='{className}'");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Acer WMI olay izleyicisi başlatılamadı", $"{className}: {ex.Message}");
        }
    }

    private void OnWmiEvent(object sender, EventArrivedEventArgs e)
    {
        uint code = ReadEventCode(e.NewEvent);

        if (!NitroKeyEventCodes.Contains(code))
        {
            // Logged rather than ignored: the code is per model, and this line is what turns "the key does nothing"
            // into a one-line fix.
            AppLog.Info("Acer WMI olayı (Nitro tuşu değil)", $"kod=0x{code:X2}");
            return;
        }

        Raise();
    }

    /// <summary>
    /// The event's numeric payload. The property is <c>EventType</c> on both classes; the alternatives are read
    /// because a driver update has renamed it before.
    /// </summary>
    private static uint ReadEventCode(ManagementBaseObject? ev)
    {
        if (ev == null) return uint.MaxValue;

        foreach (var name in new[] { "EventType", "EventCode", "KeyCode" })
        {
            try
            {
                var value = ev.Properties[name]?.Value;
                if (value != null) return Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                // A property that exists but is not numeric is not the payload; try the next name.
            }
        }

        return uint.MaxValue;
    }

    // ------------------------------------------------------------------ the raw-input route

    private void StartRawInput()
    {
        try
        {
            _rawInput = new RawInputWindow(OnRawInput);
            _rawInput.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ham giriş penceresi kurulamadı", ex.Message);
            _rawInput?.Dispose();
            _rawInput = null;
        }
    }

    private void OnRawInput(RawInputReport report)
    {
        if (report.IsKeyboard)
        {
            if (NitroKeyVirtualKeys.Contains(report.VirtualKey)) Raise();
            return;
        }

        if (report.Payload.Length == 0) return;

        // The report layout is the device's own, so the match is on the 16-bit consumer usage rather than on a byte
        // offset: a report that carries AL Launch Application 1 or 2 is the button, whatever else it carries.
        for (int i = 0; i + 1 < report.Payload.Length; i++)
        {
            ushort usage = (ushort)(report.Payload[i] | (report.Payload[i + 1] << 8));
            if (Array.IndexOf(NitroKeyUsages, usage) < 0) continue;

            Raise();
            return;
        }

        // Logged once per distinct report so a machine whose button arrives as a vendor report can be identified
        // without guessing at the layout.
        if (_loggedReports < 8)
        {
            _loggedReports++;
            AppLog.Info("Acer HID raporu", $"sayfa=0x{report.UsagePage:X4} veri={Convert.ToHexString(report.Payload)}");
        }
    }

    private void Raise()
    {
        if (!_debounce.ShouldAccept()) return;

        try
        {
            NitroKeyPressed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A subscriber that throws must not take the message loop down with it.
            AppLog.Warn("Nitro tuşu abonesi hata verdi", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }

    // ================================================================== the message-only window

    /// <summary>One raw-input report, flattened out of the <c>RAWINPUT</c> union.</summary>
    internal readonly record struct RawInputReport(bool IsKeyboard, ushort VirtualKey, ushort UsagePage, byte[] Payload);

    /// <summary>
    /// A hidden window whose only job is to receive <c>WM_INPUT</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HWND_MESSAGE</c> makes the window a message-only child: it is never drawn, never appears in the task bar and
    /// cannot take focus, which is exactly what a background listener needs. <c>RIDEV_INPUTSINK</c> is what makes the
    /// reports arrive while another window is in front.
    /// </para>
    /// <para>
    /// The window lives on its own thread with its own message loop. Creating it on the UI thread would put a second
    /// message pump next to Avalonia's, and the two would compete for the same queue.
    /// </para>
    /// </remarks>
    private sealed class RawInputWindow : IDisposable
    {
        private const int WmInput = 0x00FF;
        private const int WmClose = 0x0010;
        private const int WmDestroy = 0x0002;
        private const int WmKeyDown = 0x0100;

        private const uint RidInput = 0x10000003;
        private const uint RimTypeKeyboard = 1;
        private const uint RimTypeHid = 2;
        private const uint RidevInputSink = 0x00000100;

        private static readonly IntPtr HwndMessage = new(-3);

        private readonly Action<RawInputReport> _onReport;
        private readonly ManualResetEventSlim _ready = new(false);

        private WndProcDelegate? _wndProc;
        private Thread? _thread;
        private IntPtr _hwnd;
        private uint _threadId;
        private string? _error;

        public RawInputWindow(Action<RawInputReport> onReport) => _onReport = onReport;

        public void Start()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "NitroLite raw input"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            // The window has to exist before the caller can decide whether the route is usable, and the wait is
            // bounded so a wedged thread cannot hang startup.
            if (!_ready.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("the raw-input window did not start");
            }

            if (_error != null) throw new InvalidOperationException(_error);
        }

        private void Run()
        {
            try
            {
                _threadId = GetCurrentThreadId();
                _wndProc = WindowProc;

                var instance = GetModuleHandle(null);
                var windowClass = new WndClassEx
                {
                    cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = instance,
                    lpszClassName = "NitroLiteRawInput"
                };

                if (RegisterClassEx(ref windowClass) == 0)
                {
                    _error = $"RegisterClassEx failed ({Marshal.GetLastWin32Error()})";
                    return;
                }

                _hwnd = CreateWindowEx(0, windowClass.lpszClassName, "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    _error = $"CreateWindowEx failed ({Marshal.GetLastWin32Error()})";
                    return;
                }

                RegisterDevices();

                Signal();

                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
            finally
            {
                Signal();
            }
        }

        /// <summary>
        /// Releases the caller waiting in <see cref="Start"/>. Guarded because the listener can be disposed while the
        /// thread is still finishing, and a disposed event would throw out of a <c>finally</c>.
        /// </summary>
        private void Signal()
        {
            try { _ready.Set(); } catch (ObjectDisposedException) { /* the listener is already gone */ }
        }

        /// <summary>
        /// Asks for the keyboard and for the vendor and consumer collections. A page the machine does not have is
        /// refused by the API and is not an error: the registration is per page, and the ones that succeed are the
        /// ones that matter.
        /// </summary>
        private void RegisterDevices()
        {
            var devices = new[]
            {
                new RawInputDevice { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RidevInputSink, hwndTarget = _hwnd },
                new RawInputDevice { usUsagePage = 0x0C, usUsage = 0x01, dwFlags = RidevInputSink, hwndTarget = _hwnd },
                new RawInputDevice { usUsagePage = 0xFF00, usUsage = 0x01, dwFlags = RidevInputSink, hwndTarget = _hwnd },
                new RawInputDevice { usUsagePage = 0xFF01, usUsage = 0x01, dwFlags = RidevInputSink, hwndTarget = _hwnd }
            };

            foreach (var device in devices)
            {
                if (RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>())) continue;

                AppLog.Info("Ham giriş sayfası kaydedilemedi",
                    $"sayfa=0x{device.usUsagePage:X4} kullanım=0x{device.usUsage:X4} ({Marshal.GetLastWin32Error()})");
            }
        }

        private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            switch (message)
            {
                case WmInput:
                    try
                    {
                        ReadReport(lParam);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Ham giriş raporu okunamadı", ex.Message);
                    }

                    return IntPtr.Zero;

                case WmClose:
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;

                case WmDestroy:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        private void ReadReport(IntPtr rawInputHandle)
        {
            uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
            uint size = 0;

            GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize);
            if (size == 0 || size > 4096) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize) != size) return;

                var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
                int payload = (int)headerSize;

                if (header.dwType == RimTypeKeyboard)
                {
                    var keyboard = Marshal.PtrToStructure<RawKeyboard>(buffer + payload);

                    // Only the press, not the release: one press must be one event.
                    if (keyboard.Message != WmKeyDown) return;

                    _onReport(new RawInputReport(true, keyboard.VKey, 0, []));
                    return;
                }

                if (header.dwType != RimTypeHid) return;

                // RAWHID: dwSizeHid, dwCount, then dwSizeHid * dwCount bytes.
                uint sizeHid = (uint)Marshal.ReadInt32(buffer, payload);
                uint count = (uint)Marshal.ReadInt32(buffer, payload + 4);
                if (sizeHid == 0 || sizeHid > 256 || count == 0 || count > 16) return;

                int dataStart = payload + 8;
                int length = (int)(sizeHid * count);
                if (dataStart + length > size) return;

                var bytes = new byte[length];
                Marshal.Copy(buffer + dataStart, bytes, 0, length);

                _onReport(new RawInputReport(false, 0, 0, bytes));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            try
            {
                if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                else if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // The thread is already gone; nothing to close.
            }

            try { _thread?.Join(TimeSpan.FromSeconds(1)); } catch { /* a wedged thread must not block exit */ }

            _ready.Dispose();
        }

        // -------------------------------------------------------------- interop

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHeader
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawKeyboard
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out Msg message, IntPtr hwnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Msg message);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
