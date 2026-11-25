using System.IO;
using System.Runtime.InteropServices;
using NitroLite.Core.Hardware;

namespace NitroLite.Core;

public class HardwareMonitorService : IDisposable
{
    private const string NvmlDll = "nvml.dll";

    static HardwareMonitorService()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(HardwareMonitorService).Assembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName.Equals("nvml.dll", StringComparison.OrdinalIgnoreCase) || libraryName.Equals("nvml", StringComparison.OrdinalIgnoreCase))
                {
                    // Enforce secure DLL resolution: strictly trusted system directories, never AppDir or CWD
                    string sys32Path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
                    if (File.Exists(sys32Path))
                    {
                        return NativeLibrary.Load(sys32Path);
                    }

                    string nvsmiPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
                    if (File.Exists(nvsmiPath))
                    {
                        return NativeLibrary.Load(nvsmiPath);
                    }

                    return IntPtr.Zero;
                }
                return IntPtr.Zero;
            });
        }
        catch { }
    }

    [DllImport(NvmlDll, EntryPoint = "nvmlInit_v2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlInit();

    [DllImport(NvmlDll, EntryPoint = "nvmlShutdown")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlShutdown();

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPowerUsage")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlDeviceGetPowerUsage(IntPtr device, out uint powerMilliwatts);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetClockInfo")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlDeviceGetClockInfo(IntPtr device, uint clockType, out uint clockMHz);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetTemperature")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);

    // Native Windows APIs for RAM & CPU fallbacks
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    /// <summary>
    /// What <c>CallNtPowerInformation</c> writes for one logical processor when asked for
    /// <c>ProcessorInformation</c>. The layout is fixed by <c>powrprof.h</c> and has not changed since Windows 2000.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    /// <summary>The <c>POWER_INFORMATION_LEVEL</c> that answers the per-core clocks.</summary>
    private const int ProcessorInformation = 11;

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint CallNtPowerInformation(
        int informationLevel, IntPtr inputBuffer, uint inputBufferLength, IntPtr outputBuffer, uint outputBufferLength);

    private bool _nvmlInitialized = false;
    private bool _ownsNvml;
    private readonly bool _allowNvml;
    private IntPtr _gpuDevice = IntPtr.Zero;
    // A monotonic clock, not DateTime.UtcNow: an NTP correction or a manual clock change would otherwise make the
    // backoff fire early (a burst of NVML calls) or stall for hours.
    private readonly System.Diagnostics.Stopwatch _nvmlClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// The PDH thermal zone is opened, collected and closed per probe, which costs a few milliseconds; a probe per
    /// telemetry tick would be wasteful for a value that moves by a degree or two a second.
    /// </summary>
    private static readonly TimeSpan ThermalProbeInterval = TimeSpan.FromSeconds(5);

    private TimeSpan _lastThermalProbeAt = TimeSpan.MinValue;
    private double? _lastThermalCelsius;

    /// <summary>Test seam: replaces the PDH thermal-zone read. Null in production.</summary>
    internal Func<double?>? ThermalZoneProvider { get; set; }

    /// <summary>Test seam: replaces the PDH GPU-engine read. Null in production.</summary>
    internal Func<GpuEngineReading?>? GpuEngineProvider { get; set; }

    /// <summary>
    /// The persistent <c>GPU Engine</c> query. Created on the first sample, not in the constructor: the window must
    /// open before PDH has enumerated a few hundred engine instances.
    /// </summary>
    private WindowsTelemetryFallbacks.GpuEngineSampler? _gpuEngineSampler;
    private volatile bool _disposed;
    private TimeSpan _lastNvmlRetryAt = TimeSpan.MinValue;
    private int _nvmlFailureCount;

    /// <summary>First retry delay; doubles per consecutive failure up to <see cref="NvmlMaxRetryDelaySeconds"/>.</summary>
    private const double NvmlBaseRetryDelaySeconds = 4.0;

    /// <summary>Ceiling for the retry delay, so a machine with no working NVML is not probed every few seconds.</summary>
    private const double NvmlMaxRetryDelaySeconds = 60.0;

    private double NvmlRetryDelaySeconds =>
        Math.Min(NvmlBaseRetryDelaySeconds * Math.Pow(2, _nvmlFailureCount), NvmlMaxRetryDelaySeconds);

    /// <summary>
    /// True for the NVML codes that mean "the device is not there right now" rather than "the query was wrong".
    /// </summary>
    /// <remarks>
    /// <c>NVML_ERROR_NOT_FOUND</c> (6) and <c>NVML_ERROR_GPU_IS_LOST</c> (15) are what a dGPU in D3cold answers, and
    /// <c>NVML_ERROR_UNINITIALIZED</c> (3) is what it answers after a driver reset. Every one of them is a reason to
    /// stop asking for a while: each query wakes the card, and a card that is awake is a card that is not saving
    /// battery.
    /// </remarks>
    private static bool IsDeviceUnavailable(int code) => code is 3 or 6 or 15;

    private long _prevIdleTime = 0;
    private long _prevKernelTime = 0;
    private long _prevUserTime = 0;
    private double _lastCpuUsage = 0.0;

    public HardwareMonitorService(bool initNvml = true)
    {
        _allowNvml = initNvml;
        if (initNvml)
        {
            TryInitNvml();
        }
    }

    private void TryInitNvml()
    {
        try
        {
            // A failed attempt used to leave _ownsNvml set, and the guard below then skipped NvmlInit() forever.
            // After a driver reset (hibernate, driver update, TDR) NVML answers NVML_ERROR_UNINITIALIZED and nothing
            // ever called NvmlInit again, so GPU telemetry stayed dead until the app was restarted.
            if (!_ownsNvml)
            {
                if (NvmlInit() != 0)
                {
                    _nvmlInitialized = false;
                    _gpuDevice = IntPtr.Zero;
                    return;
                }
                _ownsNvml = true;
            }

            if (NvmlDeviceGetHandleByIndex(0, out _gpuDevice) == 0)
            {
                _nvmlInitialized = true;
                _nvmlFailureCount = 0;
                return;
            }

            // The library is up but the device is not there (driver mid-reset, dGPU in D3cold). Close the session
            // so the next attempt starts from a clean NvmlInit instead of reusing a half-open one.
            ResetNvmlSession();
        }
        catch
        {
            ResetNvmlSession();
        }
    }

    /// <summary>Drops the NVML session so the next attempt calls NvmlInit again instead of reusing a dead one.</summary>
    private void ResetNvmlSession()
    {
        _nvmlInitialized = false;
        _gpuDevice = IntPtr.Zero;
        if (!_ownsNvml) return;
        _ownsNvml = false;
        // The session is being torn down either way; a shutdown that fails has nothing left to clean up.
        try { _ = NvmlShutdown(); } catch { }
    }

    public double GetNativeCpuUsage()
    {
        try
        {
            if (GetSystemTimes(out long idleTime, out long kernelTime, out long userTime))
            {
                if (_prevKernelTime != 0 || _prevUserTime != 0)
                {
                    long usrDiff = userTime - _prevUserTime;
                    long kerDiff = kernelTime - _prevKernelTime;
                    long idlDiff = idleTime - _prevIdleTime;
                    long sysDiff = usrDiff + kerDiff;

                    if (sysDiff > 0)
                    {
                        double usage = (1.0 - (double)idlDiff / sysDiff) * 100.0;
                        _lastCpuUsage = Math.Round(Math.Clamp(usage, 0.0, 100.0), 1);
                    }
                }

                _prevIdleTime = idleTime;
                _prevKernelTime = kernelTime;
                _prevUserTime = userTime;
            }
        }
        catch { }

        return _lastCpuUsage;
    }

    /// <summary>
    /// The average current clock across the logical processors, in MHz, or 0 when Windows will not answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the Acer service was the only source of a clock reading, and on a machine where that
    /// service is not installed the monitoring page showed <c>-- GHz</c> forever. The Acer Nitro AN16-61 report that
    /// prompted this is exactly that machine: fans and modes work over direct WMI, and the clock was blank.
    /// </para>
    /// <para>
    /// <c>CallNtPowerInformation(ProcessorInformation)</c> is the documented way to ask for the running clock without
    /// a driver and without the Acer stack. It writes one <c>PROCESSOR_POWER_INFORMATION</c> per logical processor and
    /// returns 0 on success; anything else is treated as "no answer" rather than as a clock of zero.
    /// </para>
    /// </remarks>
    public static double GetNativeCpuFrequencyMhz()
    {
        int count = Environment.ProcessorCount;
        if (count <= 0) return 0;

        int stride = Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(stride * count);
            uint status = CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buffer, (uint)(stride * count));
            if (status != 0) return 0;

            // Sum only the cores that answered a non-zero clock. A parked core reports 0, and averaging the parked
            // ones in would drag the number down to whatever fraction of the die happened to be awake.
            double total = 0;
            int answered = 0;
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(IntPtr.Add(buffer, i * stride));
                if (info.CurrentMhz == 0) continue;
                total += info.CurrentMhz;
                answered++;
            }

            return answered == 0 ? 0 : Math.Round(total / answered, 0);
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    public void ApplyNativeRamFallback(MonitorData data)    {
        try
        {
            var mem = new MEMORYSTATUSEX();
            mem.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (GlobalMemoryStatusEx(ref mem))
            {
                double totalMB = Math.Round(mem.ullTotalPhys / (1024.0 * 1024.0), 0);
                double availMB = Math.Round(mem.ullAvailPhys / (1024.0 * 1024.0), 0);
                double usedMB = Math.Max(0, totalMB - availMB);

                if (data.RamTotalMB <= 0)
                {
                    data.RamTotalMB = totalMB;
                }
                if (data.RamUsage <= 0 && totalMB > 0)
                {
                    data.RamUsage = Math.Round((usedMB / totalMB) * 100.0, 1);
                }
            }
        }
        catch { }
    }

    /// <param name="allowNvml">
    /// False when the dGPU may be powered down and nothing has told us otherwise. NVML queries wake a sleeping
    /// dGPU and keep it out of D3cold, which costs battery; with AASSvc gone there is no GPU1_TEMPERATURE to say
    /// whether it is asleep, so the safe answer is not to ask. The caller passes true only when it knows the
    /// machine is on the adapter, where waking the dGPU costs nothing.
    /// </param>
    public MonitorData CreateFallbackMonitorData(bool allowNvml = false)
    {
        var data = new MonitorData
        {
            CpuCores = Environment.ProcessorCount,
            CpuUsage = GetNativeCpuUsage(),
            Timestamp = DateTime.Now
        };

        ApplyNativeRamFallback(data);

        // A dGPU the engine counters show running a game is awake whatever the power source says, and asking NVML
        // about it costs nothing it is not already spending.
        var engines = SampleGpuEngines();
        EnrichMonitorData(data, GpuAsleep(!allowNvml, engines), engines);
        return data;
    }

    /// <summary>
    /// Whether the dGPU should be treated as asleep (and NVML left alone) for this sample.
    /// <para>
    /// The caller's own guess — a GPU temperature of 0, or "on battery and nobody said otherwise" — is overruled only
    /// by positive evidence: the engine counters showing the discrete adapter busy. On the AN16-61 without AASSvc the
    /// GPU temperature comes from an unverified firmware sensor id, and when that reads 0 the old rule kept NVML shut
    /// while a game ran, so usage, temperature, VRAM and watts all stayed blank. The reverse never happens: no
    /// reading, or an idle dGPU, leaves the guess standing, so a sleeping dGPU on battery stays asleep.
    /// </para>
    /// </summary>
    internal static bool GpuAsleep(bool callerSaysAsleep, GpuEngineReading? engines) =>
        callerSaysAsleep && engines?.DiscreteBusy != true;

    /// <summary>
    /// One collect of the <c>GPU Engine</c> counters, or null when there is no reading. Call once per telemetry tick:
    /// the counter is a rate, and a second collect straight after the first measures a few microseconds.
    /// </summary>
    internal GpuEngineReading? SampleGpuEngines()
    {
        if (GpuEngineProvider != null) return GpuEngineProvider();
        if (_disposed) return null;

        var sampler = LazyInitializer.EnsureInitialized(
            ref _gpuEngineSampler, () => new WindowsTelemetryFallbacks.GpuEngineSampler());
        return sampler.Sample();
    }

    /// <param name="gpuAsleepPerService">
    /// True when AASSvc reported the dGPU as powered down (GPU1_TEMPERATURE 0 = RTD3/D3cold on Optimus).
    /// NVML queries (power, memory, clocks) can wake a sleeping dGPU and keep it out of D3cold, costing
    /// battery; in that state NVML is not touched at all and the GPU values stay at 0. The GPU usage can still
    /// come from the engine counters, which do not wake the card.
    /// </param>
    public void EnrichMonitorData(MonitorData data, bool gpuAsleepPerService = false) =>
        EnrichMonitorData(data, gpuAsleepPerService, SampleGpuEngines());

    /// <param name="data">The sample to fill in.</param>
    /// <param name="gpuAsleepPerService">See the public overload. Taken as given: this overload does not overrule it.</param>
    /// <param name="engines">This tick's engine-counter reading, already sampled by the caller, or null.</param>
    internal void EnrichMonitorData(MonitorData data, bool gpuAsleepPerService, GpuEngineReading? engines)
    {
        bool mayQueryNvml = !gpuAsleepPerService;
        bool gpuTemperatureFromNvml = false;

        // A usage the service already reported (GPU1_USAGE) is the primary source and is kept.
        string gpuUsageSource = data.GpuUsage > 0 ? "firmware" : "unavailable";

        // 1. Retry NVML initialization when dGPU wakes from RTD3 deep sleep
        if (mayQueryNvml && _allowNvml && (!_nvmlInitialized || _gpuDevice == IntPtr.Zero))
        {
            if (_lastNvmlRetryAt == TimeSpan.MinValue
                || (_nvmlClock.Elapsed - _lastNvmlRetryAt).TotalSeconds >= NvmlRetryDelaySeconds)
            {
                _lastNvmlRetryAt = _nvmlClock.Elapsed;
                TryInitNvml();
                // Back off on failure: a machine whose driver never answers must not be probed every four seconds.
                if (!_nvmlInitialized) _nvmlFailureCount = Math.Min(_nvmlFailureCount + 1, 5);
            }
        }

        // 2. GPU Wattage, VRAM, Frequency, and Temperature via NVML
        if (mayQueryNvml && _nvmlInitialized && _gpuDevice != IntPtr.Zero)
        {
            try
            {
                int power = NvmlDeviceGetPowerUsage(_gpuDevice, out uint milliwatts);
                if (power == 0)
                {
                    data.GpuPowerWatts = Math.Round(milliwatts / 1000.0, 1);
                    data.GpuMetricsAvailable = true;
                }
                else
                {
                    // The query failed: the value is unknown, not zero.
                    data.GpuPowerWatts = 0.0;
                    data.GpuMetricsAvailable = false;
                }

                // The card answered "I am not here". Every further query would wake it for an answer that is not
                // there, so the session is dropped and the retry backoff applies: the next attempt waits instead of
                // asking again on the next tick, which is what kept a sleeping dGPU out of D3cold for the whole
                // session.
                if (IsDeviceUnavailable(power))
                {
                    ResetNvmlSession();
                    _nvmlFailureCount = Math.Min(_nvmlFailureCount + 1, 5);
                    _lastNvmlRetryAt = _nvmlClock.Elapsed;
                }
                else
                {
                    if (NvmlDeviceGetMemoryInfo(_gpuDevice, out NvmlMemory mem) == 0)
                    {
                        data.VramUsedMB = Math.Round(mem.Used / (1024.0 * 1024.0), 0);
                        data.VramTotalMB = Math.Round(mem.Total / (1024.0 * 1024.0), 0);
                    }

                    if (data.GpuUsage <= 0 && NvmlDeviceGetUtilizationRates(_gpuDevice, out var utilization) == 0
                        && utilization.Gpu <= 100)
                    {
                        data.GpuUsage = utilization.Gpu;
                        if (utilization.Gpu > 0) gpuUsageSource = "NVML";
                    }

                    // Query GPU Graphics Clock (Type 0 = Graphics)
                    if (NvmlDeviceGetClockInfo(_gpuDevice, 0, out uint clockMhz) == 0)
                    {
                        data.GpuFrequencyMHz = clockMhz;
                    }

                    // If GPU temperature was unpopulated (e.g. AASSvc disconnected), query NVML
                    if (data.GpuTemperature <= 0)
                    {
                        if (NvmlDeviceGetTemperature(_gpuDevice, 0, out uint tempC) == 0)
                        {
                            data.GpuTemperature = (int)tempC;
                            gpuTemperatureFromNvml = tempC > 0;
                        }
                    }
                }
            }
            catch
            {
                // NVML communication failed (e.g. RTD3 transition). Drop the session so the retry path calls
                // NvmlInit again rather than reusing a handle the driver has already forgotten.
                ResetNvmlSession();
                _nvmlFailureCount = Math.Min(_nvmlFailureCount + 1, 5);
                _lastNvmlRetryAt = _nvmlClock.Elapsed;
                data.GpuMetricsAvailable = false;
            }
        }
        else
        {
            data.GpuPowerWatts = 0.0;
            data.GpuMetricsAvailable = false;
        }

        // 2b. GPU usage from the engine counters when neither the service nor NVML gave one. This is the reading
        // Task Manager shows, and it is available even while NVML is held back to let the dGPU sleep.
        if (data.GpuUsage <= 0 && engines?.Usage is { } engineUsage && engineUsage > 0)
        {
            data.GpuUsage = Math.Round(Math.Clamp(engineUsage, 0.0, 100.0), 1);
            gpuUsageSource = "PDH GPU Engine";
        }

        data.Provenance["gpu-usage"] = gpuUsageSource;

        // 3. Fallbacks for missing RAM and CPU metrics
        if (data.RamTotalMB <= 0 || data.RamUsage <= 0)
        {
            ApplyNativeRamFallback(data);
        }

        if (data.CpuUsage <= 0)
        {
            data.CpuUsage = GetNativeCpuUsage();
        }

        if (data.CpuCores <= 0)
        {
            data.CpuCores = Environment.ProcessorCount;
        }

        // The clock used to come only from the Acer service, so a machine without that service showed "-- GHz" for
        // as long as it ran. Ask Windows directly before giving up on the reading; the power estimate below uses it.
        if (data.CpuFrequencyMHz <= 0)
        {
            data.CpuFrequencyMHz = GetNativeCpuFrequencyMhz();
            data.Provenance["cpu-clock"] = data.CpuFrequencyMHz > 0 ? "CallNtPowerInformation" : "unavailable";
        }
        else
        {
            data.Provenance["cpu-clock"] = "firmware";
        }

        // 3b. CPU temperature through Windows when the firmware had nothing to say.
        //
        // This is the AN16-61 case and every unprofiled model like it: the sensor ids come from a firmware-wide enum,
        // and a machine whose layout has not been read publishes 0 (or a sentinel) for the CPU die. The ACPI thermal
        // zone is a real measurement from the platform driver, it needs no kernel driver of ours, and it is what makes
        // a fan curve possible on a laptop NitroLite has never seen. The window check is the same one the firmware
        // readings go through — a fallback is not allowed to be less plausible than the primary source.
        data.Provenance["cpu-temp"] = SensorPlausibility.IsPlausibleTemperature(data.CpuTemperature)
            ? "firmware"
            : "unavailable";

        if (!SensorPlausibility.IsPlausibleTemperature(data.CpuTemperature))
        {
            var thermal = ThermalZoneCelsius();
            if (thermal is { } celsius)
            {
                data.CpuTemperature = (int)Math.Round(celsius, MidpointRounding.AwayFromZero);
                data.Provenance["cpu-temp"] = "PDH thermal zone";

                // The page shows the number, but an ACPI zone is often a static ~28 degC board value and says
                // nothing about the die. Marking it unconfirmed keeps the fan curve from following it and lets the
                // lost-sensor hand-back to firmware work.
                data.IsTemperatureMeasured = false;
            }
        }

        // A temperature NVML filled in is NVML's, not the firmware's; the report must not credit the sensor that read 0.
        data.Provenance["gpu-temp"] = !SensorPlausibility.IsPlausibleTemperature(data.GpuTemperature)
            ? "unavailable"
            : gpuTemperatureFromNvml
                ? "NVML"
                : (data.GpuMetricsAvailable ? "firmware + NVML" : "firmware");
        data.Provenance["vram"] = data.VramTotalMB > 0 ? "NVML" : "unavailable";
        data.Provenance["gpu-power"] = data.GpuMetricsAvailable ? "NVML" : "unavailable";
        data.Provenance["cpu-power"] = "estimate";

        // 4. CPU Package Power (Watts) — an estimate, not a measurement.
        // AASSvc does not report CPU power and reading the MSR would need a kernel driver, which this project does
        // not ship (anti-cheat). The curve below is therefore a model, and the UI labels it "(tahmini)". Its
        // reference clock used to be hardcoded to 3800 MHz — one Ryzen 7 7840HS and no other Nitro — so every other
        // model got a number that was wrong by tens of percent. The caller sets CpuReferenceFrequencyMHz from
        // Win32_Processor.MaxClockSpeed; the default only applies when Windows did not report it.
        if (data.CpuUsage > 0)
        {
            double reference = CpuReferenceFrequencyMHz > 0 ? CpuReferenceFrequencyMHz : DefaultCpuReferenceFrequencyMHz;
            double freqRatio = data.CpuFrequencyMHz > 0 ? (data.CpuFrequencyMHz / reference) : 1.0;
            double loadRatio = Math.Clamp(data.CpuUsage / 100.0, 0.0, 1.0);

            // Dynamic non-linear power curve
            double dynamicWatts = 8.0 + (loadRatio * 46.0 * Math.Pow(freqRatio, 1.3));
            // Whole watts: a model that is off by a few watts must not claim a tenth of a watt of precision.
            data.CpuPowerWatts = Math.Round(Math.Clamp(dynamicWatts, 7.0, 135.0), 0);
        }
        else
        {
            data.CpuPowerWatts = 8.5;
        }
    }

    /// <summary>
    /// The ACPI thermal zone in Celsius, cached for <see cref="ThermalProbeInterval"/>. Null when this machine has no
    /// zone the counter can read, which is the honest answer on a desktop board with no thermal zone at all.
    /// </summary>
    private double? ThermalZoneCelsius()
    {
        if (ThermalZoneProvider != null) return ThermalZoneProvider();

        if (_lastThermalProbeAt != TimeSpan.MinValue
            && (_nvmlClock.Elapsed - _lastThermalProbeAt) < ThermalProbeInterval)
        {
            return _lastThermalCelsius;
        }

        _lastThermalProbeAt = _nvmlClock.Elapsed;
        _lastThermalCelsius = WindowsTelemetryFallbacks.CpuTemperatureCelsius();
        return _lastThermalCelsius;
    }

    /// <summary>Reference clock for the CPU power estimate when Windows did not report the processor's rating.</summary>
    public const double DefaultCpuReferenceFrequencyMHz = 3800.0;

    /// <summary>
    /// The processor's rated maximum clock, used as the reference point of the CPU power estimate. Set from
    /// <see cref="HardwareInfo.CpuMaxClockMhz"/> once the hardware names are read; 0 keeps the default.
    /// </summary>
    public double CpuReferenceFrequencyMHz { get; set; } = DefaultCpuReferenceFrequencyMHz;

    public void Dispose()
    {
        // No finalizer here, but a derived type that adds one would otherwise have to re-implement
        // IDisposable just to suppress it.
        GC.SuppressFinalize(this);
        _disposed = true;

        // The PDH query is a handle in this process; closing it is all the cleanup it needs.
        Interlocked.Exchange(ref _gpuEngineSampler, null)?.Dispose();

        if (_ownsNvml)
        {
            _ownsNvml = false;
            try
            {
                _ = NvmlShutdown();
            }
            catch { }
            _nvmlInitialized = false;
            _gpuDevice = IntPtr.Zero;
        }
    }
}
