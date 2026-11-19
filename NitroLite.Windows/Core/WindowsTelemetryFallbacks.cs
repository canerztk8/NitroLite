using System.Runtime.InteropServices;

namespace NitroLite.Core;

/// <summary>
/// The user-mode telemetry sources Windows itself offers, for the readings the Acer firmware does not answer.
/// <para>
/// Strictly read-only and strictly user mode. No kernel driver is installed and none is required: the thermal zone
/// comes from the ACPI driver through PDH, the CPU clock from <c>CallNtPowerInformation</c> (see
/// <see cref="HardwareMonitorService.GetNativeCpuFrequencyMhz"/>), and the GPU values from NVIDIA's user-mode library.
/// That is deliberate — NitroLite ships next to anti-cheat software, and a project that loads an unsigned kernel
/// driver to read a thermometer is a project whose users get banned.
/// </para>
/// <para>
/// DXGI's video-memory budget and AMD's ADL are <i>detected</i> but not queried. Both are COM interfaces whose vtables
/// would have to be declared by hand to stay trim-safe, and one wrong slot is an access violation that no catch block
/// can survive. The reading they would add is already available from NVML on the machines that matter, so the honest
/// answer is a report line that says "present, not queried" rather than a crash in the field.
/// </para>
/// </summary>
public static class WindowsTelemetryFallbacks
{
    /// <summary>The counter path that carries the ACPI thermal zone's temperature, in kelvin.</summary>
    private const string ThermalZoneCounter = @"\Thermal Zone Information(*)\Temperature";

    /// <summary>PDH_FMT_DOUBLE.</summary>
    private const uint PdhFormatDouble = 0x0000_0200;

    /// <summary>PDH_MORE_DATA: the buffer was too small, which is how the required size is discovered.</summary>
    private const uint PdhMoreData = 0x8000_07D2;

    /// <summary>PDH_CSTATUS_VALID_DATA: the counter value in the item is a real measurement.</summary>
    private const uint PdhStatusValidData = 0x0000_0000;

    private const double KelvinOffset = 273.15;

    private static readonly Lazy<string> ThermalZoneProbe = new(ProbeThermalZone);

    /// <summary>
    /// The CPU temperature from the ACPI thermal zone, in degrees Celsius, or null when this machine has no zone the
    /// counter can read.
    /// <para>
    /// One zone is used, the first that reports a plausible value. A laptop with several zones reports the same
    /// package temperature in all of them on most models, and averaging zones that are not comparable (a battery zone
    /// and a package zone differ by twenty degrees) would invent a reading nobody measured.
    /// </para>
    /// </summary>
    public static double? CpuTemperatureCelsius()
    {
        IntPtr query = IntPtr.Zero;
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return null;
            if (PdhAddEnglishCounterW(query, ThermalZoneCounter, IntPtr.Zero, out var counter) != 0) return null;
            if (PdhCollectQueryData(query) != 0) return null;

            uint size = 0;
            var status = PdhGetFormattedCounterArrayW(counter, PdhFormatDouble, ref size, out _, IntPtr.Zero);
            if (status != PdhMoreData || size == 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PdhFormatDouble, ref size, out var count, buffer) != 0)
                    return null;

                int stride = Marshal.SizeOf<PdhCounterValueItem>();
                for (int index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<PdhCounterValueItem>(buffer + (index * stride));
                    if (item.Value.Status != PdhStatusValidData) continue;

                    double celsius = item.Value.Value - KelvinOffset;
                    if (celsius is >= 1 and <= 130) return celsius;
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            // Closing the query cannot fail in a way this method could act on: the reading has already been taken or
            // given up on, and there is no handle left to leak once the call returns.
            if (query != IntPtr.Zero) _ = PdhCloseQuery(query);
        }
    }

    /// <summary>
    /// What the fallback layers look like on this machine, one line each, for the hardware report. Read-only: the
    /// thermal zone is probed once and the two COM libraries are only checked for existence.
    /// </summary>
    public static IReadOnlyList<string> Describe()
    {
        var lines = new List<string>
        {
            $"cpu-temp fallback: {ThermalZoneProbe.Value}",
            $"cpu-clock fallback: CallNtPowerInformation (ntdll) · {DescribeNtPower()}",
            $"gpu fallback: NVML {Presence("nvml.dll")}",
            $"dxgi: {Presence("dxgi.dll")} · detected, not queried (VRAM comes from NVML)",
            $"amd adl: {Presence("atiadlxx.dll")} · detected, not queried"
        };

        return lines;
    }

    private static string DescribeNtPower()
    {
        try
        {
            return HardwareMonitorService.GetNativeCpuFrequencyMhz() > 0 ? "readable" : "not readable";
        }
        catch (Exception ex)
        {
            return $"not readable ({ex.GetType().Name})";
        }
    }

    private static string Presence(string fileName)
    {
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, fileName)) ? "present" : "absent";
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.GetType().Name})";
        }
    }

    private static string ProbeThermalZone()
    {
        var celsius = CpuTemperatureCelsius();
        return celsius is { } value
            ? $"PDH thermal zone readable ({value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} °C)"
            : "PDH thermal zone not readable";
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        out uint itemCount,
        IntPtr buffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>
    /// <c>PDH_FMT_COUNTERVALUE</c>: a status word followed by the value. The double is aligned to eight bytes, which
    /// is what <c>LayoutKind.Sequential</c> produces here and what the C declaration produces in <c>pdh.h</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterValue
    {
        public uint Status;
        public double Value;
    }

    /// <summary><c>PDH_FMT_COUNTERVALUE_ITEM_W</c>: the instance name (unused here) and its value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterValueItem
    {
        public IntPtr Name;
        public PdhCounterValue Value;
    }
}
