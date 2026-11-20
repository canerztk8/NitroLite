using System.Globalization;
using System.Management;
using System.Text.Json.Nodes;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>What a pair of fan percentages asks the firmware for.</summary>
public enum FanSpeedRoute
{
    /// <summary>Both fans at a fixed speed, written through <c>SetGamingFanSpeed</c>.</summary>
    FixedSpeed = 0,

    /// <summary>Both fans back under firmware control, written through <c>SetGamingFanBehavior</c>.</summary>
    ReleaseToFirmware = 1,

    /// <summary>One fan released and one driven, which the firmware cannot express.</summary>
    Invalid = 2
}

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

    /// <summary>The class that carries CoolBoost and the backlight timeout. Absent on most models.</summary>
    private const string ApgeClass = "APGeAction";

    /// <summary>
    /// The hotkey index the backlight-timeout calls are addressed to. The reference implementation takes it from the
    /// caller; NitroLite has one backlight-off hotkey, so it is a constant here rather than a setting nobody can
    /// change.
    /// </summary>
    private const int BacklightHotkey = 0;

    /// <summary>WMID_GUID5, the battery charge limit interface. Not registered on an AN16-41.</summary>
    public const string BatteryGuid = "79772EC5-04B1-4BFD-843C-61E7F77B6CC9";

    /// <summary>
    /// Method names the battery interface has shipped under. The class is identified by GUID, but WMI methods are
    /// addressed by name, so the executor tries the documented names and reports not-supported when none is there —
    /// the same outcome the caller sees on a machine with no class at all.
    /// <para>
    /// <c>GetBatteryHealthControl</c> / <c>SetBatteryHealthControl</c> come first because that is the pair the Linux
    /// <c>acer-wmi-battery</c> driver documents for <c>WMID_GUID5</c>, and it is the pair that takes a four-byte
    /// buffer rather than a scalar.
    /// </para>
    /// </summary>
    private static readonly string[] BatteryGetMethods =
        { "GetBatteryHealthControl", "GetBatteryHealth", "GetBatteryCare", "GetBatteryChargeMode" };

    private static readonly string[] BatterySetMethods =
        { "SetBatteryHealthControl", "SetBatteryHealth", "SetBatteryCare", "SetBatteryChargeMode" };

    private readonly object _gate = new();
    private ManagementObject? _gaming;
    private ManagementObject? _battery;
    private ManagementObject? _apge;
    private bool _batteryLookupDone;
    private bool _apgeLookupDone;
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
            var methodNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (gaming != null)
            {
                foreach (var name in MethodNames(gaming))
                {
                    methods.Add(name);
                    methodNames.Add(name);
                }
            }

            // The keyboard is only writable when the colour setter is actually on the class. A machine that exposes
            // the fan methods but no SetGamingRgbKb must not be offered a lighting control that cannot work.
            bool keyboardLighting = methodNames.Contains("SetGamingRgbKb");

            // The mode bitmask and the WMI MUX signal are read here rather than in the window: both need elevation,
            // and both are cheap once the instance is already open.
            var supportedModes = new JsonArray();
            ulong? supportedModeMask = null;
            bool wmiMux = false;
            if (gaming != null)
            {
                try
                {
                    var mask = InvokeGetter(gaming, "GetGamingMiscSetting", AcerWmiProtocol.MiscIndexSupportedModes);
                    if (mask.HasValue)
                    {
                        supportedModeMask = mask.Value;
                        foreach (var mode in AcerWmiProtocol.DecodeSupportedModes(mask.Value))
                            supportedModes.Add(mode.ToString());
                    }
                }
                catch { }

                try
                {
                    var support = InvokeGetter(gaming, "GetGamingMiscSetting", AcerWmiProtocol.MiscIndexGpuModeSupport);
                    if (support.HasValue) wmiMux = AcerWmiProtocol.DecodeGpuModeSupport(support.Value);
                }
                catch { }
            }

            var apge = EnsureApge();

            return WmiOperationResult.Success(new JsonObject
            {
                ["gamingClass"] = gaming != null,
                ["batteryClass"] = battery != null,
                ["apgeClass"] = apge != null,
                ["methods"] = methods,
                ["fanControl"] = gaming != null,
                ["operatingMode"] = gaming != null,
                ["muxSwitch"] = registry.DgpuModeCapability is > 0 || wmiMux,
                ["wmiMuxSwitch"] = wmiMux,
                ["batteryLimit"] = battery != null,
                ["keyboardLighting"] = keyboardLighting,
                ["coolBoost"] = apge != null,
                // The class being registered is not the same as the selector answering. On the reference AN16-41
                // APGeAction is present but GetFunction(0x80001) returns a status this build does not recognise, so a
                // switch built on the class alone showed "off" over a firmware timeout that was still on — and could
                // neither read nor write it. Asking the selector is what makes the capability mean something.
                ["backlightTimeout"] = apge != null && GetBacklightTimeout().Ok,
                ["lcdOverdrive"] = registry.LcdOverdriveSupport is > 0,
                ["winKeyLock"] = gaming != null,
                ["supportedModes"] = supportedModes,
                ["supportedModeMask"] = supportedModeMask.HasValue ? supportedModeMask.Value : null,
                ["dgpuModeCapability"] = registry.DgpuModeCapability,
                ["keyboardArea"] = registry.KeyboardArea
            });
        }
    }

    // ---------------------------------------------------------------- reads

    public WmiOperationResult ReadTelemetry(SensorQuery query)
    {
        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            try
            {
                var data = new JsonObject
                {
                    ["sensors"] = new JsonObject
                    {
                        ["cpuTemp"] = ReadSensor(gaming, query.CpuTemp),
                        ["cpuFanRpm"] = ReadSensor(gaming, query.CpuFanRpm),
                        ["sys1Temp"] = ReadSensor(gaming, query.Sys1Temp),
                        ["gpuFanRpm"] = ReadSensor(gaming, query.GpuFanRpm),
                        ["gpuTemp"] = ReadSensor(gaming, query.GpuTemp)
                    }
                };

                // The percentage is the fallback for a machine whose tachometer did not answer, and the caller reads
                // it only in that case. When the tachometer is confirmed and both fans reported, reading it is two
                // firmware round trips per sample for a number nobody looks at.
                if (!query.TachometerConfirmed || !BothTachometersAnswered(data))
                {
                    AppendFanSpeed(data, gaming, "cpu", AcerWmiProtocol.FanIdCpu);
                    AppendFanSpeed(data, gaming, "gpu", AcerWmiProtocol.FanIdGpu);
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

                var behavior = InvokeGetter(gaming, "GetGamingFanBehavior", AcerWmiProtocol.FanBitmapBoth);
                if (behavior.HasValue)
                {
                    var (status, cpu, gpu) = AcerWmiProtocol.DecodeFanBehavior(behavior.Value);
                    data["fanBehaviorStatus"] = status;
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

    /// <summary>
    /// Which write a pair of fan percentages asks for.
    /// </summary>
    /// <remarks>
    /// Zero is not a speed, it is "hand the fan back". Writing 0 to <c>SetGamingFanSpeed</c> asks the EC for a stopped
    /// fan, which is the one thing this app must never do on a hot machine, so a pair of zeros is routed to the
    /// behaviour call that actually returns the fans to the firmware. A single zero is refused rather than guessed
    /// at: <c>SetGamingFanBehavior</c> drives both fans at once, so there is no way to release one and keep driving
    /// the other, and picking one of the two readings would be a silent decision about a fan.
    /// </remarks>
    public static FanSpeedRoute RouteFanSpeed(int cpuPercent, int gpuPercent) => (cpuPercent, gpuPercent) switch
    {
        (0, 0) => FanSpeedRoute.ReleaseToFirmware,
        (0, _) or (_, 0) => FanSpeedRoute.Invalid,
        _ => FanSpeedRoute.FixedSpeed
    };

    public WmiOperationResult SetFanSpeed(int cpuPercent, int gpuPercent)
    {
        if (!WmiWorkerValidation.IsValidFanPercent(cpuPercent))
            return WmiOperationResult.InvalidArgument($"cpu fan percentage {cpuPercent} outside 0 or 30..100");
        if (!WmiWorkerValidation.IsValidFanPercent(gpuPercent))
            return WmiOperationResult.InvalidArgument($"gpu fan percentage {gpuPercent} outside 0 or 30..100");

        switch (RouteFanSpeed(cpuPercent, gpuPercent))
        {
            case FanSpeedRoute.ReleaseToFirmware:
                return SetFanBehavior(FanMode.Auto, FanMode.Auto);

            case FanSpeedRoute.Invalid:
                return WmiOperationResult.InvalidArgument(
                    "a fan speed of 0 releases both fans; it cannot be combined with a driven fan");
        }

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
                SetBatteryInput(inParams, (byte)mode);
                using var outParams = battery.InvokeMethod(method, inParams, null);

                // No status check here: WMID_GUID5 answers the mode it now holds, not a status code, so the low byte
                // of a scalar answer and byte 0 of a buffer answer are the mode itself. Reading that as a status made
                // every write of "Full" look like a failure. The controller's read-back is the real verification.
                return WmiOperationResult.Success(new JsonObject
                {
                    ["method"] = method,
                    ["batteryLimit"] = mode.ToString(),
                    ["readBack"] = ReadBatteryAnswer(outParams)?.ToString()
                });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    // ---------------------------------------------------------------- keyboard backlight

    /// <summary>
    /// Reads the keyboard backlight. <c>GetGamingKBBacklight(0x01)</c> answers a byte structure whose byte 2 is
    /// brightness (0..100) and bytes 5..7 are the colour; <c>GetGamingRgbKb(0x01)</c> answers the same colour packed
    /// into a 64-bit integer. Both are reported because the packed form is what a write is compared against.
    /// <para>
    /// The structure is 15 bytes on an AN16-41 and 9 on an AN515-44. Both start with the same eight, so the decoder
    /// reads the common prefix and reports the length instead of assuming one.
    /// </para>
    /// </summary>
    public WmiOperationResult GetKeyboardBacklight()
    {
        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            try
            {
                var data = new JsonObject();

                var structure = InvokeArrayGetter(gaming, "GetGamingKBBacklight", 1);
                if (structure.HasValue)
                {
                    var bytes = new JsonArray();
                    foreach (var b in structure.Value.Bytes) bytes.Add(b);
                    data["rawBytes"] = bytes;
                    data["status"] = structure.Value.Status;
                    data["length"] = structure.Value.Bytes.Length;

                    var decoded = AcerWmiProtocol.DecodeKbBacklight(structure.Value.Bytes);
                    if (decoded.HasValue)
                    {
                        data["mode"] = decoded.Value.Mode;
                        data["speed"] = decoded.Value.Speed;
                        data["brightness"] = decoded.Value.Brightness;
                        data["direction"] = decoded.Value.Direction;
                        data["red"] = decoded.Value.Red;
                        data["green"] = decoded.Value.Green;
                        data["blue"] = decoded.Value.Blue;
                    }
                }

                var packed = InvokeGetter(gaming, "GetGamingRgbKb", 1);
                if (packed.HasValue)
                {
                    var (red, green, blue, mask) = AcerWmiProtocol.DecodeRgbKb(packed.Value);
                    data["rgbRaw"] = packed.Value;
                    data["rgbRed"] = red;
                    data["rgbGreen"] = green;
                    data["rgbBlue"] = blue;
                    data["rgbZoneMask"] = mask;
                }

                if (!structure.HasValue && !packed.HasValue)
                    return WmiOperationResult.NotSupported("this machine exposes no keyboard backlight getter");

                return WmiOperationResult.Success(data);
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    /// <summary>
    /// Writes the keyboard lighting through the full four-zone sequence.
    /// <para>
    /// The order is not cosmetic. Some four-zone models keep the LED zones disabled until the gaming system info has
    /// been polled and all four zones are switched on through <c>SetGamingLED</c>; per-zone colours written before
    /// that are stored by the EC and never displayed. The trailing <c>SetGamingKBBacklight</c> is what commits them:
    /// the static lock for a held colour, the animated payload for an effect.
    /// </para>
    /// <para>
    /// The per-zone colours are scaled by the brightness before they are packed. The firmware does not apply the
    /// brightness byte to a per-zone colour, so an unscaled 50 % write comes out at full brightness.
    /// </para>
    /// <para>
    /// Neighbouring zones that share a colour go out in one write, because the zone mask is a bitmap: a split keyboard
    /// is two writes (<c>0x03</c> then <c>0x0C</c>) and a solid colour is one (<c>0x0F</c>). See
    /// <see cref="AcerWmiProtocol.GroupZoneRuns"/> for why the grouping matters beyond the round trips saved.
    /// </para>
    /// <para>
    /// The commit step is tolerated: some firmware revisions apply the zone colours and still answer a non-zero
    /// status to <c>SetGamingKBBacklight</c>. When the colour writes succeeded, that answer is reported in
    /// <c>commitStatus</c> and the write is still a success — failing it would throw away colours the keyboard is
    /// already showing.
    /// </para>
    /// </summary>
    public WmiOperationResult SetKeyboardLighting(KeyboardLightingRequest request)
    {
        if (request == null) return WmiOperationResult.InvalidArgument("lighting request is missing");

        if (!WmiWorkerValidation.IsValidBrightnessPercent(request.Brightness))
            return WmiOperationResult.InvalidArgument($"brightness {request.Brightness} is outside 0..100");

        if (request.Zones == null || request.Zones.Count != AcerWmiProtocol.LedZoneCount)
            return WmiOperationResult.InvalidArgument(
                $"zones must carry exactly {AcerWmiProtocol.LedZoneCount} colours");

        foreach (var packed in request.Zones)
        {
            if (!WmiWorkerValidation.IsValidPackedColor(packed))
                return WmiOperationResult.InvalidArgument($"colour 0x{packed:X6} is outside 0x000000..0xFFFFFF");
        }

        var mode = AcerWmiProtocol.KbModeFor(request.Effect);
        if (mode == null)
            return WmiOperationResult.InvalidArgument($"effect '{request.Effect}' has no firmware mode");

        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            try
            {
                // 1. Poll the gaming system info. The zones stay disabled on some models until this has run.
                InvokeGetter(gaming, "GetGamingSysInfo", 0);

                // 2. Switch all four zones on. Only the models whose zones start disabled need this, and the ones
                //    that do not need it reject the call outright: an AN16-41 answers WBEM_E_INVALID_PARAMETER and
                //    its LED getters all report status 0x02. A refusal here is therefore not a failure of the write,
                //    so it is logged and the zone colours are sent anyway.
                string? armError = null;
                try
                {
                    var arm = InvokeArraySetter(gaming, "SetGamingLED",
                        AcerWmiProtocol.ToLittleEndianBytes(AcerWmiProtocol.LedArmValue));
                    if (arm.HasValue && arm.Value != AcerWmiProtocol.StatusOk)
                        armError = $"SetGamingLED status 0x{arm.Value:X2}";
                }
                catch (Exception ex)
                {
                    armError = Describe(ex);
                }

                // 3. One brightness-scaled colour per zone, held per zone for the report and for the commit below.
                var scaled = new (byte Red, byte Green, byte Blue)[AcerWmiProtocol.LedZoneCount];
                for (int i = 0; i < scaled.Length; i++)
                {
                    int packed = request.Zones[i];
                    scaled[i] = AcerWmiProtocol.ScaleColor(
                        (byte)((packed >> 16) & 0xFF),
                        (byte)((packed >> 8) & 0xFF),
                        (byte)(packed & 0xFF),
                        request.Brightness);
                }

                // 3a. Write them back as runs of neighbouring zones, not as four separate calls: the mask is a zone
                //     bitmap, so the left half and the right half of a split keyboard are two writes. Four writes that
                //     arrive while the firmware is still applying the previous frame are how a zone ends up holding the
                //     colour the keyboard had before — the "three zones one colour, one zone the other" report.
                var runs = AcerWmiProtocol.GroupZoneRuns(request.Zones);
                foreach (var (mask, packed) in runs)
                {
                    var color = AcerWmiProtocol.ScaleColor(
                        (byte)((packed >> 16) & 0xFF),
                        (byte)((packed >> 8) & 0xFF),
                        (byte)(packed & 0xFF),
                        request.Brightness);

                    var status = InvokeSetter(gaming, "SetGamingRgbKb",
                        AcerWmiProtocol.EncodeRgbKb(color.Red, color.Green, color.Blue, mask));
                    if (!status.HasValue)
                        return WmiOperationResult.WmiFailed($"SetGamingRgbKb (mask 0x{mask:X2}) returned no status");
                    if (status.Value != AcerWmiProtocol.StatusOk)
                        return WmiOperationResult.StatusError(
                            $"SetGamingRgbKb (mask 0x{mask:X2}) status 0x{status.Value:X2}");
                }

                // The report keeps one entry per zone, in zone order, whatever the writes were grouped as.
                var reported = new JsonArray();
                foreach (var zone in scaled)
                    reported.Add(AcerWmiProtocol.FormatHexColor(zone.Red, zone.Green, zone.Blue));

                // 4. Commit. The static lock carries no colour; an effect carries the first zone's colour.
                var payload = mode.Value == AcerWmiProtocol.KbModeStatic
                    ? AcerWmiProtocol.EncodeKbStaticLock(request.Brightness)
                    : AcerWmiProtocol.EncodeKbEffect(
                        mode.Value, request.Speed, request.Brightness, request.Direction,
                        scaled[0].Red, scaled[0].Green, scaled[0].Blue);

                var commit = InvokeArraySetter(gaming, "SetGamingKBBacklight", payload);
                int commitStatus = commit.HasValue ? (int)commit.Value : -1;

                return WmiOperationResult.Success(new JsonObject
                {
                    ["effect"] = request.Effect,
                    ["mode"] = mode.Value,
                    ["brightness"] = request.Brightness,
                    ["speed"] = request.Speed,
                    ["direction"] = request.Direction,
                    ["zones"] = reported,
                    ["zoneWrites"] = runs.Count,
                    ["armError"] = armError,
                    ["commitStatus"] = commitStatus,
                    ["commitApplied"] = commitStatus == AcerWmiProtocol.StatusOk
                });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    // ---------------------------------------------------------------- CoolBoost, overdrive, WinKey lock

    /// <summary>
    /// Reads CoolBoost from <c>APGeAction.GetFunction(0x207)</c>.
    /// <para>
    /// Measured on an AN16-41: the class is registered, the call answers <c>0x0000000000000000</c>, and the decode
    /// reads that as "off" because the low byte is zero and bits 15:8 are zero. A firmware that does not understand
    /// the selector answers a non-zero low byte, which is reported as not-supported rather than as "off".
    /// </para>
    /// </summary>
    public WmiOperationResult GetCoolBoost()
    {
        lock (_gate)
        {
            var apge = EnsureApge();
            if (apge == null) return WmiOperationResult.NotSupported("APGeAction is not registered");

            try
            {
                var answer = InvokeApgeGetter(apge, "GetFunction", AcerWmiProtocol.ApgeCoolBoostGet);
                if (!answer.HasValue) return WmiOperationResult.WmiFailed("GetFunction returned no value");

                var on = AcerWmiProtocol.DecodeCoolBoost(answer.Value);
                if (on == null)
                    return WmiOperationResult.NotSupported(
                        $"CoolBoost selector answered 0x{answer.Value:X16}");

                return WmiOperationResult.Success(new JsonObject
                {
                    ["coolBoost"] = on.Value,
                    ["raw"] = answer.Value
                });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    /// <summary>Writes CoolBoost through <c>APGeAction.SetFunction</c>.</summary>
    public WmiOperationResult SetCoolBoost(bool on)
    {
        lock (_gate)
        {
            var apge = EnsureApge();
            if (apge == null) return WmiOperationResult.NotSupported("APGeAction is not registered");

            try
            {
                var status = InvokeApgeSetter(apge, "SetFunction", AcerWmiProtocol.EncodeCoolBoost(on));
                if (!status.HasValue) return WmiOperationResult.WmiFailed("SetFunction returned no status");
                if (status.Value != AcerWmiProtocol.StatusOk)
                    return WmiOperationResult.StatusError($"SetFunction status 0x{status.Value:X2}");

                return WmiOperationResult.Success(new JsonObject { ["coolBoost"] = on });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    /// <summary>Reads the LCD overdrive state from <c>GetGamingProfile(0)</c>.</summary>
    public WmiOperationResult GetLcdOverdrive()
    {
        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            try
            {
                var answer = InvokeGetter(gaming, "GetGamingProfile", 0);
                if (!answer.HasValue) return WmiOperationResult.WmiFailed("GetGamingProfile returned no value");

                var on = AcerWmiProtocol.DecodeLcdOverdrive(answer.Value);
                if (on == null)
                    return WmiOperationResult.NotSupported(
                        $"panel does not report overdrive (GetGamingProfile(0) = 0x{answer.Value:X16})");

                return WmiOperationResult.Success(new JsonObject
                {
                    ["lcdOverdrive"] = on.Value,
                    ["raw"] = answer.Value
                });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    /// <summary>Writes LCD overdrive through <c>SetGamingProfile</c>.</summary>
    public WmiOperationResult SetLcdOverdrive(bool on)
    {
        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            try
            {
                var status = InvokeSetter(gaming, "SetGamingProfile", AcerWmiProtocol.EncodeLcdOverdrive(on));
                if (!status.HasValue) return WmiOperationResult.WmiFailed("SetGamingProfile returned no status");
                if (status.Value != AcerWmiProtocol.StatusOk)
                    return WmiOperationResult.StatusError($"SetGamingProfile status 0x{status.Value:X2}");

                return WmiOperationResult.Success(new JsonObject { ["lcdOverdrive"] = on });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    /// <summary>
    /// Writes the Windows-key lock. Reported as unverified because the firmware has no getter for it: the answer
    /// carries <c>verified: false</c> so the caller does not claim a read-back that never happened.
    /// </summary>
    public WmiOperationResult SetWinKeyLock(bool enabled)
    {
        lock (_gate)
        {
            var gaming = EnsureGaming();
            if (gaming == null) return WmiOperationResult.Unavailable("AcerGamingFunction is not registered");

            try
            {
                var status = InvokeSetter(gaming, "SetGamingProfile", AcerWmiProtocol.EncodeWinKeyLock(enabled));
                if (!status.HasValue) return WmiOperationResult.WmiFailed("SetGamingProfile returned no status");
                if (status.Value != AcerWmiProtocol.StatusOk)
                    return WmiOperationResult.StatusError($"SetGamingProfile status 0x{status.Value:X2}");

                return WmiOperationResult.Success(new JsonObject
                {
                    ["winKeyLock"] = enabled,
                    ["verified"] = false
                });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    /// <summary>Reads the 30-second keyboard backlight timeout from <c>APGeAction.GetFunction</c>.</summary>
    public WmiOperationResult GetBacklightTimeout()
    {
        lock (_gate)
        {
            var apge = EnsureApge();
            if (apge == null) return WmiOperationResult.NotSupported("APGeAction is not registered");

            try
            {
                var answer = InvokeApgeGetter(
                    apge, "GetFunction", AcerWmiProtocol.EncodeBacklightTimeoutGet(BacklightHotkey));
                if (!answer.HasValue) return WmiOperationResult.WmiFailed("GetFunction returned no value");

                var decoded = AcerWmiProtocol.DecodeBacklightTimeout(answer.Value);
                if (decoded == null)
                    return WmiOperationResult.NotSupported(
                        $"backlight selector answered 0x{answer.Value:X16}");

                return WmiOperationResult.Success(new JsonObject
                {
                    ["enabled"] = decoded.Value.Enabled,
                    ["brightness"] = decoded.Value.BrightnessPercent,
                    ["raw"] = answer.Value
                });
            }
            catch (Exception ex)
            {
                return WmiOperationResult.WmiFailed(Describe(ex));
            }
        }
    }

    /// <summary>
    /// Writes the 30-second keyboard backlight timeout. The brightness the firmware stores alongside it is read
    /// first so the write does not reset the user's level; when the read fails the current level is left at 100.
    /// </summary>
    public WmiOperationResult SetBacklightTimeout(bool enabled)
    {
        lock (_gate)
        {
            var apge = EnsureApge();
            if (apge == null) return WmiOperationResult.NotSupported("APGeAction is not registered");

            try
            {
                int brightness = 100;
                try
                {
                    var current = InvokeApgeGetter(
                        apge, "GetFunction", AcerWmiProtocol.EncodeBacklightTimeoutGet(BacklightHotkey));
                    if (current.HasValue &&
                        AcerWmiProtocol.DecodeBacklightTimeout(current.Value) is { } decoded)
                    {
                        brightness = decoded.BrightnessPercent;
                    }
                }
                catch
                {
                    // A failed read is not a reason to refuse the write; the level simply stays at the default.
                }

                var status = InvokeApgeSetter(
                    apge, "SetFunction",
                    AcerWmiProtocol.EncodeBacklightTimeoutSet(BacklightHotkey, brightness, enabled));
                if (!status.HasValue) return WmiOperationResult.WmiFailed("SetFunction returned no status");
                if (status.Value != AcerWmiProtocol.StatusOk)
                    return WmiOperationResult.StatusError($"SetFunction status 0x{status.Value:X2}");

                return WmiOperationResult.Success(new JsonObject
                {
                    ["enabled"] = enabled,
                    ["brightness"] = brightness
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
            // The adopted instance is released before the field is cleared. The enumeration did not finish, so
            // nothing is known about the class and the next call has to look again — but the COM object that was
            // already adopted is still ours, and dropping the reference without disposing it leaks it for the life
            // of the worker.
            try { _gaming?.Dispose(); } catch { }
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

                    if (_battery != null)
                    {
                        _batteryLookupDone = true;
                        return _battery;
                    }
                }
            }

            // The enumeration finished and found nothing. That answer cannot change while the worker is running, so
            // it is remembered and every later call is free. A throw skips this line, which is the point: a transient
            // WMI failure is not an answer, and remembering it as one disabled the battery limiter for the rest of
            // the session.
            _batteryLookupDone = true;
        }
        catch
        {
            try { _battery?.Dispose(); } catch { }
            _battery = null;
        }

        return _battery;
    }

    /// <summary>
    /// The <c>APGeAction</c> instance, cached. A separate class from <c>AcerGamingFunction</c> with a different
    /// parameter naming convention — <c>uiInput</c>/<c>uiOutput</c> rather than <c>gmInput</c>/<c>gmOutput</c> — so it
    /// gets its own invokers rather than a shared one with a name switch.
    /// <para>
    /// Absent on every model whose firmware has no CoolBoost step, which is why a miss is remembered: the lookup
    /// costs an enumeration and the answer cannot change while the worker is running.
    /// </para>
    /// </summary>
    private ManagementObject? EnsureApge()
    {
        if (_apge != null) return _apge;
        if (_apgeLookupDone) return null;

        try
        {
            var scope = new ManagementScope(WmiNamespace);
            scope.Connect();
            using var cls = new ManagementClass(scope, new ManagementPath(ApgeClass), null);
            using var instances = cls.GetInstances();
            foreach (ManagementObject instance in instances)
            {
                if (_apge == null)
                {
                    _apge = instance;
                    continue;
                }

                instance.Dispose();
            }

            // Same rule as the battery lookup: only a finished enumeration is an answer. A throw leaves the flag
            // clear so the next call tries again instead of reporting "no CoolBoost" for the rest of the session.
            _apgeLookupDone = true;
        }
        catch
        {
            try { _apge?.Dispose(); } catch { }
            _apge = null;
        }

        return _apge;
    }

    private static ulong? InvokeApgeGetter(ManagementObject instance, string method, ulong input)
    {
        using var inParams = instance.GetMethodParameters(method);
        inParams["uiInput"] = input;
        using var outParams = instance.InvokeMethod(method, inParams, null);
        var raw = outParams?["uiOutput"];
        return raw == null ? null : Convert.ToUInt64(raw, CultureInfo.InvariantCulture);
    }

    private static uint? InvokeApgeSetter(ManagementObject instance, string method, ulong input)
    {
        using var inParams = instance.GetMethodParameters(method);
        inParams["uiInput"] = input;
        using var outParams = instance.InvokeMethod(method, inParams, null);
        var raw = outParams?["uiOutput"];
        return raw == null ? null : Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
    }

    private static ulong? InvokeGetter(ManagementObject instance, string method, uint input)
    {        using var inParams = instance.GetMethodParameters(method);
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

    /// <summary>
    /// A setter whose input is a byte array rather than an integer. <c>SetGamingLED</c> and
    /// <c>SetGamingKBBacklight</c> are the two, and both answer their status in <c>gmOutput</c>.
    /// </summary>
    private static uint? InvokeArraySetter(ManagementObject instance, string method, byte[] payload)
    {
        using var inParams = instance.GetMethodParameters(method);
        inParams["gmInput"] = payload;
        using var outParams = instance.InvokeMethod(method, inParams, null);
        var raw = outParams?["gmOutput"];
        return raw == null ? null : Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A getter whose answer is a byte array rather than a 64-bit integer. <c>GetGamingKBBacklight</c> is the only
    /// one, and its status lives in a separate <c>gmReturn</c> byte instead of the low byte of the answer.
    /// </summary>
    private static (byte[] Bytes, byte Status)? InvokeArrayGetter(ManagementObject instance, string method, uint input)
    {
        using var inParams = instance.GetMethodParameters(method);
        inParams["gmInput"] = input;
        using var outParams = instance.InvokeMethod(method, inParams, null);
        if (outParams?["gmOutput"] is not byte[] bytes) return null;

        byte status = 0;
        var rawStatus = outParams["gmReturn"];
        if (rawStatus != null)
        {
            try { status = Convert.ToByte(rawStatus, CultureInfo.InvariantCulture); }
            catch { status = 0; }
        }

        return (bytes, status);
    }

    /// <summary>
    /// One <c>GetGamingSysInfo</c> call for one sensor id. The answer carries a status byte in bits 7:0 and the
    /// reading in bits 23:8; the reading is only published when the status says the answer is valid, because a
    /// firmware that does not know the id answers a status code and a zero.
    /// </summary>
    private static JsonObject? ReadSensor(ManagementObject gaming, byte sensorId)
    {
        var answer = InvokeGetter(gaming, "GetGamingSysInfo", AcerWmiProtocol.EncodeSysInfoQuery(sensorId));
        if (!answer.HasValue) return null;

        var status = AcerWmiProtocol.DecodeStatus(answer.Value);
        var node = new JsonObject
        {
            ["sensor"] = sensorId,
            ["status"] = status,
            ["raw"] = answer.Value
        };

        if (status == AcerWmiProtocol.StatusOk)
            node["value"] = AcerWmiProtocol.DecodeSysInfoValue(answer.Value);

        return node;
    }

    /// <summary>
    /// Whether both tachometers answered a speed this build believes. It mirrors the gate
    /// <see cref="WmiHardwareController.BuildMonitorData"/> applies before it trusts a measured speed, so the
    /// percentage fallback is skipped exactly when that gate would have discarded it.
    /// </summary>
    private static bool BothTachometersAnswered(JsonObject data) =>
        data["sensors"] is JsonObject sensors &&
        AnsweredRpm(sensors, SensorDiscovery.CpuFanRpm) &&
        AnsweredRpm(sensors, SensorDiscovery.GpuFanRpm);

    private static bool AnsweredRpm(JsonObject sensors, string key)
    {
        if (sensors[key] is not JsonObject node) return false;

        try
        {
            return node["status"]?.GetValue<int>() == AcerWmiProtocol.StatusOk &&
                   node["value"]?.GetValue<int>() is int rpm &&
                   SensorPlausibility.IsPlausibleFanRpm(rpm);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reports one fan's percentage and status.
    /// <para>
    /// The percentage is only published when the status byte says the answer is valid. The EC answers status
    /// <c>0x01</c> while a fan sits in auto mode with no setpoint — measured on an AN16-41, where
    /// <c>GetGamingFanSpeed(0x04)</c> returned <c>0x0001</c> in one run and <c>0x2200</c> (34 %, status 0) in the
    /// next. The value in a status-1 answer is not a speed, and publishing it would show a fan at 0 % that is
    /// physically spinning.
    /// </para>
    /// </summary>
    private static void AppendFanSpeed(JsonObject data, ManagementObject gaming, string prefix, byte fanId)
    {
        var answer = InvokeGetter(gaming, "GetGamingFanSpeed", fanId);
        if (!answer.HasValue) return;

        var status = AcerWmiProtocol.DecodeStatus(answer.Value);
        data[$"{prefix}FanStatus"] = status;
        if (status == AcerWmiProtocol.StatusOk)
            data[$"{prefix}FanPercent"] = AcerWmiProtocol.DecodeFanPercent(answer.Value);
    }

    private static BatteryChargeMode? ReadBatteryLimit(ManagementObject battery)
    {
        try
        {
            var method = FindMethod(battery, BatteryGetMethods);
            if (method == null) return null;

            using var inParams = battery.GetMethodParameters(method);
            SetBatteryInput(inParams, 0);
            using var outParams = battery.InvokeMethod(method, inParams, null);
            return ReadBatteryAnswer(outParams);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fills the charge-limit input. <c>WMID_GUID5</c>'s documented interface is a four-byte buffer — the Linux
    /// <c>acer-wmi-battery</c> driver sends <c>{mode, 0, 0, 0}</c> — while some firmware exposes the same method with a
    /// scalar parameter. The two are told apart by the parameter's type rather than by the method name, because the
    /// name has shipped under both shapes.
    /// </summary>
    private static void SetBatteryInput(ManagementBaseObject parameters, byte mode)
    {
        foreach (PropertyData property in parameters.Properties)
        {
            if (property.IsArray)
            {
                parameters[property.Name] = AcerWmiProtocol.EncodeBatteryLimit(mode);
                return;
            }

            parameters[property.Name] = (ulong)mode;
            return;
        }
    }

    /// <summary>
    /// Reads the charge mode out of a <c>WMID_GUID5</c> answer. The buffer form carries the mode in byte 0; the scalar
    /// form carries it in the low byte. Neither carries a status code, so this deliberately does not decode one.
    /// </summary>
    private static BatteryChargeMode? ReadBatteryAnswer(ManagementBaseObject? parameters)
    {
        if (parameters == null) return null;

        foreach (PropertyData property in parameters.Properties)
        {
            if (property.IsArray)
            {
                return property.Value is byte[] bytes ? AcerWmiProtocol.DecodeBatteryLimit(bytes) : null;
            }

            try
            {
                var raw = Convert.ToUInt64(property.Value, CultureInfo.InvariantCulture);
                var scalar = (BatteryChargeMode)(byte)(raw & 0xFF);
                return Enum.IsDefined(scalar) ? scalar : null;
            }
            catch
            {
                return null;
            }
        }

        return null;
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
            try { _apge?.Dispose(); } catch { }
            _gaming = null;
            _battery = null;
            _apge = null;
        }
    }
}
