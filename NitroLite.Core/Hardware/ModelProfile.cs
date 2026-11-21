using NitroLite.Core.Hardware.Wmi;

namespace NitroLite.Core.Hardware;

/// <summary>
/// Which firmware sensor id answers each reading, for <c>GetGamingSysInfo</c> (WMID_Gaming method 5).
/// <para>
/// <c>GetGamingSysInfo</c> is not a packed page. Each call names one sensor in the high byte of the input
/// (<c>1 | (sensor &lt;&lt; 8)</c>) and answers one value in bits 23:8 with a status byte in bits 7:0. The ids below
/// are the firmware's own enum, the same one the Linux <c>acer-wmi</c> driver defines as
/// <c>acer_wmi_predator_v4_sensor_id</c>, so they are the same on every model that answers the call at all.
/// </para>
/// <para>
/// The ids are still per-profile data rather than constants: a model whose firmware numbers its sensors differently
/// is then a one-line data change, and a profile that has not been measured can leave <see cref="Supported"/> false
/// instead of decoding a number that is not a temperature.
/// </para>
/// </summary>
public sealed record SensorLayout
{
    public int CpuTempSensor { get; init; } = AcerSensorIds.CpuTemperature;
    public int CpuFanRpmSensor { get; init; } = AcerSensorIds.CpuFanSpeed;
    public int Sys1TempSensor { get; init; } = AcerSensorIds.SystemTemperature;
    public int GpuFanRpmSensor { get; init; } = AcerSensorIds.GpuFanSpeed;
    public int GpuTempSensor { get; init; } = AcerSensorIds.GpuTemperature;

    /// <summary>True only when the ids were confirmed against a real read-back on this model.</summary>
    public bool Verified { get; init; }

    /// <summary>
    /// False when this model's firmware does not answer per-sensor queries, so the ids above describe nothing and
    /// must not be decoded. The status byte is the runtime gate either way: a sensor that answers non-zero is
    /// discarded even on a supported profile, and the UI renders the missing reading as "-- °C".
    /// </summary>
    public bool Supported { get; init; } = true;

    /// <summary>
    /// The layout used when nothing better is known. <see cref="Supported"/> is false because no shipped id has been
    /// confirmed on a machine the table does not name; a profile that has been measured sets it back to true.
    /// </summary>
    public static SensorLayout Default { get; } = new() { Supported = false, Verified = false };

    /// <summary>
    /// A layout is usable when every id fits the byte the firmware reads it from. A file that fails this is rejected
    /// rather than clamped: a silently shifted id would report the GPU temperature as the CPU temperature, which is
    /// worse than reporting nothing.
    /// </summary>
    public bool IsValid =>
        CpuTempSensor is >= 0 and <= 255 &&
        CpuFanRpmSensor is >= 0 and <= 255 &&
        Sys1TempSensor is >= 0 and <= 255 &&
        GpuFanRpmSensor is >= 0 and <= 255 &&
        GpuTempSensor is >= 0 and <= 255;
}

/// <summary>
/// One model's hardware facts, as opposed to its capabilities. Capabilities are answered by the machine itself
/// (the service, the WMI schema, the registry); this record only carries the things a machine cannot tell us about
/// itself: which sensor layout its firmware uses, how fast its fans can spin, and which WMI methods are known to
/// fail on it.
/// <para>
/// Nothing here can loosen a safety limit. The 85/90/95 °C fan floors, the 30–100 % PWM range and the school-mode
/// rules are constants in C#; a profile that tried to change them would have no field to write them into.
/// </para>
/// </summary>
public sealed record ModelProfile
{
    public string MatchBasis { get; init; } = "none";
    /// <summary>Stable identifier, e.g. "nitro-16-an16-41". Shown in the hardware report.</summary>
    public string Id { get; init; } = "";

    /// <summary>SMBIOS model codes this profile claims, e.g. "AN16-41". Matched case-insensitively.</summary>
    public IReadOnlyList<string> ModelCodes { get; init; } = Array.Empty<string>();

    /// <summary>Baseboard product patterns. Usually an internal codename, so this is a fallback only.</summary>
    public IReadOnlyList<string> BoardPatterns { get; init; } = Array.Empty<string>();

    /// <summary>Marketing family patterns, e.g. "Nitro 16".</summary>
    public IReadOnlyList<string> FamilyPatterns { get; init; } = Array.Empty<string>();

    /// <summary>True for the catch-all profile that claims any Acer machine.</summary>
    public bool AnyAcer { get; init; }

    public SensorLayout Sensors { get; init; } = SensorLayout.Default;

    /// <summary>Highest plausible CPU fan speed. A decoded value above this is a decode error, not a real speed.</summary>
    public int MaxCpuFanRpm { get; init; } = 7000;

    /// <summary>Highest plausible GPU fan speed.</summary>
    public int MaxGpuFanRpm { get; init; } = 7000;

    /// <summary>
    /// True when the model is known to have a MUX switch, false when it is known not to, null when the table does
    /// not say. A null never hides a control: the machine's own answer wins, and this is only a tie-breaker for the
    /// report.
    /// </summary>
    public bool? MuxSwitch { get; init; }

    /// <summary>Same contract as <see cref="MuxSwitch"/>, for the four-zone keyboard.</summary>
    public bool? FourZoneKeyboard { get; init; }

    /// <summary>
    /// True when this model's keyboard is driven over USB HID and the WMI RGB methods are silently ignored. The
    /// firmware accepts <c>SetGamingRgbKb</c> and answers success, then changes nothing, so a caller that trusts the
    /// status byte reports a write that never happened. The lighting path checks this flag and falls through to the
    /// Acer service or OpenRGB instead.
    /// </summary>
    public bool NitroHidKeyboard { get; init; }

    /// <summary>
    /// True when this model's firmware drives the operating mode through EC MMIO rather than through
    /// <c>SetGamingMiscSetting(0x0B)</c>. Writing the WMI selector on such a machine is accepted and then ignored, so
    /// the mode write reports not-supported instead of claiming a change that never happened. The Windows power plan
    /// and CPU boost path still works, which is what the UI falls back to.
    /// </summary>
    public bool EcMmioProfile { get; init; }

    /// <summary>
    /// True when this model's keyboard LED zones stay disabled until <c>SetGamingLED</c> arms them. The arm step runs
    /// on every model anyway and a refusal is tolerated, so this flag only decides whether a refusal is worth
    /// logging as a warning.
    /// </summary>
    public bool KbZoneArm { get; init; }

    /// <summary>
    /// WMI methods known to fail on this model. Empty for every profile shipped today: no report has proven a
    /// method broken on a specific model yet, and guessing would disable a working feature. The mechanism exists so
    /// the first such report is a one-line data change instead of a code change.
    /// </summary>
    public IReadOnlyList<string> UnsupportedMethods { get; init; } = Array.Empty<string>();

    /// <summary>Free-form note for whoever reads the report. Never interpreted.</summary>
    public string Notes { get; init; } = "";

    /// <summary>"embedded" or "user". A user profile is reported as such so a bug report says where it came from.</summary>
    public string Source { get; init; } = "embedded";

    /// <summary>
    /// True when this profile claims the machine. A model-code hit wins outright; the board and family patterns are
    /// only consulted when no profile matched on the code, which is why the caller tries them in that order.
    /// </summary>
    public bool Matches(DeviceIdentity identity)
    {
        if (AnyAcer) return identity.IsAcer || !string.IsNullOrWhiteSpace(identity.ModelCode);

        var code = identity.ModelCode?.Trim() ?? "";
        if (code.Length > 0 && ModelCodes.Any(c => c.Equals(code, StringComparison.OrdinalIgnoreCase)))
            return true;

        var board = identity.BoardProduct?.Trim() ?? "";
        if (board.Length > 0 && BoardPatterns.Any(p => Contains(board, p))) return true;

        var family = identity.ModelFamily?.Trim() ?? "";
        if (family.Length > 0 && FamilyPatterns.Any(p => Contains(family, p))) return true;

        return false;
    }

    /// <summary>True when this profile claims the machine on the model code alone.</summary>
    public bool MatchesModelCode(DeviceIdentity identity)
    {
        var code = identity.ModelCode?.Trim() ?? "";
        return code.Length > 0 && ModelCodes.Any(c => c.Equals(code, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrWhiteSpace(needle) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
