using System.Globalization;
using System.Management;
using System.Text.Json.Nodes;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// The real WMID_Gaming executor. Runs only inside the elevated <c>--wmi-worker</c> process.
/// <para>
/// <c>AcerGamingFunction</c> is a <c>dynamic</c> class: its methods live on instances, and reaching an instance needs
/// elevation. That is why this class is not used from the window — an unelevated call fails with
/// <c>WBEM_E_INVALID_METHOD_PARAMETERS</c> no matter what parameters are passed, which is exactly the trap the
/// read-only probe documented in PR 1.
/// </para>
/// <para>
/// The instance is cached for the life of the worker. Re-enumerating per call costs a round trip and, worse, can
/// hand back a different instance mid-sequence, so a fan write and the read that follows it would not be talking
/// about the same object.
/// </para>
/// </summary>
public sealed class AcerWmiExecutor : IWmiExecutor
{
    private const string WmiNamespace = @"root\WMI";
    private const string GamingClass = "AcerGamingFunction";

    /// <summary>WMID_GUID5, the battery charge limit interface. Not registered on an AN16-41.</summary>
    public const string BatteryGuid = "79772EC5-04B1-4BFD-843C-61E7F77B6CC9";

    /// <summary>
    /// Method names the battery interface has shipped under. The class is identified by GUID, but WMI methods are
    /// addressed by name, so the executor tries the documented names and reports not-supported when none is there —
    /// the same outcome the caller sees on a machine with no class at all.
    /// </summary>
    private static readonly string[] BatteryGetMethods = { "GetBatteryHealth", "GetBatteryCare", "GetBatteryChargeMode" };
    private static readonly string[] BatterySetMethods = { "SetBatteryHealth", "SetBatteryCare", "SetBatteryChargeMode" };

    private readonly object _gate = new();
    private ManagementObject? _gaming;
    private ManagementObject? _battery;
    private bool _batteryLookupDone;
    private bool _disposed;

    public bool IsAvailable
    {
        get
        {
            lock (_gate) return EnsureGaming() != null;
        }
    }

    // ---------------------------------------------------------------- liveness and capabilities

    public WmiOperationResult Ping() => WmiOperationResult.Success(new JsonObject
    {
        ["pong"] = true,
        ["gamingClass"] = IsAvailable
    });

    public WmiOperationResult GetCapabilities()
    {
        lock (_gate)
        {
            var gaming = EnsureGaming();
            var battery = EnsureBattery();
            var registry = AcerRegistrySnapshot.Read();

            var methods = new JsonArray();
            if (gaming != null)
            {
                foreach (var name in MethodNames(gaming)) methods.Add(name);
            }

            return WmiOperationResult.Success(new JsonObject
            {
                ["gamingClass"] = gaming != null,
                ["batteryClass"] = battery != null,
                ["methods"] = methods,
                ["fanControl"] = gaming != null,
                ["operatingMode"] = gaming != null,
                ["muxSwitch"] = registry.DgpuModeCapability is > 0,
                ["batteryLimit"] = battery != null,
                ["dgpuModeCapability"] = registry.DgpuModeCapability,
                ["keyboardArea"] = registry.KeyboardArea
            });
        }
    }

    // ---------------------------------------------------------------- reads

    public WmiOperationResult ReadTelemetry()
    {
        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            try
            {
                var data = new JsonObject();

                var sysInfo = InvokeGetter(gaming, "GetGamingSysInfo", 0);
                if (sysInfo.HasValue)
                {
                    data["sysInfoRaw"] = sysInfo.Value;
                    var bytes = new JsonArray();
                    foreach (var b in AcerWmiProtocol.ToBytes(sysInfo.Value)) bytes.Add(b);
                    data["sysInfoBytes"] = bytes;
                }

                AppendFanSpeed(data, gaming, "cpu", AcerWmiProtocol.FanIdCpu);
                AppendFanSpeed(data, gaming, "gpu", AcerWmiProtocol.FanIdGpu);

                var mode = InvokeGetter(gaming, "GetGamingMiscSetting", AcerWmiProtocol.MiscIndexPlatformProfile);
                if (mode.HasValue)
                {
                    data["operatingModeStatus"] = AcerWmiProtocol.DecodeStatus(mode.Value);
                    var decoded = AcerWmiProtocol.DecodeOperatingMode(mode.Value);
                    if (decoded.HasValue) data["operatingMode"] = decoded.Value.ToString();
                }

                var behavior = InvokeGetter(gaming, "GetGamingFanBehavior", 0);
                if (behavior.HasValue)
                {
                    var (bitmap, cpu, gpu) = AcerWmiProtocol.DecodeFanBehavior(behavior.Value);
                    data["fanBehaviorBitmap"] = bitmap;
                    data["cpuFanMode"] = cpu.ToString();
                    data["gpuFanMode"] = gpu.ToString();
                }

                return WmiOperationResult.Success(data);
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    public WmiOperationResult ReadDeviceState()
    {
        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            try
            {
                var data = new JsonObject();

                var mode = InvokeGetter(gaming, "GetGamingMiscSetting", AcerWmiProtocol.MiscIndexPlatformProfile);
                if (mode.HasValue)
                {
                    data["operatingModeStatus"] = AcerWmiProtocol.DecodeStatus(mode.Value);
                    var decoded = AcerWmiProtocol.DecodeOperatingMode(mode.Value);
                    if (decoded.HasValue) data["operatingMode"] = decoded.Value.ToString();
                }

                var behavior = InvokeGetter(gaming, "GetGamingFanBehavior", 0);
                if (behavior.HasValue)
                {
                    var (bitmap, cpu, gpu) = AcerWmiProtocol.DecodeFanBehavior(behavior.Value);
                    data["fanBehaviorBitmap"] = bitmap;
                    data["cpuFanMode"] = cpu.ToString();
                    data["gpuFanMode"] = gpu.ToString();
                }

                AppendFanSpeed(data, gaming, "cpu", AcerWmiProtocol.FanIdCpu);
                AppendFanSpeed(data, gaming, "gpu", AcerWmiProtocol.FanIdGpu);

                var mux = InvokeGetter(gaming, "GetGamingMiscSetting", AcerWmiProtocol.MiscIndexMux);
                if (mux.HasValue)
                {
                    // The raw answer is reported as well as the decoded mode: the caller has to be able to tell a
                    // firmware that numbers the modes from one from one that numbers them from zero before it
                    // writes a MUX value it cannot take back.
                    data["muxRaw"] = mux.Value;
                    data["muxStatus"] = AcerWmiProtocol.DecodeStatus(mux.Value);
                    var decoded = AcerWmiProtocol.DecodeMuxMode(mux.Value);
                    if (decoded.HasValue) data["muxMode"] = decoded.Value.ToString();
                }

                var registry = AcerRegistrySnapshot.Read();
                data["dgpuModeCapability"] = registry.DgpuModeCapability;

                var battery = EnsureBattery();
                data["batteryLimitSupported"] = battery != null;
                if (battery != null)
                {
                    var limit = ReadBatteryLimit(battery);
                    if (limit.HasValue) data["batteryLimit"] = limit.Value.ToString();
                }

                return WmiOperationResult.Success(data);
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    // ---------------------------------------------------------------- writes

    public WmiOperationResult SetFanBehavior(FanMode cpu, FanMode gpu)
    {
        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            try
            {
                var input = AcerWmiProtocol.EncodeFanBehavior(AcerWmiProtocol.FanBitmapBoth, cpu, gpu);
                var status = InvokeSetter(gaming, "SetGamingFanBehavior", input);
                if (!status.HasValue) return WmiOperationResult.WmiFailed("SetGamingFanBehavior returned no status");
                if (status.Value != AcerWmiProtocol.StatusOk)
                    return WmiOperationResult.StatusError($"SetGamingFanBehavior status 0x{status.Value:X2}");

                return WmiOperationResult.Success(new JsonObject
                {
                    ["input"] = input,
                    ["cpuFanMode"] = cpu.ToString(),
                    ["gpuFanMode"] = gpu.ToString()
                });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    public WmiOperationResult SetFanSpeed(int cpuPercent, int gpuPercent)
    {
        if (!WmiWorkerValidation.IsValidFanPercent(cpuPercent))
            return WmiOperationResult.InvalidArgument($"cpu fan percentage {cpuPercent} outside 0 or 30..100");
        if (!WmiWorkerValidation.IsValidFanPercent(gpuPercent))
            return WmiOperationResult.InvalidArgument($"gpu fan percentage {gpuPercent} outside 0 or 30..100");

        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            try
            {
                var cpuStatus = InvokeSetter(gaming, "SetGamingFanSpeed",
                    AcerWmiProtocol.EncodeFanSpeed(AcerWmiProtocol.FanIdCpu, cpuPercent));
                if (!cpuStatus.HasValue) return WmiOperationResult.WmiFailed("SetGamingFanSpeed (cpu) returned no status");
                if (cpuStatus.Value != AcerWmiProtocol.StatusOk)
                    return WmiOperationResult.StatusError($"SetGamingFanSpeed (cpu) status 0x{cpuStatus.Value:X2}");

                var gpuStatus = InvokeSetter(gaming, "SetGamingFanSpeed",
                    AcerWmiProtocol.EncodeFanSpeed(AcerWmiProtocol.FanIdGpu, gpuPercent));
                if (!gpuStatus.HasValue) return WmiOperationResult.WmiFailed("SetGamingFanSpeed (gpu) returned no status");
                if (gpuStatus.Value != AcerWmiProtocol.StatusOk)
                    return WmiOperationResult.StatusError($"SetGamingFanSpeed (gpu) status 0x{gpuStatus.Value:X2}");

                return WmiOperationResult.Success(new JsonObject
                {
                    ["cpuPercent"] = cpuPercent,
                    ["gpuPercent"] = gpuPercent
                });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    public WmiOperationResult SetOperatingMode(OperatingMode mode)
    {
        if (!Enum.IsDefined(mode)) return WmiOperationResult.InvalidArgument($"unknown operating mode {(int)mode}");

        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            try
            {
                var input = AcerWmiProtocol.EncodeOperatingMode(mode);
                var status = InvokeSetter(gaming, "SetGamingMiscSetting", input);
                if (!status.HasValue) return WmiOperationResult.WmiFailed("SetGamingMiscSetting returned no status");
                if (status.Value != AcerWmiProtocol.StatusOk)
                    return WmiOperationResult.StatusError($"SetGamingMiscSetting(0x0B) status 0x{status.Value:X2}");

                return WmiOperationResult.Success(new JsonObject
                {
                    ["input"] = input,
                    ["operatingMode"] = mode.ToString()
                });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    public WmiOperationResult SetMuxMode(GpuOperatingMode mode)
    {
        // Automatic is a firmware decision (Advanced Optimus / DDS); writing it is not a MUX switch.
        if (mode is not (GpuOperatingMode.Optimus or GpuOperatingMode.DiscreteGpu))
            return WmiOperationResult.InvalidArgument($"mux mode {mode} is not switchable");

        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            var registry = AcerRegistrySnapshot.Read();
            if (registry.DgpuModeCapability is not int mask || mask <= 0)
                return WmiOperationResult.NotSupported("this machine reports no MUX switch");
            if (!AcerRegistrySnapshot.IsGpuModeSupported(mask, mode))
                return WmiOperationResult.NotSupported($"mux mode {mode} is not in the capability bitmask {mask}");

            try
            {
                var input = AcerWmiProtocol.EncodeMuxMode(mode);
                var status = InvokeSetter(gaming, "SetGamingMiscSetting", input);
                if (!status.HasValue) return WmiOperationResult.WmiFailed("SetGamingMiscSetting returned no status");
                if (status.Value != AcerWmiProtocol.StatusOk)
                    return WmiOperationResult.StatusError($"SetGamingMiscSetting(0x02) status 0x{status.Value:X2}");

                return WmiOperationResult.Success(new JsonObject
                {
                    ["input"] = input,
                    ["muxMode"] = mode.ToString()
                });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    public WmiOperationResult SetBatteryLimit(BatteryChargeMode mode)
    {
        if (!Enum.IsDefined(mode)) return WmiOperationResult.InvalidArgument($"unknown charge mode {(int)mode}");

        lock (_gate)
        {
            var battery = EnsureBattery();
            if (battery == null)
                return WmiOperationResult.NotSupported("WMID_GUID5 is not registered on this machine");

            try
            {
                var method = FindMethod(battery, BatterySetMethods);
                if (method == null)
                    return WmiOperationResult.NotSupported("WMID_GUID5 exposes no known charge-limit setter");

                using var inParams = battery.GetMethodParameters(method);
                SetFirstInput(inParams, (ulong)(byte)mode);
                using var outParams = battery.InvokeMethod(method, inParams, null);
                var status = ReadStatus(outParams);
                if (status.HasValue && status.Value != AcerWmiProtocol.StatusOk)
                    return WmiOperationResult.StatusError($"{method} status 0x{status.Value:X2}");

                return WmiOperationResult.Success(new JsonObject
                {
                    ["method"] = method,
                    ["batteryLimit"] = mode.ToString()
                });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    // ---------------------------------------------------------------- WMI plumbing

    /// <summary>
    /// The gaming instance, cached. A dynamic class has no class-level methods, so this is the only way in; the
    /// enumeration is done once and the extra instances (there is normally exactly one) are disposed.
    /// </summary>
    private ManagementObject? EnsureGaming()
    {
        if (_gaming != null) return _gaming;
        try
        {
            var scope = new ManagementScope(WmiNamespace);
            scope.Connect();
            using var cls = new ManagementClass(scope, new ManagementPath(GamingClass), null);
            using var instances = cls.GetInstances();
            foreach (ManagementObject instance in instances)
            {
                if (_gaming == null)
                {
                    _gaming = instance;
                    continue;
                }

                instance.Dispose();
            }
        }
        catch
        {
            _gaming = null;
        }

        return _gaming;
    }

    /// <summary>
    /// The battery instance, found by GUID rather than by class name: the firmware picks the name, and on an AN16-41
    /// there is no such class at all. The lookup runs once; a miss is remembered so every later call is free.
    /// </summary>
    private ManagementObject? EnsureBattery()
    {
        if (_battery != null) return _battery;
        if (_batteryLookupDone) return null;
        _batteryLookupDone = true;

        try
        {
            var wanted = AcerWmiProbe.NormalizeGuid(BatteryGuid);
            var scope = new ManagementScope(WmiNamespace);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(
                scope, new ObjectQuery("SELECT * FROM meta_class WHERE __CLASS LIKE 'Acer%'"));
            using var classes = searcher.Get();
            foreach (ManagementClass cls in classes)
            {
                using (cls)
                {
                    string? name = null;
                    try
                    {
                        if (AcerWmiProbe.NormalizeGuid(cls.Qualifiers["guid"]?.Value?.ToString()) == wanted)
                            name = cls.ClassPath.ClassName;
                    }
                    catch { }

                    if (string.IsNullOrWhiteSpace(name)) continue;

                    using var concrete = new ManagementClass(scope, new ManagementPath(name), null);
                    using var instances = concrete.GetInstances();
                    foreach (ManagementObject instance in instances)
                    {
                        if (_battery == null)
                        {
                            _battery = instance;
                            continue;
                        }

                        instance.Dispose();
                    }

                    if (_battery != null) return _battery;
                }
            }
        }
        catch
        {
            _battery = null;
        }

        return _battery;
    }

    private static ulong? InvokeGetter(ManagementObject instance, string method, uint input)
    {
        using var inParams = instance.GetMethodParameters(method);
        inParams["gmInput"] = input;
        using var outParams = instance.InvokeMethod(method, inParams, null);
        var raw = outParams?["gmOutput"];
        return raw == null ? null : Convert.ToUInt64(raw, CultureInfo.InvariantCulture);
    }

    private static uint? InvokeSetter(ManagementObject instance, string method, ulong input)
    {
        using var inParams = instance.GetMethodParameters(method);
        inParams["gmInput"] = input;
        using var outParams = instance.InvokeMethod(method, inParams, null);
        var raw = outParams?["gmOutput"];
        return raw == null ? null : Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
    }

    private static void AppendFanSpeed(JsonObject data, ManagementObject gaming, string prefix, byte fanId)
    {
        var answer = InvokeGetter(gaming, "GetGamingFanSpeed", fanId);
        if (!answer.HasValue) return;
        data[$"{prefix}FanPercent"] = AcerWmiProtocol.DecodeFanPercent(answer.Value);
        data[$"{prefix}FanStatus"] = AcerWmiProtocol.DecodeStatus(answer.Value);
    }

    private static BatteryChargeMode? ReadBatteryLimit(ManagementObject battery)
    {
        try
        {
            var method = FindMethod(battery, BatteryGetMethods);
            if (method == null) return null;

            using var inParams = battery.GetMethodParameters(method);
            SetFirstInput(inParams, 0);
            using var outParams = battery.InvokeMethod(method, inParams, null);
            var raw = ReadFirstOutput(outParams);
            if (raw == null) return null;

            var mode = (BatteryChargeMode)(byte)(raw.Value & 0xFF);
            return Enum.IsDefined(mode) ? mode : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindMethod(ManagementObject instance, IEnumerable<string> candidates)
    {
        var available = MethodNames(instance);
        return candidates.FirstOrDefault(available.Contains);
    }

    /// <summary>
    /// The method names on an instance's class. <c>ManagementObject</c> has no <c>Methods</c> of its own — the
    /// metadata lives on the class — so the class is rebuilt from the instance's scope and class path.
    /// </summary>
    private static HashSet<string> MethodNames(ManagementObject instance)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = new ManagementClass(instance.Scope, instance.ClassPath, null);
            foreach (MethodData method in cls.Methods)
            {
                if (!string.IsNullOrWhiteSpace(method.Name)) names.Add(method.Name);
            }
        }
        catch
        {
            // A class that cannot be re-opened simply reports no methods; the caller then reports not-supported.
        }

        return names;
    }

    /// <summary>
    /// Sets the first in-parameter whatever the firmware named it. The battery interface is not the gaming one, so
    /// its parameter is not necessarily called <c>gmInput</c>.
    /// </summary>
    private static void SetFirstInput(ManagementBaseObject parameters, ulong value)
    {
        foreach (PropertyData property in parameters.Properties)
        {
            if (property.IsArray) continue;
            parameters[property.Name] = value;
            return;
        }
    }

    private static ulong? ReadFirstOutput(ManagementBaseObject? parameters)
    {
        if (parameters == null) return null;
        foreach (PropertyData property in parameters.Properties)
        {
            if (property.IsArray) continue;
            try
            {
                return Convert.ToUInt64(property.Value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static byte? ReadStatus(ManagementBaseObject? parameters)
    {
        var raw = ReadFirstOutput(parameters);
        return raw.HasValue ? AcerWmiProtocol.DecodeStatus(raw.Value) : null;
    }

    private static string Describe(Exception ex) =>
        ex is ManagementException management
            ? $"{management.ErrorCode} (0x{(int)management.ErrorCode:X8})"
            : $"{ex.GetType().Name}: {ex.Message}";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _gaming?.Dispose(); } catch { }
            try { _battery?.Dispose(); } catch { }
            _gaming = null;
            _battery = null;
        }
    }
}
