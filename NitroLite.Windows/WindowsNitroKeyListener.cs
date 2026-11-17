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
/// The class name is per model, so it is discovered rather than assumed. Older machines register
/// <c>AcerGamingEvent</c>; the AN16-41 registers <c>AcerGenericEvent</c>, the generic ACPI-WMI event class, and no
/// gaming class at all. The preferred names are tried first and the namespace is then scanned for Acer/APGE event
/// classes, so a name nobody has seen yet still gets a subscription.
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
/// disappears mid-read all end the same way, with nothing else changed.
/// </para>
/// </remarks>
public sealed class WindowsNitroKeyListener : INitroKeyListener
{
    /// <summary>
    /// The event codes the embedded controller reports for the Nitro key.
    /// </summary>
    /// <remarks>
    /// A set rather than a single value because the code is per model, and a set rather than "any event" because the
    /// same class also carries the fan-mode and lighting keys: opening the window for those would be a bug the user
    /// could not turn off. The code is read from <c>EventType</c> on the gaming class and from the first byte of
    /// <c>EventDetail</c> on the generic one. Every code that arrives is logged, so a machine that reports something
    /// else here is a one-line correction rather than a mystery.
    /// </remarks>
    public static readonly IReadOnlySet<uint> NitroKeyEventCodes = new HashSet<uint> { 0x04 };

    /// <summary>The virtual-key codes some models use for the same button.</summary>
    public static readonly IReadOnlySet<ushort> NitroKeyVirtualKeys = new HashSet<ushort> { 0xB6, 0xB7 };

    /// <summary>
    /// The HID consumer usages behind <c>VK_LAUNCH_APP1</c> and <c>VK_LAUNCH_APP2</c>. A consumer report that carries
    /// either of these is the button.
    /// </summary>
    public static readonly ushort[] NitroKeyUsages = { 0x0194, 0x0195 };

    /// <summary>
    /// The scan codes some models report for the button instead of a virtual key.
    /// </summary>
    /// <remarks>
    /// The AN16-41 sends <c>VK 0xFF</c> — "no virtual key" — with scan code <c>0x75</c> and the E0 prefix, which is
    /// not a keyboard key at all. The scan code is the identity here; the virtual key is deliberately not part of the
    /// match, because the value the firmware sends is the one that means "none".
    /// </remarks>
    public static readonly IReadOnlySet<ushort> NitroKeyScanCodes = new HashSet<ushort> { 0x75 };

    /// <summary>The <c>RI_KEY_E0</c> flag: the scan code came with the E0 prefix.</summary>
    public const ushort RiKeyE0 = 0x02;

    /// <summary>
    /// How often WMI polls the ACPI event class.
    /// </summary>
    /// <remarks>
    /// ACPI-WMI events are not pushed. Without a <c>WITHIN</c> clause the watcher subscribes successfully and then
    /// never fires, which is indistinguishable from a machine that has no such event — the exact "the key does
    /// nothing" symptom. One second is the interval the Acer tooling itself uses.
    /// </remarks>
    public static readonly TimeSpan EventPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many unmatched reports are logged before the log goes quiet. Enough to identify a device, few enough that a
    /// chatty collection cannot fill the file.
    /// </summary>
    private const int MaxLoggedReports = 12;

    /// <summary>
    /// The event classes tried first, before the namespace is scanned.
    /// </summary>
    /// <remarks>
    /// <c>AcerGenericEvent</c> is the ACPI-WMI generic event class the AN16-41 exposes; the other names are what older
    /// Nitro and Predator models register. The list is a head start, not the whole answer: the namespace is scanned
    /// afterwards so a model whose class is not named here is still found.
    /// </remarks>
    public static readonly IReadOnlyList<string> PreferredEventClassNames =
        new[] { "AcerGamingEvent", "AcerWmiEvent", "AcerGenericEvent", "AcerEvent" };

    /// <summary>
    /// Name fragments that mark a <c>root\WMI</c> class as Acer's, used by the namespace scan.
    /// </summary>
    private static readonly string[] EventClassFragments = { "Acer", "APGE" };

    /// <summary>
    /// <c>InstanceName</c> fragments that identify the Acer gaming engine's ACPI device. The generic event class is
    /// shared with every other ACPI-WMI device on the machine, so the instance is what tells them apart.
    /// </summary>
    private static readonly string[] AcerInstanceFragments = { "PNP0C14", "APGE", "ACER" };

    /// <summary>
    /// The <c>root\WMI</c> classes that exist only on an Acer machine. Their presence is what makes the settings row
    /// appear on a model whose event class is not one of the names above.
    /// </summary>
    private static readonly string[] AcerIdentityClassNames =
        { "AcerGenericEvent", "AcerGamingFunction", "AcerGenericMethod", "AcerBiosConfigurationTool" };

    private readonly NitroKeyDebounceGuard _debounce;
    private readonly object _gate = new();
    private readonly List<ManagementEventWatcher> _watchers = new();
    private readonly List<string> _eventClassNames = new();

    private RawInputWindow? _rawInput;
    private bool _probed;
    private bool _started;
    private bool _disposed;

    /// <summary>How many distinct HID reports have been logged, so a live run can be diagnosed without a flood.</summary>
    private int _loggedReports;

    /// <summary>How many unmatched keyboard reports have been logged, for the same reason.</summary>
    private int _loggedKeys;

    public WindowsNitroKeyListener(NitroKeyDebounceGuard? debounce = null) =>
        _debounce = debounce ?? new NitroKeyDebounceGuard();

    public event EventHandler? NitroKeyPressed;

    /// <summary>
    /// True when this machine is an Acer with a Nitro key the app can hear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two independent answers count as support: an Acer event class in <c>root\WMI</c>, or Acer hardware identity —
    /// an Acer-only WMI class, or an Acer/Predator model string. The second one is what the AN16-41 needs: it
    /// registers <c>AcerGenericEvent</c> and no <c>AcerGamingEvent</c>, so a check that only looked for the gaming
    /// class hid the settings row on a machine that does have the button.
    /// </para>
    /// <para>
    /// Raw input on its own is still not evidence. Every Windows machine accepts a keyboard registration, so treating
    /// that as support would put the row in front of users whose laptop has no such button.
    /// </para>
    /// <para>
    /// Asking is what runs the probe, because the settings row has to decide whether to exist before the listener is
    /// started. The probe is a metadata query, not a subscription: it answers "is the class there" without listening.
    /// </para>
    /// </remarks>
    public bool IsSupported
    {
        get
        {
            EnsureProbed();
            return IsSupportedFor(_eventClassNames.Count > 0, AcerHardwarePresent);
        }
    }

    /// <summary>The support rule, as a pure function so it can be asserted without a WMI namespace.</summary>
    public static bool IsSupportedFor(bool eventClassFound, bool acerHardwarePresent) =>
        eventClassFound || acerHardwarePresent;

    /// <summary>
    /// True when the machine identifies itself as an Acer. The model string is the fallback for a machine whose WMI
    /// classes are all missing but whose SMBIOS still says who built it.
    /// </summary>
    public static bool LooksLikeAcer(string? machineModel) =>
        machineModel != null &&
        (machineModel.Contains("acer", StringComparison.OrdinalIgnoreCase) ||
         machineModel.Contains("predator", StringComparison.OrdinalIgnoreCase));

    /// <summary>The Acer event classes that answered, for the log and the hardware report.</summary>
    public IReadOnlyList<string> EventClassNames
    {
        get
        {
            EnsureProbed();
            return _eventClassNames;
        }
    }

    /// <summary>
    /// True when the machine identifies as Acer hardware, whether or not an event class was found. Set by the probe.
    /// </summary>
    public bool AcerHardwarePresent { get; private set; }

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

        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EventArrived -= OnWmiEvent;
                watcher.Stop();
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Acer WMI olay izleyicisi kapatılamadı", ex.Message);
            }
        }

        _watchers.Clear();

        _rawInput?.Dispose();
        _rawInput = null;
    }

    // ------------------------------------------------------------------ the WMI route

    /// <summary>
    /// Finds the Acer event classes once, and decides whether the machine is an Acer at all.
    /// </summary>
    /// <remarks>
    /// A metadata query is the only way to ask about an event class: it has no instances to enumerate, so
    /// <c>SELECT * FROM AcerGamingEvent</c> would wait for an event rather than answer. The preferred names are tried
    /// first, then the whole namespace is scanned for Acer/APGE event classes, because the class name is per model and
    /// the AN16-41 registers <c>AcerGenericEvent</c> rather than the gaming class older machines use.
    /// </remarks>
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

                foreach (var className in PreferredEventClassNames)
                {
                    if (ClassExists(scope, className)) AddEventClass(className);
                }

                foreach (var className in ScanEventClasses(scope))
                {
                    AddEventClass(className);
                }

                if (_eventClassNames.Count > 0)
                {
                    AppLog.Info("Nitro tuşu WMI olay sınıfı bulundu", $"sınıf='{string.Join("', '", _eventClassNames)}'");
                }
                else
                {
                    AppLog.Info("Nitro tuşu WMI üzerinden bulunamadı", "Acer olay sınıfı kayıtlı değil.");
                }

                AcerHardwarePresent = DetectAcerHardware(scope);
                if (AcerHardwarePresent)
                {
                    AppLog.Info("Acer donanımı algılandı",
                        $"model='{WindowsMachineModel.Read() ?? "bilinmiyor"}' olay sınıfı={_eventClassNames.Count}");
                }
            }
            catch (Exception ex)
            {
                // A machine without the Acer hotkey driver, or a WMI service that is not answering.
                AppLog.Info("Acer WMI olay sınıfı sorgulanamadı", ex.Message);
            }
        }
    }

    private void AddEventClass(string className)
    {
        if (_eventClassNames.Contains(className, StringComparer.OrdinalIgnoreCase)) return;
        _eventClassNames.Add(className);
    }

    private static bool ClassExists(ManagementScope scope, string className)
    {
        using var searcher = new ManagementObjectSearcher(
            scope, new ObjectQuery($"SELECT * FROM meta_class WHERE __CLASS = '{className}'"));
        using var results = searcher.Get();
        return results.Count > 0;
    }

    /// <summary>
    /// Every Acer/APGE class in <c>root\WMI</c> that is an event class. The derivation chain is what separates an
    /// event class from a method class such as <c>AcerGamingFunction</c>, which cannot be subscribed to.
    /// </summary>
    private static IEnumerable<string> ScanEventClasses(ManagementScope scope)
    {
        var found = new List<string>();

        try
        {
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM meta_class"));
            using var results = searcher.Get();

            foreach (ManagementClass item in results)
            {
                using (item)
                {
                    var name = item["__CLASS"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!EventClassFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!IsEventClass(item)) continue;

                    found.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("Acer WMI olay sınıfları taranamadı", ex.Message);
        }

        return found;
    }

    private static bool IsEventClass(ManagementClass item)
    {
        if (item["__DERIVATION"] is not string[] chain) return false;

        return chain.Any(link =>
            link.Equals("__ExtrinsicEvent", StringComparison.OrdinalIgnoreCase) ||
            link.Equals("WMIEvent", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the machine is an Acer: an Acer-only WMI class, or an Acer/Predator model string.</summary>
    private static bool DetectAcerHardware(ManagementScope scope)
    {
        foreach (var className in AcerIdentityClassNames)
        {
            if (ClassExists(scope, className)) return true;
        }

        return LooksLikeAcer(WindowsMachineModel.Read());
    }

    /// <summary>
    /// True when an event from a shared ACPI class belongs to the Acer device. An instance name that matches nothing
    /// is kept rather than dropped: the name is per firmware revision, and dropping an unrecognised one would silence
    /// the key on exactly the machines this fallback exists for.
    /// </summary>
    private static bool IsAcerInstance(string instanceName) =>
        instanceName.Length == 0 ||
        AcerInstanceFragments.Any(f => instanceName.Contains(f, StringComparison.OrdinalIgnoreCase));

    private void StartWmi()
    {
        if (_eventClassNames.Count == 0) return;

        foreach (var className in _eventClassNames)
        {
            // WITHIN first: ACPI-WMI events are not pushed, and without the clause the watcher subscribes and then
            // never fires. Some providers reject the clause outright with "Invalid query", so the plain form is the
            // fallback rather than the only form — a subscription that never fires still beats no subscription.
            if (TryStartWatcher(className, EventPollInterval)) continue;
            if (TryStartWatcher(className, null)) continue;

            AppLog.Warn("Acer WMI olay izleyicisi başlatılamadı", $"{className}: iki sorgu biçimi de reddedildi");
        }
    }

    private bool TryStartWatcher(string className, TimeSpan? within)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();

            var query = new WqlEventQuery($"SELECT * FROM {className}");
            if (within.HasValue) query.WithinInterval = within.Value;

            var watcher = new ManagementEventWatcher(scope, query);
            watcher.EventArrived += OnWmiEvent;
            watcher.Start();

            _watchers.Add(watcher);
            AppLog.Info("Nitro tuşu WMI olay izleyicisi bağlandı",
                within.HasValue
                    ? $"sınıf='{className}' aralık={within.Value.TotalSeconds:0} sn"
                    : $"sınıf='{className}' aralık=yok");

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("Acer WMI olay izleyicisi reddedildi",
                $"sınıf='{className}' aralık={(within.HasValue ? "var" : "yok")}: {ex.Message}");

            return false;
        }
    }

    private void OnWmiEvent(object sender, EventArrivedEventArgs e)
    {
        var className = (sender as ManagementEventWatcher)?.Query?.QueryString ?? "?";
        var instanceName = ReadString(e.NewEvent, "InstanceName");
        uint code = ReadEventCode(e.NewEvent);

        if (NitroKeyEventCodes.Contains(code) && IsAcerInstance(instanceName))
        {
            Raise($"WMI {className}");
            return;
        }

        // Logged rather than ignored: the code is per model, and this line is what turns "the key does nothing" into a
        // one-line fix. The instance name is included because the generic ACPI event class is shared with every other
        // ACPI-WMI device on the machine.
        AppLog.Info("Acer WMI olayı (Nitro tuşu değil)",
            $"sınıf='{className}' örnek='{instanceName}' kod=0x{code:X2} " +
            $"veri={Convert.ToHexString(ReadEventBytes(e.NewEvent))}");
    }

    /// <summary>
    /// The event's numeric payload. <c>EventType</c> is the property on the gaming class; <c>EventDetail</c> is the
    /// ACPI byte array the generic class carries, and its first byte is the event code. The alternatives are read
    /// because a driver update has renamed the property before.
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

        var detail = ReadEventBytes(ev);
        return detail.Length > 0 ? detail[0] : uint.MaxValue;
    }

    /// <summary>The ACPI payload of a generic Acer event, or an empty array when the class does not carry one.</summary>
    private static byte[] ReadEventBytes(ManagementBaseObject? ev)
    {
        try
        {
            return ev?.Properties["EventDetail"]?.Value as byte[] ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string ReadString(ManagementBaseObject? ev, string property)
    {
        try
        {
            return ev?.Properties[property]?.Value?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
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
            if (NitroKeyVirtualKeys.Contains(report.VirtualKey))
            {
                Raise($"ham giriş VK=0x{report.VirtualKey:X2}");
                return;
            }

            if (NitroKeyScanCodes.Contains(report.MakeCode) && (report.Flags & RiKeyE0) != 0)
            {
                Raise($"ham giriş tarama=0x{report.MakeCode:X2} VK=0x{report.VirtualKey:X2}");
                return;
            }

            // Logged so a model whose button arrives as an OEM scan code can be identified without guessing at it.
            if (_loggedKeys < MaxLoggedReports)
            {
                _loggedKeys++;
                AppLog.Info("Acer klavye raporu",
                    $"vk=0x{report.VirtualKey:X2} tarama=0x{report.MakeCode:X2} bayrak=0x{report.Flags:X2}");
            }

            return;
        }

        if (report.Payload.Length == 0) return;

        // The report layout is the device's own, so the match is on the 16-bit consumer usage rather than on a byte
        // offset: a report that carries AL Launch Application 1 or 2 is the button, whatever else it carries.
        for (int i = 0; i + 1 < report.Payload.Length; i++)
        {
            ushort usage = (ushort)(report.Payload[i] | (report.Payload[i + 1] << 8));
            if (Array.IndexOf(NitroKeyUsages, usage) < 0) continue;

            Raise($"ham giriş kullanım=0x{usage:X4}");
            return;
        }

        // Logged once per distinct report so a machine whose button arrives as a vendor report can be identified
        // without guessing at the layout. The usage page comes from the device, not from the report.
        if (_loggedReports < MaxLoggedReports)
        {
            _loggedReports++;
            AppLog.Info("Acer HID raporu",
                $"sayfa=0x{report.UsagePage:X4} kullanım=0x{report.Usage:X4} veri={Convert.ToHexString(report.Payload)}");
        }
    }

    /// <summary>
    /// Accepts one press: debounces it, logs it and raises the event. Internal so a test can drive the whole path
    /// without a device.
    /// </summary>
    internal void Raise(string source)
    {
        if (!_debounce.ShouldAccept()) return;

        // Logged on every accepted press. Without this line the only evidence that the key works is the window
        // moving, which is exactly what a user reporting "it does nothing" cannot show.
        AppLog.Info("Nitro tuşu algılandı", source);

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
    internal readonly record struct RawInputReport(
        bool IsKeyboard,
        ushort VirtualKey,
        ushort MakeCode,
        ushort Flags,
        ushort UsagePage,
        ushort Usage,
        byte[] Payload);

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
        private const uint RidiDeviceInfo = 0x2000000B;

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
                new RawInputDevice { usUsagePage = 0xFF01, usUsage = 0x01, dwFlags = RidevInputSink, hwndTarget = _hwnd },
                new RawInputDevice { usUsagePage = 0xFF02, usUsage = 0x01, dwFlags = RidevInputSink, hwndTarget = _hwnd },
                new RawInputDevice { usUsagePage = 0xFFA0, usUsage = 0x01, dwFlags = RidevInputSink, hwndTarget = _hwnd },
                new RawInputDevice { usUsagePage = 0xFFBC, usUsage = 0x01, dwFlags = RidevInputSink, hwndTarget = _hwnd }
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

                    _onReport(new RawInputReport(
                        true, keyboard.VKey, keyboard.MakeCode, keyboard.Flags, 0x01, 0x06, []));
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

                var (usagePage, usage) = ReadDeviceUsage(header.hDevice);

                _onReport(new RawInputReport(false, 0, 0, 0, usagePage, usage, bytes));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// The usage page and usage of the device that sent a report. <c>RAWHID</c> does not carry them, and without
        /// them the log line cannot say which collection the button arrived on.
        /// </summary>
        private static (ushort Page, ushort Usage) ReadDeviceUsage(IntPtr device)
        {
            var info = new RawDeviceInfo { cbSize = (uint)Marshal.SizeOf<RawDeviceInfo>() };
            uint size = info.cbSize;

            if (!GetRawInputDeviceInfo(device, RidiDeviceInfo, ref info, ref size)) return (0, 0);
            if (info.dwType != RimTypeHid) return (0, 0);

            return (info.usUsagePage, info.usUsage);
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

        /// <summary>
        /// <c>RID_DEVICE_INFO</c>, flattened. The union is sixteen bytes for every device type, so the HID members sit
        /// at the same offset as the mouse and keyboard ones and one struct covers all three.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct RawDeviceInfo
        {
            public uint cbSize;
            public uint dwType;
            public uint dwVendorId;
            public uint dwProductId;
            public uint dwVersionNumber;
            public ushort usUsagePage;
            public ushort usUsage;
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

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetRawInputDeviceInfo(IntPtr device, uint command, ref RawDeviceInfo data, ref uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
