using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The Windows half of the monitoring page: model strings from WMI, load and clocks from the performance counters,
/// package watts and VRAM from the same estimator the WPF front end used.
/// </summary>
/// <remarks>
/// <para>
/// The Acer backend answers temperatures and fan speeds. Everything else on the page — which processor this is, how
/// loaded it is, how many watts it is pulling, how much VRAM the game has taken — comes from here, so a machine whose
/// service is missing still shows a useful page.
/// </para>
/// <para>
/// The service is created lazily and only once: constructing it initialises NVML, which is a driver call, and the
/// window must open before that finishes.
/// </para>
/// </remarks>
public sealed class WindowsHardwareMonitor : IHardwareMonitor, IDisposable
{
    private readonly Lazy<HardwareMonitorService> _monitor;
    private readonly Lazy<MonitorInfo> _info;
    private bool _disposed;

    public WindowsHardwareMonitor()
    {
        _monitor = new Lazy<HardwareMonitorService>(() =>
        {
            try
            {
                return new HardwareMonitorService();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Hardware monitor could not be set up", ex.Message);
                return new HardwareMonitorService(initNvml: false);
            }
        });

        _info = new Lazy<MonitorInfo>(ReadInfo);
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                return _monitor.Value != null;
            }
            catch
            {
                return false;
            }
        }
    }

    public MonitorInfo Info
    {
        get
        {
            try
            {
                return _info.Value;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Hardware information could not be read", ex.Message);
                return new MonitorInfo();
            }
        }
    }

    /// <summary>
    /// Reads the model strings once. A read that produced nothing is not cached for the session: a WMI service that
    /// was still starting would otherwise leave the page without labels until the next restart.
    /// </summary>
    private static MonitorInfo ReadInfo()
    {
        try
        {
            var info = HardwareInfoService.Get();
            return new MonitorInfo
            {
                CpuName = info.CpuName,
                GpuName = info.DiscreteGpuName,
                CpuCores = info.CpuCores,
                CpuThreads = info.CpuThreads,
                CpuMaxClockMhz = info.CpuMaxClockMhz,
                RamTotalMb = info.MemoryTotalGb * 1024.0,
                MemoryText = info.FormatMemoryText()
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn("Hardware information could not be read", ex.Message);
            return new MonitorInfo();
        }
    }

    public void Enrich(MonitorData data)
    {
        if (data == null || _disposed) return;

        try
        {
            var monitor = _monitor.Value;
            var info = Info;

            // The power estimate is built around the processor's rated clock; feeding it the real rating is what
            // keeps the number honest on a machine that is not this one.
            if (info.CpuMaxClockMhz > 0) monitor.CpuReferenceFrequencyMHz = info.CpuMaxClockMhz;

            // A GPU temperature of 0 is taken to mean the dGPU is powered down, so NVML is left alone to let it sleep.
            // That was written for the service's GPU1_TEMPERATURE; on the direct-WMI path the number comes from an
            // unverified firmware sensor id and can read 0 while a game runs. The engine counters — which do not wake
            // the card — are the tie-breaker: a busy discrete adapter is awake, and NVML may be asked about it.
            var engines = monitor.SampleGpuEngines();
            monitor.EnrichMonitorData(
                data, HardwareMonitorService.GpuAsleep(data.GpuTemperature <= 0, engines), engines);

            if (data.CpuCores <= 0 && info.CpuThreads > 0) data.CpuCores = info.CpuThreads;
            if (data.RamTotalMB <= 0 && info.RamTotalMb > 0) data.RamTotalMB = info.RamTotalMb;
            if (data.CpuName.Length == 0) data.CpuName = info.CpuName;
            if (data.GpuName.Length == 0) data.GpuName = info.GpuName;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The telemetry could not be enriched", ex.Message);
        }
    }

    public MonitorData CreateFallback()
    {
        var data = new MonitorData();
        if (_disposed) return data;

        try
        {
            var monitor = _monitor.Value;
            var info = Info;

            // NVML is only touched when the machine is on the charger: waking the dGPU every second on battery is
            // exactly the trap the fallback exists to avoid.
            data = monitor.CreateFallbackMonitorData(allowNvml: false);

            if (data.CpuCores <= 0 && info.CpuThreads > 0) data.CpuCores = info.CpuThreads;
            if (data.RamTotalMB <= 0 && info.RamTotalMb > 0) data.RamTotalMB = info.RamTotalMb;
            data.CpuName = info.CpuName;
            data.GpuName = info.GpuName;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Backup telemetry could not be read", ex.Message);
        }

        return data;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_monitor.IsValueCreated) _monitor.Value.Dispose();
        }
        catch
        {
            // A driver handle that will not close is not a reason to fail shutdown.
        }
    }
}
