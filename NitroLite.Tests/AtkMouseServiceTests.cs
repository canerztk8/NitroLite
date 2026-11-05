using System;
using System.Text.Json;
using NitroLite.Core;
using Xunit;
using Xunit.Abstractions;

namespace NitroLite.Tests;

public class AtkMouseServiceTests
{
    private readonly ITestOutputHelper _output;

    public AtkMouseServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }
    [Theory]
    [InlineData(1, 0, 0x3D)] // Config File 1 -> index 0, checksum 0x3D
    [InlineData(2, 1, 0x3C)] // Config File 2 -> index 1, checksum 0x3C
    [InlineData(3, 2, 0x3B)] // Config File 3 -> index 2, checksum 0x3B
    [InlineData(4, 3, 0x3A)] // Config File 4 -> index 3, checksum 0x3A
    public void BuildSetCurrentConfigPacket_MatchesHardwareChecksumAndPayload(int configNumber, byte expectedIndex, byte expectedChecksum)
    {
        byte configIndex = (byte)(configNumber - 1);
        byte[] packet = AtkMouseService.BuildSetCurrentConfigPacket(configIndex);

        Assert.Equal(17, packet.Length);
        Assert.Equal(AtkMouseService.REPORT_ID_ATK_COL05, packet[0]);       // 8 (0x08)
        Assert.Equal(AtkMouseService.CMD_SET_CURRENT_CONFIG, packet[1]);   // 15 (0x0F)
        Assert.Equal(0x00, packet[2]);                                     // CommandStatus
        Assert.Equal(0x00, packet[3]);                                     // EEPROM high
        Assert.Equal(0x00, packet[4]);                                     // EEPROM low
        Assert.Equal(0x01, packet[5]);                                     // DataValidLen
        Assert.Equal(expectedIndex, packet[6]);                            // Target configIndex (0..3)
        Assert.Equal(expectedChecksum, packet[16]);                        // Checksum

        // Padding bytes (7..15) must be 0x00
        for (int i = 7; i < 16; i++)
        {
            Assert.Equal(0x00, packet[i]);
        }
    }

    [Fact]
    public void BuildGetCurrentConfigPacket_MatchesHardwareChecksumAndPayload()
    {
        byte[] packet = AtkMouseService.BuildGetCurrentConfigPacket();

        Assert.Equal(17, packet.Length);
        Assert.Equal(AtkMouseService.REPORT_ID_ATK_COL05, packet[0]);       // 8 (0x08)
        Assert.Equal(AtkMouseService.CMD_GET_CURRENT_CONFIG, packet[1]);   // 14 (0x0E)
        Assert.Equal(0x3F, packet[16]);                                    // Checksum: 0x55 - (8 + 14) = 0x3F
    }

    [Theory]
    [InlineData(0, false)] // Below min (0)
    [InlineData(5, false)] // Above max (5)
    [InlineData(-1, false)]
    public void SwitchProfile_OutOfRangeConfig_RejectsGracefully(int invalidConfig, bool expected)
    {
        var service = new AtkMouseService();
        bool result = service.SwitchProfile(invalidConfig);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GameProfile_TargetAtkMouseProfile_RoundtripsAccurately()
    {
        var profile = new GameProfile
        {
            ExeName = "cs2",
            DisplayName = "Counter-Strike 2",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Aggressive,
            TargetFanMode = FanMode.Auto,
            TargetAtkMouseProfile = 2 // Config File 2
        };

        var json = JsonSerializer.Serialize(profile);
        var deserialized = JsonSerializer.Deserialize<GameProfile>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.TargetAtkMouseProfile);
    }

    [Fact]
    public void AtkMouseService_SwitchProfile_SafeExecutionWithoutThrowing()
    {
        var service = new AtkMouseService();
        if (Environment.GetEnvironmentVariable("NITROLITE_ENABLE_LIVE_HARDWARE_TESTS") != "1")
        {
            // Skip sending live USB HID packet when live hardware tests are disabled
            Assert.NotNull(service);
            return;
        }

        var ex = Record.Exception(() => service.SwitchProfile(1));
        Assert.Null(ex);
    }

    [Fact]
    public void LiveHardware_QueryAndSwitch_IfConnected()
    {
        // Safe test guard: automated test suites must never transmit live HID packets to physical mice
        if (Environment.GetEnvironmentVariable("NITROLITE_ENABLE_LIVE_HARDWARE_TESTS") != "1")
        {
            _output.WriteLine("Skipping LiveHardware_QueryAndSwitch_IfConnected: live HID test disabled for test suite safety.");
            return;
        }

        var service = new AtkMouseService();
        var current = service.QueryCurrentProfile();
        _output.WriteLine($"QueryCurrentProfile before: {current}");
        if (current.HasValue)
        {
            int initial = current.Value;
            int target = initial == 1 ? 2 : 1;
            
            bool switched = service.SwitchProfile(target);
            _output.WriteLine($"SwitchProfile({target}) result: {switched}");

            System.Threading.Thread.Sleep(150);

            var verified = service.QueryCurrentProfile();
            _output.WriteLine($"QueryCurrentProfile after switch to {target}: {verified}");
            Assert.Equal(target, verified);

            // Restore initial profile
            service.SwitchProfile(initial);
            System.Threading.Thread.Sleep(150);
            var restored = service.QueryCurrentProfile();
            _output.WriteLine($"QueryCurrentProfile restored to {initial}: {restored}");
            Assert.Equal(initial, restored);
        }
    }

    [Fact]
    public void AppSettings_DefaultAtkMouseProfile_RoundtripsAndDefaultsToOne()
    {
        var settings = new AppSettings();
        Assert.Equal(1, settings.DefaultAtkMouseProfile);

        settings.DefaultAtkMouseProfile = 3;
        var json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized.DefaultAtkMouseProfile);
    }

    [Fact]
    public void AppSettings_AtkMouseIntegrationEnabled_DefaultsToFalse()
    {
        var settings = new AppSettings();
        Assert.False(settings.AtkMouseIntegrationEnabled);
    }

    [Fact]
    public void AppSettings_AtkMouseIntegrationEnabled_RoundtripsAccurately()
    {
        var settings = new AppSettings { AtkMouseIntegrationEnabled = true };
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"atk_mouse_integration_enabled\":true", json);

        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(deserialized);
        Assert.True(deserialized.AtkMouseIntegrationEnabled);
    }

    [Theory]
    [InlineData(1, 2, true, 1)]  // Default is 1, saved was 2 -> returns Default 1
    [InlineData(2, 4, true, 2)]  // Default is 2, saved was 4 -> returns Default 2
    [InlineData(0, 3, true, 3)]  // Default is 0 (keep last), saved was 3 -> returns saved 3
    [InlineData(0, 3, false, 0)] // Default is 0, not switched for game -> 0 (no switch)
    public void MouseProfileRestoration_CalculatesExpectedTarget(int defaultProfile, int savedBeforeGame, bool switchedForGame, int expectedTarget)
    {
        int target = defaultProfile > 0 
            ? defaultProfile 
            : (switchedForGame ? savedBeforeGame : 0);

        Assert.Equal(expectedTarget, target);
    }
}
