using System;
using System.Collections.Generic;
using NitroLite.Core.Hardware.Lighting;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The rule that decides which HID interface a colour may be written to, and the byte layout that goes there.
/// </summary>
/// <remarks>
/// Both halves are safety rules rather than features. A lighting transport that opens the wrong collection is a stuck
/// key, and one that sends a mode byte nobody documented is an unverified hardware write — the same line this project
/// has drawn on every fan, MUX and battery path. These tests exist so neither can be relaxed by accident.
/// </remarks>
public class HidLightingFilterTests
{
    [Theory]
    [InlineData(0x01, 0x06)]
    [InlineData(0x01, 0x02)]
    [InlineData(0x01, 0x00)]
    [InlineData(0x01, 0x80)]
    public void TheKeyboardAndMousePagesAreNeverOpenedEvenForAControllerVendor(int page, int usage)
    {
        // 0x0CF2 is the writable ENE vendor, so this is not a "wrong vendor" answer: the page alone is the refusal.
        var decision = HidLightingControllers.Decide(0x0CF2, 0x5130, page, usage);

        Assert.False(decision.Candidate);
        Assert.False(decision.Writable);
        Assert.Contains("input collections are never opened", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x00)]
    [InlineData(0x0C)]
    [InlineData(0xFE00)]
    public void EveryStandardPageIsRefused(int page)
    {
        Assert.True(HidLightingControllers.IsStandardUsagePage(page));

        // Neither an undocumented vendor's interface nor the one controller with a documented frame may be written
        // through a standard page: the frame belongs to a vendor collection.
        Assert.False(HidLightingControllers.Decide(0x048D, 0x8295, page, 0x00).Candidate);
        Assert.False(HidLightingControllers.Decide(0x0CF2, 0x5130, page, 0x01).Writable);
    }

    [Fact]
    public void TheDocumentedControllerIsWritable()
    {
        var decision = HidLightingControllers.Decide(0x0CF2, 0x5130, 0xFF00, 0x01);

        Assert.True(decision.Candidate);
        Assert.True(decision.Writable);
        Assert.Null(decision.Reason);
        Assert.Contains("ENE KB5130", decision.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x048D, 0x8295)] // ITE
    [InlineData(0x0C45, 0x8006)] // Sonix
    [InlineData(0x04F2, 0x0132)] // Chicony
    [InlineData(0x05AF, 0x766A)] // Sunrex, from Acer's RGBDevice.ini (FF02)
    [InlineData(0x05AF, 0x867E)] // Sunrex, another PID family
    [InlineData(0x0D62, 0xBA51)] // Darfon, from Acer's RGBDevice.ini (FF01)
    [InlineData(0x0D62, 0xA21A)] // Darfon
    public void RecognisedButUndocumentedControllersAreNamedAndRefused(int vendorId, int productId)
    {
        var decision = HidLightingControllers.Decide(vendorId, productId, 0xFF00, 0x01);

        Assert.True(decision.Candidate);
        Assert.False(decision.Writable);
        Assert.NotNull(decision.Reason);
        Assert.NotEmpty(decision.Description);

        // Listed, so a report can say which controller the machine has.
        Assert.NotNull(HidLightingControllers.Find(vendorId, productId));
    }

    [Fact]
    public void AnUnknownVendorIsNotACandidate()
    {
        var decision = HidLightingControllers.Decide(0x1234, 0x5678, 0xFF00, 0x01);

        Assert.False(decision.Candidate);
        Assert.Null(HidLightingControllers.Find(0x1234, 0x5678));
    }

    [Fact]
    public void TheVendorPageBoundaryIsTheOneTheDocumentationUses()
    {
        Assert.Equal(0xFF00, HidLightingControllers.FirstVendorUsagePage);
        Assert.False(HidLightingControllers.IsStandardUsagePage(0xFF00));
        Assert.False(HidLightingControllers.IsStandardUsagePage(0xFFFF));
    }

    [Fact]
    public void ASingleColourIsOneReportWithEveryZoneMasked()
    {
        var frame = KeyboardLightingFrame.Create("STATIC", 100, 4, 1, "#FF0000", null);

        Assert.True(EneKb5130Protocol.TryBuild(frame, out var reports, out var error));
        Assert.Null(error);
        Assert.Single(reports);
        Assert.Equal(
            new byte[] { 0xA4, 0x21, 0x02, 0x64, 0x04, 0x01, 0xFF, 0x00, 0x00, 0x0F, 0x00 },
            reports[0]);
    }

    [Fact]
    public void FourDifferentZonesAreFourReportsWithTheirOwnMask()
    {
        var frame = new KeyboardLightingFrame
        {
            ZoneColors = new[] { 0x112233, 0x445566, 0x778899, 0xAABBCC },
            BrightnessPercent = 50,
            Effect = "BREATHING",
            Speed = 3,
            Direction = 2
        };

        Assert.True(EneKb5130Protocol.TryBuild(frame, out var reports, out _));
        Assert.Equal(4, reports.Count);

        for (int zone = 0; zone < reports.Count; zone++)
        {
            Assert.Equal(EneKb5130Protocol.ReportLength, reports[zone].Length);
            Assert.Equal(0xA4, reports[zone][0]);
            Assert.Equal(0x21, reports[zone][1]);
            Assert.Equal(0x04, reports[zone][2]);
            Assert.Equal(50, reports[zone][3]);
            Assert.Equal(3, reports[zone][4]);
            Assert.Equal(2, reports[zone][5]);
            Assert.Equal(EneKb5130Protocol.ZoneMasks[zone], reports[zone][9]);
            Assert.Equal(0x00, reports[zone][10]);
        }

        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, new[] { reports[0][6], reports[0][7], reports[0][8] });
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, new[] { reports[3][6], reports[3][7], reports[3][8] });
    }

    [Fact]
    public void AnEffectThatCyclesItsOwnColoursStillUsesOneReport()
    {
        var frame = new KeyboardLightingFrame
        {
            ZoneColors = new[] { 0x112233, 0x445566, 0x778899, 0xAABBCC },
            BrightnessPercent = 70,
            Effect = "WAVE",
            Speed = 6,
            Direction = 1
        };

        Assert.True(EneKb5130Protocol.TryBuild(frame, out var reports, out _));
        Assert.Single(reports);
        Assert.Equal(0x07, reports[0][2]);
        Assert.Equal(0x0F, reports[0][9]);
    }

    [Fact]
    public void TurningTheBacklightOffIsStaticAtZeroBrightness()
    {
        var frame = KeyboardLightingFrame.Create("STATIC", 0, 4, 1, "#FF0000", null);

        Assert.True(frame.IsOff);
        Assert.True(EneKb5130Protocol.TryBuild(frame, out var reports, out _));
        Assert.Single(reports);
        Assert.Equal(0x02, reports[0][2]);
        Assert.Equal(0x00, reports[0][3]);
        Assert.Equal(0x00, reports[0][6]);
        Assert.Equal(0x00, reports[0][7]);
        Assert.Equal(0x00, reports[0][8]);
    }

    [Fact]
    public void AnEffectWithNoDocumentedModeIsRefused()
    {
        var frame = new KeyboardLightingFrame { Effect = "SPIRAL", BrightnessPercent = 50 };

        Assert.False(EneKb5130Protocol.TryBuild(frame, out var reports, out var error));
        Assert.Empty(reports);
        Assert.Contains("no documented mode", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostileModeBytesAreNeverProduced()
    {
        // 0x01, 0x03, 0x06, 0x08 and 0x0C are the identifiers this protocol also names for firmware work and for
        // "black". No effect name in this app may reach any of them.
        var forbidden = new HashSet<byte> { 0x01, 0x03, 0x06, 0x08, 0x0C };

        foreach (var effect in new[] { "STATIC", "BREATHING", "NEON", "WAVE", "SHIFTING", "ZOOM", "OFF" })
        {
            var frame = KeyboardLightingFrame.Create(effect, 60, 5, 1, "#00FF00", null);
            Assert.True(EneKb5130Protocol.TryBuild(frame, out var reports, out var error), effect);
            Assert.Null(error);

            foreach (var report in reports) Assert.DoesNotContain(report[2], forbidden);
        }
    }

    [Fact]
    public void TheFrameClampsWhatItWasGiven()
    {
        var frame = KeyboardLightingFrame.Create("static", 400, 99, 7, "12ab34", null);

        Assert.Equal(100, frame.BrightnessPercent);
        Assert.Equal(10, frame.Speed);
        Assert.Equal(1, frame.Direction);
        Assert.Equal("STATIC", frame.Effect);
        Assert.Equal(0x12AB34, frame.Zone(0));
        Assert.True(frame.AllZonesEqual);
    }

    [Theory]
    [InlineData("#FF0000", true)]
    [InlineData("FF0000", true)]
    [InlineData("#ff0000", true)]
    [InlineData("#FF00", false)]
    [InlineData("#GG0000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AColourIsParsedStrictly(string? text, bool expected)
    {
        Assert.Equal(expected, KeyboardLightingFrame.TryParseHexColor(text, out _));
    }

    [Fact]
    public void AMalformedZoneColourFallsBackToThePrimaryOne()
    {
        var frame = KeyboardLightingFrame.Create("STATIC", 60, 4, 1, "#FFFFFF", new[] { "#FF0000", "nope" });

        Assert.Equal(0xFF0000, frame.Zone(0));
        Assert.Equal(0xFFFFFF, frame.Zone(1));
        Assert.False(frame.AllZonesEqual);
    }
}
