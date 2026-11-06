using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class ModeSwitchKeyTests
{
    [Theory]
    [InlineData(OperatingMode.Default, OperatingMode.Extreme)]
    [InlineData(OperatingMode.Extreme, OperatingMode.Turbo)]
    [InlineData(OperatingMode.Turbo, OperatingMode.Quiet)]
    [InlineData(OperatingMode.Quiet, OperatingMode.Default)]
    [InlineData(OperatingMode.Eco, OperatingMode.Default)]
    public void OnAdapter_FollowsNitroSenseCycle(OperatingMode current, OperatingMode expected)
    {
        Assert.Equal(expected, ModeSwitchKey.Next(current, onAc: true));
    }

    [Theory]
    [InlineData(OperatingMode.Default, OperatingMode.Quiet)]
    [InlineData(OperatingMode.Quiet, OperatingMode.Default)]
    [InlineData(OperatingMode.Turbo, OperatingMode.Default)]
    [InlineData(OperatingMode.Eco, OperatingMode.Default)]
    public void OnBattery_NeverSelectsHighPowerModes(OperatingMode current, OperatingMode expected)
    {
        var next = ModeSwitchKey.Next(current, onAc: false);
        Assert.Equal(expected, next);
        Assert.NotEqual(OperatingMode.Turbo, next);
        Assert.NotEqual(OperatingMode.Extreme, next);
    }
}
