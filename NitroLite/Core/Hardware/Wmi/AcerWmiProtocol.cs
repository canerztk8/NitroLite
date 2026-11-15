namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// The WMID_Gaming wire format, as pure functions.
/// <para>
/// Every value NitroLite sends to the embedded controller is a 64-bit integer built out of shifted fields, and every
/// answer packs a value and a status byte into the same integer. Getting one shift wrong writes a fan speed into a
/// power-mode register, so the encoding lives here as arithmetic with no WMI, no pipe and no hardware: it can be
/// pinned by tests that run anywhere.
/// </para>
/// <para>
/// The formulas are the ones NitroSense uses and the ones the Linux <c>acer-wmi</c> driver documents. They are
/// verified against this machine where a read-back was possible: <c>GetGamingFanSpeed(0x01)</c> returned
/// <c>0x2200</c> (34 %, status 0) and <c>GetGamingMiscSetting(0x0B)</c> returned <c>0x0100</c> (mode 1, status 0).
/// </para>
/// </summary>
public static class AcerWmiProtocol
{
    // ---------------------------------------------------------------- fan speed (methods 16 / 17)

    /// <summary>Fan id for the CPU fan in <c>SetGamingFanSpeed</c> / <c>GetGamingFanSpeed</c>.</summary>
    public const byte FanIdCpu = 0x01;

    /// <summary>Fan id for the GPU fan in the same pair of methods.</summary>
    public const byte FanIdGpu = 0x04;

    // ---------------------------------------------------------------- fan behaviour (methods 14 / 15)

    /// <summary>Firmware value for "let the EC decide".</summary>
    public const byte FanBehaviorAuto = 1;

    /// <summary>Firmware value for the maximum-speed behaviour.</summary>
    public const byte FanBehaviorTurbo = 2;

    /// <summary>Firmware value for a fixed speed written by <c>SetGamingFanSpeed</c>.</summary>
    public const byte FanBehaviorCustom = 3;

    /// <summary>Fan bitmap bit for the CPU fan.</summary>
    public const byte FanBitmapCpu = 0x01;

    /// <summary>Fan bitmap bit for the GPU fan.</summary>
    public const byte FanBitmapGpu = 0x04;

    /// <summary>
    /// Fan bitmap for "both fans" in the behaviour call. Deliberately a separate constant rather than
    /// <c>FanBitmapCpu | FanBitmapGpu</c>: the reference encoding for both fans is <c>0x09</c>, which is not the OR
    /// of the two single-fan bits. Computing it would send <c>0x05</c> and address a different set of fans, so the
    /// value is written down instead of derived.
    /// </summary>
    public const byte FanBitmapBoth = 0x09;

    // ---------------------------------------------------------------- misc settings (methods 22 / 23)

    /// <summary>Misc index for the MUX / GPU mode setting.</summary>
    public const byte MiscIndexMux = 0x02;

    /// <summary>Misc index for the platform (power) profile.</summary>
    public const byte MiscIndexPlatformProfile = 0x0B;

    /// <summary>Status byte the firmware returns when it accepted the call.</summary>
    public const byte StatusOk = 0x00;

    /// <summary>Lowest fan percentage the firmware accepts for an active write.</summary>
    public const int MinFanPercent = 30;

    /// <summary>Highest fan percentage.</summary>
    public const int MaxFanPercent = 100;

    // ---------------------------------------------------------------- fan speed

    /// <summary>
    /// <c>(percent &lt;&lt; 8) | fanId</c>. 60 % on the CPU fan is <c>0x3C01</c>, on the GPU fan <c>0x3C04</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The percentage is outside 0..100.</exception>
    public static ulong EncodeFanSpeed(byte fanId, int percent)
    {
        if (percent is < 0 or > MaxFanPercent)
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "fan percentage must be 0..100");
        return ((ulong)(uint)percent << 8) | fanId;
    }

    /// <summary>The percentage out of a <c>GetGamingFanSpeed</c> answer: <c>(output &gt;&gt; 8) &amp; 0xFF</c>.</summary>
    public static int DecodeFanPercent(ulong output) => (int)((output >> 8) & 0xFF);

    /// <summary>The status byte out of any answer: the low byte. <see cref="StatusOk"/> means accepted.</summary>
    public static byte DecodeStatus(ulong output) => (byte)(output & 0xFF);

    // ---------------------------------------------------------------- fan behaviour

    /// <summary>
    /// <c>fanBitmap | (cpuMode &lt;&lt; 16) | (gpuMode &lt;&lt; 22)</c>. Both fans on Custom is <c>0x00C30009</c>.
    /// </summary>
    public static ulong EncodeFanBehavior(byte fanBitmap, FanMode cpu, FanMode gpu) =>
        fanBitmap | ((ulong)FanBehaviorValue(cpu) << 16) | ((ulong)FanBehaviorValue(gpu) << 22);

    /// <summary>Splits a <c>GetGamingFanBehavior</c> answer back into its three fields.</summary>
    public static (byte Bitmap, FanMode Cpu, FanMode Gpu) DecodeFanBehavior(ulong output) =>
        ((byte)(output & 0xFF),
         FanModeFromBehavior((byte)((output >> 16) & 0x3F)) ?? FanMode.Auto,
         FanModeFromBehavior((byte)((output >> 22) & 0x3F)) ?? FanMode.Auto);

    /// <summary>Maps the app's fan mode onto the firmware's behaviour value.</summary>
    public static byte FanBehaviorValue(FanMode mode) => mode switch
    {
        FanMode.Max => FanBehaviorTurbo,
        FanMode.Custom => FanBehaviorCustom,
        _ => FanBehaviorAuto
    };

    /// <summary>Maps a firmware behaviour value back onto the app's fan mode. Null when the value is not one of the three.</summary>
    public static FanMode? FanModeFromBehavior(byte value) => value switch
    {
        FanBehaviorAuto => FanMode.Auto,
        FanBehaviorTurbo => FanMode.Max,
        FanBehaviorCustom => FanMode.Custom,
        _ => null
    };

    // ---------------------------------------------------------------- operating mode

    /// <summary><c>(mode &lt;&lt; 8) | 0x0B</c>. Performance (Extreme) is <c>0x040B</c>.</summary>
    public static ulong EncodeOperatingMode(OperatingMode mode) =>
        ((ulong)(byte)mode << 8) | MiscIndexPlatformProfile;

    /// <summary>The mode out of a <c>GetGamingMiscSetting(0x0B)</c> answer. Null when the value is not a known mode.</summary>
    public static OperatingMode? DecodeOperatingMode(ulong output)
    {
        var raw = (OperatingMode)(byte)((output >> 8) & 0xFF);
        return Enum.IsDefined(raw) ? raw : null;
    }

    // ---------------------------------------------------------------- MUX

    /// <summary>
    /// <c>((mode + 1) &lt;&lt; 8) | 0x02</c>. The firmware numbers the GPU modes from one, so Optimus (0) is written as
    /// 1 and comes out as <c>0x0102</c>; discrete is <c>0x0202</c>.
    /// <para>
    /// The migration brief listed <c>0x0202</c> as the Optimus value and <c>0x0302</c> as the discrete one, which is
    /// what the same formula produces if the already-one-based mode number is passed in. The formula is the part
    /// that is self-consistent with <see cref="DecodeMuxMode"/> and with the capability bitmask, so it is the part
    /// that is implemented here. PR 4 settles it empirically: reading <c>GetGamingMiscSetting(0x02)</c> on a machine
    /// sitting in hybrid mode returns either <c>0x0102</c> or <c>0x0202</c>, and the read-back check will refuse to
    /// write if the encoder disagrees with what the firmware reports.
    /// </para>
    /// </summary>
    public static ulong EncodeMuxMode(GpuOperatingMode mode) =>
        ((ulong)((byte)mode + 1) << 8) | MiscIndexMux;

    /// <summary>The GPU mode out of a <c>GetGamingMiscSetting(0x02)</c> answer. Null when the value is out of range.</summary>
    public static GpuOperatingMode? DecodeMuxMode(ulong output)
    {
        int raw = (int)((output >> 8) & 0xFF) - 1;
        if (raw < 0 || raw > (int)GpuOperatingMode.Automatic) return null;
        return (GpuOperatingMode)raw;
    }

    // ---------------------------------------------------------------- raw dump

    /// <summary>
    /// The 64-bit answer as eight bytes, little-endian. The sensor layout in <c>profiles.json</c> is expressed as
    /// byte offsets into this array, so PR 4 can confirm the offsets against a real read instead of trusting them.
    /// </summary>
    public static byte[] ToBytes(ulong value)
    {
        var bytes = new byte[8];
        for (int i = 0; i < 8; i++) bytes[i] = (byte)((value >> (8 * i)) & 0xFF);
        return bytes;
    }

    /// <summary>Reads one byte out of a raw dump. Returns null when the offset is outside the array.</summary>
    public static byte? ByteAt(IReadOnlyList<byte>? bytes, int offset) =>
        bytes == null || offset < 0 || offset >= bytes.Count ? null : bytes[offset];

    /// <summary>Reads a 16-bit little-endian word out of a raw dump. Null when the word does not fit.</summary>
    public static int? WordAt(IReadOnlyList<byte>? bytes, int wordIndex)
    {
        if (bytes == null || wordIndex < 0) return null;
        int offset = wordIndex * 2;
        if (offset + 1 >= bytes.Count) return null;
        return bytes[offset] | (bytes[offset + 1] << 8);
    }
}
