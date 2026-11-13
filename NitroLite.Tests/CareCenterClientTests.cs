using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class CareCenterClientTests
{
    [Fact]
    public void ExpectedAuthAnswer_MatchesWhatAcerCCAgentSends()
    {
        // Captured from the live AcerCCAgent (wss://localhost:4343) handshake reply on the AN16-41.
        const string observed = "l6bNwzMCXklbG39DV6M/szNBzpkl57IGwcAO1XO4kcydjaDmqQbwq09WKyg0wgphS1XAyYsBiggAYavHUY+wMQ==";
        Assert.Equal(observed, CareCenterClient.ExpectedAuthAnswer());
    }

    [Fact]
    public void BuildAuthData_DecryptsToQuestionAndKeyB()
    {
        var cipher = Convert.FromBase64String(CareCenterClient.BuildAuthData());
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(CareCenterClient.KeyA);
        var plain = Encoding.UTF8.GetString(aes.DecryptEcb(cipher, PaddingMode.PKCS7));

        var node = JsonNode.Parse(plain)!;
        Assert.Equal(CareCenterClient.Question, node["Question"]!.GetValue<string>());
        Assert.Equal(CareCenterClient.KeyB, node["Key"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"Result\":{\"Value\":0},\"PacketType\":3}", 0)]
    [InlineData("{\"Result\":{\"Value\":1},\"PacketType\":3}", 1)]
    public void TryGetResultValue_ReadsBatteryHealthyValue(string json, int expected)
    {
        Assert.Equal(expected, CareCenterClient.TryGetResultValue(JsonNode.Parse(json)));
    }

    [Fact]
    public void ChargeModeValues_MatchNitroSenseEnum()
    {
        // NitroSense BATTERY_CHARGE_MODE: OptimizedBatteryCharging=0, FullBatteryCharging=1, AdaptiveBatteryCharging=2
        Assert.Equal(0, (int)BatteryChargeMode.Optimized80);
        Assert.Equal(1, (int)BatteryChargeMode.Full);
        Assert.Equal(2, (int)BatteryChargeMode.Adaptive);
    }

    [Fact]
    public void DescribeChargeLimit_NoAnswerMeansNotControllable()
    {
        // A machine without AcerCCAgent must not show a ticked box for a limit nothing enforces.
        var state = CareCenterClient.DescribeChargeLimit(null);
        Assert.False(state.Available);
        Assert.False(state.Limited);
    }

    [Theory]
    [InlineData(BatteryChargeMode.Optimized80, true)]
    [InlineData(BatteryChargeMode.Full, false)]
    [InlineData(BatteryChargeMode.Adaptive, false)]
    public void DescribeChargeLimit_ReportsTheLimitOnlyForOptimized80(BatteryChargeMode mode, bool expected)
    {
        var state = CareCenterClient.DescribeChargeLimit(mode);
        Assert.True(state.Available);
        Assert.Equal(expected, state.Limited);
    }
}
