using System.Text.Json;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the second step of the hardware-layer migration: the embedded model table, the strict validation of the
/// optional user override, the three-layer capability probe and its cache, and the registry bitmask the MUX answer
/// comes from.
/// </summary>
public class CapabilityProbeTests
{
    private static DeviceIdentity Identity(string modelCode, string bios = "1.27", string family = "", bool acer = true) =>
        new()
        {
            Manufacturer = acer ? "Acer" : "Dell Inc.",
            Model = string.IsNullOrEmpty(modelCode) ? "Unknown" : $"Nitro {modelCode}",
            ModelCode = modelCode,
            ModelFamily = family,
            BiosVersion = bios,
            IsAcer = acer
        };

    // ---------------------------------------------------------------- embedded profiles.json

    [Fact]
    public void EmbeddedProfileTableIsPresentAndParses()
    {
        var json = ProfileCatalog.ReadEmbeddedJson();
        Assert.False(string.IsNullOrWhiteSpace(json));

        var catalog = ProfileCatalog.FromJson(json, null, out var rejections);
        Assert.Empty(rejections);
        Assert.True(catalog.Profiles.Count >= 7, $"only {catalog.Profiles.Count} profiles loaded");
        Assert.Contains(catalog.Profiles, p => p.Id == "nitro-16-an16-41");
        Assert.Contains(catalog.Profiles, p => p.Id == "nitro-v-15-anv15-51");
        Assert.Contains(catalog.Profiles, p => p.AnyAcer);
    }

    [Fact]
    public void EmbeddedProfilesCarryNoUnsupportedMethodsYet()
    {
        // No report has proven a method broken on a specific model. The mechanism exists; guessing would disable a
        // working feature, so the shipped table stays empty until evidence arrives.
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        Assert.All(catalog.Profiles, p => Assert.Empty(p.UnsupportedMethods));
    }

    [Fact]
    public void EmbeddedSensorLayoutIsMarkedUnverified()
    {
        // The offsets are a hypothesis until an elevated read-back confirms them. Shipping them as verified would
        // make the report claim a measurement nobody took.
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        Assert.All(catalog.Profiles, p => Assert.False(p.Sensors.Verified));
    }

    // ---------------------------------------------------------------- SMBIOS matching

    [Theory]
    [InlineData("AN16-41", "nitro-16-an16-41")]
    [InlineData("AN17-42", "nitro-17-an17-42")]
    [InlineData("AN515-58", "nitro-5-an515")]
    [InlineData("ANV15-51", "nitro-v-15-anv15-51")]
    [InlineData("ANV16-41", "nitro-v-16-anv16-41")]
    [InlineData("PHN16-71", "predator-helios-16-phn16-71")]
    public void KnownModelCodesMatchTheirProfile(string modelCode, string expectedId)
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        Assert.Equal(expectedId, catalog.Match(Identity(modelCode)).Id);
    }

    [Fact]
    public void UnknownAcerModelFallsBackToTheCatchAll()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        Assert.Equal("acer-gaming-default", catalog.Match(Identity("AN999-99")).Id);
    }

    [Fact]
    public void NonAcerMachineWithNoModelCodeMatchesNothing()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var profile = catalog.Match(Identity("", acer: false));
        Assert.Equal("unknown", profile.Id);
        Assert.Equal("none", profile.Source);
    }

    [Fact]
    public void ModelCodeBeatsAFamilyPattern()
    {
        // The family string is marketing text and can be relabelled by a firmware update; the code is the identity.
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var identity = Identity("ANV15-51", family: "Nitro 16");
        Assert.Equal("nitro-v-15-anv15-51", catalog.Match(identity).Id);
    }

    [Fact]
    public void UserProfileWinsOverTheEmbeddedOneForTheSameModel()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "my-an16-41",
              "match": { "modelCodes": [ "AN16-41" ] },
              "maxFanRpm": { "cpu": 6500, "gpu": 6300 }
            }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);
        Assert.Empty(rejections);

        var profile = catalog.Match(Identity("AN16-41"));
        Assert.Equal("my-an16-41", profile.Id);
        Assert.Equal("user", profile.Source);
        Assert.Equal(6500, profile.MaxCpuFanRpm);
    }

    // ---------------------------------------------------------------- user profile validation

    [Fact]
    public void UserProfileWithAnOutOfRangeFanCeilingIsRejected()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            { "id": "too-fast", "match": { "modelCodes": [ "AN16-41" ] }, "maxFanRpm": { "cpu": 99000, "gpu": 6000 } }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);
        Assert.Contains(rejections, r => r.Contains("too-fast") && r.Contains("RPM"));
        Assert.DoesNotContain(catalog.Profiles, p => p.Id == "too-fast");

        // The machine falls back to the shipped profile, which is the state it would be in without the user file.
        Assert.Equal("nitro-16-an16-41", catalog.Match(Identity("AN16-41")).Id);
    }

    [Fact]
    public void UserProfileWithAnUnknownFieldIsRejected()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            { "id": "sneaky", "match": { "modelCodes": [ "AN16-41" ] }, "thermalFloorC": 100 }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);
        Assert.Contains(rejections, r => r.Contains("sneaky") && r.Contains("thermalFloorC"));
        Assert.DoesNotContain(catalog.Profiles, p => p.Id == "sneaky");
    }

    [Fact]
    public void UserProfileMayNotClaimEveryAcerMachine()
    {
        // A catch-all in a hand-edited file would replace the shipped default for every model, including the ones
        // the user does not own.
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [ { "id": "everything", "match": { "anyAcer": true } } ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);
        Assert.Contains(rejections, r => r.Contains("everything") && r.Contains("anyAcer"));
        Assert.DoesNotContain(catalog.Profiles, p => p.Id == "everything");
    }

    [Fact]
    public void UserProfileWithAnUnknownWmiMethodIsRejected()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            { "id": "typo", "match": { "modelCodes": [ "AN16-41" ] }, "unsupportedMethods": [ "SetGamingFanSped" ] }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);
        Assert.Contains(rejections, r => r.Contains("typo") && r.Contains("SetGamingFanSped"));
        Assert.DoesNotContain(catalog.Profiles, p => p.Id == "typo");
    }

    [Fact]
    public void UserProfileWithAnOutOfRangeSensorOffsetIsRejected()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            { "id": "shifted", "match": { "modelCodes": [ "AN16-41" ] }, "sensors": { "cpuTempByte": 42 } }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);
        Assert.Contains(rejections, r => r.Contains("shifted") && r.Contains("sensör"));
        Assert.DoesNotContain(catalog.Profiles, p => p.Id == "shifted");
    }

    [Fact]
    public void InvalidUserJsonIsRejectedWithoutLosingTheEmbeddedTable()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), "{ not json", out var rejections);
        Assert.Contains(rejections, r => r.Contains("geçersiz JSON"));
        Assert.Contains(catalog.Profiles, p => p.Id == "nitro-16-an16-41");
    }

    [Fact]
    public void UnsupportedSchemaVersionIsRejected()
    {
        const string user = """{ "schemaVersion": 99, "profiles": [] }""";
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);
        Assert.Contains(rejections, r => r.Contains("schemaVersion"));
        Assert.Contains(catalog.Profiles, p => p.Id == "nitro-16-an16-41");
    }

    [Fact]
    public void AValidUserProfileIsAccepted()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "an16-41-tuned",
              "match": { "modelCodes": [ "AN16-41" ], "familyPatterns": [ "Nitro 16" ] },
              "sensors": { "cpuTempByte": 0, "gpuTempByte": 1, "sys1TempByte": 2, "cpuFanRpmWord": 2, "gpuFanRpmWord": 3, "rpmScale": 100, "verified": true },
              "maxFanRpm": { "cpu": 6200, "gpu": 6000 },
              "muxSwitch": true,
              "fourZoneKeyboard": true,
              "unsupportedMethods": [ "GetGamingFanTable" ],
              "notes": "read back on hardware"
            }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);
        Assert.Empty(rejections);

        var profile = catalog.Match(Identity("AN16-41"));
        Assert.Equal("an16-41-tuned", profile.Id);
        Assert.True(profile.Sensors.Verified);
        Assert.Equal(new[] { "GetGamingFanTable" }, profile.UnsupportedMethods);
    }

    // ---------------------------------------------------------------- capability probe

    private static SchemaProbeResult Schema(
        AcerWmiProbeResult? wmi = null,
        IReadOnlyDictionary<string, string>? guids = null,
        AcerRegistrySnapshot? registry = null) =>
        new()
        {
            Wmi = wmi ?? new AcerWmiProbeResult { ClassPresent = true, DynamicClass = true, InstanceAccessDenied = true },
            AcerClassGuids = guids ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AcerGamingFunction"] = "{7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56}"
            },
            Registry = registry ?? new AcerRegistrySnapshot
            {
                KeyPresent = true,
                DgpuModeCapability = 7,
                KeyboardArea = 4,
                GpuOverclockSupport = 1,
                LcdOverdriveSupport = 1
            }
        };

    [Fact]
    public void ProbeReportsTheAn16_41Answers()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var snapshot = CapabilityProbe.Probe(Identity("AN16-41"), catalog, Schema());

        Assert.Equal("nitro-16-an16-41", snapshot.ProfileId);
        Assert.Equal("AN16-41|1.27", snapshot.CacheKey);

        // The class is registered but its instances need elevation: that is not a "no".
        var wmi = snapshot.Find(CapabilitySnapshot.WmiGamingFunction);
        Assert.NotNull(wmi);
        Assert.Equal(HardwareProbeOutcome.AccessDenied, wmi!.Outcome);
        Assert.True(wmi.IsInconclusive);

        Assert.True(snapshot.IsSupported(CapabilitySnapshot.MuxSwitch));
        Assert.True(snapshot.IsSupported(CapabilitySnapshot.KeyboardZones));
        Assert.True(snapshot.IsSupported(CapabilitySnapshot.GpuOverclock));
        Assert.True(snapshot.IsSupported(CapabilitySnapshot.PanelOverdrive));
    }

    [Fact]
    public void ProbeReportsTheBatteryInterfaceAsMissingWhenTheGuidIsAbsent()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var snapshot = CapabilityProbe.Probe(Identity("AN16-41"), catalog, Schema());

        var battery = snapshot.Find(CapabilitySnapshot.WmiBatteryChargeLimit);
        Assert.NotNull(battery);
        Assert.Equal(HardwareProbeOutcome.Unsupported, battery!.Outcome);
        Assert.Contains("WMID_GUID5", battery.Detail);
    }

    [Fact]
    public void ProbeFindsTheBatteryInterfaceByGuidRatherThanByName()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AcerBatteryCare"] = "{79772EC5-04B1-4BFD-843C-61E7F77B6CC9}"
        };

        var snapshot = CapabilityProbe.Probe(Identity("AN16-41"), catalog, Schema(guids: guids));
        var battery = snapshot.Find(CapabilitySnapshot.WmiBatteryChargeLimit);
        Assert.NotNull(battery);
        Assert.True(battery!.IsSupported);
        Assert.Equal("AcerBatteryCare", battery.Detail);
    }

    [Fact]
    public void ProbeKeepsAnUnreadableRegistryApartFromAMissingFeature()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var snapshot = CapabilityProbe.Probe(Identity("AN16-41"), catalog, Schema(registry: AcerRegistrySnapshot.Empty));

        // No Acer software installed: the service may still answer, so nothing may be reported as unsupported.
        Assert.Equal(HardwareProbeOutcome.Unknown, snapshot.Find(CapabilitySnapshot.MuxSwitch)!.Outcome);
        Assert.Equal(HardwareProbeOutcome.Unknown, snapshot.Find(CapabilitySnapshot.KeyboardZones)!.Outcome);
        Assert.False(snapshot.IsSupported(CapabilitySnapshot.MuxSwitch));
    }

    [Fact]
    public void ProbeReportsAZeroBitmaskAsUnsupported()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var registry = new AcerRegistrySnapshot { KeyPresent = true, DgpuModeCapability = 0, KeyboardArea = 0 };
        var snapshot = CapabilityProbe.Probe(Identity("AN16-41"), catalog, Schema(registry: registry));

        Assert.Equal(HardwareProbeOutcome.Unsupported, snapshot.Find(CapabilitySnapshot.MuxSwitch)!.Outcome);
        Assert.Equal(HardwareProbeOutcome.Unsupported, snapshot.Find(CapabilitySnapshot.KeyboardZones)!.Outcome);
    }

    [Fact]
    public void ProbeReportsAMissingWmiClassAsUnsupported()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var snapshot = CapabilityProbe.Probe(Identity("AN16-41"), catalog,
            Schema(wmi: new AcerWmiProbeResult { ClassPresent = false }));

        var wmi = snapshot.Find(CapabilitySnapshot.WmiGamingFunction);
        Assert.Equal(HardwareProbeOutcome.Unsupported, wmi!.Outcome);
        Assert.False(wmi.IsInconclusive);
    }

    // ---------------------------------------------------------------- cache

    [Fact]
    public void CacheIsAHitForTheSameModelAndBios()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var identity = Identity("AN16-41");
        var snapshot = CapabilityProbe.Probe(identity, catalog, Schema());

        var settings = new AppSettings();
        CapabilityProbe.Store(settings, snapshot);

        var cached = CapabilityProbe.TryFromCache(settings, identity, catalog);
        Assert.NotNull(cached);
        Assert.True(cached!.FromCache);
        Assert.Equal(snapshot.CacheKey, cached.CacheKey);
        Assert.Equal(snapshot.Capabilities.Count, cached.Capabilities.Count);
        Assert.True(cached.IsSupported(CapabilitySnapshot.MuxSwitch));
    }

    [Fact]
    public void CacheIsAMissAfterABiosUpdate()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var snapshot = CapabilityProbe.Probe(Identity("AN16-41", "1.27"), catalog, Schema());

        var settings = new AppSettings();
        CapabilityProbe.Store(settings, snapshot);

        // A firmware update can add or remove a WMI method, so the old answer must not survive it.
        Assert.Null(CapabilityProbe.TryFromCache(settings, Identity("AN16-41", "1.28"), catalog));
    }

    [Fact]
    public void CacheIsAMissForADifferentModel()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var settings = new AppSettings();
        CapabilityProbe.Store(settings, CapabilityProbe.Probe(Identity("AN16-41"), catalog, Schema()));

        Assert.Null(CapabilityProbe.TryFromCache(settings, Identity("ANV15-51"), catalog));
    }

    [Fact]
    public void CacheIsAMissWhenNothingWasStored()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        Assert.Null(CapabilityProbe.TryFromCache(new AppSettings(), Identity("AN16-41"), catalog));
    }

    [Fact]
    public void InvalidateDropsTheCache()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var identity = Identity("AN16-41");
        var settings = new AppSettings();
        CapabilityProbe.Store(settings, CapabilityProbe.Probe(identity, catalog, Schema()));

        CapabilityProbe.Invalidate(settings);
        Assert.Null(settings.HardwareCapabilityCache);
        Assert.Null(CapabilityProbe.TryFromCache(settings, identity, catalog));
    }

    [Fact]
    public void CacheSurvivesASettingsRoundTrip()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var identity = Identity("AN16-41");
        var settings = new AppSettings();
        CapabilityProbe.Store(settings, CapabilityProbe.Probe(identity, catalog, Schema()));

        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("hardware_capability_cache", json);

        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        var cached = CapabilityProbe.TryFromCache(restored!, identity, catalog);
        Assert.NotNull(cached);
        Assert.True(cached!.IsSupported(CapabilitySnapshot.MuxSwitch));
    }

    // ---------------------------------------------------------------- registry bitmask

    [Theory]
    [InlineData(7, GpuOperatingMode.Optimus, true)]
    [InlineData(7, GpuOperatingMode.DiscreteGpu, true)]
    [InlineData(7, GpuOperatingMode.Automatic, true)]
    [InlineData(1, GpuOperatingMode.Optimus, true)]
    [InlineData(1, GpuOperatingMode.DiscreteGpu, false)]
    [InlineData(0, GpuOperatingMode.Optimus, false)]
    public void GpuModeBitmaskIsReadByEnumValue(int mask, GpuOperatingMode mode, bool expected) =>
        Assert.Equal(expected, AcerRegistrySnapshot.IsGpuModeSupported(mask, mode));

    [Fact]
    public void SupportedGpuModesListsEverySetBit()
    {
        var registry = new AcerRegistrySnapshot { DgpuModeCapability = 7 };
        Assert.Equal(
            new[] { GpuOperatingMode.Optimus, GpuOperatingMode.DiscreteGpu, GpuOperatingMode.Automatic },
            registry.SupportedGpuModes);

        Assert.Empty(new AcerRegistrySnapshot { DgpuModeCapability = 0 }.SupportedGpuModes);
        Assert.Empty(AcerRegistrySnapshot.Empty.SupportedGpuModes);
    }

    [Fact]
    public void TheWindowPickerAndTheProbeAgreeOnTheBitmask()
    {
        // Both used to carry their own copy of the shift; a divergence would show a mode the hardware does not have.
        foreach (var mode in Enum.GetValues<GpuOperatingMode>())
        {
            for (int mask = 0; mask <= 7; mask++)
            {
                Assert.Equal(
                    AcerRegistrySnapshot.IsGpuModeSupported(mask, mode),
                    MainWindow.IsGpuModeSupported(mask, mode));
            }
        }
    }

    // ---------------------------------------------------------------- hardware report

    [Fact]
    public void HardwareReportCarriesTheCapabilityProbeSection()
    {
        var text = HardwareReport.BuildText();

        // Only the parts that do not depend on the machine this runs on. The report is built from a live probe, so
        // asserting on the WMI signature list would pass on an Acer laptop and fail on a CI runner that has no
        // AcerGamingFunction class at all — the section is written either way, its contents are not.
        Assert.Contains("CAPABILITY PROBE", text);
        Assert.Contains("Cache key", text);
        Assert.Contains("Profile", text);
        Assert.Contains(CapabilitySnapshot.MuxSwitch, text);
    }

    [Fact]
    public void HardwareReportJsonCarriesTheProbeNode()
    {
        var json = HardwareReport.BuildJson();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("capabilityProbe", out var probe));
        Assert.True(probe.TryGetProperty("profile", out _));
        Assert.True(probe.TryGetProperty("cacheKey", out _));
        Assert.True(probe.TryGetProperty("capabilities", out var capabilities));
        Assert.True(capabilities.TryGetProperty(CapabilitySnapshot.MuxSwitch, out var mux));
        Assert.True(mux.TryGetProperty("outcome", out _));

        Assert.True(root.TryGetProperty("acerWmi", out var wmi));
        Assert.True(wmi.TryGetProperty("signatures", out _));
    }

    [Fact]
    public void ProbeSummaryReadsAsProse()
    {
        Assert.Equal("mux.switch: access denied (needs elevation)",
            HardwareProbeResult.AccessDenied(CapabilitySnapshot.MuxSwitch, "needs elevation").Summary);
        Assert.Equal("mux.switch: not supported", HardwareProbeResult.Unsupported(CapabilitySnapshot.MuxSwitch).Summary);
    }
}
