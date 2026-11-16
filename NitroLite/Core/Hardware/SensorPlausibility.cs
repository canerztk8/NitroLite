namespace NitroLite.Core.Hardware;

/// <summary>
/// The window a firmware reading has to fall inside before NitroLite treats it as a measurement.
/// <para>
/// This exists because <c>GetGamingSysInfo</c> answers a status byte and a value, and the status byte is not always
/// honest: a firmware that does not know a sensor id sometimes answers status zero with a sentinel in the value
/// field. Publishing that sentinel would show a 6553.5 °C die or a 65535 RPM fan, and — worse — would let the fan
/// stall watchdog compare a real temperature against a number that was never a speed.
/// </para>
/// <para>
/// Two different questions are answered here, and they deliberately disagree about zero:
/// </para>
/// <list type="bullet">
/// <item><see cref="IsPlausibleFanRpm"/> accepts zero. A stopped fan is a real, alarming reading, and the stall
/// watchdog exists precisely to notice it. Rejecting zero would blind the one guard that matters.</item>
/// <item><see cref="IsPresentFanRpm"/> rejects zero. During discovery a zero answer is indistinguishable from "this
/// sensor id does not exist", so it is not evidence that the tachometer is there.</item>
/// </list>
/// </summary>
public static class SensorPlausibility
{
    /// <summary>Coldest reading a running die can report. Zero is a sentinel, not a temperature.</summary>
    public const int MinTemperatureCelsius = 1;

    /// <summary>Hottest reading accepted. Above this the value is a decode error, not a die temperature.</summary>
    public const int MaxTemperatureCelsius = 130;

    /// <summary>Lowest fan speed accepted as a measurement. Zero is allowed here: a stopped fan is a real reading.</summary>
    public const int MinFanRpm = 0;

    /// <summary>Highest fan speed accepted. No laptop fan in this class reaches it, so a value above is a decode error.</summary>
    public const int MaxFanRpm = 12000;

    /// <summary>
    /// Values a firmware uses to mean "no data". They are listed explicitly rather than left to the range check so
    /// the intent is visible: a future range widening must not quietly start accepting them.
    /// </summary>
    private static readonly int[] Sentinels =
    {
        0xFFFF, // 65535, the classic "unset unsigned word"
        0x7FFF, // 32767, INT16_MAX used as "invalid"
        0x8000, // 32768, INT16_MIN used as "invalid"
        0xDEAD, // 57005, the joke constant several EC firmwares actually use
        0xBEEF  // 48879, its partner
    };

    /// <summary>True when the value is one of the documented "no data" constants.</summary>
    public static bool IsSentinel(int value) => Array.IndexOf(Sentinels, value) >= 0;

    /// <summary>True when a temperature reading can be shown: 1–130 °C and not a sentinel.</summary>
    public static bool IsPlausibleTemperature(int value) =>
        !IsSentinel(value) && value is >= MinTemperatureCelsius and <= MaxTemperatureCelsius;

    /// <summary>
    /// True when a fan speed can be shown as a measurement: 0–12000 RPM and not a sentinel. Zero passes on purpose;
    /// see the type remarks.
    /// </summary>
    public static bool IsPlausibleFanRpm(int value) =>
        !IsSentinel(value) && value is >= MinFanRpm and <= MaxFanRpm;

    /// <summary>
    /// True when a fan speed is evidence that the tachometer exists at all: 1–12000 RPM. Used only by discovery,
    /// where a zero answer cannot be told apart from a missing sensor.
    /// </summary>
    public static bool IsPresentFanRpm(int value) =>
        !IsSentinel(value) && value is >= 1 and <= MaxFanRpm;

    /// <summary>One line for the hardware report, so a reader knows which window was applied.</summary>
    public static string Describe() =>
        $"temperature {MinTemperatureCelsius}..{MaxTemperatureCelsius} °C, fan {MinFanRpm}..{MaxFanRpm} RPM, "
        + $"{Sentinels.Length} sentinels rejected";
}
