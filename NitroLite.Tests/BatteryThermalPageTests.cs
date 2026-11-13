using System.Collections.Generic;
using System.Linq;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class InternalPanelTests
{
    [Theory]
    [InlineData(11u, true)]          // DisplayPort embedded (eDP) – the AN16-41 panel
    [InlineData(6u, true)]           // LVDS
    [InlineData(0x80000000u, true)]  // INTERNAL
    [InlineData(10u, false)]         // external DisplayPort – the Lenovo monitor on this machine
    [InlineData(5u, false)]          // HDMI
    public void OutputTechnology_DistinguishesBuiltInPanel(uint technology, bool expected)
    {
        Assert.Equal(expected, DisplayRefreshRateService.IsInternalOutputTechnology(technology));
    }

    [Fact]
    public void LiveQuery_FindsAGdiDeviceNameOrNothing()
    {
        // Read-only QueryDisplayConfig; on a laptop with the lid open it returns e.g. "\\.\DISPLAY1".
        var name = DisplayRefreshRateService.FindInternalPanelDeviceName();
        Assert.True(name == null || name.StartsWith(@"\\.\DISPLAY"), $"Unexpected device name '{name}'");
    }
}

public class BatteryIdentityTests
{
    [Theory]
    [InlineData("34643LGCAP21A8T", "AP21A8T", "LGC")]
    [InlineData("AP21A8T", "AP21A8T", "")]        // nothing besides the name
    [InlineData("12345SMPXYZ", "ABC", "")]        // name not at the end
    [InlineData(null, "AP21A8T", "")]
    public void ManufacturerIsParsedFromDeviceId(string? deviceId, string name, string expected)
    {
        Assert.Equal(expected, BatteryWatchdogService.ParseBatteryManufacturer(deviceId, name));
    }

    [Theory]
    [InlineData(6, "Lityum-iyon")]
    [InlineData(8, "Lityum polimer")]
    [InlineData(0, "")]
    public void ChemistryCodesMapToNames(int code, string expected)
    {
        Assert.Equal(expected, BatteryWatchdogService.BatteryChemistryName(code));
    }
}
