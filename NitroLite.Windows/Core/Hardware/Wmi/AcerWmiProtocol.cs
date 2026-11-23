using NitroLite.Core;

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

    /// <summary>
    /// Fan bitmap bit for the GPU fan: <c>BIT(3)</c>, not <c>BIT(2)</c>.
    /// <para>
    /// Measured on an AN16-41: <c>GetGamingFanBehavior(0x08)</c> answered <c>0x00004000</c>, with the GPU mode in
    /// bits 15:14, while <c>0x04</c> selected nothing. The Linux <c>acer-wmi</c> driver defines the same bit as
    /// <c>ACER_GAMING_FAN_BEHAVIOR_GPU</c>.
    /// </para>
    /// </summary>
    public const byte FanBitmapGpu = 0x08;

    /// <summary>
    /// Fan bitmap for "both fans" in the behaviour call: <c>FanBitmapCpu | FanBitmapGpu</c> = <c>0x09</c>.
    /// <para>
    /// The getter only fills in the fields whose bitmap bit was set, so a read that wants both modes has to ask for
    /// both. Measured on an AN16-41: <c>0x01</c> answered <c>0x00000100</c> (CPU only), <c>0x08</c> answered
    /// <c>0x00004000</c> (GPU only), <c>0x09</c> answered <c>0x00004100</c> (both), and <c>0x00</c> answered status
    /// <c>0x02</c> with no fields at all.
    /// </para>
    /// </summary>
    public const byte FanBitmapBoth = FanBitmapCpu | FanBitmapGpu;

    // ---------------------------------------------------------------- misc settings (methods 22 / 23)

    /// <summary>Misc index for the MUX / GPU mode setting.</summary>
    public const byte MiscIndexMux = 0x02;

    /// <summary>Misc index for the platform (power) profile.</summary>
    public const byte MiscIndexPlatformProfile = 0x0B;

    /// <summary>
    /// Misc index for the WMI MUX capability signal. The reference driver reads it as
    /// <c>CommonFunction.Get_GPUMode_Support</c> and treats <c>((output &gt;&gt; 8) &amp; 0xFF) == 3</c> as "this machine
    /// can switch the GPU mode over WMI".
    /// </summary>
    public const byte MiscIndexGpuModeSupport = 0x09;

    /// <summary>
    /// Misc index for the bitmask of operating modes the firmware accepts. Measured on an AN16-41:
    /// <c>GetGamingMiscSetting(0x0A)</c> answered <c>0x7300</c>, so the mask is <c>0x73</c>.
    /// </summary>
    public const byte MiscIndexSupportedModes = 0x0A;

    /// <summary>The value <see cref="MiscIndexGpuModeSupport"/> answers when the MUX is switchable over WMI.</summary>
    public const int GpuModeSupportSwitchable = 3;

    // ---------------------------------------------------------------- operating-mode bitmask (misc 0x0A)

    /// <summary>Mode bit for Quiet / Silent.</summary>
    public const int ModeBitQuiet = 0x01;

    /// <summary>Mode bit for Balanced / Default.</summary>
    public const int ModeBitBalanced = 0x02;

    /// <summary>Mode bit for Performance / Extreme.</summary>
    public const int ModeBitPerformance = 0x10;

    /// <summary>Mode bit for Turbo. Absent on the models whose firmware has no Turbo step.</summary>
    public const int ModeBitTurbo = 0x20;

    /// <summary>Mode bit for Eco.</summary>
    public const int ModeBitEco = 0x40;

    /// <summary>
    /// Every bit this build knows how to name. Bits outside it are ignored rather than guessed at: a firmware that
    /// sets an unknown bit is not evidence of a mode the app can offer.
    /// </summary>
    public const int KnownModeBits = ModeBitQuiet | ModeBitBalanced | ModeBitPerformance | ModeBitTurbo | ModeBitEco;

    // ---------------------------------------------------------------- APGeAction (CoolBoost, backlight timeout)

    /// <summary>GUID of the <c>APGeAction</c> class, which carries CoolBoost and the backlight timeout.</summary>
    public const string ApgeActionGuid = "61EF69EA-865C-4BC3-A502-A0DEBA0CB531";

    /// <summary>Selector for the CoolBoost getter in <c>APGeAction.GetFunction</c>.</summary>
    public const uint ApgeCoolBoostGet = 0x207;

    /// <summary>Selector for the backlight-timeout getter in <c>APGeAction.GetFunction</c>.</summary>
    public const uint ApgeBacklightGetBase = 0x01;

    /// <summary>Selector for the backlight-timeout setter in <c>APGeAction.SetFunction</c>.</summary>
    public const uint ApgeBacklightSetBase = 0x02;

    /// <summary>Selector for the CoolBoost setter in <c>APGeAction.SetFunction</c>.</summary>
    public const uint ApgeCoolBoostSetBase = 0x07;

    /// <summary>Bit that marks a backlight-timeout call as the keyboard-backlight variant.</summary>
    public const ulong ApgeBacklightFlag = 0x80000UL;

    /// <summary>The idle delay the firmware applies when the backlight timeout is on, in seconds.</summary>
    public const int BacklightTimeoutSeconds = 30;

    // ---------------------------------------------------------------- gaming profile (methods 1 / 3)

    /// <summary>
    /// <c>GetGamingProfile(0)</c> answer when LCD overdrive is off but the panel supports it. Measured on an
    /// AN16-41: <c>0x0000000001000000</c>.
    /// </summary>
    public const ulong GamingProfileOverdriveOff = 0x1000000UL;

    /// <summary>The same answer with overdrive on: <c>0x1000000</c> plus bit 48.</summary>
    public const ulong GamingProfileOverdriveOn = 0x1000001000000UL;

    /// <summary>Selector for the Windows-key lock in <c>SetGamingProfile</c>.</summary>
    public const ulong GamingProfileWinKeyLock = 0x02UL;

    /// <summary>Bit that carries the Windows-key lock state in the <c>SetGamingProfile</c> input.</summary>
    public const ulong GamingProfileWinKeyLockBit = 1UL << 24;

    /// <summary>Selector for LCD overdrive in <c>SetGamingProfile</c>.</summary>
    public const ulong GamingProfileOverdrive = 0x10UL;

    /// <summary>Bit that carries the LCD overdrive state in the <c>SetGamingProfile</c> input.</summary>
    public const ulong GamingProfileOverdriveBit = 1UL << 48;

    // ---------------------------------------------------------------- keyboard RGB (Get / SetGamingRgbKb)

    /// <summary>Zone mask bit for keyboard zone 1.</summary>
    public const byte LedZone1 = 0x01;

    /// <summary>Zone mask bit for keyboard zone 2.</summary>
    public const byte LedZone2 = 0x02;

    /// <summary>Zone mask bit for keyboard zone 3.</summary>
    public const byte LedZone3 = 0x04;

    /// <summary>Zone mask bit for keyboard zone 4.</summary>
    public const byte LedZone4 = 0x08;

    /// <summary>Every zone at once.</summary>
    public const byte LedZoneAll = LedZone1 | LedZone2 | LedZone3 | LedZone4;

    /// <summary>How many independently addressable keyboard zones the firmware exposes.</summary>
    public const int LedZoneCount = 4;

    /// <summary>Highest brightness level the app's lighting model uses (0..9).</summary>
    public const int MaxBrightnessLevel = 9;

    // ---------------------------------------------------------------- keyboard backlight (methods 20 / 21)

    /// <summary>Effect mode byte for a held colour.</summary>
    public const byte KbModeStatic = 0;

    /// <summary>Effect mode byte for breathing.</summary>
    public const byte KbModeBreathing = 1;

    /// <summary>Effect mode byte for the neon colour cycle.</summary>
    public const byte KbModeNeon = 2;

    /// <summary>Effect mode byte for the wave.</summary>
    public const byte KbModeWave = 3;

    /// <summary>Effect mode byte for the colour shift.</summary>
    public const byte KbModeShifting = 4;

    /// <summary>Effect mode byte for the zoom.</summary>
    public const byte KbModeZoom = 5;

    /// <summary>
    /// Byte 3 of an animated payload when the effect is the wave; every other effect leaves it zero. The firmware
    /// reads it as "this is a directional wave" and ignores the direction byte without it.
    /// </summary>
    public const byte KbWaveFlag = 0x08;

    /// <summary>Length of the <c>SetGamingKBBacklight</c> payload.</summary>
    public const int KbBacklightPayloadLength = 16;

    /// <summary>
    /// Shortest <c>GetGamingKBBacklight</c> answer that carries the fields this app reads. The getter answers 15
    /// bytes on an AN16-41 and 9 on an AN515-44; both start with the same eight, so the decoder reads the common
    /// prefix and reports the length rather than assuming one.
    /// </summary>
    public const int KbBacklightMinimumReadLength = 8;

    /// <summary>Highest effect speed the firmware accepts (1..9).</summary>
    public const int MaxEffectSpeed = 9;

    /// <summary>Highest effect direction the firmware accepts (1 right, 2 left, 3 up, 4 down).</summary>
    public const int MaxEffectDirection = 4;

    /// <summary>
    /// The value <c>SetGamingLED</c> takes to switch the four LED zones on: <c>BIT(3)</c> for "on" and
    /// <c>GENMASK_ULL(43, 40)</c> for "all four zones". The method's input is a byte array, so this goes out as
    /// eight little-endian bytes rather than an integer.
    /// </summary>
    public const ulong LedArmValue = 0x08UL | (0xFUL << 40);

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

    /// <summary>
    /// Splits a <c>GetGamingFanBehavior</c> answer into its status byte and the two fan modes.
    /// <para>
    /// The getter does not use the setter's bit positions. <c>SetGamingFanBehavior</c> takes the CPU mode in bits
    /// 17:16 and the GPU mode in bits 23:22, but <c>GetGamingFanBehavior</c> answers the CPU mode in bits 9:8 and
    /// the GPU mode in bits 15:14. Measured on an AN16-41: <c>GetGamingFanBehavior(0x09)</c> returned
    /// <c>0x00004100</c>, which is Auto/Auto under this layout and 0/0 under the setter's.
    /// </para>
    /// <para>
    /// The low byte is the status, not the bitmap: the getter does not echo the bitmap it was asked about. A field
    /// whose bitmap bit was not set reads back as 0 and is reported as <see cref="FanMode.Auto"/>, so a caller that
    /// wants both modes must ask with <see cref="FanBitmapBoth"/>.
    /// </para>
    /// </summary>
    public static (byte Status, FanMode Cpu, FanMode Gpu) DecodeFanBehavior(ulong output) =>
        ((byte)(output & 0xFF),
         FanModeFromBehavior((byte)((output >> 8) & 0x03)) ?? FanMode.Auto,
         FanModeFromBehavior((byte)((output >> 14) & 0x03)) ?? FanMode.Auto);

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

    // ---------------------------------------------------------------- sensors (method 5)

    // The canonical values live in NitroLite.Core (AcerSensorIds) because the model table is keyed by them and
    // has to be readable on a platform with no WMI. These aliases keep every existing call site compiling.

    /// <summary>Sensor id for the CPU temperature in <c>GetGamingSysInfo</c>.</summary>
    public const byte SensorCpuTemperature = AcerSensorIds.CpuTemperature;

    /// <summary>Sensor id for the CPU fan tachometer.</summary>
    public const byte SensorCpuFanSpeed = AcerSensorIds.CpuFanSpeed;

    /// <summary>Sensor id for the board / system temperature.</summary>
    public const byte SensorSystemTemperature = AcerSensorIds.SystemTemperature;

    /// <summary>Sensor id for the GPU fan tachometer.</summary>
    public const byte SensorGpuFanSpeed = AcerSensorIds.GpuFanSpeed;

    /// <summary>Sensor id for the GPU temperature.</summary>
    public const byte SensorGpuTemperature = AcerSensorIds.GpuTemperature;

    /// <summary>The command nibble in the low byte of a <c>GetGamingSysInfo</c> input: "read one sensor".</summary>
    public const uint SysInfoCommandReadSensor = 0x0001;

    /// <summary>
    /// <c>1 | (sensorId &lt;&lt; 8)</c>. The sensor id is not a byte offset into a packed page: each call names one
    /// sensor and answers one value. Measured on an AN16-41, where <c>0x0101</c> answered <c>0x00003F00</c>
    /// (63 °C) and <c>0x0201</c> answered <c>0x00098400</c> (2436 RPM), both matching the Acer service's own
    /// telemetry at the same moment.
    /// </summary>
    public static uint EncodeSysInfoQuery(byte sensorId) =>
        SysInfoCommandReadSensor | ((uint)sensorId << 8);

    /// <summary>The reading out of a <c>GetGamingSysInfo</c> answer: <c>(output &gt;&gt; 8) &amp; 0xFFFF</c>.</summary>
    public static int DecodeSysInfoValue(ulong output) => (int)((output >> 8) & 0xFFFF);

    // ---------------------------------------------------------------- operating mode

    /// <summary><c>(mode &lt;&lt; 8) | 0x0B</c>. Performance (Extreme) is <c>0x040B</c>.</summary>
    public static ulong EncodeOperatingMode(OperatingMode mode) =>
        ((ulong)(byte)mode << 8) | MiscIndexPlatformProfile;

    /// <summary>The mode out of a <c>GetGamingMiscSetting(0x0B)</c> answer. Null when the value is not a known mode.</summary>
    public static OperatingMode? DecodeOperatingMode(ulong output)
    {
        if (DecodeStatus(output) != StatusOk) return null;
        var raw = (OperatingMode)(byte)((output >> 8) & 0xFF);
        return Enum.IsDefined(raw) ? raw : null;
    }

    // ---------------------------------------------------------------- MUX

    /// <summary>
    /// <c>(firmwareMode &lt;&lt; 8) | 0x02</c>, where the firmware numbers the GPU modes from one: 1 is discrete only
    /// (<c>0x0102</c>), 2 is hybrid / Optimus (<c>0x0202</c>), 3 is the firmware's own automatic choice
    /// (<c>0x0302</c>).
    /// <para>
    /// The polarity is measured, not inferred. <c>GetGamingMiscSetting(0x02)</c> on an AN16-41 sitting in hybrid
    /// mode answers <c>0x0202</c>, and the Linux <c>acer-wmi</c> driver's <c>ACER_WMID_GPU_MODE_HYBRID</c> is 2.
    /// The earlier encoder derived the value from the app's own enum index (<c>mode + 1</c>), which put Optimus at
    /// 1 and discrete at 2 — exactly backwards, so a MUX write would have selected the opposite mode.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not one of the three the firmware knows.</exception>
    public static ulong EncodeMuxMode(GpuOperatingMode mode) =>
        ((ulong)MuxModeValue(mode) << 8) | MiscIndexMux;

    /// <summary>The firmware's one-based number for a GPU mode.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not one of the three the firmware knows.</exception>
    public static byte MuxModeValue(GpuOperatingMode mode) => mode switch
    {
        GpuOperatingMode.DiscreteGpu => 1,
        GpuOperatingMode.Optimus => 2,
        GpuOperatingMode.Automatic => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown GPU mode")
    };

    /// <summary>The GPU mode out of a <c>GetGamingMiscSetting(0x02)</c> answer. Null when the value is out of range.</summary>
    public static GpuOperatingMode? DecodeMuxMode(ulong output) => (byte)((output >> 8) & 0xFF) switch
    {
        1 => GpuOperatingMode.DiscreteGpu,
        2 => GpuOperatingMode.Optimus,
        3 => GpuOperatingMode.Automatic,
        _ => null
    };

    // ---------------------------------------------------------------- keyboard RGB

    /// <summary>
    /// <c>(b &lt;&lt; 24) | (g &lt;&lt; 16) | (r &lt;&lt; 8) | zoneMask</c>.
    /// <para>
    /// The low byte is the zone mask, and it must not be zero. Measured on an AN16-41: a write with mask
    /// <c>0x00</c> answers <c>gmOutput = 0x00000001</c> and changes nothing, while <c>0x01</c> and <c>0x0F</c> both
    /// answer <c>0x00000000</c> and apply the colour. That is the whole reason the four-zone picker never worked
    /// through the service path: the colour was sent without a zone to put it in.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The mask is zero or outside the four zone bits.</exception>
    public static ulong EncodeRgbKb(byte red, byte green, byte blue, byte zoneMask)
    {
        if (zoneMask == 0 || (zoneMask & ~LedZoneAll) != 0)
            throw new ArgumentOutOfRangeException(nameof(zoneMask), zoneMask, "zone mask must be 1..15");

        return ((ulong)blue << 24) | ((ulong)green << 16) | ((ulong)red << 8) | zoneMask;
    }

    /// <summary>Splits a <c>GetGamingRgbKb</c> answer into its colour and zone mask.</summary>
    public static (byte Red, byte Green, byte Blue, byte ZoneMask) DecodeRgbKb(ulong output) =>
        ((byte)((output >> 8) & 0xFF),
         (byte)((output >> 16) & 0xFF),
         (byte)((output >> 24) & 0xFF),
         (byte)(output & 0xFF));

    /// <summary>
    /// The four zone colours folded into the fewest <c>SetGamingRgbKb</c> writes that can carry them: one entry per run
    /// of neighbouring zones that share a colour, with the run's zone bits OR-ed into its mask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mask is a four-bit zone bitmap, so a run of zones is one write. The split the temperature card paints is
    /// therefore two writes — <c>0x03</c> for the left half and <c>0x0C</c> for the right half — instead of four, and a
    /// keyboard taken over by one solid colour is a single <c>0x0F</c> write.
    /// </para>
    /// <para>
    /// This is also the fix for a split that came out as three zones on one colour and one on the other. Four
    /// back-to-back writes are four chances for the firmware to still be applying the previous frame when the next
    /// arrives, and a zone whose write is dropped keeps whatever colour it had; two writes, one per half, leave much
    /// less of that window. The order is the zone order either way, so the leftmost zones are written first.
    /// </para>
    /// </remarks>
    /// <param name="zones">One packed <c>0xRRGGBB</c> colour per zone, in zone order.</param>
    /// <returns>One <c>(Mask, Color)</c> pair per run, in zone order. Empty when there is nothing to write.</returns>
    public static IReadOnlyList<(byte Mask, int Color)> GroupZoneRuns(IReadOnlyList<int>? zones)
    {
        if (zones == null || zones.Count == 0) return Array.Empty<(byte Mask, int Color)>();

        var runs = new List<(byte Mask, int Color)>();
        int start = 0;

        for (int i = 1; i <= zones.Count; i++)
        {
            if (i < zones.Count && zones[i] == zones[start]) continue;

            byte mask = 0;
            for (int zone = start; zone < i && zone < LedZoneCount; zone++)
                mask |= (byte)(LedZone1 << zone);

            // A run that starts beyond the fourth zone cannot be addressed at all; the caller's own length check is what
            // rejects such a list, and this keeps the helper total rather than shifting past the mask.
            if (mask == 0) return runs;

            runs.Add((mask, zones[start]));
            start = i;
        }

        return runs;
    }

    /// <summary>One <c>SetGamingRgbKb</c> call in a lighting write: a zone bitmap and the colour it carries.</summary>
    /// <param name="Mask">The zone bitmap: one bit per zone, <c>0x01</c>..<c>0x08</c>, or <c>0x0F</c> for all four.</param>
    /// <param name="Color">The packed colour, <c>0xRRGGBB</c>.</param>
    /// <param name="Pass">Zero for the first pass, one for the repeat pass that re-states every zone.</param>
    public readonly record struct ZoneWrite(byte Mask, int Color, int Pass);

    /// <summary>
    /// The calls one keyboard write makes, in zone order, and then the whole set again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One zone per call, with the single-bit mask the capture measured — <c>0x01</c>, <c>0x02</c>, <c>0x04</c>,
    /// <c>0x08</c>. Combined masks were the tidier idea and they are what the reference AN16-41 refused to honour:
    /// the left half was written as <c>0x03</c>, the right half as <c>0x0C</c>, both answered <c>0x00</c>, and the whole
    /// keyboard came out in the first run's colour — twice, with and without the vendor service, with and without a gap
    /// between the calls. A combined mask that paints every zone is worse than four calls: it looks applied and folds
    /// the split into one colour. Four per-zone calls are what this firmware showed as three zones right and one wrong,
    /// which is a dropped call, not a misunderstood mask.
    /// </para>
    /// <para>
    /// The repeat pass is what answers the dropped call: the EC applies a mask asynchronously, and a write that lands in
    /// that window is accepted with status 0 and dropped, leaving the zone on its old colour. A single colour needs no
    /// repeat — nothing can be left behind when every zone is the same — so it stays one <c>0x0F</c> write.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ZoneWrite> BuildZoneWritePlan(IReadOnlyList<int>? zones)
    {
        if (zones == null || zones.Count == 0) return Array.Empty<ZoneWrite>();

        var single = GroupZoneRuns(zones);
        if (single.Count == 1) return new[] { new ZoneWrite(single[0].Mask, single[0].Color, 0) };

        var perZone = new List<ZoneWrite>(8);
        for (int zone = 0; zone < LedZoneCount && zone < zones.Count; zone++)
            perZone.Add(new ZoneWrite((byte)(LedZone1 << zone), zones[zone], 0));

        foreach (var step in perZone.ToArray()) perZone.Add(step with { Pass = 1 });
        return perZone;
    }

    /// <summary>Parses <c>#RRGGBB</c> (the app's colour format) into its three channels.</summary>
    public static bool TryParseHexColor(string? color, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (string.IsNullOrWhiteSpace(color)) return false;

        var text = color.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6) return false;

        if (!byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out red)) return false;
        if (!byte.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out green)) return false;
        if (!byte.TryParse(text[4..], System.Globalization.NumberStyles.HexNumber, null, out blue)) return false;
        return true;
    }

    /// <summary>Formats three channels as <c>#RRGGBB</c>, the app's colour format.</summary>
    public static string FormatHexColor(byte red, byte green, byte blue) => $"#{red:X2}{green:X2}{blue:X2}";

    /// <summary>
    /// The app's 0..9 brightness level as the 0..100 percentage the firmware stores. The mapping round-trips:
    /// <c>BrightnessPercentToLevel(BrightnessLevelToPercent(n)) == n</c> for every level.
    /// </summary>
    public static int BrightnessLevelToPercent(int level) =>
        Math.Clamp(level, 0, MaxBrightnessLevel) * 100 / MaxBrightnessLevel;

    /// <summary>The firmware's 0..100 percentage as the app's 0..9 brightness level.</summary>
    public static int BrightnessPercentToLevel(int percent) =>
        (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * MaxBrightnessLevel);

    // ---------------------------------------------------------------- keyboard backlight payloads

    /// <summary>The firmware's effect mode for an app effect name, or null when the firmware has no such mode.</summary>
    public static byte? KbModeFor(string? effect) => (effect ?? "").Trim().ToUpperInvariant() switch
    {
        "STATIC" or "DIRECT" => KbModeStatic,
        "BREATHING" => KbModeBreathing,
        "NEON" => KbModeNeon,
        "WAVE" => KbModeWave,
        "SHIFTING" => KbModeShifting,
        "ZOOM" => KbModeZoom,
        _ => null
    };

    /// <summary>
    /// The 16-byte payload that locks the keyboard into a held colour: mode 0, no speed, no direction, no colour,
    /// brightness in byte 2 and a single <c>0x01</c> in byte 9.
    /// <para>
    /// This is the last step of the four-zone sequence, not a colour write. The per-zone colours are already in the
    /// EC by the time it runs; without it the zones stay disabled on the models that need the sequence and the
    /// colours are stored but never displayed.
    /// </para>
    /// </summary>
    public static byte[] EncodeKbStaticLock(int brightnessPercent)
    {
        var payload = new byte[KbBacklightPayloadLength];
        payload[2] = (byte)Math.Clamp(brightnessPercent, 0, 100);
        payload[9] = 0x01;
        return payload;
    }

    /// <summary>
    /// The 16-byte payload for an animated effect:
    /// <c>[mode, speed, brightness, waveFlag, direction, r, g, b, 3, 1, 0…]</c>.
    /// </summary>
    public static byte[] EncodeKbEffect(
        byte mode, int speed, int brightnessPercent, int direction, byte red, byte green, byte blue)
    {
        var payload = new byte[KbBacklightPayloadLength];
        payload[0] = mode;
        payload[1] = (byte)Math.Clamp(speed, 1, MaxEffectSpeed);
        payload[2] = (byte)Math.Clamp(brightnessPercent, 0, 100);
        payload[3] = mode == KbModeWave ? KbWaveFlag : (byte)0x00;
        payload[4] = (byte)Math.Clamp(direction, 1, MaxEffectDirection);
        payload[5] = red;
        payload[6] = green;
        payload[7] = blue;
        payload[8] = 0x03;
        payload[9] = 0x01;
        return payload;
    }

    /// <summary>
    /// <c>(channel * brightness) / 100</c> per channel.
    /// <para>
    /// The firmware does not apply the brightness byte to a per-zone colour, so the caller has to scale the colour
    /// itself: a 50 % setting sent unscaled comes out at full brightness. Measured against the reference driver,
    /// which scales the same way before every per-zone write.
    /// </para>
    /// </summary>
    public static (byte Red, byte Green, byte Blue) ScaleColor(byte red, byte green, byte blue, int brightnessPercent)
    {
        int scale = Math.Clamp(brightnessPercent, 0, 100);
        return ((byte)(red * scale / 100), (byte)(green * scale / 100), (byte)(blue * scale / 100));
    }

    /// <summary>An eight-byte little-endian array, the shape <c>SetGamingLED</c> takes.</summary>
    public static byte[] ToLittleEndianBytes(ulong value)
    {
        var bytes = new byte[8];
        for (int i = 0; i < 8; i++) bytes[i] = (byte)((value >> (8 * i)) & 0xFF);
        return bytes;
    }

    /// <summary>
    /// The fields of a <c>GetGamingKBBacklight</c> answer. Null when the answer is shorter than the eight bytes the
    /// fields live in, so a firmware that answers something else is reported as unreadable rather than decoded.
    /// </summary>
    public static KbBacklightState? DecodeKbBacklight(IReadOnlyList<byte>? bytes)
    {
        if (bytes == null || bytes.Count < KbBacklightMinimumReadLength) return null;

        return new KbBacklightState(
            bytes[0], bytes[1], bytes[2], bytes[4], bytes[5], bytes[6], bytes[7], bytes.Count);
    }

    // ---------------------------------------------------------------- battery charge limit

    /// <summary>
    /// The four-byte buffer <c>SetBatteryHealthControl</c> takes on <c>WMID_GUID5</c>: the mode in byte 0 and three
    /// reserved bytes. This is the shape the Linux <c>acer-wmi-battery</c> driver sends and the one the firmware
    /// documents; the scalar form some builds expose is handled separately by the executor.
    /// </summary>
    public static byte[] EncodeBatteryLimit(byte mode) => new byte[] { mode, 0, 0, 0 };

    /// <summary>
    /// The charge mode out of a <c>WMID_GUID5</c> answer buffer. Null when the buffer is empty or carries a value that
    /// is not a known mode, so an unexpected answer is reported as unreadable rather than as a mode.
    /// </summary>
    public static BatteryChargeMode? DecodeBatteryLimit(IReadOnlyList<byte>? bytes)
    {
        if (bytes == null || bytes.Count == 0) return null;

        var mode = (BatteryChargeMode)bytes[0];
        return Enum.IsDefined(mode) ? mode : null;
    }

    // ---------------------------------------------------------------- operating-mode bitmask

    /// <summary>
    /// The operating modes a <c>GetGamingMiscSetting(0x0A)</c> answer advertises, in the order the UI shows them.
    /// <para>
    /// An empty list means the firmware did not answer or answered zero, which is not evidence that no mode works:
    /// the caller keeps its existing set rather than hiding every mode button. Bits outside
    /// <see cref="KnownModeBits"/> are ignored, so a firmware that sets an unknown bit cannot conjure a mode.
    /// </para>
    /// </summary>
    public static IReadOnlyList<OperatingMode> DecodeSupportedModes(ulong answer)
    {
        var mask = (int)((answer >> 8) & 0xFF);
        if (mask == 0) return Array.Empty<OperatingMode>();

        var modes = new List<OperatingMode>(5);
        if ((mask & ModeBitQuiet) != 0) modes.Add(OperatingMode.Quiet);
        if ((mask & ModeBitBalanced) != 0) modes.Add(OperatingMode.Default);
        if ((mask & ModeBitPerformance) != 0) modes.Add(OperatingMode.Extreme);
        if ((mask & ModeBitTurbo) != 0) modes.Add(OperatingMode.Turbo);
        if ((mask & ModeBitEco) != 0) modes.Add(OperatingMode.Eco);
        return modes;
    }

    /// <summary>True when the <c>GetGamingMiscSetting(0x09)</c> answer says the MUX is switchable over WMI.</summary>
    public static bool DecodeGpuModeSupport(ulong answer) =>
        (int)((answer >> 8) & 0xFF) == GpuModeSupportSwitchable;

    // ---------------------------------------------------------------- APGeAction

    /// <summary>
    /// <c>APGeAction.SetFunction</c> input for CoolBoost: <c>7 | (on ? 1 &lt;&lt; 16 : 0)</c>.
    /// </summary>
    public static ulong EncodeCoolBoost(bool on) => ApgeCoolBoostSetBase | (on ? 1UL << 16 : 0UL);

    /// <summary>
    /// The CoolBoost state in a <c>GetFunction(0x207)</c> answer, or null when the firmware answered a status byte
    /// this build does not recognise. A non-zero low byte is the firmware's "I did not understand the selector".
    /// </summary>
    public static bool? DecodeCoolBoost(ulong answer)
    {
        if ((answer & 0xFF) != 0) return null;
        return ((answer >> 8) & 0xFF) == 1;
    }

    /// <summary><c>APGeAction.GetFunction</c> input for the backlight-timeout state.</summary>
    public static ulong EncodeBacklightTimeoutGet(int hotkeyNumber) =>
        ApgeBacklightGetBase | ((ulong)(hotkeyNumber & 0xFF) << 8) | ApgeBacklightFlag;

    /// <summary>
    /// <c>APGeAction.SetFunction</c> input for the backlight timeout:
    /// <c>2 | (hotkey &lt;&lt; 8) | 0x80000 | (brightness &lt;&lt; 32) | (enabled ? 30 &lt;&lt; 40 : 0)</c>.
    /// </summary>
    public static ulong EncodeBacklightTimeoutSet(int hotkeyNumber, int brightnessPercent, bool enabled)
    {
        var value = ApgeBacklightSetBase
                    | ((ulong)(hotkeyNumber & 0xFF) << 8)
                    | ApgeBacklightFlag
                    | ((ulong)(Math.Clamp(brightnessPercent, 0, 100) & 0xFF) << 32);

        if (enabled) value |= (ulong)BacklightTimeoutSeconds << 40;
        return value;
    }

    /// <summary>
    /// The backlight-timeout state in a <c>GetFunction</c> answer: the brightness percentage and whether the
    /// 30-second timeout is on. Null when the low byte is a status this build does not recognise.
    /// </summary>
    public static (int BrightnessPercent, bool Enabled)? DecodeBacklightTimeout(ulong answer)
    {
        if ((answer & 0xFF) != 0) return null;
        var brightness = (int)((answer >> 32) & 0xFF);
        var enabled = (int)((answer >> 40) & 0xFF) == BacklightTimeoutSeconds;
        return (brightness, enabled);
    }

    // ---------------------------------------------------------------- gaming profile

    /// <summary>
    /// The LCD overdrive state in a <c>GetGamingProfile(0)</c> answer: true when on, false when off, null when the
    /// panel does not support it. The firmware answers exactly two values for a supported panel, so anything else is
    /// "no overdrive here" rather than a guess.
    /// </summary>
    public static bool? DecodeLcdOverdrive(ulong answer) => answer switch
    {
        GamingProfileOverdriveOn => true,
        GamingProfileOverdriveOff => false,
        _ => null
    };

    /// <summary><c>SetGamingProfile</c> input for LCD overdrive: <c>0x10 | (on ? 1 &lt;&lt; 48 : 0)</c>.</summary>
    public static ulong EncodeLcdOverdrive(bool on) =>
        GamingProfileOverdrive | (on ? GamingProfileOverdriveBit : 0UL);

    /// <summary>
    /// <c>SetGamingProfile</c> input for the Windows-key lock: <c>2 | (enabled ? 1 &lt;&lt; 24 : 0)</c>.
    /// <para>
    /// There is no getter for this one. <c>GetGamingProfile</c> with a non-zero selector echoes the selector back
    /// rather than the state, and <c>GetGamingProfile(0)</c> answers the LCD-overdrive word, whose bit 24 means
    /// "overdrive is supported" — not "the Windows key is locked". The write is therefore reported as unverified
    /// rather than read back.
    /// </para>
    /// </summary>
    public static ulong EncodeWinKeyLock(bool enabled) =>
        GamingProfileWinKeyLock | (enabled ? GamingProfileWinKeyLockBit : 0UL);

    // ---------------------------------------------------------------- byte arrays

    /// <summary>
    /// Reads one byte out of a byte-array answer. <c>GetGamingKBBacklight</c> is the only getter that answers an
    /// array rather than an integer, and its fields are addressed by offset.
    /// </summary>
    public static byte? ByteAt(IReadOnlyList<byte>? bytes, int offset) =>
        bytes == null || offset < 0 || offset >= bytes.Count ? null : bytes[offset];
}

/// <summary>
/// One <c>GetGamingKBBacklight</c> answer, decoded. <see cref="Length"/> is carried because the getter answers 15
/// bytes on an AN16-41 and 9 on an AN515-44; the fields are the same eight-byte prefix either way, and reporting the
/// length lets a diagnostic say which firmware answered.
/// </summary>
public readonly record struct KbBacklightState(
    byte Mode, int Speed, int Brightness, int Direction, byte Red, byte Green, byte Blue, int Length);
