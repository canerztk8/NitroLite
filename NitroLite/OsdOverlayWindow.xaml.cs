using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using NitroLite.Core;

namespace NitroLite;

public partial class OsdOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newStyle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newStyle)
    {
        return IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, newStyle) : new IntPtr(SetWindowLong32(hwnd, index, newStyle.ToInt32()));
    }

    public int CurrentCorner { get; private set; } = 0; // 0: TopLeft, 1: TopRight, 2: BottomRight, 3: BottomLeft
    public int CurrentMonitor { get; private set; } = 0; // index into Screen.AllScreens
    public bool IsExiting { get; set; } = false;

    public static int MonitorCount
    {
        get
        {
            try { return Math.Max(1, System.Windows.Forms.Screen.AllScreens.Length); }
            catch { return 1; }
        }
    }

    public OsdOverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (s, e) => UpdatePosition();
        // Resolution, scaling or monitor changes: put the OSD back in its corner (or on the primary
        // monitor if its monitor was removed). Click-through means it cannot be moved by mouse.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (s, e) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(UpdatePosition));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (IsExiting)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // The handle exists here and the window is not visible yet, so the overlay never takes focus from a
        // full-screen game on its first Show().
        ApplyOverlayStyles();
    }

    private void ApplyOverlayStyles()
    {
        // 64-bit safe click-through and non-activating window flags.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        IntPtr exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long newExStyle = exStyle.ToInt64() | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(newExStyle));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Safety net: re-apply in case the style was reset, then position the window.
        ApplyOverlayStyles();
        UpdatePosition();
    }

    public void SetCorner(int corner)
    {
        CurrentCorner = ((corner % 4) + 4) % 4;
        UpdatePosition();
    }

    public void SetMonitor(int monitor)
    {
        CurrentMonitor = Math.Max(0, monitor);
        UpdatePosition();
    }

    /// <summary>Next corner; after the fourth corner it moves to the next monitor (when there is more than one).</summary>
    public void CycleCorner()
    {
        CurrentCorner = (CurrentCorner + 1) % 4;
        if (CurrentCorner == 0)
        {
            CurrentMonitor = (CurrentMonitor + 1) % MonitorCount;
        }
        UpdatePosition();
    }

    /// <summary>
    /// Places the OSD in its corner of its monitor's work area (taskbar excluded). Works in physical pixels with
    /// SetWindowPos, so mixed-DPI multi-monitor setups need no DIP conversion.
    /// </summary>
    public void UpdatePosition()
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdatePosition);
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return; // positioned on Loaded

        System.Drawing.Rectangle work;
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (CurrentMonitor >= screens.Length) CurrentMonitor = 0; // monitor unplugged → primary list start
            work = screens.Length > 0 ? screens[CurrentMonitor].WorkingArea : System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        }
        catch
        {
            return;
        }

        GetWindowRect(hwnd, out var rect);
        int w = Math.Max(1, rect.Right - rect.Left);
        int h = Math.Max(1, rect.Bottom - rect.Top);
        int margin = 20;

        int x, y;
        switch (CurrentCorner)
        {
            case 1: // Top-Right
                x = work.Right - w - margin; y = work.Top + margin; break;
            case 2: // Bottom-Right
                x = work.Right - w - margin; y = work.Bottom - h - margin; break;
            case 3: // Bottom-Left
                x = work.Left + margin; y = work.Bottom - h - margin; break;
            default: // Top-Left
                x = work.Left + margin; y = work.Top + margin; break;
        }
        x = Math.Max(work.Left, x);
        y = Math.Max(work.Top, y);

        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void UpdateData(MonitorData data, OperatingMode mode, string? gameTitle = null)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;

            // CPU
            TxtOsdCpuTemp.Text = TelemetryFormatter.FormatCpuTemperature(data.CpuTemperature);
            // CPU package power is estimated from usage and clock (no sensor reading), so mark it as approximate.
            TxtOsdCpuPower.Text = "~" + TelemetryFormatter.FormatCpuPower(data.CpuPowerWatts);
            TxtOsdCpuFreq.Text = TelemetryFormatter.FormatCpuFrequency(data.CpuFrequencyMHz);
            TxtOsdCpuUsage.Text = $"{data.CpuUsage:F0}%";

            // GPU & Sleep State Handling
            bool isGpuSleeping = data.GpuTemperature <= 0;
            TxtOsdGpuTemp.Text = TelemetryFormatter.FormatGpuTemperature(data.GpuTemperature, isGpuSleeping, data.GpuMetricsAvailable);
            TxtOsdGpuPower.Text = TelemetryFormatter.FormatGpuPower(data.GpuPowerWatts, isGpuSleeping, data.GpuMetricsAvailable);
            TxtOsdGpuFreq.Text = TelemetryFormatter.FormatGpuFrequency(data.GpuFrequencyMHz, isGpuSleeping, data.GpuMetricsAvailable);
            TxtOsdVram.Text = TelemetryFormatter.FormatVramUsage(data.VramUsedMB, data.VramTotalMB, isGpuSleeping, data.GpuMetricsAvailable);

            // RAM & Fans
            if (data.RamTotalMB > 0)
            {
                double ramUsedGB = (data.RamTotalMB * (data.RamUsage / 100.0)) / 1024.0;
                TxtOsdRam.Text = FormattableString.Invariant($"{ramUsedGB:F1} GB");
            }
            else
            {
                TxtOsdRam.Text = Loc.T("-- GB");
            }

            TxtOsdFan.Text = Loc.T($"FAN: {TelemetryFormatter.FormatFanSpeedRpm(data.CpuFanSpeed)}");
        });
    }

    public void UpdateData(MonitorData data, OperatingMode mode)
    {
        UpdateData(data, mode, (string?)null);
    }
}
