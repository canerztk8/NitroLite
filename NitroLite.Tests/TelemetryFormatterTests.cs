using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class TelemetryFormatterTests
{
    [Fact]
    public void CalculateFanPercent_NormalValues_ReturnsExpectedPercentage()
    {
        // 3000 RPM out of 6000 RPM should be exactly 50%
        double pct = TelemetryFormatter.CalculateFanPercent(3000, 6000);
        Assert.Equal(50.0, pct);

        // 1500 RPM out of 6000 RPM should be 25%
        pct = TelemetryFormatter.CalculateFanPercent(1500, 6000);
        Assert.Equal(25.0, pct);

        // 6000 RPM out of 6000 RPM should be 100%
        pct = TelemetryFormatter.CalculateFanPercent(6000, 6000);
        Assert.Equal(100.0, pct);
    }

    [Fact]
    public void CalculateFanPercent_OverMaxRpm_ClampsTo100Percent()
    {
        // Over 6000 RPM (e.g. 6800 RPM in turbo boost) should clamp to 100.0%
        double pct = TelemetryFormatter.CalculateFanPercent(6800, 6000);
        Assert.Equal(100.0, pct);
    }

    [Fact]
    public void CalculateFanPercent_ZeroOrNegative_ReturnsZero()
    {
        Assert.Equal(0.0, TelemetryFormatter.CalculateFanPercent(0, 6000));
        Assert.Equal(0.0, TelemetryFormatter.CalculateFanPercent(-500, 6000));
        Assert.Equal(0.0, TelemetryFormatter.CalculateFanPercent(3000, 0));
        Assert.Equal(0.0, TelemetryFormatter.CalculateFanPercent(3000, -100));
    }

    [Fact]
    public void FormatCpuTemperature_ActiveAndIdleValues_FormatsExpectedText()
    {
        Assert.Equal("68 °C", TelemetryFormatter.FormatCpuTemperature(68.4));
        Assert.Equal("75 °C", TelemetryFormatter.FormatCpuTemperature(74.6));
        Assert.Equal("-- °C", TelemetryFormatter.FormatCpuTemperature(0.0));
        Assert.Equal("-- °C", TelemetryFormatter.FormatCpuTemperature(-5.0));
    }

    [Fact]
    public void FormatGpuTemperature_SleepAndActiveStates_FormatsAccurately()
    {
        // When active and warm
        Assert.Equal("62 °C", TelemetryFormatter.FormatGpuTemperature(62.1, false));

        // When in D3cold sleep state (0 °C or isSleeping = true)
        // No reading is not a sleeping GPU: show it as unknown.
        Assert.Equal("-- °C", TelemetryFormatter.FormatGpuTemperature(0.0, false));
        Assert.Equal("-- °C", TelemetryFormatter.FormatGpuTemperature(-1.0, false));
        Assert.Equal("-- °C", TelemetryFormatter.FormatGpuTemperature(62.0, false, available: false));
        Assert.Equal("0 °C (Uyku / D3cold)", TelemetryFormatter.FormatGpuTemperature(55.0, true));
    }
}
