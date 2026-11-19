using System;
using System.Collections.Generic;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The two sections the report gained: the session's runtime sensor verdict, and the USB HID collections the machine
/// exposes.
/// </summary>
/// <remarks>
/// Both exist because of the same report shape. A triager looking at "the fan curve is locked" and "the keyboard
/// lighting does nothing" cannot tell from the old report whether the app refused to act or the machine never answered,
/// and the two need opposite fixes. The runtime row says which permission the fan curve actually ran under; the USB
/// table says which controller is plugged in when no Acer WMI class will admit to driving the backlight.
/// </remarks>
public class HardwareReportContextTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 19, 6, 0, DateTimeKind.Utc);

    private static CapabilitySnapshot Snapshot() => new()
    {
        CacheKey = "AN16-61|1.53",
        ProfileId = "nitro-16-an16-61",
        ProfileSource = "embedded",
        Sensors = new SensorLayout
        {
            CpuTempSensor = 1,
            GpuTempSensor = 10,
            Sys1TempSensor = 3,
            CpuFanRpmSensor = 2,
            GpuFanRpmSensor = 6,
            Supported = true,
            Verified = false
        },
        MaxCpuFanRpm = 6200,
        MaxGpuFanRpm = 6000
    };

    private static DeviceIdentity Identity() => new()
    {
        Manufacturer = "Acer",
        Model = "Nitro AN16-61",
        ModelCode = "AN16-61",
        ModelFamily = "Acer Nitro 16 AI",
        BoardProduct = "EV9_SKF",
        BiosVendor = "INSYDE Corp.",
        BiosVersion = "1.53",
        CpuVendor = CpuVendor.Amd,
        IsAcer = true
    };

    private static MonitorData Sample() => new()
    {
        CpuTemperature = 62,
        GpuTemperature = 55,
        CpuFanSpeed = 2200,
        GpuFanSpeed = 2100
    };

    private static RuntimeSensorVerification Promoted()
    {
        var trust = new RuntimeSensorVerification();
        trust.Observe(Sample(), T0, layoutConfirmed: true);
        trust.Observe(Sample(), T0.AddSeconds(2), layoutConfirmed: true);
        Assert.True(trust.RuntimeVerified);
        return trust;
    }

    private static UsbHidDevice Ite() => new()
    {
        VendorId = 0x048D,
        ProductId = 0xC985,
        UsagePage = 0xFF00,
        Usage = 0x0001
    };

    [Fact]
    public void WithoutAContextTheRowSaysTheCheckDidNotRun()
    {
        var markdown = HardwareReport.BuildMarkdown(Snapshot(), identity: Identity());

        Assert.Contains("Runtime verification", markdown);
        Assert.Contains("not run (no live backend)", markdown);
        Assert.DoesNotContain("USB HID", markdown);
    }

    [Fact]
    public void AVerifiedSessionSaysRuntimeVerified()
    {
        var markdown = HardwareReport.BuildMarkdown(
            Snapshot(), identity: Identity(), context: HardwareReportContext.ForSensors(Promoted()));

        Assert.Contains("| Runtime verification | runtime-verified (2 samples) |", markdown);
        Assert.Contains("Runtime window", markdown);
        Assert.Contains("15..115", markdown);
        Assert.DoesNotContain("unverified (", markdown);
    }

    [Fact]
    public void AnUnverifiedSessionSaysWhatTheMachineAnswered()
    {
        var trust = new RuntimeSensorVerification();
        trust.Observe(new MonitorData { CpuTemperature = 0, GpuTemperature = 0 }, T0, layoutConfirmed: true);
        trust.Observe(new MonitorData { CpuTemperature = 0, GpuTemperature = 0 }, T0.AddSeconds(2), layoutConfirmed: true);

        var markdown = HardwareReport.BuildMarkdown(
            Snapshot(), identity: Identity(), context: HardwareReportContext.ForSensors(trust));

        Assert.Contains("| Runtime verification | unverified (2 of 2 refused, last: TemperatureOutOfRange) |", markdown);
        Assert.DoesNotContain("runtime-verified", markdown);
    }

    [Fact]
    public void UsbHidCollectionsAreListedWithTheirIds()
    {
        var context = HardwareReportContext.ForUsbHid(new[] { Ite() });

        var markdown = HardwareReport.BuildMarkdown(Snapshot(), identity: Identity(), context: context);

        Assert.Contains("| USB HID | VID:PID | UsagePage | Usage | Class |", markdown);
        Assert.Contains("| ITE Tech | 0x048D:0xC985 | 0xFF00 | 0x0001 | vendor-defined |", markdown);
    }

    [Fact]
    public void NoCollectionIsReportedRatherThanAnEmptySection()
    {
        var markdown = HardwareReport.BuildMarkdown(
            Snapshot(), identity: Identity(), context: HardwareReportContext.Empty);

        Assert.Contains("| (none found) | - | - | - | - |", markdown);
    }

    [Fact]
    public void TheTextReportCarriesTheSameTwoSections()
    {
        var context = new HardwareReportContext { SensorTrust = Promoted(), UsbHid = new[] { Ite() } };

        var text = HardwareReport.BuildText(context: context);

        Assert.Contains("RUNTIME SENSOR CHECK", text);
        Assert.Contains("runtime-verified", text);
        Assert.Contains("USB HID", text);
        Assert.Contains("0x048D:0xC985", text);
        Assert.Contains("This file contains no serial number, user name, file path or log content.", text);
    }

    [Theory]
    [InlineData("SerialNumber")]
    [InlineData("UserName")]
    [InlineData("C:\\Users\\")]
    [InlineData("/home/")]
    public void TheReportStillCarriesNothingPrivate(string forbidden)
    {
        var context = new HardwareReportContext { SensorTrust = Promoted(), UsbHid = new[] { Ite() } };

        Assert.DoesNotContain(forbidden, HardwareReport.BuildMarkdown(Snapshot(), identity: Identity(), context: context));
    }

    [Fact]
    public void OneContextCanCarryBothAndStaysImmutable()
    {
        var context = new HardwareReportContext { SensorTrust = Promoted(), UsbHid = new[] { Ite() } };
        var narrowed = context with { SensorTrust = null };

        Assert.False(context.IsEmpty);
        Assert.Single(context.UsbHid);
        Assert.Null(narrowed.SensorTrust);
        Assert.Single(narrowed.UsbHid);
        Assert.True(HardwareReportContext.Empty.IsEmpty);
    }
}
