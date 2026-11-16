namespace NitroLite.Core.Hardware;

/// <summary>
/// Layer 1 of the capability probe: what can be learned without elevation and without the Acer service.
/// <para>
/// Four sources, all read-only: the <c>root\WMI</c> schema (which Acer classes exist, what they advertise, whether
/// their instances are reachable), the GUID qualifier of each Acer class (which is how the battery charge limit
/// interface is identified), the Acer agent's registry key (the MUX bitmask and the keyboard zone count), and the
/// Acer Care Center agent's service state (the other way the charge limit is provided).
/// </para>
/// </summary>
public sealed record SchemaProbeResult
{
    public AcerWmiProbeResult Wmi { get; init; } = new();

    /// <summary>Acer-prefixed <c>root\WMI</c> classes and their GUID qualifiers.</summary>
    public IReadOnlyDictionary<string, string> AcerClassGuids { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public AcerRegistrySnapshot Registry { get; init; } = AcerRegistrySnapshot.Empty;

    /// <summary>
    /// True when the Acer Care Center agent is installed and running. The 80 % charge limit is not always a WMI
    /// interface — an AN16-41 has no <c>WMID_GUID5</c> class and the agent owns the limit instead — so the battery
    /// answer has to consider both.
    /// </summary>
    public bool CareCenterAvailable { get; init; }

    /// <summary>What the Care Center check saw, for the report. Empty when it was never read.</summary>
    public string CareCenterDetail { get; init; } = "";

    /// <summary>Reads all four sources. Never throws; a source that fails simply contributes nothing.</summary>
    public static SchemaProbeResult Read()
    {
        var wmi = AcerWmiProbe.ReadWithTimeout(TimeSpan.FromSeconds(5));
        var careCenter = CareCenterProbe.Read();
        return new SchemaProbeResult
        {
            Wmi = wmi,
            AcerClassGuids = AcerWmiProbe.ReadAcerClassGuids(),
            Registry = AcerRegistrySnapshot.Read(),
            CareCenterAvailable = careCenter.Available,
            CareCenterDetail = careCenter.Detail
        };
    }
}

/// <summary>
/// The answer the rest of the app reads: which capabilities this machine has, which profile it matched, and where
/// each answer came from.
/// <para>
/// Capabilities are keyed by a stable name (<c>wmi.gamingFunction</c>, <c>mux.switch</c>, …) so they can be cached in
/// the settings file and compared across versions. A capability is only <see cref="HardwareProbeOutcome.Supported"/>
/// when something positively confirmed it; "could not ask" stays <see cref="HardwareProbeOutcome.AccessDenied"/> or
/// <see cref="HardwareProbeOutcome.Unknown"/> and never hides a control.
/// </para>
/// </summary>
public sealed record CapabilitySnapshot
{
    /// <summary>Stable capability names. Used as cache keys and in the hardware report.</summary>
    public const string WmiGamingFunction = "wmi.gamingFunction";
    public const string WmiBatteryChargeLimit = "wmi.batteryChargeLimit";
    public const string MuxSwitch = "mux.switch";
    public const string KeyboardZones = "keyboard.zones";
    public const string GpuOverclock = "gpu.overclock";
    public const string PanelOverdrive = "panel.overdrive";
    public const string ProfileMatch = "profile.match";

    /// <summary>"ModelCode|BiosVersion". A BIOS update changes it, which invalidates the cache.</summary>
    public string CacheKey { get; init; } = "";

    public string ProfileId { get; init; } = "";
    public string ProfileSource { get; init; } = "";
    public string ProfileNotes { get; init; } = "";

    public SensorLayout Sensors { get; init; } = SensorLayout.Default;
    public int MaxCpuFanRpm { get; init; }
    public int MaxGpuFanRpm { get; init; }

    /// <summary>
    /// True when the matched profile says this model's keyboard is driven over USB HID and the WMI RGB methods are
    /// silently ignored. The lighting path reads it to fall through to the service or OpenRGB instead of reporting a
    /// write the firmware accepted and discarded.
    /// </summary>
    public bool NitroHidKeyboard { get; init; }

    /// <summary>
    /// The <c>GetGamingMiscSetting(0x09) == 3</c> answer, when an elevated session read it. Null means nobody asked,
    /// which is not the same as "the machine said no": the registry value still decides in that case.
    /// </summary>
    public bool? WmiMuxSignal { get; init; }

    /// <summary>
    /// True when the matched profile says this model drives its operating mode through EC MMIO, so a
    /// <c>SetGamingMiscSetting(0x0B)</c> write is accepted and ignored. The mode write reports not-supported and the
    /// UI falls back to the Windows power plan.
    /// </summary>
    public bool EcMmioProfile { get; init; }

    /// <summary>True when the matched profile says this model's LED zones need the <c>SetGamingLED</c> arm step.</summary>
    public bool KbZoneArm { get; init; }

    /// <summary>
    /// True when <c>WMID_GUID5</c> is registered on this machine, false when the enumeration succeeded and it is not,
    /// null when the enumeration itself failed or the cache predates this field.
    /// <para>
    /// Kept apart from the combined battery capability because the WMI backend can only write through that interface:
    /// on an AN16-41 the limit is provided by Acer Care Center, so the capability is supported while the class this
    /// backend writes through does not exist. Only a positive "no" may stop the write; a null answer must not, or a
    /// machine that does have the class would lose the feature to a cache written by an older build.
    /// </para>
    /// </summary>
    public bool? WmiBatteryChargeLimitPresent { get; init; }

    public IReadOnlyList<HardwareProbeResult> Capabilities { get; init; } = Array.Empty<HardwareProbeResult>();

    /// <summary>The WMI schema answer, kept so the hardware report does not have to probe twice.</summary>
    public AcerWmiProbeResult Wmi { get; init; } = new();

    /// <summary>
    /// The Acer-prefixed <c>root\WMI</c> classes and their GUID qualifiers, as the schema layer found them. Empty on
    /// a snapshot restored from the cache, which is why the report says "not read" rather than "none".
    /// </summary>
    public IReadOnlyDictionary<string, string> AcerClassGuids { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public AcerRegistrySnapshot Registry { get; init; } = AcerRegistrySnapshot.Empty;

    /// <summary>Profile records that were dropped by validation, one line each.</summary>
    public IReadOnlyList<string> Rejections { get; init; } = Array.Empty<string>();

    /// <summary>True when this snapshot came out of the settings cache rather than a live probe.</summary>
    public bool FromCache { get; init; }

    public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;

    public HardwareProbeResult? Find(string capability) =>
        Capabilities.FirstOrDefault(c => c.Capability == capability);

    /// <summary>True only when the capability was positively confirmed. An unknown answer is not a yes.</summary>
    public bool IsSupported(string capability) => Find(capability)?.IsSupported == true;

    /// <summary>
    /// True only when the hardware positively said the capability is absent. This is the only answer that may hide a
    /// control: <see cref="HardwareProbeOutcome.AccessDenied"/> and <see cref="HardwareProbeOutcome.Unknown"/> mean the
    /// probe never got to ask, and hiding on those would remove a working feature from an unelevated session.
    /// </summary>
    public bool IsUnsupported(string capability) =>
        Find(capability)?.Outcome == HardwareProbeOutcome.Unsupported;
}

/// <summary>
/// The three-layer capability probe.
/// <list type="number">
/// <item>Schema and registry, unelevated: which Acer WMI classes exist, what they advertise, whether their instances
/// are reachable, and what the Acer agent wrote into <c>HKLM\SOFTWARE\OEM\AcerAgentService</c>.</item>
/// <item>Profile match: SMBIOS identity against the embedded model table and any validated user overrides, which
/// supplies the sensor layout and the fan plausibility ceilings.</item>
/// <item>Cache: the result is stored in the settings file under <c>ModelCode|BiosVersion</c>, so a BIOS update
/// invalidates it automatically and a rescan can be forced from the settings page.</item>
/// </list>
/// <para>
/// Nothing here writes to the hardware, and nothing here can loosen a safety limit: the thermal floors, the PWM
/// range and the school-mode rules are constants in C#, and no profile field maps onto them.
/// </para>
/// </summary>
public static class CapabilityProbe
{
    /// <summary>WMID_GUID5, the battery charge limit interface. Not registered on an AN16-41.</summary>
    public const string BatteryChargeLimitGuid = "79772EC5-04B1-4BFD-843C-61E7F77B6CC9";

    /// <summary>
    /// Bumped when a capability answer changes meaning, which makes every older cache a miss.
    /// <para>
    /// Version 2 added the Acer Care Center source for the battery charge limit. A version 1 cache says "not
    /// supported" on a machine where the limit works, so without this the user would have to find the rescan button
    /// to see the fix — or conclude there was nothing to fix.
    /// </para>
    /// </summary>
    public const int CurrentProbeVersion = 2;

    /// <summary>
    /// Runs all three layers. The schema read is the only part that touches the system; the profile match and the
    /// capability assembly are pure, which is what makes them testable.
    /// </summary>
    public static CapabilitySnapshot Probe(DeviceIdentity identity, ProfileCatalog catalog)
    {
        var schema = SchemaProbeResult.Read();
        var snapshot = Probe(identity, catalog, schema);
        return snapshot with { Rejections = catalog.Rejections };
    }

    /// <summary>Layer 1 and 2 with the schema answer supplied, so tests never touch WMI or the registry.</summary>
    public static CapabilitySnapshot Probe(DeviceIdentity identity, ProfileCatalog catalog, SchemaProbeResult schema)
        => Probe(identity, catalog, schema, wmiMuxSignal: null);

    /// <summary>
    /// Layer 1 and 2, with the elevated <c>GetGamingMiscSetting(0x09)</c> answer supplied when a worker has already
    /// read it. The signal can only ever add MUX support, never remove it: a machine whose registry advertises the
    /// switch keeps it even when the WMI selector answers something this build does not recognise.
    /// </summary>
    public static CapabilitySnapshot Probe(
        DeviceIdentity identity, ProfileCatalog catalog, SchemaProbeResult schema, bool? wmiMuxSignal)
    {
        var profile = catalog.Match(identity);
        var capabilities = new List<HardwareProbeResult>
        {
            DescribeGamingFunction(schema.Wmi),
            DescribeBatteryChargeLimit(schema),
            DescribeMuxSwitch(schema.Registry, wmiMuxSignal),
            DescribeKeyboardZones(schema.Registry),
            DescribeFlag(CapabilitySnapshot.GpuOverclock, schema.Registry.GpuOverclockSupport, "GPU_OC_Support"),
            DescribeFlag(CapabilitySnapshot.PanelOverdrive, schema.Registry.LcdOverdriveSupport, "LCD_Overdrive_support"),
            HardwareProbeResult.Supported(CapabilitySnapshot.ProfileMatch, $"{profile.Id} ({profile.Source})")
        };

        return new CapabilitySnapshot
        {
            CacheKey = CacheKey(identity),
            ProfileId = profile.Id,
            ProfileSource = profile.Source,
            ProfileNotes = profile.Notes,
            Sensors = profile.Sensors,
            MaxCpuFanRpm = profile.MaxCpuFanRpm,
            MaxGpuFanRpm = profile.MaxGpuFanRpm,
            NitroHidKeyboard = profile.NitroHidKeyboard,
            EcMmioProfile = profile.EcMmioProfile,
            KbZoneArm = profile.KbZoneArm,
            WmiMuxSignal = wmiMuxSignal,
            WmiBatteryChargeLimitPresent = HasBatteryChargeLimitClass(schema.AcerClassGuids),
            Capabilities = capabilities,
            Wmi = schema.Wmi,
            AcerClassGuids = schema.AcerClassGuids,
            Registry = schema.Registry,
            Rejections = catalog.Rejections,
            CapturedUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// The cache key: model code plus BIOS version. A BIOS update can add or remove a WMI method, so the cached
    /// answer must not survive one. The model code is used rather than the full SMBIOS model string because the
    /// latter carries marketing text that changes with a firmware relabel.
    /// </summary>
    public static string CacheKey(DeviceIdentity identity)
    {
        var code = string.IsNullOrWhiteSpace(identity.ModelCode) ? identity.Model.Trim() : identity.ModelCode.Trim();
        var bios = identity.BiosVersion?.Trim() ?? "";
        return $"{code}|{bios}";
    }

    /// <summary>
    /// Returns the cached snapshot when it was captured for this exact model and BIOS by this probe version, otherwise
    /// null. A cache with no key, or one written by an older build, is a miss rather than a partial answer.
    /// </summary>
    public static CapabilitySnapshot? TryFromCache(AppSettings settings, DeviceIdentity identity, ProfileCatalog catalog)
    {
        var cache = settings.HardwareCapabilityCache;
        if (cache == null || cache.Capabilities.Count == 0) return null;
        if (cache.ProbeVersion != CurrentProbeVersion) return null;

        var key = CacheKey(identity);
        if (!string.Equals(cache.Key, key, StringComparison.Ordinal)) return null;

        var profile = catalog.Match(identity);
        var capabilities = cache.Capabilities
            .Where(c => !string.IsNullOrWhiteSpace(c.Capability))
            .Select(c => new HardwareProbeResult
            {
                Capability = c.Capability,
                Outcome = Enum.TryParse<HardwareProbeOutcome>(c.Outcome, out var outcome)
                    ? outcome
                    : HardwareProbeOutcome.Unknown,
                Detail = c.Detail
            })
            .ToList();

        return new CapabilitySnapshot
        {
            CacheKey = key,
            ProfileId = profile.Id,
            ProfileSource = profile.Source,
            ProfileNotes = profile.Notes,
            Sensors = profile.Sensors,
            MaxCpuFanRpm = profile.MaxCpuFanRpm,
            MaxGpuFanRpm = profile.MaxGpuFanRpm,
            NitroHidKeyboard = profile.NitroHidKeyboard,
            EcMmioProfile = profile.EcMmioProfile,
            KbZoneArm = profile.KbZoneArm,
            WmiMuxSignal = cache.WmiMuxSignal,
            WmiBatteryChargeLimitPresent = cache.WmiBatteryChargeLimitPresent,
            Capabilities = capabilities,
            Rejections = catalog.Rejections,
            FromCache = true,
            CapturedUtc = cache.CapturedUtc
        };
    }

    /// <summary>Writes the snapshot into the settings object. The caller saves the file.</summary>
    public static void Store(AppSettings settings, CapabilitySnapshot snapshot)
    {
        settings.HardwareCapabilityCache = new HardwareCapabilityCache
        {
            Key = snapshot.CacheKey,
            ProbeVersion = CurrentProbeVersion,
            CapturedUtc = snapshot.CapturedUtc,
            WmiMuxSignal = snapshot.WmiMuxSignal,
            WmiBatteryChargeLimitPresent = snapshot.WmiBatteryChargeLimitPresent,
            Capabilities = snapshot.Capabilities
                .Select(c => new CachedCapability
                {
                    Capability = c.Capability,
                    Outcome = c.Outcome.ToString(),
                    Detail = c.Detail
                })
                .ToList()
        };
    }

    /// <summary>Drops the cache so the next read probes again. Used by the rescan button.</summary>
    public static void Invalidate(AppSettings settings) => settings.HardwareCapabilityCache = null;

    // ------------------------------------------------------------------ capability descriptions

    private static HardwareProbeResult DescribeGamingFunction(AcerWmiProbeResult wmi)
    {
        if (wmi.TimedOut) return HardwareProbeResult.TimedOut(CapabilitySnapshot.WmiGamingFunction, "probe timed out");
        if (!wmi.ClassPresent)
            return HardwareProbeResult.Unsupported(CapabilitySnapshot.WmiGamingFunction, "class not registered");
        if (wmi.InstanceAccessDenied)
            return HardwareProbeResult.AccessDenied(CapabilitySnapshot.WmiGamingFunction,
                "dynamic class, instance needs elevation");
        if (wmi.CallSucceeded)
            return HardwareProbeResult.Supported(CapabilitySnapshot.WmiGamingFunction, wmi.CallMethod);

        return HardwareProbeResult.Failed(CapabilitySnapshot.WmiGamingFunction,
            string.IsNullOrWhiteSpace(wmi.CallError) ? "no method answered" : wmi.CallError);
    }

    /// <summary>
    /// The class registered under <c>WMID_GUID5</c>, or null when no Acer class carries that GUID. The class is found
    /// by GUID rather than by name because the name has shipped under several spellings.
    /// </summary>
    private static string? FindBatteryChargeLimitClass(IReadOnlyDictionary<string, string> guids)
    {
        var wanted = AcerWmiProbe.NormalizeGuid(BatteryChargeLimitGuid);
        foreach (var (name, guid) in guids)
        {
            if (AcerWmiProbe.NormalizeGuid(guid) == wanted) return name;
        }

        return null;
    }

    /// <summary>
    /// True when the WMI battery interface is registered, false when the enumeration succeeded and it is not, null when
    /// the enumeration itself failed. The three answers matter: only a positive "no" may stop the WMI backend from
    /// trying, and an empty map is not evidence of absence.
    /// </summary>
    private static bool? HasBatteryChargeLimitClass(IReadOnlyDictionary<string, string> guids) =>
        guids.Count == 0 ? null : FindBatteryChargeLimitClass(guids) != null;

    /// <summary>
    /// The 80 % charge limit, from whichever interface this machine actually has. <c>WMID_GUID5</c> is the WMI one;
    /// Acer Care Center is the other, and on an AN16-41 it is the only one. Reporting the second as "not supported"
    /// hid a working feature from the walkthrough and from the recommended profile.
    /// </summary>
    private static HardwareProbeResult DescribeBatteryChargeLimit(SchemaProbeResult schema)
    {
        if (FindBatteryChargeLimitClass(schema.AcerClassGuids) is { } name)
            return HardwareProbeResult.Supported(CapabilitySnapshot.WmiBatteryChargeLimit, name);

        if (schema.CareCenterAvailable)
            return HardwareProbeResult.Supported(CapabilitySnapshot.WmiBatteryChargeLimit,
                string.IsNullOrWhiteSpace(schema.CareCenterDetail)
                    ? $"Acer Care Center ({CareCenterProbe.ServiceName})"
                    : schema.CareCenterDetail);

        // An empty map means the enumeration itself failed, which is not evidence that the interface is missing.
        if (schema.AcerClassGuids.Count == 0)
            return HardwareProbeResult.Unknown(CapabilitySnapshot.WmiBatteryChargeLimit, "no Acer class enumerated");

        return HardwareProbeResult.Unsupported(CapabilitySnapshot.WmiBatteryChargeLimit,
            $"WMID_GUID5 not registered ({schema.AcerClassGuids.Count} Acer classes)");
    }

    private static HardwareProbeResult DescribeMuxSwitch(AcerRegistrySnapshot registry, bool? wmiMuxSignal)
    {
        // The WMI selector is the stronger signal: it is the firmware answering about itself, where the registry is
        // the Acer agent's opinion. It can only add support, never remove it, because a selector this build does not
        // recognise is not evidence that the switch is gone.
        if (wmiMuxSignal == true)
            return HardwareProbeResult.Supported(CapabilitySnapshot.MuxSwitch, "GetGamingMiscSetting(0x09)=3");

        if (registry.DgpuModeCapability is not int mask)
        {
            return wmiMuxSignal == false
                ? HardwareProbeResult.Unsupported(CapabilitySnapshot.MuxSwitch,
                    "GetGamingMiscSetting(0x09) is not 3 and dgpu_mode_capability is not readable")
                : HardwareProbeResult.Unknown(CapabilitySnapshot.MuxSwitch, "dgpu_mode_capability not readable");
        }

        if (mask <= 0) return HardwareProbeResult.Unsupported(CapabilitySnapshot.MuxSwitch, "dgpu_mode_capability=0");

        var modes = registry.SupportedGpuModes;
        var detail = modes.Count > 0
            ? $"dgpu_mode_capability={mask} ({string.Join(", ", modes)})"
            : $"dgpu_mode_capability={mask}";
        return HardwareProbeResult.Supported(CapabilitySnapshot.MuxSwitch, detail);
    }

    private static HardwareProbeResult DescribeKeyboardZones(AcerRegistrySnapshot registry)
    {
        if (registry.KeyboardArea is not int zones)
            return HardwareProbeResult.Unknown(CapabilitySnapshot.KeyboardZones, "KeyBoardArea not readable");

        return zones > 0
            ? HardwareProbeResult.Supported(CapabilitySnapshot.KeyboardZones, $"{zones} zones")
            : HardwareProbeResult.Unsupported(CapabilitySnapshot.KeyboardZones, "KeyBoardArea=0");
    }

    private static HardwareProbeResult DescribeFlag(string capability, int? value, string valueName)
    {
        if (value is not int flag) return HardwareProbeResult.Unknown(capability, $"{valueName} not readable");
        return flag > 0
            ? HardwareProbeResult.Supported(capability, $"{valueName}={flag}")
            : HardwareProbeResult.Unsupported(capability, $"{valueName}=0");
    }
}
