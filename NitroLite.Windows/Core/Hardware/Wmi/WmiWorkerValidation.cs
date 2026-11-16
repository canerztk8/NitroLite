namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// The worker's own argument checks, applied before anything reaches WMI.
/// <para>
/// These duplicate the checks the window already makes, and that duplication is deliberate. The worker is the only
/// process holding an elevated token, and it accepts commands over a pipe: whatever the caller believes it sent, the
/// worker has to decide for itself whether the value is safe. A fan percentage of 15 is not a request the firmware
/// should ever see, no matter which process asked for it.
/// </para>
/// <para>
/// The floor is 30 %, not 0 %. Below roughly a third the fans on these chassis stall, and a stalled fan under load is
/// how a laptop cooks itself. Zero is accepted only as "hand the fan back to the firmware", which is a different
/// operation from "spin at 0 %".
/// </para>
/// </summary>
public static class WmiWorkerValidation
{
    /// <summary>
    /// True when <paramref name="percent"/> is a value the firmware can be given: 0 to release the fan, or
    /// <see cref="AcerWmiProtocol.MinFanPercent"/>..<see cref="AcerWmiProtocol.MaxFanPercent"/> to drive it.
    /// </summary>
    public static bool IsValidFanPercent(int percent) =>
        percent == 0 || percent is >= AcerWmiProtocol.MinFanPercent and <= AcerWmiProtocol.MaxFanPercent;

    /// <summary>True when the value names a real <see cref="FanMode"/>.</summary>
    public static bool TryParseFanMode(int value, out FanMode mode)
    {
        if (Enum.IsDefined(typeof(FanMode), value))
        {
            mode = (FanMode)value;
            return true;
        }

        mode = FanMode.Auto;
        return false;
    }

    /// <summary>
    /// True when the value names a real <see cref="OperatingMode"/>. The enum has gaps — 2 and 3 are not modes — so
    /// this is a defined-value check rather than a range check.
    /// </summary>
    public static bool TryParseOperatingMode(int value, out OperatingMode mode)
    {
        if (Enum.IsDefined(typeof(OperatingMode), value))
        {
            mode = (OperatingMode)value;
            return true;
        }

        mode = OperatingMode.Default;
        return false;
    }

    /// <summary>
    /// True when the value names a switchable <see cref="GpuOperatingMode"/>. <c>Automatic</c> is rejected: it is a
    /// firmware decision (Advanced Optimus), not something a caller can ask for.
    /// </summary>
    public static bool TryParseMuxMode(int value, out GpuOperatingMode mode)
    {
        if (value is (int)GpuOperatingMode.Optimus or (int)GpuOperatingMode.DiscreteGpu)
        {
            mode = (GpuOperatingMode)value;
            return true;
        }

        mode = GpuOperatingMode.Optimus;
        return false;
    }

    /// <summary>True when the value names a real <see cref="BatteryChargeMode"/>.</summary>
    public static bool TryParseBatteryMode(int value, out BatteryChargeMode mode)
    {
        if (Enum.IsDefined(typeof(BatteryChargeMode), value))
        {
            mode = (BatteryChargeMode)value;
            return true;
        }

        mode = BatteryChargeMode.Optimized80;
        return false;
    }

    /// <summary>
    /// True when the value is a usable keyboard zone mask: at least one zone, and no bit outside the four zones.
    /// <para>
    /// Zero is rejected rather than treated as "all zones". The firmware answers <c>gmOutput = 0x01</c> to a mask of
    /// zero and applies nothing, so accepting it here would turn a caller's mistake into a silent no-op that looks
    /// like a successful write.
    /// </para>
    /// </summary>
    public static bool IsValidLedZoneMask(int mask) => mask is >= 1 and <= AcerWmiProtocol.LedZoneAll;

    /// <summary>True when the value fits in one colour channel.</summary>
    public static bool IsValidColorChannel(int value) => value is >= 0 and <= 255;

    /// <summary>True when the value is a brightness percentage the firmware accepts.</summary>
    public static bool IsValidBrightnessPercent(int value) => value is >= 0 and <= 100;

    /// <summary>True when the value is a packed <c>0xRRGGBB</c> colour.</summary>
    public static bool IsValidPackedColor(int value) => value is >= 0 and <= 0xFFFFFF;

    /// <summary>True when the value is an effect speed the firmware accepts (1..9).</summary>
    public static bool IsValidEffectSpeed(int value) => value is >= 1 and <= AcerWmiProtocol.MaxEffectSpeed;

    /// <summary>True when the value is an effect direction the firmware accepts (1..4).</summary>
    public static bool IsValidEffectDirection(int value) => value is >= 1 and <= AcerWmiProtocol.MaxEffectDirection;
}
