using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NitroLite.Core;

namespace NitroLite;

public partial class OsdOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newStyle);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newStyle)
    {
        return IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, newStyle) : new IntPtr(SetWindowLong32(hwnd, index, newStyle.ToInt32()));
    }

    public int CurrentCorner { get; private set; } = 0; // 0: TopLeft, 1: TopRight, 2: BottomRight, 3: BottomLeft

    public OsdOverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (s, e) => UpdatePosition();
        MouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount == 2)
            {
                CycleCorner();
            }
        };
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Apply 64-bit safe click-through and non-activating window flags
        var hwnd = new WindowInteropHelper(this).Handle;
        IntPtr exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long newExStyle = exStyle.ToInt64() | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(newExStyle));

        UpdatePosition();
    }

    public void SetCorner(int corner)
    {
        CurrentCorner = ((corner % 4) + 4) % 4;
        UpdatePosition();
    }

    public void CycleCorner()
    {
        CurrentCorner = (CurrentCorner + 1) % 4;
        UpdatePosition();
    }

    public void UpdatePosition()
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdatePosition);
            return;
        }

        var workArea = SystemParameters.WorkArea;
        const double margin = 20.0;
        double w = ActualWidth > 0 ? ActualWidth : 350.0;
        double h = ActualHeight > 0 ? ActualHeight : 90.0;

        switch (CurrentCorner)
        {
            case 0: // Top-Left
                Left = workArea.Left + margin;
                Top = workArea.Top + margin;
                break;
            case 1: // Top-Right
                Left = Math.Max(workArea.Left, workArea.Right - w - margin);
                Top = workArea.Top + margin;
                break;
            case 2: // Bottom-Right
                Left = Math.Max(workArea.Left, workArea.Right - w - margin);
                Top = Math.Max(workArea.Top, workArea.Bottom - h - margin);
                break;
            case 3: // Bottom-Left
                Left = workArea.Left + margin;
                Top = Math.Max(workArea.Top, workArea.Bottom - h - margin);
                break;
        }
    }

    public void UpdateData(MonitorData data, OperatingMode mode, string? gameTitle = null)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;

            // CPU
            TxtOsdCpuTemp.Text = TelemetryFormatter.FormatCpuTemperature(data.CpuTemperature);
            TxtOsdCpuPower.Text = TelemetryFormatter.FormatCpuPower(data.CpuPowerWatts);
            TxtOsdCpuFreq.Text = TelemetryFormatter.FormatCpuFrequency(data.CpuFrequencyMHz);
            TxtOsdCpuUsage.Text = $"{data.CpuUsage:F0}%";

            // GPU & Sleep State Handling
            bool isGpuSleeping = data.GpuTemperature <= 0;
            TxtOsdGpuTemp.Text = TelemetryFormatter.FormatGpuTemperature(data.GpuTemperature, isGpuSleeping);
            TxtOsdGpuPower.Text = TelemetryFormatter.FormatGpuPower(data.GpuPowerWatts, isGpuSleeping);
            TxtOsdGpuFreq.Text = TelemetryFormatter.FormatGpuFrequency(data.GpuFrequencyMHz, isGpuSleeping);
            TxtOsdVram.Text = TelemetryFormatter.FormatVramUsage(data.VramUsedMB, data.VramTotalMB, isGpuSleeping);

            // RAM & Fans
            if (data.RamTotalMB > 0)
            {
                double ramUsedGB = (data.RamTotalMB * (data.RamUsage / 100.0)) / 1024.0;
                TxtOsdRam.Text = $"{ramUsedGB:F1}GB";
            }
            else
            {
                TxtOsdRam.Text = "-- GB";
            }

            TxtOsdFan.Text = $"FAN: {TelemetryFormatter.FormatFanSpeed(data.CpuFanSpeed)}";
        });
    }

    public void UpdateData(MonitorData data, OperatingMode mode)
    {
        UpdateData(data, mode, (string?)null);
    }

    public void UpdateFpsData(FpsTelemetryInfo? fpsInfo)
    {
    }

    public void UpdateData(MonitorData data, OperatingMode mode, FpsTelemetryInfo? fpsInfo)
    {
        UpdateData(data, mode, fpsInfo?.GameTitle);
    }
}
