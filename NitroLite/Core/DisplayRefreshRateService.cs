using System;
using System.Runtime.InteropServices;

namespace NitroLite.Core;

/// <summary>
/// Win32 P/Invoke implementation of display refresh rate switching using User32 EnumDisplaySettings
/// and ChangeDisplaySettingsEx. Operates strictly at the OS display configuration layer, preserving
/// GPU hardware topology, MUX switch states, and vendor drivers intact.
/// </summary>
public class DisplayRefreshRateService : IDisplayRefreshRateService
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_GLOBAL = 0x00000008;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DM_DISPLAYFREQUENCY = 0x00400000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public short dmOrientation;
        public short dmPaperSize;
        public short dmPaperLength;
        public short dmPaperWidth;
        public short dmScale;
        public short dmCopies;
        public short dmDefaultSource;
        public short dmPrintQuality;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    public int GetCurrentRefreshRate()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
            {
                return dm.dmDisplayFrequency;
            }
        }
        catch { }
        return 60; // Safe fallback
    }

    public int GetMaxRefreshRate()
    {
        try
        {
            var currentDm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref currentDm))
            {
                return 60;
            }

            int width = currentDm.dmPelsWidth;
            int height = currentDm.dmPelsHeight;
            int bpp = currentDm.dmBitsPerPel;

            int maxRate = 60;
            int modeIndex = 0;
            var testDm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            while (EnumDisplaySettings(null, modeIndex, ref testDm))
            {
                if (testDm.dmPelsWidth == width &&
                    testDm.dmPelsHeight == height &&
                    testDm.dmBitsPerPel == bpp)
                {
                    if (testDm.dmDisplayFrequency > maxRate)
                    {
                        maxRate = testDm.dmDisplayFrequency;
                    }
                }
                modeIndex++;
            }
            return maxRate;
        }
        catch
        {
            return 60;
        }
    }

    public bool SetRefreshRate(int hz)
    {
        if (hz <= 0) return false;

        try
        {
            var currentDm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref currentDm))
            {
                return false;
            }

            if (currentDm.dmDisplayFrequency == hz)
            {
                return true; // Frequency already active
            }

            // Enumerate supported display modes to locate matching resolution and target refresh rate
            int width = currentDm.dmPelsWidth;
            int height = currentDm.dmPelsHeight;
            int bpp = currentDm.dmBitsPerPel;

            var targetDm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            bool found = false;

            int modeIndex = 0;
            var testDm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            while (EnumDisplaySettings(null, modeIndex, ref testDm))
            {
                if (testDm.dmPelsWidth == width &&
                    testDm.dmPelsHeight == height &&
                    testDm.dmDisplayFrequency == hz &&
                    testDm.dmBitsPerPel == bpp)
                {
                    targetDm = testDm;
                    found = true;
                    break;
                }
                modeIndex++;
            }

            if (!found)
            {
                // Target refresh rate is not supported by display hardware/driver for this resolution.
                // Do not attempt to force an unsupported mode via CDS_UPDATEREGISTRY to prevent black screens.
                return false;
            }

            targetDm.dmFields |= DM_DISPLAYFREQUENCY;
            int result = ChangeDisplaySettingsEx(null, ref targetDm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_GLOBAL, IntPtr.Zero);
            return result == DISP_CHANGE_SUCCESSFUL;
        }
        catch
        {
            return false;
        }
    }
}
