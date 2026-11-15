namespace NitroLite.Core.Hardware;

/// <summary>
/// Where each sensor sits inside the 64-bit answer of <c>GetGamingSysInfo</c> (WMID_Gaming method 5).
/// <para>
/// The <c>*Byte</c> members are byte offsets into that value; the <c>*Word</c> members are 16-bit word indices.
/// They overlap on purpose — a packed structure can carry a byte field and a word field at the same position — so
/// an overlap is not a bug in the table.
/// </para>
/// <para>
/// <see cref="Verified"/> is false for every layout shipped today. The packing below is the one NitroSense appears
/// to use, but it has not been read back from hardware: the class is dynamic and instance access needs elevation,
/// which the read-only probe does not have. Nothing decodes a sensor through this table yet, and nothing
/// safety-critical ever will — the thermal floors live in C# and are not configurable.
/// </para>
/// </summary>
public sealed record SensorLayout
{
    public int CpuTempByte { get; init; }
    public int GpuTempByte { get; init; } = 1;
    public int Sys1TempByte { get; init; } = 2;
    public int CpuFanRpmWord { get; init; } = 2;
    public int GpuFanRpmWord { get; init; } = 3;

    /// <summary>Divisor that turns the raw word into RPM. 100 on every model seen so far.</summary>
    public int RpmScale { get; init; } = 100;

    /// <summary>True only when the offsets were confirmed against a real read-back on this model.</summary>
    public bool Verified { get; init; }

    public static SensorLayout Default { get; } = new();

    /// <summary>
    /// A layout is usable when every offset lands inside the 64-bit answer and the scale cannot divide by zero.
    /// A file that fails this is rejected rather than clamped: a silently shifted offset would report the GPU
    /// temperature as the CPU temperature, which is worse than reporting nothing.
    /// </summary>
    public bool IsValid =>
        CpuTempByte is >= 0 and <= 7 &&
        GpuTempByte is >= 0 and <= 7 &&
        Sys1TempByte is >= 0 and <= 7 &&
        CpuFanRpmWord is >= 0 and <= 3 &&
        GpuFanRpmWord is >= 0 and <= 3 &&
        RpmScale is >= 1 and <= 1000;
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
