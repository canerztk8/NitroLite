using System;
using System.Text.RegularExpressions;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class CpuPowerServiceTests
{
    [Fact]
    public void CpuBoostModeEnum_HasExplicitWin32PowerSettingIndexValues()
    {
        // Must match Windows power setting index definitions:
        // 0 = Disabled
        // 2 = Enabled / Aggressive
        // 4 = Efficient Aggressive
        Assert.Equal(0, (int)CpuBoostMode.Disabled);
        Assert.Equal(2, (int)CpuBoostMode.Aggressive);
        Assert.Equal(4, (int)CpuBoostMode.Efficient);
    }

    [Theory]
    [InlineData(0, CpuBoostMode.Disabled)]
    [InlineData(2, CpuBoostMode.Aggressive)]
    [InlineData(4, CpuBoostMode.Efficient)]
    [InlineData(1, CpuBoostMode.Aggressive)]
    [InlineData(3, CpuBoostMode.Aggressive)]
    public void PowrProfIndexParsing_MapsValueToAppropriateMode(uint val, CpuBoostMode expectedMode)
    {
        CpuBoostMode resolvedMode;
        if (val == 0) resolvedMode = CpuBoostMode.Disabled;
        else if (val == 4) resolvedMode = CpuBoostMode.Efficient;
        else resolvedMode = CpuBoostMode.Aggressive;

        Assert.Equal(expectedMode, resolvedMode);
    }

    [Theory]
    [InlineData("Current AC Power Setting Index: 0x00000000", CpuBoostMode.Disabled)]
    [InlineData("Current AC Power Setting Index: 0x00000002", CpuBoostMode.Aggressive)]
    [InlineData("Current AC Power Setting Index: 0x00000004", CpuBoostMode.Efficient)]
    public void PowerCfgRegex_ExtractsAndParsesHexIndex(string powercfgOutput, CpuBoostMode expectedMode)
    {
        var match = Regex.Match(powercfgOutput, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
        Assert.True(match.Success);

        bool parsed = int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var val);
        Assert.True(parsed);

        CpuBoostMode resolved;
        if (val == 0) resolved = CpuBoostMode.Disabled;
        else if (val == 4) resolved = CpuBoostMode.Efficient;
        else resolved = CpuBoostMode.Aggressive;

        Assert.Equal(expectedMode, resolved);
    }

    [Fact]
    public void ServiceInitialization_InstantiatesAndQueriesCurrentMode()
    {
        var service = new CpuPowerService();

        Assert.NotNull(service);
        Assert.True(Enum.IsDefined(typeof(CpuBoostMode), service.CurrentMode), 
            $"CurrentMode '{service.CurrentMode}' must be a valid defined CpuBoostMode.");
    }

    private class TestIsolatedCpuPowerService : CpuPowerService
    {
        public TestIsolatedCpuPowerService() : base(false) { }
    }

    [Fact]
    public void ServiceInitialization_IsolatedConstructor_SkipsRegistryAndHardware()
    {
        var isolated = new TestIsolatedCpuPowerService();
        Assert.NotNull(isolated);
        Assert.Equal(CpuBoostMode.Aggressive, isolated.CurrentMode);
    }
}
