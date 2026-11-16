namespace NitroLite.Core;

/// <summary>
/// The battery's own numbers: what the pack reports about its capacity, its wear and the power flowing through it.
/// </summary>
/// <remarks>
/// <para>
/// One reading rather than three calls, because the page shows the level, the flow and the capacity together and a
/// figure from three different moments would not add up. <see cref="Status"/> is the live half (level, plug state,
/// watts), <see cref="Details"/> the slow half (capacities, cycles, chemistry).
/// </para>
/// <para>
/// <see cref="Details"/> is null when the platform cannot read capacities at all. That is different from a pack that
/// reports zero: the page prints "--" for a missing reading and never invents one.
/// </para>
/// </remarks>
public sealed class BatteryHealthSnapshot
{
    /// <summary>The live half: level, plug state, charge and discharge watts.</summary>
    public BatteryStatusInfo Status { get; init; } = new();

    /// <summary>The capacity half, or null when this platform cannot read it.</summary>
    public BatteryHealthDetails? Details { get; init; }

    /// <summary>True when the pack is above the 80 % threshold the watchdog watches.</summary>
    public bool IsOvercharged => Status.IsOvercharged;

    /// <summary>A snapshot for a machine with no battery at all.</summary>
    public static BatteryHealthSnapshot Empty { get; } = new();
}

/// <summary>
/// Reads the battery and keeps the wear history.
/// </summary>
/// <remarks>
/// <para>
/// The wear history lives behind this interface rather than in the view model because it is a file on disk with a
/// retention rule, and the rule belongs next to the readings it filters. The view model only asks for the trend and
/// draws it.
/// </para>
/// <para>
/// Linux answers through <c>/sys/class/power_supply/BAT*</c>, which carries the level, the flow and — on most
/// machines — the design and full-charge capacities. A machine with no battery node reports
/// <see cref="IsSupported"/> false and the page hides its battery cards instead of drawing zeros.
/// </para>
/// </remarks>
public interface IBatteryHealthService
{
    /// <summary>True when this machine has a battery this platform can read.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// The 80 % watchdog. Setting it does not write to the firmware; it only decides whether the app raises the
    /// overcharge banner. The charge limit itself is a hardware capability.
    /// </summary>
    bool WatchdogEnabled { get; set; }

    /// <summary>Reads the live status and, when the platform can, the capacity details. Never throws.</summary>
    Task<BatteryHealthSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Every recorded wear reading, oldest first.</summary>
    IReadOnlyList<BatteryWearSample> GetWearSamples();

    /// <summary>What the recorded readings say about the rate of wear.</summary>
    BatteryWearTrend GetWearTrend();

    /// <summary>
    /// Appends a reading when it carries new information. Returns true when the history file was written.
    /// </summary>
    bool RecordWearSample(BatteryHealthDetails details);

    /// <summary>Forgets the whole history. The caller asks the user first.</summary>
    bool ClearWearHistory();
}
