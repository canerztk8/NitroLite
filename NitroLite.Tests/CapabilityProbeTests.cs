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
    public void EmbeddedProfilesListUnsupportedMethodsOnlyWithEvidence()
    {
        // Guessing would disable a working feature, so a profile lists a method only when a report proved the firmware
        // refuses it. Each entry here names its evidence; any other profile must stay empty.
        var evidence = new Dictionary<string, string[]>
        {
            // Issue 11: v1.3.0, elevated worker, every write refused three times; white backlight, no RGB.
            ["nitro-lite-16-nl16-71g"] = new[]
            {
                "SetGamingFanSpeed", "SetGamingFanBehavior", "SetGamingRgbKb", "SetGamingKBBacklight",
                "SetGamingMiscSetting"
            }
        };

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        foreach (var profile in catalog.Profiles)
        {
            if (evidence.TryGetValue(profile.Id, out var expected))
                Assert.Equal(expected, profile.UnsupportedMethods);
            else
                Assert.Empty(profile.UnsupportedMethods);
        }

        // Every listed name must be one the gate maps to a control or routes elsewhere, or the entry would do nothing.
        Assert.All(evidence.Values.SelectMany(v => v), m => Assert.True(
            UnsupportedMethodGate.Methods.ContainsKey(m) || UnsupportedMethodGate.RoutedElsewhere.ContainsKey(m)));
    }

    [Fact]
    public void TheNitroLite16_MatchesItsOwnProfile_AndLosesTheControlsItsFirmwareRefused()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var profile = catalog.Match(new DeviceIdentity
        {
            Manufacturer = "Acer",
            Model = "Nitro NL16-71G",
            ModelCode = "NL16-71G",
            ModelFamily = "Nitro Lite",
            IsAcer = true,
            BoardProduct = "CTE_NB_NL160P1G_RTH"
        });

        Assert.Equal("nitro-lite-16-nl16-71g", profile.Id);
        Assert.False(profile.Sensors.Supported);

        var all = new HardwareCapabilities { FanControl = true, KeyboardLighting = true, OperatingModeControl = true };
        var gated = UnsupportedMethodGate.Apply(all, profile, out _);
        Assert.False(gated.FanControl);
        Assert.False(gated.KeyboardLighting);
        Assert.True(gated.OperatingModeControl); // kept, and moved to the Windows power mode
        Assert.True(UnsupportedMethodGate.ModesThroughWindows(profile));
    }

    [Theory]
    [InlineData("AN14-41", "nitro-14-an14-41")]
    [InlineData("AN16-51", "nitro-16-an16-51")]
    [InlineData("AN16-71", "nitro-16-an16-71")]
    [InlineData("AN16-72", "nitro-16-an16-72")]
    [InlineData("AN16-73", "nitro-16-an16-73")]
    [InlineData("AN17-71", "nitro-17-an17-71")]
    [InlineData("AN17-72", "nitro-17-an17-72")]
    [InlineData("AN517-55", "nitro-5-an517-55")]
    [InlineData("ANV14-61", "nitro-v-14-anv14-61")]
    [InlineData("PH315-55", "predator-helios-300-ph315-55")]
    [InlineData("PH315-55S", "predator-helios-300-ph315-55")]
    [InlineData("PH3D15-71", "predator-helios-3d-ph3d15-71")]
    [InlineData("PT16-51", "predator-triton-pt16-51")]
    [InlineData("ANV15-42", "nitro-v-15-anv15-42")]
    [InlineData("ANV16-61", "nitro-v-16-anv16-61")]
    [InlineData("ANV17-41", "nitro-v-17-anv17-41")]
    [InlineData("ANV17-61", "nitro-v-17-anv17-61")]
    [InlineData("PH18-72", "predator-helios-18-ph18-72")]
    [InlineData("PHN18-71", "predator-helios-neo-18-phn18-71")]
    [InlineData("PTN16-51", "predator-triton-neo-16-ptn16-51")]
    public void TheModelsNitroSenseListed_HaveTheirOwnUnverifiedProfile(string code, string id)
    {
        // From the NitroSense 5.0.1327.0 inventory (docs/NitroSense-inventory-2026-11-25.md). Each profile names the
        // model and nothing more: unverified sensors, no MUX or keyboard claim, no method switched off.
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var profile = catalog.Match(new DeviceIdentity { Manufacturer = "Acer", Model = code, ModelCode = code, IsAcer = true });

        Assert.Equal(id, profile.Id);
        Assert.False(profile.Sensors.Verified);
        Assert.Null(profile.MuxSwitch);
        Assert.Null(profile.FourZoneKeyboard);
        Assert.False(profile.NitroHidKeyboard);
        Assert.Empty(profile.UnsupportedMethods);
        Assert.Contains("unverified", profile.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("AN17-41", "nitro-17-an17-41", true)]
    [InlineData("ANV16-42", "nitro-v-16-anv16-42", false)]
    [InlineData("PH16-71", "predator-helios-16-ph16-71", false)]
    [InlineData("PT14-51", "predator-triton-pt14-51", false)]
    [InlineData("AN515-46", "nitro-5-an515-46", true)]
    [InlineData("AN515-47", "nitro-5-an515-47", false)]
    [InlineData("PHN18-72", "predator-helios-neo-18-phn18-72", false)]
    [InlineData("PHN16-73", "predator-helios-neo-16-phn16-73", true)]
    [InlineData("PHIN16S-71", "predator-triton-phin16s-71", false)]
    [InlineData("AN515-44", "nitro-5-an515-44", null)]
    public void PublicSourceFourZoneHints_AreInTheTable(string code, string id, bool? fourZone)
    {
        // acer-wmi-linuwu README (supported models table). The field is only a tie-breaker; null means the table does not say.
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var profile = catalog.Match(new DeviceIdentity { Manufacturer = "Acer", Model = code, ModelCode = code, IsAcer = true });

        Assert.Equal(id, profile.Id);
        Assert.Equal(fourZone, profile.FourZoneKeyboard);
        Assert.False(profile.Sensors.Verified);
        Assert.Empty(profile.UnsupportedMethods);
    }

    [Fact]
    public void OnlyTheNitroLite_MovesItsModesToWindows()
    {
        // Every other shipped profile keeps writing its operating mode to the laptop's own firmware.
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        Assert.Equal(
            new[] { "nitro-lite-16-nl16-71g" },
            catalog.Profiles.Where(UnsupportedMethodGate.ModesThroughWindows).Select(p => p.Id));
    }

    [Fact]
    public void EmbeddedSensorLayoutIsMarkedUnverified()
    {
        // The ids are the firmware-wide enum, but only the reference machine has been measured. Shipping the rest as
        // verified would make the report claim a measurement nobody took.
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        Assert.All(
            catalog.Profiles.Where(p => p.Id != "nitro-16-an16-41"),
            p => Assert.False(p.Sensors.Verified));
    }

    // ---------------------------------------------------------------- SMBIOS matching

    [Theory]
    [InlineData("AN16-41", "nitro-16-an16-41")]
    [InlineData("AN16-61", "nitro-16-an16-61")]
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
    public void FamilyPatternMatch_DoesNotInheritTheVerifiedFlag()
    {
        // AN16-99 is in no model-code list; it reaches the AN16-41 profile only through the "Nitro 16" family
        // pattern. That profile is verified on the reference machine, but the flag describes a read-back taken
        // there — reporting it for an unmeasured model would claim a measurement nobody made.
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var byFamily = catalog.Match(Identity("AN16-99", family: "Nitro 16"));
        Assert.Equal("nitro-16-an16-41", byFamily.Id);
        Assert.False(byFamily.Sensors.Verified);
        Assert.False(byFamily.Sensors.Supported);
        Assert.Null(byFamily.FourZoneKeyboard);
        Assert.Null(byFamily.MuxSwitch);
        Assert.Equal("family or board only", byFamily.MatchBasis);

        // A model-code hit is the measured machine itself and keeps the flag.
        var byCode = catalog.Match(Identity("AN16-41", family: "Nitro 16"));
        Assert.Equal("nitro-16-an16-41", byCode.Id);
        Assert.True(byCode.Sensors.Verified);
    }

    [Fact]
    public void An16_61HasItsOwnProfile()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var profile = catalog.Match(Identity("AN16-61", family: "Nitro 16"));
        Assert.Equal("nitro-16-an16-61", profile.Id);
        Assert.False(profile.Sensors.Verified);
        Assert.Contains("AN16-61", profile.ModelCodes, StringComparer.OrdinalIgnoreCase);
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

    [Fact]
    public void DownloadedTable_KeepsItsCatchAll_AndSaysRemote()
    {
        // The published table, as RemoteProfiles saves it: the same document as the embedded one.
        var remote = ProfileCatalog.ReadEmbeddedJson();

        var catalog = ProfileCatalog.FromJson(null, null, remote, out var rejections);

        Assert.DoesNotContain(rejections, r => r.Contains("anyAcer", StringComparison.Ordinal));
        Assert.Equal("remote", catalog.Match(Identity("AN16-41")).Source);
        // A model no record names still falls back to the catch-all instead of to nothing.
        Assert.NotEqual("none", catalog.Match(Identity("AN99-99")).Source);
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
    public void UserProfileWithAnOutOfRangeSensorIdIsRejected()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            { "id": "shifted", "match": { "modelCodes": [ "AN16-41" ] }, "sensors": { "cpuTempSensor": 999 } }
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
              "sensors": { "cpuTempSensor": 1, "cpuFanRpmSensor": 2, "sys1TempSensor": 3, "gpuFanRpmSensor": 6, "gpuTempSensor": 10, "verified": true },
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
        // A user file cannot vouch for its own sensors; only the embedded table may.
        Assert.False(profile.Sensors.Verified);
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
                GpuOverclockSupport = 1
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
