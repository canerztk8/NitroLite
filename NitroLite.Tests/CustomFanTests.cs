using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class CustomFanTests
{
    [Fact]
    public void CustomMode_SendsManualSpeedsForBothFans()
    {
        var json = JsonSerializer.Serialize(AcerAgentClient.BuildFanControlParameter(FanMode.Custom, 60, 75));
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal(2, root.GetProperty("mode").GetInt32());
        var fans = root.GetProperty("custom_fan_data");
        Assert.Equal("CPU", fans[0].GetProperty("fan_name").GetString());
        Assert.Equal(0, fans[0].GetProperty("fan_custom_auto").GetInt32());
        Assert.Equal(60, fans[0].GetProperty("fan_custom_speed").GetInt32());
        Assert.Equal("GPU", fans[1].GetProperty("fan_name").GetString());
        Assert.Equal(75, fans[1].GetProperty("fan_custom_speed").GetInt32());
    }

    [Fact]
    public void AutoMode_MarksFansAutomatic()
    {
        var json = JsonSerializer.Serialize(AcerAgentClient.BuildFanControlParameter(FanMode.Auto, 60, 60));
        var fans = JsonDocument.Parse(json).RootElement.GetProperty("custom_fan_data");
        Assert.Equal(1, fans[0].GetProperty("fan_custom_auto").GetInt32());
        Assert.Equal(1, fans[1].GetProperty("fan_custom_auto").GetInt32());
    }

    [Theory]
    [InlineData(10, 30)]
    [InlineData(30, 30)]
    [InlineData(65, 65)]
    [InlineData(150, 100)]
    public void SavedSpeedsAreKeptInTheOfferedRange(int value, int expected)
    {
        Assert.Equal(expected, AppSettings.ClampCustomFanPercent(value));
    }

    [Fact]
    public void Settings_RoundTripCustomSpeeds()
    {
        var json = JsonSerializer.Serialize(new AppSettings { CustomFanCpuPercent = 70, CustomFanGpuPercent = 45 });
        Assert.Contains("\"custom_fan_cpu_percent\":70", json);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(70, back.CustomFanCpuPercent);
        Assert.Equal(45, back.CustomFanGpuPercent);
        Assert.Equal(50, new AppSettings().CustomFanCpuPercent);
    }

    [Fact]
    public void Client_DefaultsToTheStoredCustomSpeeds()
    {
        var client = new AcerAgentClient { CustomFanCpuPercent = 65, CustomFanGpuPercent = 80 };
        Assert.Equal(65, client.CustomFanCpuPercent);
        Assert.Equal(80, client.CustomFanGpuPercent);
    }
}
