using System;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class BatteryTelemetryTests
{
    [Theory]
    [InlineData(66500, 15000, 266)] // (66500 / 15000) * 60 = 266 min (4 hr 26 min)
    [InlineData(40000, 20000, 120)] // (40000 / 20000) * 60 = 120 min (2 hr)
    [InlineData(30000, 10000, 180)] // 3 hr
    [InlineData(10000, 20000, 30)]  // 30 min
    public void CalculateRemainingMinutes_ValidInputs_CalculatesAccurately(double remMWh, double dischargeMw, int expectedMinutes)
    {
        int minutes = TelemetryFormatter.CalculateRemainingMinutes(remMWh, dischargeMw);
        Assert.Equal(expectedMinutes, minutes);
    }

    [Fact]
    public void CalculateRemainingMinutes_ExcessiveCapacity_ClampedTo24Hours()
    {
        // 80,000 mWh at 500 mW would theoretically last 9600 minutes (~160 hours)
        // Must clamp to 1440 minutes (24 hours)
        int minutes = TelemetryFormatter.CalculateRemainingMinutes(80000, 500);
        Assert.Equal(1440, minutes);
    }

    [Fact]
    public void CalculateRemainingMinutes_VeryLowCapacity_ClampedToMinimumOneMinute()
    {
        // 1 mWh at 60,000 mW = 0.001 min -> clamped to 1
        int minutes = TelemetryFormatter.CalculateRemainingMinutes(1, 60000);
        Assert.Equal(1, minutes);
    }

    [Theory]
    [InlineData(0, 15000)]
    [InlineData(-500, 15000)]
    [InlineData(66500, 0)]
    [InlineData(66500, -1000)]
    [InlineData(double.NaN, 15000)]
    [InlineData(66500, double.NaN)]
    [InlineData(double.PositiveInfinity, 15000)]
    [InlineData(66500, double.PositiveInfinity)]
    public void CalculateRemainingMinutes_ZeroNegativeOrInvalidInputs_ReturnsZero(double remMWh, double dischargeMw)
    {
        int minutes = TelemetryFormatter.CalculateRemainingMinutes(remMWh, dischargeMw);
        Assert.Equal(0, minutes);
    }

    [Fact]
    public void UpdateSmoothedRate_ColdStartSeeding_FirstReadingSeedsDirectly()
    {
        bool initialized = false;
        double current = 0.0;
        double instant = 18500.0;

        double result = TelemetryFormatter.UpdateSmoothedRate(current, instant, ref initialized, 0.20);

        Assert.True(initialized);
        Assert.Equal(18500.0, result);
    }

    [Fact]
    public void UpdateSmoothedRate_SubsequentReadings_AppliesEmaWeighting()
    {
        bool initialized = true;
        double current = 18500.0;
        double instant = 25000.0;

        // Formula: 0.20 * 25000 + 0.80 * 18500 = 5000 + 14800 = 19800
        double result = TelemetryFormatter.UpdateSmoothedRate(current, instant, ref initialized, 0.20);

        Assert.Equal(19800.0, result);

        // Third reading: instant = 15000.0
        // 0.20 * 15000 + 0.80 * 19800 = 3000 + 15840 = 18840
        double result2 = TelemetryFormatter.UpdateSmoothedRate(result, 15000.0, ref initialized, 0.20);
        Assert.Equal(18840.0, result2);
    }

    [Fact]
    public void UpdateSmoothedRate_ZeroOrNegativeInput_MaintainsExistingSmoothedValue()
    {
        bool initialized = true;
        double current = 19800.0;

        double resultZero = TelemetryFormatter.UpdateSmoothedRate(current, 0, ref initialized, 0.20);
        Assert.Equal(19800.0, resultZero);

        double resultNeg = TelemetryFormatter.UpdateSmoothedRate(current, -500, ref initialized, 0.20);
        Assert.Equal(19800.0, resultNeg);
    }

    [Fact]
    public void FormatBatteryPowerFlow_DualUnit_WhenDischargingWithMilliwatts()
    {
        var status = new BatteryStatusInfo
        {
            IsDischarging = true,
            DischargeRateWatts = 14.8,
            DischargeRateMilliwatts = 14820,
            IsPluggedIn = false,
            IsCharging = false
        };

        string flow = TelemetryFormatter.FormatBatteryPowerFlow(status);
        Assert.Equal("14.8 W (14,820 mW)", flow);
    }

    [Fact]
    public void FormatBatteryPowerFlow_BackwardCompatibility_WhenMilliwattsZero()
    {
        var status = new BatteryStatusInfo
        {
            IsDischarging = true,
            DischargeRateWatts = 18.5,
            DischargeRateMilliwatts = 0,
            IsPluggedIn = false,
            IsCharging = false
        };

        string flow = TelemetryFormatter.FormatBatteryPowerFlow(status);
        Assert.Equal("18.5 W (Deşarj Oluyor)", flow);
    }

    [Fact]
    public void FormatRemainingTime_DynamicHoursAndMinutes_FormatsTurkishTime()
    {
        Assert.Equal("2 sa 30 dk", TelemetryFormatter.FormatRemainingTime(150, false, false));
        Assert.Equal("1 sa 1 dk", TelemetryFormatter.FormatRemainingTime(61, false, false));
        Assert.Equal("45 dk", TelemetryFormatter.FormatRemainingTime(45, false, false));
        Assert.Equal("Adaptöre Bağlı", TelemetryFormatter.FormatRemainingTime(0, true, false));
        Assert.Equal("Hesaplanıyor...", TelemetryFormatter.FormatRemainingTime(0, true, true));
        Assert.Equal("Hesaplanıyor...", TelemetryFormatter.FormatRemainingTime(0, false, false));
        Assert.Equal("Hesaplanıyor...", TelemetryFormatter.FormatRemainingTime(1441, false, false));
    }
}
