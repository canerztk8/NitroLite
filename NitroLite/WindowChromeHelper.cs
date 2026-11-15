using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NitroLite.Core;

namespace NitroLite;

/// <summary>
/// Native fixes for a borderless (WindowStyle=None + WindowChrome) WPF window:
/// <list type="bullet">
/// <item>WM_GETMINMAXINFO: a maximized borderless window would otherwise cover the taskbar and overhang the
/// screen edges; the maximized bounds are clamped to the work area of the monitor the window is on.</item>
/// <item>Windows 11 rounded corners (DWMWA_WINDOW_CORNER_PREFERENCE); ignored on Windows 10.</item>
/// <item>DWMWA_USE_IMMERSIVE_DARK_MODE: the resize border and the system-drawn parts of the frame follow the
/// palette. Without it a dark window keeps a light 1px frame, which reads as a seam around the whole app.</item>
/// </list>
/// </summary>
internal static class WindowChromeHelper
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    // 20 is the documented attribute; 19 is what Windows 10 1809–1903 answered to before it was renamed.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1 = 19;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Attach(Window window, AppTheme theme)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

            try
            {
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // dwmapi attribute not available (older Windows): keep square corners.
            }

            ApplyDarkFrame(hwnd, ThemeManager.IsDark(theme));
        };
    }

    /// <summary>
    /// Tells DWM whether the window frame is dark. Every dark-based palette asks for it through
    /// <see cref="ThemeCatalog.IsDarkTheme"/>, so a new one needs no change here.
    /// </summary>
    public static void ApplyDarkFrame(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        int value = dark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref value, sizeof(int));
            }
        }
        catch
        {
            // dwmapi attribute not available (older Windows): the frame keeps the system colour.
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            ClampMaximizedToWorkArea(hwnd, lParam);
            // Not marked handled: WPF still applies MinWidth/MinHeight to ptMinTrackSize.
        }
        return IntPtr.Zero;
    }

    private static void ClampMaximizedToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        // Positions are relative to the monitor's origin.
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
    }
}
