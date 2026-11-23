using System.IO;
using System.Text.Json.Nodes;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the seventh step of the hardware-layer migration: the keyboard RGB protocol that was measured on an AN16-41,
/// the worker commands that carry it, the read-back rule that guards it, and the sensor-layout cleanup that stops the
/// WMI backend from decoding temperatures out of bytes that are not sensors.
/// <para>
/// The encodings here are not guesses. They come from a live elevated capture: <c>SetGamingRgbKb</c> with a zone mask
/// of <c>0x00</c> answered <c>gmOutput = 0x00000001</c> and changed nothing, while <c>0x01</c> and <c>0x0F</c> both
/// answered <c>0x00000000</c> and applied the colour.
/// </para>
/// <para>
/// A second capture established the rest of the sequence on the same machine. Four per-zone <c>SetGamingRgbKb</c>
/// calls with masks <c>0x01</c>, <c>0x02</c>, <c>0x04</c> and <c>0x08</c> all answered <c>0x00</c>;
/// <c>SetGamingKBBacklight</c> with the static lock answered <c>0x00</c> and the read-back came back as mode 0 with
/// the brightness and colour that were sent; and the breathing payload answered <c>0x00</c> with the read-back
/// showing mode 1, speed 4, brightness 80, direction 1 and the colour. The arm step is the exception: an AN16-41
/// answers <c>WBEM_E_INVALID_PARAMETER</c> to <c>SetGamingLED</c> and <c>0x02</c> to every LED getter, so a refusal
/// there is logged and the zone colours are sent anyway.
/// </para>
/// </summary>
public class HalStep7Tests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
    private static Task NoDelay(TimeSpan _) => Task.CompletedTask;

    // ---------------------------------------------------------------- RGB encoding

    [Fact]
    public void EncodeRgbKb_RedOnZone1_Is0xFF01()
    {
        // Red sits in the second byte of the colour triple, so a pure red on zone 1 is 0x0000FF01.
        Assert.Equal(0x000000000000FF01UL, AcerWmiProtocol.EncodeRgbKb(0xFF, 0x00, 0x00, AcerWmiProtocol.LedZone1));
    }

    [Fact]
    public void EncodeRgbKb_GreenOnAllZones_Is0xFF000F()
    {
        Assert.Equal(0x0000000000FF000FUL, AcerWmiProtocol.EncodeRgbKb(0x00, 0xFF, 0x00, AcerWmiProtocol.LedZoneAll));
    }

    [Fact]
    public void EncodeRgbKb_BlueOnZone4_Is0xFF000008()
    {
        Assert.Equal(0x00000000FF000008UL, AcerWmiProtocol.EncodeRgbKb(0x00, 0x00, 0xFF, AcerWmiProtocol.LedZone4));
    }

    [Fact]
    public void EncodeRgbKb_ZeroMask_IsRejected()
    {
        // The firmware answers gmOutput 0x01 to a mask of zero and applies nothing. Accepting it here would turn a
        // caller's mistake into a silent no-op that looks like a successful write.
        Assert.Throws<ArgumentOutOfRangeException>(() => AcerWmiProtocol.EncodeRgbKb(0xFF, 0, 0, 0x00));
    }

    [Fact]
    public void EncodeRgbKb_MaskOutsideTheFourZones_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AcerWmiProtocol.EncodeRgbKb(0xFF, 0, 0, 0x10));
    }

    [Theory]
    [InlineData(0xFF, 0x00, 0x00, 0x01)]
    [InlineData(0x00, 0xFF, 0x00, 0x0F)]
    [InlineData(0x12, 0x34, 0x56, 0x08)]
    [InlineData(0x00, 0x00, 0x00, 0x0F)]
    public void EncodeRgbKb_RoundTripsThroughTheDecoder(byte red, byte green, byte blue, byte mask)
    {
        var (decodedRed, decodedGreen, decodedBlue, decodedMask) =
            AcerWmiProtocol.DecodeRgbKb(AcerWmiProtocol.EncodeRgbKb(red, green, blue, mask));

        Assert.Equal(red, decodedRed);
        Assert.Equal(green, decodedGreen);
        Assert.Equal(blue, decodedBlue);
        Assert.Equal(mask, decodedMask);
    }

    [Fact]
    public void DecodeRgbKb_MatchesTheMeasuredReadBack()
    {
        // GetGamingRgbKb(0x01) answered 0x0000000000FF0000 after a green write.
        var (red, green, blue, mask) = AcerWmiProtocol.DecodeRgbKb(0x0000000000FF0000UL);

        Assert.Equal(0x00, red);
        Assert.Equal(0xFF, green);
        Assert.Equal(0x00, blue);
        Assert.Equal(0x00, mask);
    }

    [Theory]
    [InlineData("#FF0000", 0xFF, 0x00, 0x00)]
    [InlineData("00FF00", 0x00, 0xFF, 0x00)]
    [InlineData("#0000ff", 0x00, 0x00, 0xFF)]
    [InlineData("  #12AB34  ", 0x12, 0xAB, 0x34)]
    public void TryParseHexColor_AcceptsTheAppColourFormat(string text, byte red, byte green, byte blue)
    {
        Assert.True(AcerWmiProtocol.TryParseHexColor(text, out var r, out var g, out var b));
        Assert.Equal(red, r);
        Assert.Equal(green, g);
        Assert.Equal(blue, b);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("#FF00000")]
    public void TryParseHexColor_RejectsAnythingElse(string? text)
    {
        Assert.False(AcerWmiProtocol.TryParseHexColor(text, out _, out _, out _));
    }

    [Fact]
    public void FormatHexColor_ProducesTheAppColourFormat()
    {
        Assert.Equal("#12AB34", AcerWmiProtocol.FormatHexColor(0x12, 0xAB, 0x34));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    public void BrightnessMapping_RoundTripsEveryLevel(int level)
    {
        var percent = AcerWmiProtocol.BrightnessLevelToPercent(level);
        Assert.Equal(level, AcerWmiProtocol.BrightnessPercentToLevel(percent));
    }

    [Fact]
    public void BrightnessMapping_ClampsOutOfRangeInput()
    {
        Assert.Equal(0, AcerWmiProtocol.BrightnessLevelToPercent(-4));
        Assert.Equal(100, AcerWmiProtocol.BrightnessLevelToPercent(99));
        Assert.Equal(0, AcerWmiProtocol.BrightnessPercentToLevel(-1));
        Assert.Equal(9, AcerWmiProtocol.BrightnessPercentToLevel(1000));
    }

    // ---------------------------------------------------------------- zone runs

    [Fact]
    public void GroupZoneRuns_ASplitKeyboardIsTwoWritesNotFour()
    {
        // What the temperature card asks for: the processor's colour on the left half, the graphics chip's on the right.
        // The mask is a bitmap, so each half is one write — and the order is zone order, left first.
        var runs = AcerWmiProtocol.GroupZoneRuns(new[] { 0xFF0000, 0xFF0000, 0x0000FF, 0x0000FF });

        Assert.Equal(2, runs.Count);
        Assert.Equal(0x03, runs[0].Mask);
        Assert.Equal(0xFF0000, runs[0].Color);
        Assert.Equal(0x0C, runs[1].Mask);
        Assert.Equal(0x0000FF, runs[1].Color);
    }

    [Fact]
    public void BuildZoneWritePlan_ASplitIsOneZonePerCallTwice()
    {
        // The reference AN16-41 wrote the combined masks 0x03 / 0x0C with status 0 and kept the whole keyboard in the
        // first run's colour, so a split is built from the single-bit masks the capture measured, in zone order, and
        // every zone is stated twice because the EC drops a write that lands while it is still applying the last one.
        var plan = AcerWmiProtocol.BuildZoneWritePlan(new[] { 0xFF0000, 0xFF0000, 0x00FF00, 0x00FF00 });

        Assert.Equal(8, plan.Count);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x04, 0x08, 0x01, 0x02, 0x04, 0x08 }, plan.Select(step => step.Mask));
        Assert.Equal(
            new[] { 0xFF0000, 0xFF0000, 0x00FF00, 0x00FF00, 0xFF0000, 0xFF0000, 0x00FF00, 0x00FF00 },
            plan.Select(step => step.Color));
        Assert.Equal(new[] { 0, 0, 0, 0, 1, 1, 1, 1 }, plan.Select(step => step.Pass));
    }

    [Fact]
    public void BuildZoneWritePlan_AOneColourWriteIsNotRepeated()
    {
        // Nothing can be left behind when all four zones carry the same colour, so a solid write stays one call and
        // costs the keyboard nothing extra.
        var plan = AcerWmiProtocol.BuildZoneWritePlan(new[] { 0x12AB34, 0x12AB34, 0x12AB34, 0x12AB34 });

        var step = Assert.Single(plan);
        Assert.Equal(AcerWmiProtocol.LedZoneAll, step.Mask);
        Assert.Equal(0x12AB34, step.Color);
        Assert.Equal(0, step.Pass);
    }

    [Fact]
    public void BuildZoneWritePlan_FourColoursAreEightCallsInZoneOrder()
    {
        var plan = AcerWmiProtocol.BuildZoneWritePlan(new[] { 0x010101, 0x020202, 0x030303, 0x040404 });

        Assert.Equal(8, plan.Count);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x04, 0x08 }, plan.Take(4).Select(step => step.Mask));
        Assert.Equal(new byte[] { 0x01, 0x02, 0x04, 0x08 }, plan.Skip(4).Select(step => step.Mask));
    }

    [Fact]
    public void BuildZoneWritePlan_NothingToWriteIsNoCalls()
    {
        Assert.Empty(AcerWmiProtocol.BuildZoneWritePlan(null));
        Assert.Empty(AcerWmiProtocol.BuildZoneWritePlan(Array.Empty<int>()));
    }

    [Fact]
    public void BuildZoneWritePlan_RepeatsPayloadsTheProtocolActuallySends()
    {
        // The repeat is only safe if it carries payloads the firmware already accepted: an encoded mask nobody measured
        // would turn the retry into a guess.
        foreach (var step in AcerWmiProtocol.BuildZoneWritePlan(new[] { 0xFF0000, 0xFF0000, 0x0000FF, 0x0000FF }))
        {
            var packed = AcerWmiProtocol.EncodeRgbKb(
                (byte)((step.Color >> 16) & 0xFF),
                (byte)((step.Color >> 8) & 0xFF),
                (byte)(step.Color & 0xFF),
                step.Mask);

            var (red, green, blue, mask) = AcerWmiProtocol.DecodeRgbKb(packed);
            Assert.Equal((byte)((step.Color >> 16) & 0xFF), red);
            Assert.Equal((byte)((step.Color >> 8) & 0xFF), green);
            Assert.Equal((byte)(step.Color & 0xFF), blue);
            Assert.Equal(step.Mask, mask);
        }
    }

    [Fact]
    public void HybridLighting_DoesNotLetTheServicePaintOneColourUnderAZoneWrite()
    {
        // The service takes one colour, so its pre-write is a solid keyboard underneath a split one: whichever lands
        // second is what the user sees, and on the reference machine that was the solid one. The check reads the source
        // because the class needs a live WMI session to exist at all.
        var source = File.ReadAllText(RepoFile("NitroLite.Windows/Core/Hardware/Wmi/HybridHardwareController.cs"));

        Assert.Contains("if (serviceHealthy && !wantsZones)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupZoneRuns_OneColourIsOneAllZoneWrite()
    {
        var runs = AcerWmiProtocol.GroupZoneRuns(new[] { 0x12AB34, 0x12AB34, 0x12AB34, 0x12AB34 });

        var run = Assert.Single(runs);
        Assert.Equal(AcerWmiProtocol.LedZoneAll, run.Mask);
        Assert.Equal(0x12AB34, run.Color);
    }

    [Fact]
    public void GroupZoneRuns_FourDifferentColoursStayFourWrites()
    {
        var runs = AcerWmiProtocol.GroupZoneRuns(new[] { 0x010101, 0x020202, 0x030303, 0x040404 });

        Assert.Equal(4, runs.Count);
        Assert.Equal(new byte[] { AcerWmiProtocol.LedZone1, AcerWmiProtocol.LedZone2, AcerWmiProtocol.LedZone3, AcerWmiProtocol.LedZone4 },
            runs.Select(r => r.Mask));
        Assert.Equal(new[] { 0x010101, 0x020202, 0x030303, 0x040404 }, runs.Select(r => r.Color));
    }

    [Fact]
    public void GroupZoneRuns_MiddleZonesThatAgreeAreOneWrite()
    {
        // Not a split this app paints today, but the grouping is the general rule: neighbouring equal zones fold, and a
        // run never swallows a zone that carries a different colour.
        var runs = AcerWmiProtocol.GroupZoneRuns(new[] { 0x0000FF, 0xFF0000, 0xFF0000, 0x00FF00 });

        Assert.Equal(3, runs.Count);
        Assert.Equal(AcerWmiProtocol.LedZone1, runs[0].Mask);
        Assert.Equal(0x0000FF, runs[0].Color);
        Assert.Equal(0x06, runs[1].Mask);
        Assert.Equal(0xFF0000, runs[1].Color);
        Assert.Equal(AcerWmiProtocol.LedZone4, runs[2].Mask);
        Assert.Equal(0x00FF00, runs[2].Color);
    }

    [Fact]
    public void GroupZoneRuns_NothingToWriteIsNoWrites()
    {
        Assert.Empty(AcerWmiProtocol.GroupZoneRuns(null));
        Assert.Empty(AcerWmiProtocol.GroupZoneRuns(Array.Empty<int>()));
    }

    [Fact]
    public void GroupZoneRuns_TheSplitPayloadsCarryTheirOwnHalfOnly()
    {
        // The bytes the EC is actually handed. Zone 4 is in the second payload, which is the whole point: the "three
        // zones one colour" report was a zone keeping a colour nobody had written to it in this pass.
        var runs = AcerWmiProtocol.GroupZoneRuns(new[] { 0xFF0000, 0xFF0000, 0x0000FF, 0x0000FF });

        Assert.Equal(0x000000000000FF03UL, AcerWmiProtocol.EncodeRgbKb(0xFF, 0x00, 0x00, runs[0].Mask));
        Assert.Equal(0x00000000FF00000CUL, AcerWmiProtocol.EncodeRgbKb(0x00, 0x00, 0xFF, runs[1].Mask));
    }

    // ---------------------------------------------------------------- backlight payloads

    [Fact]
    public void EncodeKbStaticLock_IsTheSixteenByteHoldPayload()
    {
        // {0,0,brightness,0,0,0,0,0,0,1,0,0,0,0,0,0} — the reference driver's set_kb_static_mode.
        var payload = AcerWmiProtocol.EncodeKbStaticLock(60);

        Assert.Equal(16, payload.Length);
        Assert.Equal(60, payload[2]);
        Assert.Equal(0x01, payload[9]);
        Assert.Equal(new byte[] { 0, 0, 60, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, payload);
    }

    [Fact]
    public void EncodeKbStaticLock_ClampsBrightness()
    {
        Assert.Equal(0, AcerWmiProtocol.EncodeKbStaticLock(-20)[2]);
        Assert.Equal(100, AcerWmiProtocol.EncodeKbStaticLock(400)[2]);
    }

    [Theory]
    [InlineData("BREATHING", 1)]
    [InlineData("NEON", 2)]
    [InlineData("WAVE", 3)]
    [InlineData("SHIFTING", 4)]
    [InlineData("ZOOM", 5)]
    public void EncodeKbEffect_CarriesTheModeSpeedBrightnessAndColour(string effect, byte expectedMode)
    {
        var mode = AcerWmiProtocol.KbModeFor(effect);
        Assert.Equal(expectedMode, mode);

        var payload = AcerWmiProtocol.EncodeKbEffect(mode!.Value, 4, 75, 2, 0x11, 0x22, 0x33);

        Assert.Equal(16, payload.Length);
        Assert.Equal(expectedMode, payload[0]);
        Assert.Equal(4, payload[1]);
        Assert.Equal(75, payload[2]);
        Assert.Equal(2, payload[4]);
        Assert.Equal(0x11, payload[5]);
        Assert.Equal(0x22, payload[6]);
        Assert.Equal(0x33, payload[7]);
        Assert.Equal(0x03, payload[8]);
        Assert.Equal(0x01, payload[9]);
        Assert.Equal(0, payload[10]);
    }

    [Fact]
    public void EncodeKbEffect_SetsTheWaveFlagOnlyForWave()
    {
        // Byte 3 is the wave flag: 0x08 for WAVE, 0x00 for every other mode. A firmware that sees 0x08 on a
        // non-wave mode ignores the whole payload.
        Assert.Equal(0x08, AcerWmiProtocol.EncodeKbEffect(AcerWmiProtocol.KbModeWave, 3, 100, 1, 0, 0, 0)[3]);

        foreach (var mode in new[]
                 {
                     AcerWmiProtocol.KbModeStatic, AcerWmiProtocol.KbModeBreathing, AcerWmiProtocol.KbModeNeon,
                     AcerWmiProtocol.KbModeShifting, AcerWmiProtocol.KbModeZoom
                 })
        {
            Assert.Equal(0x00, AcerWmiProtocol.EncodeKbEffect(mode, 3, 100, 1, 0, 0, 0)[3]);
        }
    }

    [Fact]
    public void EncodeKbEffect_ClampsSpeedAndDirection()
    {
        var low = AcerWmiProtocol.EncodeKbEffect(AcerWmiProtocol.KbModeBreathing, 0, 100, 0, 0, 0, 0);
        Assert.Equal(1, low[1]);
        Assert.Equal(1, low[4]);

        var high = AcerWmiProtocol.EncodeKbEffect(AcerWmiProtocol.KbModeBreathing, 99, 100, 99, 0, 0, 0);
        Assert.Equal(AcerWmiProtocol.MaxEffectSpeed, high[1]);
        Assert.Equal(AcerWmiProtocol.MaxEffectDirection, high[4]);
    }

    [Theory]
    [InlineData("STATIC", 0)]
    [InlineData("static", 0)]
    [InlineData("DIRECT", 0)]
    [InlineData("BREATHING", 1)]
    [InlineData("NEON", 2)]
    [InlineData("WAVE", 3)]
    [InlineData("SHIFTING", 4)]
    [InlineData("ZOOM", 5)]
    public void KbModeFor_MapsTheAppEffectNames(string effect, byte expected)
    {
        Assert.Equal(expected, AcerWmiProtocol.KbModeFor(effect));
    }

    [Theory]
    [InlineData("OFF")]
    [InlineData("METEOR")]
    [InlineData("TWINKLING")]
    [InlineData("")]
    [InlineData(null)]
    public void KbModeFor_ReturnsNullForEffectsTheFirmwareCannotAnimate(string? effect)
    {
        // OFF is a colour write, not a mode; METEOR and TWINKLING have no firmware mode on this generation.
        Assert.Null(AcerWmiProtocol.KbModeFor(effect));
    }

    [Fact]
    public void ScaleColor_AppliesTheBrightnessTheFirmwareIgnores()
    {
        // The firmware does not scale a per-zone colour by the brightness byte, so a 50 % setting sent unscaled
        // comes out at full brightness. (255,128,0) at 50 % is (127,64,0).
        Assert.Equal(((byte)127, (byte)64, (byte)0), AcerWmiProtocol.ScaleColor(255, 128, 0, 50));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 255, 255, 255)]
    [InlineData(25, 63, 63, 63)]
    public void ScaleColor_HandlesTheEndpoints(int percent, byte red, byte green, byte blue)
    {
        Assert.Equal((red, green, blue), AcerWmiProtocol.ScaleColor(255, 255, 255, percent));
    }

    [Fact]
    public void ScaleColor_ClampsAnOutOfRangeBrightness()
    {
        Assert.Equal(((byte)0, (byte)0, (byte)0), AcerWmiProtocol.ScaleColor(255, 255, 255, -50));
        Assert.Equal(((byte)255, (byte)255, (byte)255), AcerWmiProtocol.ScaleColor(255, 255, 255, 500));
    }

    [Fact]
    public void ToLittleEndianBytes_EncodesTheLedArmValue()
    {
        // 0x08 | (0xF << 40): the "set on" bit plus all four zone bits, as the eight-byte array SetGamingLED takes.
        Assert.Equal(
            new byte[] { 0x08, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00 },
            AcerWmiProtocol.ToLittleEndianBytes(AcerWmiProtocol.LedArmValue));
    }

    [Fact]
    public void LedArmValue_SetsTheOnBitAndEveryZoneBit()
    {
        Assert.Equal(0x08UL, AcerWmiProtocol.LedArmValue & 0x08UL);
        Assert.Equal(0xFUL, (AcerWmiProtocol.LedArmValue >> 40) & 0xFUL);
    }

    [Fact]
    public void DecodeKbBacklight_ReadsTheFifteenByteAnswer()
    {
        // AN16-41 answers 15 bytes; the fields live in the first eight.
        var bytes = new byte[] { 0x01, 0x04, 0x50, 0x00, 0x02, 0x11, 0x22, 0x33, 0x03, 0x01, 0, 0, 0, 0, 0 };

        var state = AcerWmiProtocol.DecodeKbBacklight(bytes);

        Assert.NotNull(state);
        Assert.Equal(1, state!.Value.Mode);
        Assert.Equal(4, state.Value.Speed);
        Assert.Equal(80, state.Value.Brightness);
        Assert.Equal(2, state.Value.Direction);
        Assert.Equal(0x11, state.Value.Red);
        Assert.Equal(0x22, state.Value.Green);
        Assert.Equal(0x33, state.Value.Blue);
        Assert.Equal(15, state.Value.Length);
    }

    [Fact]
    public void DecodeKbBacklight_ReadsTheNineByteAnswer()
    {
        // AN515-44 answers 9 bytes with the same eight-byte prefix.
        var bytes = new byte[] { 0x00, 0x03, 0x64, 0x00, 0x01, 0xAA, 0xBB, 0xCC, 0x03 };

        var state = AcerWmiProtocol.DecodeKbBacklight(bytes);

        Assert.NotNull(state);
        Assert.Equal(0, state!.Value.Mode);
        Assert.Equal(100, state.Value.Brightness);
        Assert.Equal(0xAA, state.Value.Red);
        Assert.Equal(9, state.Value.Length);
    }

    [Fact]
    public void DecodeKbBacklight_RefusesAnAnswerShorterThanTheFields()
    {
        // Seven bytes cannot hold the colour, so the answer is reported as unreadable rather than half-decoded.
        Assert.Null(AcerWmiProtocol.DecodeKbBacklight(new byte[] { 0, 0, 100, 0, 1, 0xFF, 0x00 }));
        Assert.Null(AcerWmiProtocol.DecodeKbBacklight(Array.Empty<byte>()));
        Assert.Null(AcerWmiProtocol.DecodeKbBacklight(null));
    }

    // ---------------------------------------------------------------- worker validation

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(15, true)]
    [InlineData(0, false)]
    [InlineData(16, false)]
    [InlineData(-1, false)]
    public void IsValidLedZoneMask_AcceptsOnlyTheFourZoneBits(int mask, bool expected)
    {
        Assert.Equal(expected, WmiWorkerValidation.IsValidLedZoneMask(mask));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(255, true)]
    [InlineData(-1, false)]
    [InlineData(256, false)]
    public void IsValidColorChannel_AcceptsOnlyAByte(int value, bool expected)
    {
        Assert.Equal(expected, WmiWorkerValidation.IsValidColorChannel(value));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(-1, false)]
    [InlineData(101, false)]
    public void IsValidBrightnessPercent_AcceptsOnlyZeroToOneHundred(int value, bool expected)
    {
        Assert.Equal(expected, WmiWorkerValidation.IsValidBrightnessPercent(value));
    }

    [Theory]
    [InlineData(0x000000, true)]
    [InlineData(0xFFFFFF, true)]
    [InlineData(-1, false)]
    [InlineData(0x1000000, false)]
    public void IsValidPackedColor_AcceptsOnlyTwentyFourBits(int value, bool expected)
    {
        Assert.Equal(expected, WmiWorkerValidation.IsValidPackedColor(value));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(9, true)]
    [InlineData(0, false)]
    [InlineData(10, false)]
    public void IsValidEffectSpeed_AcceptsOneToNine(int value, bool expected)
    {
        Assert.Equal(expected, WmiWorkerValidation.IsValidEffectSpeed(value));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    [InlineData(5, false)]
    public void IsValidEffectDirection_AcceptsOneToFour(int value, bool expected)
    {
        Assert.Equal(expected, WmiWorkerValidation.IsValidEffectDirection(value));
    }

    // ---------------------------------------------------------------- worker dispatch

    private static WmiWorkerServer ServerWith(FakeLedExecutor executor) =>
        new("nitrolite-test-pipe", executor);

    [Fact]
    public void Dispatch_SetKeyboardLighting_ForwardsTheEffectAndZones()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardLighting,
            Args = new JsonObject
            {
                ["effect"] = "STATIC",
                ["brightness"] = 50,
                ["speed"] = 3,
                ["direction"] = 1,
                ["zones"] = new JsonArray(0xFF0000, 0x00FF00, 0x0000FF, 0xFFFFFF)
            }
        });

        Assert.True(response.Ok);
        Assert.Equal(1, executor.SetCalls);

        var write = executor.LastWrite!;
        Assert.Equal("STATIC", write.Effect);
        Assert.Equal(50, write.Brightness);
        Assert.Equal(3, write.Speed);
        Assert.Equal(1, write.Direction);
        Assert.Equal(new[] { 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFFFF }, write.Zones);
    }

    [Fact]
    public void Dispatch_SetKeyboardLighting_RejectsAnUnknownEffect()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardLighting,
            Args = LightingArgs(effect: "DISCO")
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCalls);
    }

    [Fact]
    public void Dispatch_SetKeyboardLighting_RejectsAThreeZoneList()
    {
        // A short list would leave the fourth zone on whatever colour the last write left it.
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardLighting,
            Args = LightingArgs(zones: new JsonArray(0xFF0000, 0x00FF00, 0x0000FF))
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCalls);
    }

    [Fact]
    public void Dispatch_SetKeyboardLighting_RejectsAnOutOfRangeBrightness()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardLighting,
            Args = LightingArgs(brightness: 101)
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCalls);
    }

    [Fact]
    public void Dispatch_SetKeyboardLighting_RejectsAnOutOfRangeSpeed()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardLighting,
            Args = LightingArgs(speed: 0)
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCalls);
    }

    [Fact]
    public void Dispatch_SetKeyboardLighting_RejectsAnOutOfRangeDirection()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardLighting,
            Args = LightingArgs(direction: 5)
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCalls);
    }

    [Fact]
    public void Dispatch_SetKeyboardLighting_RejectsAColourAboveTwentyFourBits()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardLighting,
            Args = LightingArgs(zones: new JsonArray(0x1FF0000, 0, 0, 0))
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCalls);
    }

    [Fact]
    public void Dispatch_SetKeyboardLighting_RejectsMissingArguments()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardLighting,
            Args = new JsonObject { ["effect"] = "STATIC" }
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCalls);
    }

    private static JsonObject LightingArgs(
        string effect = "STATIC",
        int brightness = 100,
        int speed = 3,
        int direction = 1,
        JsonArray? zones = null) => new()
    {
        ["effect"] = effect,
        ["brightness"] = brightness,
        ["speed"] = speed,
        ["direction"] = direction,
        ["zones"] = zones ?? new JsonArray(0xFF0000, 0x00FF00, 0x0000FF, 0xFFFFFF)
    };

    [Fact]
    public void Dispatch_GetKeyboardBacklight_ReachesTheExecutor()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest { Command = WmiWorkerCommand.GetKeyboardBacklight });

        Assert.True(response.Ok);
        Assert.Equal(1, executor.GetCalls);
        Assert.Equal(100, response.Data!["brightness"]!.GetValue<int>());
    }

    [Fact]
    public void WorkerCommandNumbers_AreStableOnTheWire()
    {
        // The enum is serialised as a number, so renaming a member must not renumber the protocol.
        Assert.Equal(9, (int)WmiWorkerCommand.GetKeyboardBacklight);
        Assert.Equal(10, (int)WmiWorkerCommand.SetKeyboardLighting);
    }

    // ---------------------------------------------------------------- read-back

    [Fact]
    public async Task ReadBack_KeyboardLighting_AppliesWhenTheColourMatches()
    {
        var settings = new AppSettings();
        var policy = new ReadBackPolicy(settings, NoDelay);

        var result = await policy.ApplyAsync(
            ReadBackPolicy.KeyboardLighting,
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<int?>(0x00FF00),
            matches: value => value == 0x00FF00);

        Assert.True(result.Applied);
        Assert.Equal(1, result.Attempts);
        Assert.False(policy.IsDisabled(ReadBackPolicy.KeyboardLighting));
    }

    [Fact]
    public async Task ReadBack_KeyboardLighting_DisablesTheSessionAfterThreeMismatches()
    {
        var settings = new AppSettings();
        var policy = new ReadBackPolicy(settings, NoDelay);

        var result = await policy.ApplyAsync(
            ReadBackPolicy.KeyboardLighting,
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<int?>(0x000000),
            matches: value => value == 0x00FF00);

        Assert.False(result.Applied);
        Assert.True(result.DisabledForSession);
        Assert.True(policy.IsDisabled(ReadBackPolicy.KeyboardLighting));
        Assert.Equal(1, policy.ConsecutiveSessionFailures(ReadBackPolicy.KeyboardLighting));
    }

    [Fact]
    public async Task ReadBack_KeyboardLighting_IsPermanentAfterThreeConsecutiveSessions()
    {
        var settings = new AppSettings();
        settings.CapabilitySessionFailures[ReadBackPolicy.KeyboardLighting] = 2;

        var policy = new ReadBackPolicy(settings, NoDelay);
        var result = await policy.ApplyAsync(
            ReadBackPolicy.KeyboardLighting,
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<int?>(0x000000),
            matches: value => value == 0x00FF00);

        Assert.True(result.PermanentlyDisabled);
        Assert.True(policy.IsPermanentlyDisabled(ReadBackPolicy.KeyboardLighting));
    }

    [Fact]
    public async Task ReadBack_KeyboardLighting_RescanBringsItBack()
    {
        var settings = new AppSettings();
        settings.CapabilitySessionFailures[ReadBackPolicy.KeyboardLighting] = 2;

        var policy = new ReadBackPolicy(settings, NoDelay);
        await policy.ApplyAsync(
            ReadBackPolicy.KeyboardLighting,
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<int?>(0x000000),
            matches: value => value == 0x00FF00);

        Assert.True(policy.IsPermanentlyDisabled(ReadBackPolicy.KeyboardLighting));

        policy.ResetAll();

        Assert.False(policy.IsPermanentlyDisabled(ReadBackPolicy.KeyboardLighting));
        Assert.False(policy.IsDisabled(ReadBackPolicy.KeyboardLighting));
        Assert.Equal(0, policy.ConsecutiveSessionFailures(ReadBackPolicy.KeyboardLighting));
    }

    // ---------------------------------------------------------------- sensor layout cleanup

    [Fact]
    public void DefaultSensorLayout_IsNotSupported()
    {
        // No sensor id has been confirmed on a machine the table does not name. Decoding one anyway would print a
        // number that is not a temperature.
        Assert.False(SensorLayout.Default.Supported);
        Assert.False(SensorLayout.Default.Verified);
    }

    [Fact]
    public void EmbeddedProfiles_ParseWithoutRejections()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out var rejections);

        Assert.Empty(rejections);
        Assert.Contains(catalog.Profiles, p => p.Id == "nitro-16-an16-41");
    }

    [Fact]
    public void EmbeddedProfiles_DeclareTheFirmwareSensorIds()
    {
        // The ids are the firmware-wide enum, so every shipped profile carries them. Only the reference machine has
        // been measured, so only it is marked verified. The one profile whose firmware refused every sensor read in a
        // report (NL16-71G, issue 11) is marked unsupported; the ids stay, so the layout is still a valid record.
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

        Assert.All(catalog.Profiles, p => Assert.Equal(p.Id != "nitro-lite-16-nl16-71g", p.Sensors.Supported));
        Assert.All(catalog.Profiles, p => Assert.Equal(1, p.Sensors.CpuTempSensor));
        Assert.All(catalog.Profiles, p => Assert.Equal(2, p.Sensors.CpuFanRpmSensor));
        Assert.All(catalog.Profiles, p => Assert.Equal(3, p.Sensors.Sys1TempSensor));
        Assert.All(catalog.Profiles, p => Assert.Equal(6, p.Sensors.GpuFanRpmSensor));
        Assert.All(catalog.Profiles, p => Assert.Equal(10, p.Sensors.GpuTempSensor));

        var reference = catalog.Profiles.Single(p => p.Id == "nitro-16-an16-41");
        Assert.True(reference.Sensors.Verified);

        Assert.All(
            catalog.Profiles.Where(p => p.Id != "nitro-16-an16-41"),
            p => Assert.False(p.Sensors.Verified));
    }

    [Fact]
    public void UserProfile_CanDeclareSupportedSensors()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "my-nitro",
              "match": { "modelCodes": [ "AN16-41" ] },
              "sensors": { "supported": true, "verified": true, "cpuTempSensor": 11, "gpuTempSensor": 12, "sys1TempSensor": 13 },
              "maxFanRpm": { "cpu": 6200, "gpu": 6000 }
            }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);

        Assert.Empty(rejections);
        var profile = catalog.Profiles.Single(p => p.Id == "my-nitro");
        Assert.True(profile.Sensors.Supported);
        Assert.True(profile.Sensors.Verified);
        Assert.Equal(11, profile.Sensors.CpuTempSensor);
        Assert.Equal(12, profile.Sensors.GpuTempSensor);
        Assert.Equal(13, profile.Sensors.Sys1TempSensor);
    }

    [Fact]
    public void UserProfile_RejectsAnUnknownSensorField()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "my-nitro",
              "match": { "modelCodes": [ "AN16-41" ] },
              "sensors": { "supported": true, "cpuTempSensor": 3, "bogusField": 1 }
            }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);

        Assert.NotEmpty(rejections);
        Assert.DoesNotContain(catalog.Profiles, p => p.Id == "my-nitro");
    }

    [Fact]
    public void UserProfile_RejectsTheRetiredByteOffsetSchema()
    {
        // The old schema described byte offsets into a packed page that GetGamingSysInfo does not return. A file
        // still carrying it is refused rather than silently reinterpreted as sensor ids.
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "my-nitro",
              "match": { "modelCodes": [ "AN16-41" ] },
              "sensors": { "supported": true, "cpuTempByte": 3, "gpuTempByte": 4 }
            }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);

        Assert.NotEmpty(rejections);
        Assert.DoesNotContain(catalog.Profiles, p => p.Id == "my-nitro");
    }

    // ---------------------------------------------------------------- effect names and the HID quirk

    [Theory]
    [InlineData(0, "STATIC")]
    [InlineData(1, "BREATHING")]
    [InlineData(2, "NEON")]
    [InlineData(3, "WAVE")]
    [InlineData(4, "SHIFTING")]
    [InlineData(5, "ZOOM")]
    [InlineData(99, "STATIC")]
    public void EffectNameFor_MapsTheFirmwareModeBackToTheAppName(int mode, string expected)
    {
        Assert.Equal(expected, WmiHardwareController.EffectNameFor(mode));
    }

    [Fact]
    public void EffectNames_RoundTripThroughTheFirmwareMode()
    {
        // Every effect the app can name must survive a write and a read-back, or the lighting page would show a
        // different effect than the one the keyboard is running.
        foreach (var effect in new[] { "STATIC", "BREATHING", "NEON", "WAVE", "SHIFTING", "ZOOM" })
        {
            var mode = AcerWmiProtocol.KbModeFor(effect);
            Assert.NotNull(mode);
            Assert.Equal(effect, WmiHardwareController.EffectNameFor(mode!.Value));
        }
    }

    [Fact]
    public void EmbeddedProfiles_DeclareTheNitroHidKeyboardQuirk()
    {
        // ANV16S-41 and AN16-61 accept the WMI lighting writes and silently ignore them, so the profile has to say so
        // or the lighting page would report success while the keyboard stays dark.
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var declared = new[] { "nitro-v-16-anv16s-41", "nitro-16-an16-61" };

        foreach (var id in declared)
        {
            var hid = catalog.Profiles.Single(p => p.Id == id);
            Assert.True(hid.NitroHidKeyboard);
            Assert.True(hid.FourZoneKeyboard);
        }

        Assert.All(
            catalog.Profiles.Where(p => !declared.Contains(p.Id)),
            p => Assert.False(p.NitroHidKeyboard));
    }

    [Fact]
    public void UserProfile_CanDeclareTheNitroHidKeyboardQuirk()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "my-hid-nitro",
              "match": { "modelCodes": [ "ANV16S-41" ] },
              "nitroHidKeyboard": true
            }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);

        Assert.Empty(rejections);
        Assert.True(catalog.Profiles.Single(p => p.Id == "my-hid-nitro").NitroHidKeyboard);
    }

    // ---------------------------------------------------------------- fake executor

    /// <summary>
    /// Answers the two keyboard commands and records what it was asked. Nothing here touches WMI, so the dispatch
    /// tests run on any machine.
    /// </summary>
    private sealed class FakeLedExecutor : IWmiExecutor
    {
        public int GetCalls { get; private set; }
        public int SetCalls { get; private set; }
        public KeyboardLightingRequest? LastWrite { get; private set; }

        public bool IsAvailable => true;

        public WmiOperationResult Ping() => WmiOperationResult.Success(new JsonObject { ["pong"] = true });

        public WmiOperationResult GetCapabilities() => WmiOperationResult.Success(new JsonObject
        {
            ["gamingClass"] = true,
            ["keyboardLighting"] = true
        });

        public WmiOperationResult ReadTelemetry(SensorQuery query) => WmiOperationResult.Success(new JsonObject());

        public WmiOperationResult ReadDeviceState() => WmiOperationResult.Success(new JsonObject());

        public WmiOperationResult SetFanBehavior(FanMode cpu, FanMode gpu) => WmiOperationResult.Success();

        public WmiOperationResult SetFanSpeed(int cpuPercent, int gpuPercent) => WmiOperationResult.Success();

        public WmiOperationResult SetOperatingMode(OperatingMode mode) => WmiOperationResult.Success();

        public WmiOperationResult SetMuxMode(GpuOperatingMode mode) => WmiOperationResult.Success();

        public WmiOperationResult SetBatteryLimit(BatteryChargeMode mode) => WmiOperationResult.Success();

        public WmiOperationResult GetKeyboardBacklight()
        {
            GetCalls++;
            return WmiOperationResult.Success(new JsonObject
            {
                ["brightness"] = 100,
                ["red"] = 0,
                ["green"] = 255,
                ["blue"] = 0,
                ["rgbRaw"] = 0x0000000000FF0000UL
            });
        }

        public WmiOperationResult SetKeyboardLighting(KeyboardLightingRequest request)
        {
            SetCalls++;
            LastWrite = request;
            return WmiOperationResult.Success(new JsonObject { ["effect"] = request.Effect });
        }

        public WmiOperationResult GetCoolBoost() => WmiOperationResult.NotSupported("APGeAction is not registered");

        public WmiOperationResult SetCoolBoost(bool on) => WmiOperationResult.NotSupported("APGeAction is not registered");

        public WmiOperationResult GetLcdOverdrive() => WmiOperationResult.NotSupported("panel has no overdrive");

        public WmiOperationResult SetLcdOverdrive(bool on) => WmiOperationResult.NotSupported("panel has no overdrive");

        public WmiOperationResult SetWinKeyLock(bool enabled) => WmiOperationResult.Success();

        public WmiOperationResult GetBacklightTimeout() => WmiOperationResult.NotSupported("APGeAction is not registered");

        public WmiOperationResult SetBacklightTimeout(bool enabled) => WmiOperationResult.NotSupported("APGeAction is not registered");

        public WmiOperationResult ProbeModeSelectors() => WmiOperationResult.NotSupported("no selector sweep in this fake");

        public void Dispose() { }
    }
}
