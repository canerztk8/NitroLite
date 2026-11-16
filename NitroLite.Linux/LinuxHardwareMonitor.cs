using System.Globalization;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// The Linux half of the monitoring page: load and memory from <c>/proc</c>, the processor's name from
/// <c>/proc/cpuinfo</c>, package watts from the kernel's RAPL counter when the machine exposes one.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is a kernel pseudo-file, so it is read through the same <see cref="ISysfsFileSystem"/> the rest of
/// the backend uses: a test can point it at a fixture tree and get the same numbers a real machine would produce.
/// </para>
/// <para>
/// Nothing is invented. A machine with no RAPL node reports 0 W, and the page reads 0 as "no reading" rather than as
/// a measurement — the same rule the temperature rows follow.
/// </para>
/// </remarks>
public sealed class LinuxHardwareMonitor : IHardwareMonitor
{
    private readonly ISysfsFileSystem _fs;
    private readonly string _procRoot;
    private readonly string _raplRoot;

    private MonitorInfo? _info;

    // /proc/stat is cumulative, so a load figure needs two reads. The first sample after start-up therefore reports
    // 0 %, which is honest: nothing has been measured yet.
    private long _lastIdle;
    private long _lastTotal;

    // RAPL energy is a monotonic micro-joule counter; watts are the delta over the elapsed time.
    private long _lastEnergyMicroJoules;
    private long _lastEnergyTicks;

    public LinuxHardwareMonitor(ISysfsFileSystem? fs = null, string procRoot = LinuxPaths.ProcRoot,
        string raplRoot = "/sys/class/powercap")
    {
        _fs = fs ?? RealSysfsFileSystem.Instance;
        _procRoot = procRoot;
        _raplRoot = raplRoot;
    }

    /// <summary><c>/proc</c> is always there on a Linux machine, so the rows that depend on it are always offered.</summary>
    public bool IsAvailable => _fs.DirectoryExists(_procRoot);

    public MonitorInfo Info => _info ??= ReadInfo();

    public void Enrich(MonitorData data)
    {
        if (data == null) return;

        var info = Info;
        if (data.CpuName.Length == 0) data.CpuName = info.CpuName;
        if (data.GpuName.Length == 0) data.GpuName = info.GpuName;
        if (data.CpuCores <= 0) data.CpuCores = info.CpuThreads;

        data.CpuUsage = ReadCpuUsage();
        data.CpuFrequencyMHz = ReadCpuFrequencyMhz();
        data.CpuPowerWatts = ReadCpuPackageWatts();

        var (totalMb, usedPercent) = ReadMemory();
        if (totalMb > 0)
        {
            data.RamTotalMB = totalMb;
            data.RamUsage = usedPercent;
        }

        // GPU load, VRAM and clocks have no portable Linux source: amdgpu exposes them per-process through debugfs,
        // which needs root, and the NVIDIA driver only through NVML. Leaving them at zero is what makes the page show
        // "no reading" instead of a fabricated 0 %.
        data.GpuMetricsAvailable = data.GpuTemperature > 0;
    }

    public MonitorData CreateFallback()
    {
        var data = new MonitorData();
        Enrich(data);
        return data;
    }

    private MonitorInfo ReadInfo()
    {
        string cpuName = "";
        int cores = 0;
        int threads = 0;
        int maxClockMhz = 0;

        var cpuInfo = _fs.ReadAllText(LinuxPaths.Combine(_procRoot, "cpuinfo"));
        if (!string.IsNullOrEmpty(cpuInfo))
        {
            foreach (var raw in cpuInfo.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("processor", StringComparison.Ordinal)) threads++;
                else if (line.StartsWith("model name", StringComparison.Ordinal) && cpuName.Length == 0)
                    cpuName = AfterColon(line);
                else if (line.StartsWith("cpu cores", StringComparison.Ordinal) && cores == 0)
                    cores = ParseInt(AfterColon(line));
                else if (line.StartsWith("cpu MHz", StringComparison.Ordinal))
                {
                    // The highest reported clock is the closest thing /proc has to a rated maximum.
                    if (double.TryParse(AfterColon(line), NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
                        maxClockMhz = Math.Max(maxClockMhz, (int)Math.Round(mhz));
                }
            }
        }

        var (totalMb, _) = ReadMemory();

        return new MonitorInfo
        {
            CpuName = cpuName,
            CpuCores = cores,
            CpuThreads = threads,
            CpuMaxClockMhz = maxClockMhz,
            RamTotalMb = totalMb,
            MemoryText = totalMb > 0 ? $"{totalMb / 1024.0:0.#} GB" : ""
        };
    }

    /// <summary>
    /// Load since the previous call, from the first <c>cpu</c> line of <c>/proc/stat</c>. The counters are cumulative
    /// across all cores, so the figure is the whole package rather than one core.
    /// </summary>
    private double ReadCpuUsage()
    {
        var stat = _fs.ReadAllText(LinuxPaths.Combine(_procRoot, "stat"));
        if (string.IsNullOrEmpty(stat)) return 0;

        foreach (var raw in stat.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("cpu ", StringComparison.Ordinal)) continue;

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5) return 0;

            long total = 0;
            long idle = 0;
            for (int i = 1; i < fields.Length && i <= 8; i++)
            {
                if (!long.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;
                total += value;
                // idle and iowait are both "not doing work"; counting only idle would report a busy machine while it
                // waits on a disk.
                if (i == 4 || i == 5) idle += value;
            }

            long deltaTotal = total - _lastTotal;
            long deltaIdle = idle - _lastIdle;
            _lastTotal = total;
            _lastIdle = idle;

            if (deltaTotal <= 0) return 0;
            double usage = (1.0 - deltaIdle / (double)deltaTotal) * 100.0;
            return Math.Clamp(usage, 0, 100);
        }

        return 0;
    }

    /// <summary>The current clock of the first processor, from cpufreq when the kernel exposes it.</summary>
    private double ReadCpuFrequencyMhz()
    {
        var khz = _fs.ReadAllText("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq");
        if (!string.IsNullOrEmpty(khz) &&
            double.TryParse(khz.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            return value / 1000.0;
        }

        var cpuInfo = _fs.ReadAllText(LinuxPaths.Combine(_procRoot, "cpuinfo"));
        if (string.IsNullOrEmpty(cpuInfo)) return 0;

        foreach (var raw in cpuInfo.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("cpu MHz", StringComparison.Ordinal)) continue;
            return double.TryParse(AfterColon(line), NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
                ? mhz
                : 0;
        }

        return 0;
    }

    /// <summary>
    /// Package watts from the kernel's running-energy counter. The node is <c>intel-rapl:0</c> on Intel and
    /// <c>amd-rapl:0</c> on recent AMD parts; a machine with neither reports 0, which the page shows as no reading.
    /// </summary>
    private double ReadCpuPackageWatts()
    {
        foreach (var name in new[] { "intel-rapl:0", "amd-rapl:0" })
        {
            var path = LinuxPaths.Combine(_raplRoot, name + "/energy_uj");
            var text = _fs.ReadAllText(path);
            if (string.IsNullOrEmpty(text)) continue;
            if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var energy)) continue;

            long now = Environment.TickCount64;
            long previousEnergy = _lastEnergyMicroJoules;
            long previousTicks = _lastEnergyTicks;
            _lastEnergyMicroJoules = energy;
            _lastEnergyTicks = now;

            if (previousEnergy <= 0 || previousTicks <= 0 || now <= previousTicks) return 0;
            if (energy < previousEnergy) return 0; // the counter wrapped or the domain was reset

            double seconds = (now - previousTicks) / 1000.0;
            if (seconds <= 0) return 0;

            double watts = (energy - previousEnergy) / 1_000_000.0 / seconds;
            return watts is > 0 and < 500 ? watts : 0;
        }

        return 0;
    }

    /// <summary>Total and used percentage from <c>/proc/meminfo</c>, using MemAvailable rather than MemFree.</summary>
    private (double TotalMb, double UsedPercent) ReadMemory()
    {
        var memInfo = _fs.ReadAllText(LinuxPaths.Combine(_procRoot, "meminfo"));
        if (string.IsNullOrEmpty(memInfo)) return (0, 0);

        double totalKb = 0;
        double availableKb = 0;

        foreach (var raw in memInfo.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) totalKb = ParseKb(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) availableKb = ParseKb(line);
        }

        if (totalKb <= 0) return (0, 0);

        // MemAvailable is the kernel's own estimate of what a new workload could claim, which is what "used" means to
        // a user looking at Task Manager. MemFree alone would report almost all of the page cache as used.
        double usedPercent = availableKb > 0 ? (1.0 - availableKb / totalKb) * 100.0 : 0;
        return (totalKb / 1024.0, Math.Clamp(usedPercent, 0, 100));
    }

    private static double ParseKb(string line)
    {
        var value = AfterColon(line);
        var space = value.IndexOf(' ');
        if (space > 0) value = value[..space];
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kb) ? kb : 0;
    }

    private static string AfterColon(string line)
    {
        int colon = line.IndexOf(':');
        return colon < 0 ? "" : line[(colon + 1)..].Trim();
    }

    private static int ParseInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
