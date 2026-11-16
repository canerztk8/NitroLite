namespace NitroLite.Core;

/// <summary>
/// The machine's own description: the model strings and the core count that no sensor reports.
/// </summary>
/// <remarks>
/// The Acer backend answers temperatures, fan speeds and modes. It has never answered "which processor is this", and
/// hard-coding one Ryzen part is what made the monitoring page wrong on every other Nitro. This is the half the
/// operating system can answer, read once and kept.
/// </remarks>
public sealed class MonitorInfo
{
    public string CpuName { get; init; } = "";

    public string GpuName { get; init; } = "";

    /// <summary>Physical cores, or 0 when the platform did not say.</summary>
    public int CpuCores { get; init; }

    /// <summary>Logical processors, or 0 when the platform did not say.</summary>
    public int CpuThreads { get; init; }

    /// <summary>
    /// The processor's rated maximum clock in MHz. The CPU package-power estimate is built around a reference clock,
    /// and feeding it the real rating is what keeps the number honest on a machine that is not this one.
    /// </summary>
    public int CpuMaxClockMhz { get; init; }

    public double RamTotalMb { get; init; }

    /// <summary>e.g. "DDR5 5600 MT/s · 2 modül · 32 GB". Empty when nothing could be read.</summary>
    public string MemoryText { get; init; } = "";

    /// <summary>e.g. "8 çekirdek / 16 iş parçacığı"; falls back to the thread count the backend reported.</summary>
    public string FormatCoreText(int fallbackThreads)
    {
        if (CpuCores > 0 && CpuThreads > 0) return Loc.T($"{CpuCores} çekirdek / {CpuThreads} iş parçacığı");
        return fallbackThreads > 0 ? Loc.T($"{fallbackThreads} iş parçacığı") : "";
    }
}

/// <summary>
/// What the operating system can measure about the machine, independent of the Acer firmware.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs, and they are separate on purpose. <see cref="Enrich"/> fills the fields the firmware does not carry —
/// load, package watts, clocks, VRAM, RAM — onto a sample the hardware controller already produced.
/// <see cref="CreateFallback"/> builds a whole sample from the OS alone, for a machine whose Acer service is missing:
/// the page then still shows load and memory instead of a wall of placeholders.
/// </para>
/// <para>
/// Neither method throws. A platform that cannot answer leaves the field at zero, and zero is what the page reads as
/// "no reading" rather than as a measurement.
/// </para>
/// </remarks>
public interface IHardwareMonitor
{
    /// <summary>True when at least one OS-level source answered. False hides the rows that depend on it.</summary>
    bool IsAvailable { get; }

    /// <summary>The model strings and core counts, read once.</summary>
    MonitorInfo Info { get; }

    /// <summary>Fills load, power, clocks, VRAM and RAM onto <paramref name="data"/>. Never throws.</summary>
    void Enrich(MonitorData data);

    /// <summary>A sample built only from what the OS can measure, for a machine with no Acer backend.</summary>
    MonitorData CreateFallback();
}
