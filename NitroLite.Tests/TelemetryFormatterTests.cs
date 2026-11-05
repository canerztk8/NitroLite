using System.Globalization;
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
    public void FormatFanSpeed_FormatsRpmAndPercentageCorrectly()
    {
        Assert.Equal("3960 RPM (%66)", TelemetryFormatter.FormatFanSpeed(3960, 6000));
        Assert.Equal("0 RPM (%0)", TelemetryFormatter.FormatFanSpeed(0, 6000));
        Assert.Equal("6000 RPM (%100)", TelemetryFormatter.FormatFanSpeed(6000, 6000));
    }

    [Fact]
    public void FormatFanSpeedRpm_FormatsRpmCorrectly()
    {
        Assert.Equal("3960 RPM", TelemetryFormatter.FormatFanSpeedRpm(3960));
        Assert.Equal("0 RPM", TelemetryFormatter.FormatFanSpeedRpm(0));
        Assert.Equal("0 RPM", TelemetryFormatter.FormatFanSpeedRpm(-100));
    }

    [Fact]
    public void FormatFanSpeedPercent_FormatsPercentageCorrectly()
    {
        Assert.Equal("%66", TelemetryFormatter.FormatFanSpeedPercent(3960, 6000));
        Assert.Equal("%0", TelemetryFormatter.FormatFanSpeedPercent(0, 6000));
        Assert.Equal("%100", TelemetryFormatter.FormatFanSpeedPercent(6000, 6000));
    }

    [Fact]
    public void FormatCpuTemperature_ActiveAndIdleValues_FormatsExpectedText()
    {
        Assert.Equal("68 °C", TelemetryFormatter.FormatCpuTemperature(68.4));
        Assert.Equal("75 °C", TelemetryFormatter.FormatCpuTemperature(74.6));
        Assert.Equal("0 °C (Beklemede)", TelemetryFormatter.FormatCpuTemperature(0.0));
        Assert.Equal("0 °C (Beklemede)", TelemetryFormatter.FormatCpuTemperature(-5.0));
    }

    [Fact]
    public void FormatGpuTemperature_SleepAndActiveStates_FormatsAccurately()
    {
        // When active and warm
        Assert.Equal("62 °C", TelemetryFormatter.FormatGpuTemperature(62.1, false));

        // When in D3cold sleep state (0 °C or isSleeping = true)
        Assert.Equal("0 °C (Uyku / D3cold)", TelemetryFormatter.FormatGpuTemperature(0.0, false));
        Assert.Equal("0 °C (Uyku / D3cold)", TelemetryFormatter.FormatGpuTemperature(-1.0, false));
        Assert.Equal("0 °C (Uyku / D3cold)", TelemetryFormatter.FormatGpuTemperature(55.0, true));
    }

    [Fact]
    public void FormatCpuPower_PositiveAndZeroValues_FormatsExpectedText()
    {
        Assert.Equal("45.2 W", TelemetryFormatter.FormatCpuPower(45.2));
        Assert.Equal("0.0 W", TelemetryFormatter.FormatCpuPower(0.0));
        Assert.Equal("0.0 W", TelemetryFormatter.FormatCpuPower(-10.0));
    }

    [Fact]
    public void FormatGpuPower_SleepAndActiveStates_FormatsAccurately()
    {
        // When active
        Assert.Equal("85.5 W", TelemetryFormatter.FormatGpuPower(85.5, false));

        // When asleep or 0 W
        Assert.Equal("0.0 W (D3cold)", TelemetryFormatter.FormatGpuPower(0.0, false));
        Assert.Equal("0.0 W (D3cold)", TelemetryFormatter.FormatGpuPower(-5.0, false));
        Assert.Equal("0.0 W (D3cold)", TelemetryFormatter.FormatGpuPower(85.5, true));
    }

    [Fact]
    public void FormatCpuFrequency_ActiveAndZeroClocks_FormatsExpectedText()
    {
        Assert.Equal("4.25 GHz", TelemetryFormatter.FormatCpuFrequency(4250.0));
        Assert.Equal("3.80 GHz", TelemetryFormatter.FormatCpuFrequency(3800.0));
        Assert.Equal("0.00 GHz (Beklemede)", TelemetryFormatter.FormatCpuFrequency(0.0));
        Assert.Equal("0.00 GHz (Beklemede)", TelemetryFormatter.FormatCpuFrequency(-100.0));
    }

    [Fact]
    public void FormatGpuFrequency_ActiveAndSleepClocks_FormatsExpectedText()
    {
        Assert.Equal("2100 MHz", TelemetryFormatter.FormatGpuFrequency(2100.0, false));
        Assert.Equal("0 MHz (Uyku)", TelemetryFormatter.FormatGpuFrequency(0.0, false));
        Assert.Equal("0 MHz (Uyku)", TelemetryFormatter.FormatGpuFrequency(-50.0, false));
        Assert.Equal("0 MHz (Uyku)", TelemetryFormatter.FormatGpuFrequency(2100.0, true));
    }

    [Fact]
    public void FormatRamUsage_NormalAndZeroFallback_FormatsExpectedText()
    {
        // Normal 12.4 GB used out of 31.9 GB (38.9%)
        string ramStr = TelemetryFormatter.FormatRamUsage(12697.6, 32665.6);
        Assert.Contains("12.4 GB / 31.9 GB", ramStr);
        Assert.Contains("%38.9", ramStr);

        // Zero total fallback
        Assert.Equal("0.0 GB / 0.0 GB (%0.0)", TelemetryFormatter.FormatRamUsage(0.0, 0.0));
        Assert.Equal("0.0 GB / 0.0 GB (%0.0)", TelemetryFormatter.FormatRamUsage(5000.0, 0.0));
    }

    [Fact]
    public void FormatVramUsage_NormalAndSleepFallback_FormatsExpectedText()
    {
        // Active 2048 MB out of 8192 MB (25%)
        Assert.Equal("2048 MB / 8192 MB (%25)", TelemetryFormatter.FormatVramUsage(2048, 8192, false));

        // When sleeping in D3cold
        Assert.Equal("0 MB / 0 MB (D3cold)", TelemetryFormatter.FormatVramUsage(0, 0, false));
        Assert.Equal("0 MB / 0 MB (D3cold)", TelemetryFormatter.FormatVramUsage(2048, 8192, true));
    }

    [Fact]
    public void FormatBatteryPowerFlow_DischargingState_ReturnsDesarjOluyor()
    {
        var status = new BatteryStatusInfo
        {
            IsDischarging = true,
            DischargeRateWatts = 24.5,
            IsPluggedIn = false,
            IsCharging = false
        };

        string flow = TelemetryFormatter.FormatBatteryPowerFlow(status);
        Assert.Equal("24.5 W (Deşarj Oluyor)", flow);
    }

    [Fact]
    public void FormatBatteryPowerFlow_ChargingState_ReturnsSarjOluyor()
    {
        var status = new BatteryStatusInfo
        {
            IsCharging = true,
            ChargeRateWatts = 58.2,
            IsPluggedIn = true,
            IsDischarging = false
        };

        string flow = TelemetryFormatter.FormatBatteryPowerFlow(status);
        Assert.Equal("58.2 W (Şarj Oluyor)", flow);
    }

    [Fact]
    public void FormatBatteryPowerFlow_PluggedInSaturated_ReturnsAdaptordeDoygun()
    {
        var status = new BatteryStatusInfo
        {
            IsPluggedIn = true,
            IsCharging = false,
            IsDischarging = false,
            ChargeRateWatts = 0.0,
            DischargeRateWatts = 0.0
        };

        string flow = TelemetryFormatter.FormatBatteryPowerFlow(status);
        Assert.Equal("0.0 W (Adaptörde / Doygun)", flow);
    }

    [Fact]
    public void FormatBatteryPowerFlow_OnBatteryZeroDischarge_ReturnsPildeBosta()
    {
        var status = new BatteryStatusInfo
        {
            IsPluggedIn = false,
            IsCharging = false,
            IsDischarging = false,
            ChargeRateWatts = 0.0,
            DischargeRateWatts = 0.0
        };

        string flow = TelemetryFormatter.FormatBatteryPowerFlow(status);
        Assert.Equal("0.0 W (Pilde / Boşta)", flow);
    }

    [Fact]
    public void FormatBatteryPowerFlow_NullStatus_ReturnsZeroWatts()
    {
        Assert.Equal("0.0 W", TelemetryFormatter.FormatBatteryPowerFlow(null));
    }

    [Fact]
    public void FormatRemainingTime_VariousConditions_FormatsAccurately()
    {
        Assert.Equal("2 sa 30 dk", TelemetryFormatter.FormatRemainingTime(150, false, false));
        Assert.Equal("45 dk", TelemetryFormatter.FormatRemainingTime(45, false, false));
        Assert.Equal("Adaptöre Bağlı", TelemetryFormatter.FormatRemainingTime(0, true, false));
        Assert.Equal("Hesaplanıyor...", TelemetryFormatter.FormatRemainingTime(0, true, true));
        Assert.Equal("Hesaplanıyor...", TelemetryFormatter.FormatRemainingTime(0, false, false));
    }

    [Fact]
    public void FormatDiskCapacity_NormalAndZero_FormatsAccurately()
    {
        string cap = TelemetryFormatter.FormatDiskCapacity(483.6, 1863.0);
        Assert.Equal("483.6 GB / 1863.0 GB (%26.0 Dolu)", cap);

        Assert.Equal("0.0 GB", TelemetryFormatter.FormatDiskCapacity(0, 0));
    }
}
