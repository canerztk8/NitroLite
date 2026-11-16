using System.Runtime.InteropServices;

namespace NitroLite.Windows;

/// <summary>
/// The AC and battery state, read from <c>GetSystemPowerStatus</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Windows counterpart of <c>LinuxPowerSupply.Read</c>: game detection needs to know whether the machine is on
/// battery so a Turbo profile can be clamped to Balanced instead of being written and silently refused by the
/// firmware.
/// </para>
/// <para>
/// A machine with no battery reports <c>BatteryLifePercent = 255</c>, which is "unknown" rather than "0 %". It is
/// returned as null so the caller keeps the profile the user configured instead of downgrading every game on a
/// desktop replacement.
/// </para>
/// </remarks>
public static class WindowsPowerStatus
{
    private const byte BatteryFlagNoSystemBattery = 128;
    private const byte UnknownPercent = 255;

    /// <summary>
    /// <c>OnAc</c> defaults to true when the query fails, which is the same answer the Linux backend gives when there
    /// is no adapter node: assuming AC keeps the target the user configured.
    /// </summary>
    public static (bool OnAc, int? BatteryPercent) Read()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return (true, null);

            var onAc = status.ACLineStatus != 0;

            // 255 is "unknown", and a machine with no battery at all reports it too. Both read as null.
            int? percent = status.BatteryLifePercent == UnknownPercent ||
                           (status.BatteryFlag & BatteryFlagNoSystemBattery) != 0
                ? null
                : status.BatteryLifePercent;

            return (onAc, percent);
        }
        catch
        {
            return (true, null);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
