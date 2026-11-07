using System;
using System.Runtime.InteropServices;

namespace NitroLite.Core;

/// <summary>
/// Refresh rate switching for the laptop's built-in panel (eDP/LVDS), found with QueryDisplayConfig.
/// It never touches external monitors: changing the Windows primary display would hit an external screen
/// whenever one is set as primary. When no internal panel is active (lid closed), nothing is changed.
/// Operates at the OS display configuration layer, leaving GPU/MUX state and drivers alone.
/// </summary>
public class DisplayRefreshRateService : IDisplayRefreshRateService
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_GLOBAL = 0x00000008;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DM_DISPLAYFREQUENCY = 0x00400000;

    // QueryDisplayConfig
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint OUTPUT_TECHNOLOGY_LVDS = 6;
    private const uint OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    private const uint OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;
    private const uint OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public LUID sourceAdapterId;
        public uint sourceId;
        public uint sourceModeInfoIdx;
        public uint sourceStatusFlags;
        public LUID targetAdapterId;
        public uint targetId;
        public uint targetModeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshNumerator;
        public uint refreshDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint targetStatusFlags;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    public static bool IsInternalOutputTechnology(uint technology) =>
        technology is OUTPUT_TECHNOLOGY_LVDS or OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
            or OUTPUT_TECHNOLOGY_UDI_EMBEDDED or OUTPUT_TECHNOLOGY_INTERNAL;

    /// <summary>GDI device name of the active built-in panel (e.g. "\.\DISPLAY1"), or null if none is active.</summary>
    public static string? FindInternalPanelDeviceName()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0 || pathCount == 0)
                return null;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return null;

            for (int i = 0; i < pathCount; i++)
            {
                if (!IsInternalOutputTechnology(paths[i].outputTechnology)) continue;
                var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = paths[i].sourceAdapterId,
                    id = paths[i].sourceId
                };
                if (DisplayConfigGetDeviceInfo(ref request) == 0 && !string.IsNullOrWhiteSpace(request.viewGdiDeviceName))
                    return request.viewGdiDeviceName;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Internal panel name for the current call; resolved each time so docking/lid changes are followed.</summary>
    private static string? Target => FindInternalPanelDeviceName();

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

    /// <summary>Current refresh rate of the built-in panel; 0 when no internal panel is active.</summary>
    public int GetCurrentRefreshRate()
    {
        try
        {
            var device = Target;
            if (device == null) return 0;
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm))
            {
                return dm.dmDisplayFrequency;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>Highest refresh rate of the built-in panel at its current resolution; 0 when no internal panel is active.</summary>
    public int GetMaxRefreshRate()
    {
        try
        {
            var device = Target;
            if (device == null) return 0;
            var currentDm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref currentDm))
            {
                return 0;
            }

            int width = currentDm.dmPelsWidth;
            int height = currentDm.dmPelsHeight;
            int bpp = currentDm.dmBitsPerPel;

            int maxRate = 60;
            int modeIndex = 0;
            var testDm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            while (EnumDisplaySettings(device, modeIndex, ref testDm))
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
            return 0;
        }
    }

    /// <summary>Sets the built-in panel refresh rate; false (and nothing changed) when no internal panel is active.</summary>
    public bool SetRefreshRate(int hz)
    {
        if (hz <= 0) return false;

        try
        {
            var device = Target;
            if (device == null) return false;
            var currentDm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref currentDm))
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
            while (EnumDisplaySettings(device, modeIndex, ref testDm))
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
            int result = ChangeDisplaySettingsEx(device, ref targetDm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_GLOBAL, IntPtr.Zero);
            return result == DISP_CHANGE_SUCCESSFUL;
        }
        catch
        {
            return false;
        }
    }
}

public class TestDisplayRefreshRateServiceStub : IDisplayRefreshRateService
{
    private int _hz = 165;
    public int SetRefreshRateCalls { get; private set; }
    public int LastSetRefreshRate { get; private set; } = 165;

    public int GetCurrentRefreshRate() => _hz;
    public int GetMaxRefreshRate() => 165;
    public bool SetRefreshRate(int hz)
    {
        SetRefreshRateCalls++;
        LastSetRefreshRate = hz;
        _hz = hz;
        return true;
    }
}
