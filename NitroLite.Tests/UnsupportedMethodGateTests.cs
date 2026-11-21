using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>The profile's unsupportedMethods, enforced: a listed method switches off the control it drives.</summary>
public class UnsupportedMethodGateTests
{
    private static readonly HardwareCapabilities Everything = new()
    {
        FanControl = true,
        KeyboardLighting = true,
        BatteryChargeLimit = true,
        OperatingModeControl = true
    };

    [Fact]
    public void ListedFanMethods_SwitchFanControlOff_AndNothingElse()
    {
        var profile = new ModelProfile { Id = "x", UnsupportedMethods = new[] { "SetGamingFanSpeed", "SetGamingFanBehavior" } };

        var gated = UnsupportedMethodGate.Apply(Everything, profile, out var disabled);

        Assert.False(gated.FanControl);
        Assert.True(gated.KeyboardLighting);
        Assert.True(gated.BatteryChargeLimit);
        Assert.True(gated.OperatingModeControl);
        Assert.Contains(disabled, d => d.Contains("FanControl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SetGamingRgbKb", nameof(HardwareCapabilities.KeyboardLighting))]
    [InlineData("SetGamingKBBacklight", nameof(HardwareCapabilities.KeyboardLighting))]
    [InlineData("SetBatteryHealthControl", nameof(HardwareCapabilities.BatteryChargeLimit))]
    public void EachKnownMethod_MapsToItsControl(string method, string capability) =>
        Assert.Equal(capability, UnsupportedMethodGate.Methods[method]);

    [Fact]
    public void AnUnknownName_IsReportedNotGuessed()
    {
        var profile = new ModelProfile { Id = "x", UnsupportedMethods = new[] { "SetSomethingNew" } };

        var gated = UnsupportedMethodGate.Apply(Everything, profile, out var disabled);

        Assert.Equal(Everything, gated);
        Assert.Contains(disabled, d => d.Contains("SetSomethingNew", StringComparison.Ordinal) && d.Contains("ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void ItCanOnlyTakeAway_NeverSwitchOn()
    {
        var none = new HardwareCapabilities();
        var profile = new ModelProfile { Id = "x", UnsupportedMethods = new[] { "SetGamingFanSpeed" } };

        Assert.False(UnsupportedMethodGate.Apply(none, profile, out _).FanControl);
        Assert.Equal(none, UnsupportedMethodGate.Apply(none, null, out _));
    }
}
