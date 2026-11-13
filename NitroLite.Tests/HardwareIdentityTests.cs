using System;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The identity layer is pure string work on top of one WMI read, so it is tested without touching hardware.
/// The cases below are the model strings real Nitro machines actually report, plus the shapes that must not be
/// mistaken for a Nitro.
/// </summary>
public class HardwareIdentityTests
{
    [Theory]
    [InlineData("Nitro AN16-41", "AN16-41")]
    [InlineData("Nitro AN16-41 ", "AN16-41")]
    [InlineData("nitro an16-41", "AN16-41")]
    [InlineData("AN515-58-79QL", "AN515-58-79QL")]
    [InlineData("Acer Nitro 5 AN515-58", "AN515-58")]
    [InlineData("Nitro V 15 ANV15-51", "ANV15-51")]
    [InlineData("Nitro ANV16-41", "ANV16-41")]
    [InlineData("Predator PH16-71", "PH16-71")]
    [InlineData("Nitro AN17-51", "AN17-51")]
    public void ExtractModelCode_FindsTheCode(string model, string expected) =>
        Assert.Equal(expected, DeviceIdentity.ExtractModelCode(model));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Nitro V15")]
    [InlineData("N50-620")]
    [InlineData("System Product Name")]
    [InlineData("To be filled by O.E.M.")]
    public void ExtractModelCode_ReturnsEmptyWhenThereIsNoCode(string? model) =>
        Assert.Equal("", DeviceIdentity.ExtractModelCode(model));

    [Theory]
    [InlineData("AN16-41", "Nitro 16")]
    [InlineData("AN17-51", "Nitro 17")]
    [InlineData("AN515-58", "Nitro 5")]
    [InlineData("AN517-55", "Nitro 5")]
    [InlineData("ANV15-51", "Nitro V 15")]
    [InlineData("ANV16-41", "Nitro V 16")]
    [InlineData("PH16-71", "Predator Helios 16")]
    [InlineData("PH18-71", "Predator Helios 18")]
    [InlineData("PT16-51", "Predator Triton 16")]
    public void DeriveFamily_MapsKnownSeries(string code, string expected) =>
        Assert.Equal(expected, DeviceIdentity.DeriveFamily(code));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("AN999-1")]
    [InlineData("XX16-41")]
    public void DeriveFamily_ReturnsEmptyForUnknownSeries(string? code) =>
        Assert.Equal("", DeviceIdentity.DeriveFamily(code));

    [Theory]
    [InlineData("AuthenticAMD", CpuVendor.Amd)]
    [InlineData("authenticamd", CpuVendor.Amd)]
    [InlineData("GenuineIntel", CpuVendor.Intel)]
    [InlineData(" genuineintel ", CpuVendor.Intel)]
    [InlineData("", CpuVendor.Unknown)]
    [InlineData(null, CpuVendor.Unknown)]
    [InlineData("ARM", CpuVendor.Unknown)]
    public void ParseCpuVendor_OnlyTrustsTheTwoKnownStrings(string? manufacturer, CpuVendor expected) =>
        Assert.Equal(expected, DeviceIdentity.ParseCpuVendor(manufacturer));

    [Theory]
    [InlineData("V1.27", "1.27")]
    [InlineData("v1.27", "1.27")]
    [InlineData("1.27", "1.27")]
    [InlineData("ACRSYS - 2", "ACRSYS - 2")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("V", "V")]
    public void NormalizeBiosVersion_StripsOnlyTheVendorPrefix(string? raw, string expected) =>
        Assert.Equal(expected, DeviceIdentity.NormalizeBiosVersion(raw));

    [Theory]
    [InlineData("Acer", true)]
    [InlineData("ACER", true)]
    [InlineData("Acer Incorporated", true)]
    [InlineData("LENOVO", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAcerManufacturer_MatchesOnlyAcer(string? manufacturer, bool expected) =>
        Assert.Equal(expected, DeviceIdentity.IsAcerManufacturer(manufacturer));

    [Fact]
    public void DisplayName_DoesNotRepeatTheBrandWhenTheFamilyAlreadyCarriesIt()
    {
        var identity = new DeviceIdentity
        {
            Manufacturer = "Acer",
            Model = "Nitro AN16-41",
            ModelCode = "AN16-41",
            ModelFamily = "Acer Nitro 16"
        };
        Assert.Equal("Acer Nitro 16 (AN16-41)", identity.DisplayName);
    }

    [Fact]
    public void DisplayName_PrependsTheBrandWhenTheFamilyLacksIt()
    {
        var identity = new DeviceIdentity
        {
            Manufacturer = "Acer",
            Model = "Nitro AN16-41",
            ModelCode = "AN16-41",
            ModelFamily = "Nitro 16"
        };
        Assert.Equal("Acer Nitro 16 (AN16-41)", identity.DisplayName);
    }

    [Fact]
    public void DisplayName_DoesNotRepeatTheCodeWhenTheModelAlreadyCarriesIt()
    {
        var identity = new DeviceIdentity
        {
            Manufacturer = "Acer",
            Model = "Nitro AN16-41",
            ModelCode = "AN16-41",
            ModelFamily = "Nitro AN16-41"
        };
        Assert.Equal("Acer Nitro AN16-41", identity.DisplayName);
    }

    [Fact]
    public void DisplayName_FallsBackToTheModelWhenFamilyIsMissing()
    {
        var identity = new DeviceIdentity { Manufacturer = "Acer", Model = "Nitro AN16-41", ModelCode = "AN16-41" };
        Assert.Equal("Acer Nitro AN16-41", identity.DisplayName);
    }

    [Fact]
    public void DisplayName_IsEmptyWhenNothingWasRead() =>
        Assert.Equal("", new DeviceIdentity().DisplayName);

    [Fact]
    public void IsEmpty_IsTrueOnlyWhenEveryIdentifyingFieldIsBlank()
    {
        Assert.True(new DeviceIdentity().IsEmpty);
        Assert.False(new DeviceIdentity { Model = "Nitro AN16-41" }.IsEmpty);
        Assert.False(new DeviceIdentity { CpuName = "AMD Ryzen 7 7840HS" }.IsEmpty);
        Assert.False(new DeviceIdentity { BiosVersion = "1.27" }.IsEmpty);
    }

    [Fact]
    public void HardwareDetector_ReturnsAnIdentityAndCachesIt()
    {
        HardwareDetector.ResetForTesting();
        var first = HardwareDetector.Current;
        var second = HardwareDetector.Current;
        Assert.NotNull(first);
        Assert.Same(first, second);
        HardwareDetector.ResetForTesting();
    }

    [Fact]
    public void HardwareReport_TextCarriesTheSectionsAndNoSerialNumber()
    {
        var text = HardwareReport.BuildText();
        Assert.Contains("NitroLite hardware report", text);
        Assert.Contains("DEVICE", text);
        Assert.Contains("ACER SERVICES", text);
        Assert.Contains("ACER WMI", text);
        Assert.Contains("AcerGamingFunction", text);
        Assert.DoesNotContain("SerialNumber", text);
        Assert.DoesNotContain("NBQLJ", text);
    }

    [Fact]
    public void HardwareReport_JsonParsesAndKeepsTheSameSections()
    {
        var json = HardwareReport.BuildJson();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("application", out _));
        Assert.True(root.TryGetProperty("device", out _));
        Assert.True(root.TryGetProperty("hardware", out _));
        Assert.True(root.TryGetProperty("acerServices", out var services));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, services.ValueKind);
        Assert.True(root.TryGetProperty("acerWmi", out _));
    }

    /// <summary>The lines between two section headings, so an assertion cannot match a word further down.</summary>
    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"section {from} missing");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"section {to} missing after {from}");
        return text[start..end];
    }

    [Fact]
    public void HardwareReport_TextWritesTheCapabilitySection()
    {
        var capabilities = new DeviceCapabilities
        {
            OperatingModes = new[] { OperatingMode.Quiet, OperatingMode.Turbo },
            GpuModeCapability = 7,
            FanMode = FanMode.Auto,
            LightingCapability = true
        };

        var text = HardwareReport.BuildText(capabilities);
        Assert.Contains("CAPABILITIES", text);
        Assert.Contains("Quiet, Turbo", text);
        Assert.Contains("Backlight", text);
        Assert.Contains("yes", text);
    }

    [Fact]
    public void HardwareReport_TextOmitsAFieldTheServiceDidNotAnswer()
    {
        // A missing answer must not be written down as a missing feature.
        var text = HardwareReport.BuildText(new DeviceCapabilities { FanMode = FanMode.Max });
        var section = Section(text, "CAPABILITIES", "ACER WMI");
        Assert.Contains("Max", section);
        Assert.DoesNotContain("Backlight", section);
        Assert.DoesNotContain("GPU modes", section);
        Assert.DoesNotContain("Modes", section);
    }

    [Fact]
    public void HardwareReport_JsonWritesNullForAnUnansweredCapability()
    {
        var json = HardwareReport.BuildJson(new DeviceCapabilities { GpuModeCapability = 3 });
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var capabilities = doc.RootElement.GetProperty("capabilities");
        Assert.Equal(3, capabilities.GetProperty("gpuModeCapability").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, capabilities.GetProperty("operatingModes").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, capabilities.GetProperty("backlightControllable").ValueKind);
    }

    [Fact]
    public void HardwareReport_JsonKeepsTheKeyboardNodeNullWhenNothingWasProbed()
    {
        var json = HardwareReport.BuildJson();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("keyboard").ValueKind);
    }

    [Fact]
    public void FormatMemory_JoinsOnlyThePartsThatWereRead()
    {
        var full = new HardwareInfo
        {
            MemoryType = "DDR5",
            MemorySpeedMts = 5600,
            MemoryModules = 2,
            MemoryTotalGb = 32
        };
        Assert.Equal("DDR5 5600 MT/s · 2 modules · 32 GB", HardwareReport.FormatMemory(full));
        Assert.Equal("", HardwareReport.FormatMemory(new HardwareInfo()));
    }

    [Fact]
    public void WindowsVersionText_ReportsAMajorMinorBuild()
    {
        var text = HardwareReport.WindowsVersionText();
        Assert.Matches(@"^\d+\.\d+\.\d+ \(", text);
    }

    [Fact]
    public void AcerWmiProbe_ReturnsAResultWithoutThrowing()
    {
        var result = AcerWmiProbe.Read();
        Assert.NotNull(result);
        Assert.NotNull(result.Methods);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }

    [Theory]
    [InlineData("Class used to Gaming Function, Version 2.88", "2.88")]
    [InlineData("Class used to Gaming Function, version 2", "2")]
    [InlineData("Version 10.0.1", "10.0.1")]
    [InlineData("Class used to Gaming Function", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ExtractVersion_ReadsTheVersionOutOfTheDescription(string? description, string expected) =>
        Assert.Equal(expected, AcerWmiProbe.ExtractVersion(description));

    [Fact]
    public void AcerWmiProbe_SummaryNamesTheFailureWhenTheCallFails()
    {
        var result = new AcerWmiProbeResult
        {
            ClassPresent = true,
            Methods = new[] { "GetGamingFanTable" },
            CallMethod = "GetGamingFanTable",
            CallSucceeded = false,
            CallError = "InvalidMethodParameters (0x8004102F)"
        };
        Assert.Contains("GetGamingFanTable failed", result.Summary);
        Assert.Contains("0x8004102F", result.Summary);
    }

    [Fact]
    public void AcerWmiProbe_SummarySaysNotRegisteredWhenTheClassIsAbsent() =>
        Assert.Equal("not registered", new AcerWmiProbeResult { ClassPresent = false }.Summary);
}
