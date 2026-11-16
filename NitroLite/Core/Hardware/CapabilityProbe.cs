namespace NitroLite.Core.Hardware;

/// <summary>
/// Layer 1 of the capability probe: what can be learned without elevation and without the Acer service.
/// <para>
/// Three sources, all read-only: the <c>root\WMI</c> schema (which Acer classes exist, what they advertise, whether
/// their instances are reachable), the GUID qualifier of each Acer class (which is how the battery charge limit
/// interface is identified), and the Acer agent's registry key (the MUX bitmask and the keyboard zone count).
/// </para>
/// </summary>
public sealed record SchemaProbeResult
{
    public AcerWmiProbeResult Wmi { get; init; } = new();

    /// <summary>Acer-prefixed <c>root\WMI</c> classes and their GUID qualifiers.</summary>
    public IReadOnlyDictionary<string, string> AcerClassGuids { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public AcerRegistrySnapshot Registry { get; init; } = AcerRegistrySnapshot.Empty;

    /// <summary>Reads all three sources. Never throws; a source that fails simply contributes nothing.</summary>
    public static SchemaProbeResult Read()
    {
        var wmi = AcerWmiProbe.ReadWithTimeout(TimeSpan.FromSeconds(5));
        return new SchemaProbeResult
        {
            Wmi = wmi,
            AcerClassGuids = AcerWmiProbe.ReadAcerClassGuids(),
            Registry = AcerRegistrySnapshot.Read()
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

    public IReadOnlyList<HardwareProbeResult> Capabilities { get; init; } = Array.Empty<HardwareProbeResult>();

    /// <summary>The WMI schema answer, kept so the hardware report does not have to probe twice.</summary>
    public AcerWmiProbeResult Wmi { get; init; } = new();

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
    {
        var profile = catalog.Match(identity);
        var capabilities = new List<HardwareProbeResult>
        {
            DescribeGamingFunction(schema.Wmi),
            DescribeBatteryChargeLimit(schema),
            DescribeMuxSwitch(schema.Registry),
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
            Capabilities = capabilities,
            Wmi = schema.Wmi,
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
    /// Returns the cached snapshot when it was captured for this exact model and BIOS, otherwise null. A cache with
    /// no key, or one written by an older build, is a miss rather than a partial answer.
    /// </summary>
    public static CapabilitySnapshot? TryFromCache(AppSettings settings, DeviceIdentity identity, ProfileCatalog catalog)
    {
        var cache = settings.HardwareCapabilityCache;
        if (cache == null || cache.Capabilities.Count == 0) return null;

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
            CapturedUtc = snapshot.CapturedUtc,
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

    private static HardwareProbeResult DescribeBatteryChargeLimit(SchemaProbeResult schema)
    {
        var wanted = AcerWmiProbe.NormalizeGuid(BatteryChargeLimitGuid);
        foreach (var (name, guid) in schema.AcerClassGuids)
        {
            if (AcerWmiProbe.NormalizeGuid(guid) == wanted)
                return HardwareProbeResult.Supported(CapabilitySnapshot.WmiBatteryChargeLimit, name);
        }

        // An empty map means the enumeration itself failed, which is not evidence that the interface is missing.
        if (schema.AcerClassGuids.Count == 0)
            return HardwareProbeResult.Unknown(CapabilitySnapshot.WmiBatteryChargeLimit, "no Acer class enumerated");

        return HardwareProbeResult.Unsupported(CapabilitySnapshot.WmiBatteryChargeLimit,
            $"WMID_GUID5 not registered ({schema.AcerClassGuids.Count} Acer classes)");
    }

    private static HardwareProbeResult DescribeMuxSwitch(AcerRegistrySnapshot registry)
    {
        if (registry.DgpuModeCapability is not int mask)
            return HardwareProbeResult.Unknown(CapabilitySnapshot.MuxSwitch, "dgpu_mode_capability not readable");

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
