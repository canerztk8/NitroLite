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
/// answered <c>0x00000000</c> and applied the colour. <c>SetGamingKBBacklight</c> rejected every payload tried, which
/// is why it is not used.
/// </para>
/// </summary>
public class HalStep7Tests
{
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

    // ---------------------------------------------------------------- worker dispatch

    private static WmiWorkerServer ServerWith(FakeLedExecutor executor) =>
        new("nitrolite-test-pipe", "nonce", executor);

    [Fact]
    public void Dispatch_SetKeyboardBacklight_ForwardsTheZoneMaskAndColour()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardBacklight,
            Args = new JsonObject { ["zoneMask"] = 0x04, ["red"] = 0x12, ["green"] = 0x34, ["blue"] = 0x56 }
        });

        Assert.True(response.Ok);
        Assert.Equal(1, executor.SetCalls);
        Assert.Equal((0x04, 0x12, 0x34, 0x56), executor.LastWrite);
    }

    [Fact]
    public void Dispatch_SetKeyboardBacklight_RejectsAZeroZoneMask()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardBacklight,
            Args = new JsonObject { ["zoneMask"] = 0, ["red"] = 0xFF, ["green"] = 0, ["blue"] = 0 }
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCalls);
    }

    [Fact]
    public void Dispatch_SetKeyboardBacklight_RejectsAColourChannelAbove255()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardBacklight,
            Args = new JsonObject { ["zoneMask"] = 0x01, ["red"] = 300, ["green"] = 0, ["blue"] = 0 }
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCalls);
    }

    [Fact]
    public void Dispatch_SetKeyboardBacklight_RejectsMissingArguments()
    {
        var executor = new FakeLedExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetKeyboardBacklight,
            Args = new JsonObject { ["zoneMask"] = 0x01 }
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCalls);
    }

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
        Assert.Equal(10, (int)WmiWorkerCommand.SetKeyboardBacklight);
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
        // No shipped offset has ever been confirmed against hardware, and on an AN16-41 the bytes are static
        // configuration rather than a sensor page. Decoding them would print a number that is not a temperature.
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
    public void EmbeddedProfiles_ClaimNoLiveSensors()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

        Assert.All(catalog.Profiles, p => Assert.False(p.Sensors.Supported));
        Assert.All(catalog.Profiles, p => Assert.False(p.Sensors.Verified));
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
              "sensors": { "supported": true, "verified": true, "cpuTempByte": 3, "gpuTempByte": 4, "sys1TempByte": 5 },
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
        Assert.Equal(3, profile.Sensors.CpuTempByte);
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
              "sensors": { "supported": true, "cpuTempByte": 3, "bogusField": 1 }
            }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);

        Assert.NotEmpty(rejections);
        Assert.DoesNotContain(catalog.Profiles, p => p.Id == "my-nitro");
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
        public (int Mask, int Red, int Green, int Blue) LastWrite { get; private set; }

        public bool IsAvailable => true;

        public WmiOperationResult Ping() => WmiOperationResult.Success(new JsonObject { ["pong"] = true });

        public WmiOperationResult GetCapabilities() => WmiOperationResult.Success(new JsonObject
        {
            ["gamingClass"] = true,
            ["keyboardLighting"] = true
        });

        public WmiOperationResult ReadTelemetry() => WmiOperationResult.Success(new JsonObject());

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

        public WmiOperationResult SetKeyboardBacklight(int zoneMask, int red, int green, int blue)
        {
            SetCalls++;
            LastWrite = (zoneMask, red, green, blue);
            return WmiOperationResult.Success(new JsonObject { ["zoneMask"] = zoneMask });
        }

        public void Dispose() { }
    }
}
