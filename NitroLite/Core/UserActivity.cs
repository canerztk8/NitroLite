using System;
using System.Runtime.InteropServices;

namespace NitroLite.Core;

/// <summary>
/// When the user last touched the keyboard or mouse, in <see cref="Environment.TickCount64"/> terms.
/// </summary>
/// <remarks>
/// A wireless mouse answers vendor HID commands only while it is awake, and it wakes on user input. The ATK
/// profile retry uses this to fire the moment the user touches something instead of waiting out a fixed
/// interval that usually lands while the mouse is asleep again.
/// </remarks>
internal static class UserActivity
{
    /// <summary>
    /// Tick count of the last input event in this session, or 0 when Windows cannot report it. Callers treat 0
    /// as "no activity seen", which only costs them the early retry.
    /// </summary>
    public static long LastInputTick
    {
        get
        {
            try
            {
                var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (!GetLastInputInfo(ref info)) return 0;

                // dwTime is a 32-bit tick count and wraps every 49.7 days. Subtracting in 32-bit space gives
                // the idle duration correctly across the wrap; the 64-bit tick count then places it in time.
                uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
                long now = Environment.TickCount64;
                return idleMs >= now ? 0 : now - idleMs;
            }
            catch
            {
                return 0;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
