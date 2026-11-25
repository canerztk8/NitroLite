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

    /// <summary>PDH_CSTATUS_NEW_DATA: also a real measurement, the first one since the instance appeared.</summary>
    private const uint PdhStatusNewData = 0x0000_0001;

    /// <summary>
    /// The counter Task Manager draws its GPU graphs from: one instance per process, per adapter, per engine. It is a
    /// rate counter, so the first collect on a query only sets the baseline and every value comes from the next one.
    /// </summary>
    private const string GpuEngineCounter = @"\GPU Engine(*)\Utilization Percentage";

    /// <summary>
    /// The engine load at which a hybrid machine's discrete adapter counts as awake. An engine that ran any work at all
    /// was powered; the margin is there so that the brief flicker of a driver housekeeping call is not read as a game.
    /// </summary>
    internal const double DiscreteBusyThresholdPercent = 5.0;

    /// <summary><c>KMTQAITYPE_ADAPTERTYPE</c>: asks dxgkrnl for the adapter's <c>D3DKMT_ADAPTERTYPE</c> flags.</summary>
    private const int KmtQueryAdapterType = 15;

    /// <summary><c>D3DKMT_ADAPTERTYPE.SoftwareDevice</c> (bit 2): the Basic Render Driver, never the dGPU.</summary>
    private const uint AdapterTypeSoftwareDevice = 1u << 2;

    /// <summary><c>D3DKMT_ADAPTERTYPE.HybridDiscrete</c> (bit 4): the discrete half of an Optimus pair.</summary>
    private const uint AdapterTypeHybridDiscrete = 1u << 4;

    /// <summary><c>D3DKMT_ADAPTERTYPE.HybridIntegrated</c> (bit 5): the integrated half of an Optimus pair.</summary>
    private const uint AdapterTypeHybridIntegrated = 1u << 5;

    /// <summary>A ceiling on the adapters read, so a driver that reports a nonsense count cannot size the buffer.</summary>
    private const uint MaxEnumeratedAdapters = 64;

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

    /// <summary>
    /// Reads one <c>GPU Engine</c> instance name, for example
    /// <c>pid_1234_luid_0x00000000_0x0000C3A4_phys_0_eng_0_engtype_3D</c>. False for anything that does not have the
    /// adapter LUID, the physical index and the engine type in that order; such an instance is skipped rather than
    /// guessed at.
    /// </summary>
    internal static bool TryParseGpuEngineInstance(string? name, out GpuEngineInstance instance)
    {
        instance = default;
        if (string.IsNullOrEmpty(name)) return false;

        const string LuidTag = "luid_0x";
        const string LowTag = "_0x";
        const string PhysTag = "_phys_";
        const string TypeTag = "_engtype_";

        int luidAt = name.IndexOf(LuidTag, StringComparison.OrdinalIgnoreCase);
        if (luidAt < 0) return false;

        int highStart = luidAt + LuidTag.Length;
        int lowTagAt = name.IndexOf(LowTag, highStart, StringComparison.OrdinalIgnoreCase);
        if (lowTagAt < 0) return false;

        int lowStart = lowTagAt + LowTag.Length;
        int lowEnd = name.IndexOf('_', lowStart);
        if (lowEnd < 0) return false;

        if (!uint.TryParse(name.AsSpan(highStart, lowTagAt - highStart), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out uint high))
            return false;
        if (!uint.TryParse(name.AsSpan(lowStart, lowEnd - lowStart), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out uint low))
            return false;

        int physAt = name.IndexOf(PhysTag, lowEnd, StringComparison.OrdinalIgnoreCase);
        if (physAt < 0) return false;

        int physStart = physAt + PhysTag.Length;
        int physEnd = name.IndexOf('_', physStart);
        if (physEnd < 0) return false;
        if (!int.TryParse(name.AsSpan(physStart, physEnd - physStart), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int phys))
            return false;

        int typeAt = name.IndexOf(TypeTag, physEnd, StringComparison.OrdinalIgnoreCase);
        if (typeAt < 0) return false;

        instance = new GpuEngineInstance(ToLuid(high, low), phys, name[(typeAt + TypeTag.Length)..]);
        return true;
    }

    /// <summary>
    /// The load of every adapter in one collect, the way Task Manager computes it: each engine type's utilisation is
    /// summed across the processes using it, and an adapter is as busy as its busiest engine type. Clamped to 0–100,
    /// because the per-process values are rounded separately and their sum can step over the ceiling.
    /// </summary>
    /// <remarks>
    /// An adapter whose engines all read 0 is still in the result, at 0: "present and idle" is a different answer from
    /// "no instance at all", and the interpretation below needs to tell them apart.
    /// </remarks>
    internal static IReadOnlyDictionary<long, double> AggregateGpuEngineUsage(IEnumerable<KeyValuePair<string, double>> samples)
    {
        var perEngine = new Dictionary<(long Luid, int Phys, string EngineType), double>();
        foreach (var (name, value) in samples)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) continue;
            if (!TryParseGpuEngineInstance(name, out var instance)) continue;

            var key = (instance.Luid, instance.Phys, instance.EngineType);
            perEngine[key] = perEngine.GetValueOrDefault(key) + value;
        }

        var perAdapter = new Dictionary<long, double>();
        foreach (var (key, total) in perEngine)
        {
            double clamped = Math.Clamp(total, 0.0, 100.0);
            perAdapter[key.Luid] = perAdapter.TryGetValue(key.Luid, out var current) ? Math.Max(current, clamped) : clamped;
        }

        return perAdapter;
    }

    /// <summary>The busiest adapter's load, or null when the collect named no adapter at all.</summary>
    internal static double? BusiestAdapter(IReadOnlyDictionary<long, double> perAdapter) =>
        perAdapter.Count == 0 ? null : perAdapter.Values.Max();

    /// <summary>What one adapter is, from its <c>D3DKMT_ADAPTERTYPE</c> flags.</summary>
    internal static GpuAdapterRole RoleFromAdapterType(uint flags)
    {
        if ((flags & AdapterTypeSoftwareDevice) != 0) return GpuAdapterRole.Software;
        if ((flags & AdapterTypeHybridDiscrete) != 0) return GpuAdapterRole.Discrete;
        if ((flags & AdapterTypeHybridIntegrated) != 0) return GpuAdapterRole.Integrated;
        return GpuAdapterRole.Other;
    }

    /// <summary>
    /// Turns the per-adapter load into the GPU usage the page shows and the one fact the NVML gate needs: is the
    /// discrete GPU awake.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page's GPU is the discrete one, so on an Optimus machine the reading is that adapter's load. When the dGPU
    /// has no engine instance at all it is not rendering anything — usually because it is asleep — and the answer is
    /// null rather than the integrated GPU's load under the RTX's name.
    /// </para>
    /// <para>
    /// "Awake" is only ever claimed for an adapter known to be the dGPU, or for the only hardware adapter of a machine
    /// that is not hybrid (no D3cold to protect there). A busy integrated GPU — a video in the browser — must never
    /// count, because the caller answers "awake" by querying NVML, and NVML on a sleeping dGPU wakes it on battery.
    /// When the adapter roles could not be read the load of the busiest adapter is still shown, but nothing is
    /// claimed about the dGPU.
    /// </para>
    /// </remarks>
    internal static GpuEngineReading InterpretGpuEngineUsage(
        IReadOnlyDictionary<long, double> perAdapter, IReadOnlyDictionary<long, GpuAdapterRole>? roles)
    {
        if (perAdapter.Count == 0) return default;

        if (roles == null || roles.Count == 0) return new GpuEngineReading(BusiestAdapter(perAdapter), false);

        double? discrete = null;
        double? hardware = null;
        foreach (var (luid, load) in perAdapter)
        {
            var role = roles.TryGetValue(luid, out var known) ? known : GpuAdapterRole.Unknown;
            if (role == GpuAdapterRole.Software) continue;

            hardware = Math.Max(hardware ?? 0.0, load);
            if (role == GpuAdapterRole.Discrete) discrete = Math.Max(discrete ?? 0.0, load);
        }

        if (discrete is { } dgpu) return new GpuEngineReading(dgpu, dgpu >= DiscreteBusyThresholdPercent);

        bool hybrid = roles.Values.Any(role => role is GpuAdapterRole.Discrete or GpuAdapterRole.Integrated);
        if (hybrid) return default;

        bool singleAdapter = roles.Values.Count(role => role == GpuAdapterRole.Other) == 1;
        return new GpuEngineReading(hardware, singleAdapter && hardware >= DiscreteBusyThresholdPercent);
    }

    /// <summary>A LUID as one number: the high part in the upper 32 bits, the low part in the lower.</summary>
    private static long ToLuid(uint high, uint low) => unchecked((long)(((ulong)high << 32) | low));

    /// <summary>
    /// The adapter roles dxgkrnl reports, keyed by LUID, or null when the enumeration is not available.
    /// <para>
    /// Read-only: every adapter handle opened by the enumeration is closed again, and the only query is the adapter
    /// type, which the kernel answers from what the driver declared at start-up.
    /// </para>
    /// </summary>
    private static Dictionary<long, GpuAdapterRole>? ReadAdapterRoles()
    {
        try
        {
            var request = new D3dkmtEnumAdapters2();
            if (D3DKMTEnumAdapters2(ref request) != 0 || request.NumAdapters == 0) return null;

            uint capacity = Math.Min(request.NumAdapters, MaxEnumeratedAdapters);
            int stride = Marshal.SizeOf<D3dkmtAdapterInfo>();
            IntPtr adapters = Marshal.AllocHGlobal(stride * (int)capacity);
            IntPtr flagsBuffer = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                request.NumAdapters = capacity;
                request.Adapters = adapters;
                if (D3DKMTEnumAdapters2(ref request) != 0) return null;

                var roles = new Dictionary<long, GpuAdapterRole>();
                uint returned = Math.Min(request.NumAdapters, capacity);
                for (int index = 0; index < returned; index++)
                {
                    var info = Marshal.PtrToStructure<D3dkmtAdapterInfo>(adapters + (index * stride));
                    try
                    {
                        Marshal.WriteInt32(flagsBuffer, 0);
                        var query = new D3dkmtQueryAdapterInfo
                        {
                            Adapter = info.Adapter,
                            Type = KmtQueryAdapterType,
                            PrivateDriverData = flagsBuffer,
                            PrivateDriverDataSize = sizeof(uint)
                        };

                        roles[ToLuid(unchecked((uint)info.LuidHighPart), info.LuidLowPart)] =
                            D3DKMTQueryAdapterInfo(ref query) == 0
                                ? RoleFromAdapterType(unchecked((uint)Marshal.ReadInt32(flagsBuffer)))
                                : GpuAdapterRole.Unknown;
                    }
                    finally
                    {
                        // A handle that will not close leaks one kernel reference; there is nothing else to do with it.
                        var close = new D3dkmtCloseAdapter { Adapter = info.Adapter };
                        _ = D3DKMTCloseAdapter(ref close);
                    }
                }

                return roles.Count == 0 ? null : roles;
            }
            finally
            {
                Marshal.FreeHGlobal(flagsBuffer);
                Marshal.FreeHGlobal(adapters);
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
    }

    /// <summary>
    /// The GPU load from the <c>GPU Engine</c> counters, sampled on a query that stays open for the life of the
    /// monitor — the same numbers Task Manager shows, and a source that does not wake a sleeping dGPU: the counters
    /// are the kernel scheduler's own bookkeeping, not a question put to the driver.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The query has to persist because the counter is a rate: the first collect yields no value, and every later one
    /// is the load since the previous collect. Opening and closing it per tick, the way the thermal zone is read,
    /// would never produce a number.
    /// </para>
    /// <para>
    /// A Windows without the counter set (before 1709, or a Server SKU without the GPU provider) fails the first add.
    /// That is remembered: the sampler answers null for the rest of the session without retrying or logging.
    /// </para>
    /// </remarks>
    internal sealed class GpuEngineSampler : IDisposable
    {
        /// <summary>The adapter roles are re-read at most this often, and only when an unknown LUID appears.</summary>
        private static readonly TimeSpan RoleRefreshInterval = TimeSpan.FromSeconds(60);

        /// <summary>
        /// A ceiling on role reads per session. A LUID the enumeration never lists (a display-only adapter) would
        /// otherwise ask again every minute for as long as the app runs.
        /// </summary>
        private const int MaxRoleReads = 6;

        /// <summary>
        /// Two samples closer than this share one collect. A tick whose backend gave nothing runs the fallback and the
        /// enrichment back to back, and a rate measured over a millisecond is noise — noise that could read as a busy
        /// dGPU and open NVML on battery.
        /// </summary>
        private static readonly TimeSpan MinCollectInterval = TimeSpan.FromMilliseconds(500);

        private readonly object _gate = new();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private IntPtr _query;
        private IntPtr _counter;
        private bool _unavailable;
        private bool _disposed;
        private Dictionary<long, GpuAdapterRole>? _roles;
        private int _roleReads;
        private TimeSpan _lastRoleReadAt;
        private bool _collected;
        private TimeSpan _lastCollectAt;
        private GpuEngineReading? _lastReading;

        /// <summary>One collect, or null when there is no reading yet (first collect) or no counter set at all.</summary>
        public GpuEngineReading? Sample()
        {
            lock (_gate)
            {
                if (_disposed || _unavailable) return null;
                if (_collected && _clock.Elapsed - _lastCollectAt < MinCollectInterval) return _lastReading;

                try
                {
                    if (_query == IntPtr.Zero && !Open()) return null;

                    _collected = true;
                    _lastCollectAt = _clock.Elapsed;
                    _lastReading = null;
                    if (PdhCollectQueryData(_query) != 0) return null;

                    var perAdapter = ReadPerAdapter();
                    if (perAdapter == null) return null;

                    RefreshRoles(perAdapter);
                    _lastReading = InterpretGpuEngineUsage(perAdapter, _roles);
                    return _lastReading;
                }
                catch (DllNotFoundException)
                {
                    _unavailable = true;
                    return null;
                }
                catch (EntryPointNotFoundException)
                {
                    _unavailable = true;
                    return null;
                }
                catch (Exception)
                {
                    // A collect that went wrong once is a missing reading for this tick, not a reason to fail the
                    // rest of the telemetry sample or to write a line to the log every second.
                    return null;
                }
            }
        }

        private bool Open()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0)
            {
                _unavailable = true;
                return false;
            }

            if (PdhAddEnglishCounterW(query, GpuEngineCounter, IntPtr.Zero, out var counter) != 0)
            {
                // The counter set is not on this machine. Asking again next tick would get the same answer.
                _ = PdhCloseQuery(query);
                _unavailable = true;
                return false;
            }

            _query = query;
            _counter = counter;
            return true;
        }

        private IReadOnlyDictionary<long, double>? ReadPerAdapter()
        {
            uint size = 0;
            var status = PdhGetFormattedCounterArrayW(_counter, PdhFormatDouble, ref size, out _, IntPtr.Zero);
            if (status != PdhMoreData || size == 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(_counter, PdhFormatDouble, ref size, out var count, buffer) != 0)
                    return null;

                int stride = Marshal.SizeOf<PdhCounterValueItem>();

                // The count is PDH's word; the buffer is ours. No item is read past the bytes that were allocated.
                int items = (int)Math.Min(count, size / (uint)stride);
                var samples = new List<KeyValuePair<string, double>>(items);
                for (int index = 0; index < items; index++)
                {
                    var item = Marshal.PtrToStructure<PdhCounterValueItem>(buffer + (index * stride));
                    if (item.Value.Status is not (PdhStatusValidData or PdhStatusNewData)) continue;

                    // The names live inside the same buffer, so they are copied out before it is freed.
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (name == null) continue;
                    samples.Add(new KeyValuePair<string, double>(name, item.Value.Value));
                }

                return samples.Count == 0 ? null : AggregateGpuEngineUsage(samples);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void RefreshRoles(IReadOnlyDictionary<long, double> perAdapter)
        {
            bool stale = _roles == null;
            if (_roles != null)
            {
                foreach (var luid in perAdapter.Keys)
                {
                    if (_roles.ContainsKey(luid)) continue;
                    stale = true;
                    break;
                }
            }

            if (!stale || _roleReads >= MaxRoleReads) return;
            if (_roleReads > 0 && _clock.Elapsed - _lastRoleReadAt < RoleRefreshInterval) return;

            _roleReads++;
            _lastRoleReadAt = _clock.Elapsed;
            _roles = ReadAdapterRoles() ?? _roles;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                // Closing cannot fail in a way that matters at shutdown; the handle is gone either way.
                if (_query != IntPtr.Zero) _ = PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                _counter = IntPtr.Zero;
            }
        }
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTEnumAdapters2(ref D3dkmtEnumAdapters2 request);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTQueryAdapterInfo(ref D3dkmtQueryAdapterInfo query);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter close);

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

    /// <summary><c>PDH_FMT_COUNTERVALUE_ITEM_W</c>: the instance name and its value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterValueItem
    {
        public IntPtr Name;
        public PdhCounterValue Value;
    }

    /// <summary><c>D3DKMT_ENUMADAPTERS2</c>: the count in and out, and the caller's array of adapter records.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtEnumAdapters2
    {
        public uint NumAdapters;
        public IntPtr Adapters;
    }

    /// <summary>
    /// <c>D3DKMT_ADAPTERINFO</c>: the handle, the LUID (<c>DWORD LowPart</c>, <c>LONG HighPart</c>), the source count
    /// and a <c>BOOL</c>. Twenty bytes, four-byte aligned, as in <c>d3dkmthk.h</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtAdapterInfo
    {
        public uint Adapter;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    /// <summary><c>D3DKMT_QUERYADAPTERINFO</c>: handle, query type, and the caller's output buffer with its size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtQueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    /// <summary><c>D3DKMT_CLOSEADAPTER</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtCloseAdapter
    {
        public uint Adapter;
    }
}

/// <summary>One parsed <c>GPU Engine</c> instance: which adapter, which physical node, which engine type.</summary>
internal readonly record struct GpuEngineInstance(long Luid, int Phys, string EngineType);

/// <summary>What an adapter is to the GPU-usage reading. <c>Unknown</c> is an adapter the enumeration did not describe.</summary>
internal enum GpuAdapterRole
{
    Unknown = 0,
    Other,
    Integrated,
    Discrete,
    Software
}

/// <summary>
/// One sample of the engine counters: the GPU usage to show (null when there is nothing honest to show) and whether the
/// counters prove the discrete GPU is awake right now.
/// </summary>
internal readonly record struct GpuEngineReading(double? Usage, bool DiscreteBusy);
