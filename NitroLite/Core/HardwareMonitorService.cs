using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

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

    private bool _nvmlInitialized = false;
    private bool _ownsNvml;
    private readonly bool _allowNvml;
    private IntPtr _gpuDevice = IntPtr.Zero;
    private DateTime _lastNvmlRetryUtc = DateTime.MinValue;

    private long _prevIdleTime = 0;
    private long _prevKernelTime = 0;
    private long _prevUserTime = 0;
    private double _lastCpuUsage = 0.0;

    public bool IsNvmlAvailable => _nvmlInitialized && _gpuDevice != IntPtr.Zero;

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
            if (_ownsNvml || NvmlInit() == 0)
            {
                _ownsNvml = true;
                if (NvmlDeviceGetHandleByIndex(0, out _gpuDevice) == 0)
                {
                    _nvmlInitialized = true;
                    return;
                }
            }
        }
        catch
        {
            _nvmlInitialized = false;
            _gpuDevice = IntPtr.Zero;
        }
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

    public void ApplyNativeRamFallback(MonitorData data)
    {
        try
        {
            var mem = new MEMORYSTATUSEX();
            mem.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
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

    public MonitorData CreateFallbackMonitorData()
    {
        var data = new MonitorData
        {
            CpuCores = Environment.ProcessorCount,
            CpuUsage = GetNativeCpuUsage(),
            Timestamp = DateTime.Now
        };

        ApplyNativeRamFallback(data);
        EnrichMonitorData(data);
        return data;
    }

    /// <param name="gpuAsleepPerService">
    /// True when AASSvc reported the dGPU as powered down (GPU1_TEMPERATURE 0 = RTD3/D3cold on Optimus).
    /// NVML queries (power, memory, clocks) can wake a sleeping dGPU and keep it out of D3cold, costing
    /// battery; in that state NVML is not touched at all and the GPU values stay at 0.
    /// </param>
    public void EnrichMonitorData(MonitorData data, bool gpuAsleepPerService = false)
    {
        bool mayQueryNvml = !gpuAsleepPerService;

        // 1. Retry NVML initialization when dGPU wakes from RTD3 deep sleep
        if (mayQueryNvml && _allowNvml && (!_nvmlInitialized || _gpuDevice == IntPtr.Zero))
        {
            if ((DateTime.UtcNow - _lastNvmlRetryUtc).TotalSeconds >= 4.0)
            {
                _lastNvmlRetryUtc = DateTime.UtcNow;
                TryInitNvml();
            }
        }

        // 2. GPU Wattage, VRAM, Frequency, and Temperature via NVML
        if (mayQueryNvml && _nvmlInitialized && _gpuDevice != IntPtr.Zero)
        {
            try
            {
                if (NvmlDeviceGetPowerUsage(_gpuDevice, out uint milliwatts) == 0)
                {
                    data.GpuPowerWatts = Math.Round(milliwatts / 1000.0, 1);
                }
                else
                {
                    // Device might be in D3cold deep sleep
                    data.GpuPowerWatts = 0.0;
                }

                if (NvmlDeviceGetMemoryInfo(_gpuDevice, out NvmlMemory mem) == 0)
                {
                    data.VramUsedMB = Math.Round(mem.Used / (1024.0 * 1024.0), 0);
                    data.VramTotalMB = Math.Round(mem.Total / (1024.0 * 1024.0), 0);
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
                    }
                }
            }
            catch
            {
                // NVML communication failed (e.g. RTD3 transition); mark for retry
                _gpuDevice = IntPtr.Zero;
                _nvmlInitialized = false;
            }
        }
        else
        {
            data.GpuPowerWatts = 0.0;
        }

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

        // 4. CPU Package Power (Watts) Calculation without artificial 65W ceiling clamp
        // Accommodates Zen 4 Phoenix base 35W TDP, boost up to 85W+ under full multi-core load
        if (data.CpuUsage > 0)
        {
            double freqRatio = data.CpuFrequencyMHz > 0 ? (data.CpuFrequencyMHz / 3800.0) : 1.0;
            double loadRatio = Math.Clamp(data.CpuUsage / 100.0, 0.0, 1.0);

            // Dynamic non-linear power curve
            double dynamicWatts = 8.0 + (loadRatio * 46.0 * Math.Pow(freqRatio, 1.3));
            // Removed 65.0W ceiling clamp so genuine boost wattage is shown; clamped to realistic socket limits
            data.CpuPowerWatts = Math.Round(Math.Clamp(dynamicWatts, 7.0, 135.0), 1);
        }
        else
        {
            data.CpuPowerWatts = 8.5;
        }
    }

    public void Dispose()
    {
        if (_ownsNvml)
        {
            _ownsNvml = false;
            try
            {
                NvmlShutdown();
            }
            catch { }
            _nvmlInitialized = false;
            _gpuDevice = IntPtr.Zero;
        }
    }
}
