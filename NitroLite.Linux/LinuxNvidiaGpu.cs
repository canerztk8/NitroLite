using System.Globalization;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>One NVIDIA reading.</summary>
public sealed record NvidiaSample
{
    public string Name { get; init; } = "";
    public int TemperatureC { get; init; }
    public double UtilizationPercent { get; init; }
    public double MemoryUsedMb { get; init; }
    public double MemoryTotalMb { get; init; }
    public double ClockMhz { get; init; }
    public double PowerWatts { get; init; }
}

/// <summary>
/// The discrete NVIDIA GPU, read through <c>nvidia-smi</c>, which ships with the proprietary driver.
/// </summary>
/// <remarks>
/// <para>
/// The NVIDIA driver publishes no <c>hwmon</c> device, so without this the monitoring page has no discrete-GPU row on
/// most Nitro laptops: the only GPU <c>hwmon</c> shows is the integrated one.
/// </para>
/// <para>
/// Two rules keep it from costing battery. A GPU the kernel has put to sleep (<c>runtime_status</c> is
/// <c>suspended</c>) is not asked at all, because <c>nvidia-smi</c> wakes it and an idle laptop would then never let it
/// sleep again. And the query runs on a worker thread at most every <see cref="MinInterval"/>, so the one-second
/// telemetry tick reads the last answer instead of waiting on a process.
/// </para>
/// </remarks>
public sealed class LinuxNvidiaGpu
{
    public const string PciRoot = "/sys/bus/pci/devices";
    public const string NvidiaVendor = "0x10de";

    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private static readonly string[] QueryArguments =
    {
        "--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total,clocks.gr,power.draw",
        "--format=csv,noheader,nounits"
    };

    private readonly ISysfsFileSystem _fs;
    private readonly ICommandRunner _runner;
    private readonly string? _smi;
    private readonly string? _device;
    private readonly object _gate = new();

    private NvidiaSample? _last;
    private long _lastStartedTicks;
    private bool _running;

    public LinuxNvidiaGpu(ISysfsFileSystem fs, ICommandRunner? runner = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _runner = runner ?? ProcessCommandRunner.Instance;
        _device = FindDevice(fs);
        _smi = _device == null ? null : _runner.Find("nvidia-smi");
    }

    /// <summary>True when the machine has an NVIDIA GPU and the driver's tool is installed.</summary>
    public bool IsAvailable => _device != null && _smi != null;

    /// <summary>The GPU's PCI directory, or null when there is no NVIDIA display controller.</summary>
    public string? Device => _device;

    /// <summary>True when the kernel has the GPU in runtime suspend.</summary>
    public bool IsSuspended =>
        _device != null &&
        string.Equals(_fs.ReadAllText(LinuxPaths.Combine(_device, "power/runtime_status"))?.Trim(), "suspended",
            StringComparison.Ordinal);

    /// <summary>
    /// The newest reading, or null when there is none yet or the GPU is asleep. Starts a refresh in the background
    /// when the last one is older than <see cref="MinInterval"/>; never blocks.
    /// </summary>
    public NvidiaSample? Latest()
    {
        if (!IsAvailable) return null;

        if (IsSuspended)
        {
            lock (_gate) return _last == null ? null : new NvidiaSample { Name = _last.Name };
        }

        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (!_running && now - _lastStartedTicks >= MinInterval.TotalMilliseconds)
            {
                _running = true;
                _lastStartedTicks = now;
                _ = Task.Run(Refresh);
            }

            return _last;
        }
    }

    /// <summary>Runs the query now, on the calling thread. Exposed for tests and for the one-shot hardware report.</summary>
    public NvidiaSample? QueryNow()
    {
        if (!IsAvailable) return null;
        var result = _runner.Run(_smi!, QueryArguments, TimeSpan.FromSeconds(4));
        return result.Ok ? Parse(result.Output) : null;
    }

    private void Refresh()
    {
        try
        {
            var sample = QueryNow();
            lock (_gate)
            {
                if (sample != null) _last = sample;
            }
        }
        finally
        {
            lock (_gate) _running = false;
        }
    }

    /// <summary>
    /// One CSV line of the query to a sample. A field the driver cannot answer reads <c>[N/A]</c> and becomes 0, which
    /// the page shows as "no reading". Null when the line is not the shape the query asked for.
    /// </summary>
    public static NvidiaSample? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var line = output.Split('\n')[0].Trim();
        var fields = line.Split(',');
        if (fields.Length < 7) return null;

        // The name may itself contain a comma, so the six numbers are taken from the end.
        var offset = fields.Length - 7;
        var name = string.Join(",", fields.Take(offset + 1)).Trim();

        return new NvidiaSample
        {
            Name = name,
            TemperatureC = (int)Math.Round(Number(fields[offset + 1])),
            UtilizationPercent = Math.Clamp(Number(fields[offset + 2]), 0, 100),
            MemoryUsedMb = Number(fields[offset + 3]),
            MemoryTotalMb = Number(fields[offset + 4]),
            ClockMhz = Number(fields[offset + 5]),
            PowerWatts = Number(fields[offset + 6])
        };
    }

    private static double Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : 0;

    /// <summary>The first PCI display controller (class <c>0x03xxxx</c>) from NVIDIA, or null.</summary>
    public static string? FindDevice(ISysfsFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);

        foreach (var directory in fs.EnumerateDirectories(PciRoot))
        {
            var vendor = fs.ReadAllText(LinuxPaths.Combine(directory, "vendor"))?.Trim();
            if (!string.Equals(vendor, NvidiaVendor, StringComparison.OrdinalIgnoreCase)) continue;

            var pciClass = fs.ReadAllText(LinuxPaths.Combine(directory, "class"))?.Trim();
            if (pciClass != null && pciClass.StartsWith("0x03", StringComparison.Ordinal)) return directory;
        }

        return null;
    }
}
