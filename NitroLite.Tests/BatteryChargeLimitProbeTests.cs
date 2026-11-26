using System;
using System.Collections.Generic;
using System.Linq;
using NitroLite;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the battery charge limit answer on a machine that has no <c>WMID_GUID5</c> class.
/// <para>
/// An AN16-41 registers no battery WMI interface at all: the 80 % limit is owned by the Acer Care Center agent, which
/// answers over <c>wss://localhost:4343</c>. The probe used to look only for the WMI class, so the first-run
/// walkthrough told the user their laptop could not do the one thing the battery page was already doing. These tests
/// hold both halves of the fix: the capability is a yes when either interface is there, and the WMI backend still
/// refuses to write through a class that does not exist.
/// </para>
/// </summary>
public class BatteryChargeLimitProbeTests
{
    private const string GamingFunctionGuid = "{7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56}";
    private const string BatteryGuid = "{79772EC5-04B1-4BFD-843C-61E7F77B6CC9}";

    private static DeviceIdentity Identity(string modelCode = "AN16-41", string bios = "1.27") => new()
    {
        Manufacturer = "Acer",
        Model = $"Nitro {modelCode}",
        ModelCode = modelCode,
        BiosVersion = bios,
        IsAcer = true
    };

    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    private static SchemaProbeResult Schema(
        IReadOnlyDictionary<string, string>? guids = null,
        bool careCenter = false,
        string careCenterDetail = "") =>
        new()
        {
            Wmi = new AcerWmiProbeResult { ClassPresent = true, DynamicClass = true, InstanceAccessDenied = true },
            AcerClassGuids = guids ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AcerGamingFunction"] = GamingFunctionGuid
            },
            Registry = new AcerRegistrySnapshot
            {
                KeyPresent = true,
                DgpuModeCapability = 7,
                KeyboardArea = 4,
                GpuOverclockSupport = 1
            },
            CareCenterAvailable = careCenter,
            CareCenterDetail = careCenterDetail
        };

    // ---------------------------------------------------------------- the capability answer

    [Fact]
    public void NoWmiClassButCareCenterRunning_ReportsTheLimitAsSupported()
    {
        var snapshot = CapabilityProbe.Probe(Identity(), Catalog(),
            Schema(careCenter: true, careCenterDetail: "Acer Care Center (AcerCCAgentSvis running)"));

        var battery = snapshot.Find(CapabilitySnapshot.WmiBatteryChargeLimit);
        Assert.NotNull(battery);
        Assert.Equal(HardwareProbeOutcome.Supported, battery!.Outcome);
        Assert.Contains("Care Center", battery.Detail);

        // The WMI interface is still absent, and the backend that writes through it has to know that.
        Assert.False(snapshot.WmiBatteryChargeLimitPresent);
    }

    [Fact]
    public void WmiClassPresent_WinsOverCareCenterAndNamesTheClass()
    {
        var guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AcerGamingFunction"] = GamingFunctionGuid,
            ["AcerBatteryCare"] = BatteryGuid
        };

        var snapshot = CapabilityProbe.Probe(Identity(), Catalog(),
            Schema(guids: guids, careCenter: true, careCenterDetail: "Acer Care Center (AcerCCAgentSvis running)"));

        var battery = snapshot.Find(CapabilitySnapshot.WmiBatteryChargeLimit);
        Assert.NotNull(battery);
        Assert.True(battery!.IsSupported);
        Assert.Equal("AcerBatteryCare", battery.Detail);
        Assert.True(snapshot.WmiBatteryChargeLimitPresent);
    }

    [Fact]
    public void NeitherInterface_ReportsUnsupported()
    {
        var snapshot = CapabilityProbe.Probe(Identity(), Catalog(), Schema());

        var battery = snapshot.Find(CapabilitySnapshot.WmiBatteryChargeLimit);
        Assert.NotNull(battery);
        Assert.Equal(HardwareProbeOutcome.Unsupported, battery!.Outcome);
        Assert.Contains("WMID_GUID5", battery.Detail);
        Assert.False(snapshot.WmiBatteryChargeLimitPresent);
    }

    [Fact]
    public void EmptySchemaWithCareCenter_IsSupportedRatherThanUnknown()
    {
        // The class enumeration failed, which is not evidence of absence — and the agent answering is positive
        // evidence of presence, so the row must not fall back to "bilinmiyor".
        var snapshot = CapabilityProbe.Probe(Identity(), Catalog(),
            Schema(guids: new Dictionary<string, string>(), careCenter: true,
                careCenterDetail: "Acer Care Center (AcerCCAgentSvis running)"));

        Assert.True(snapshot.IsSupported(CapabilitySnapshot.WmiBatteryChargeLimit));

        // Nobody managed to enumerate the classes, so the WMI backend must not be told the class is missing.
        Assert.Null(snapshot.WmiBatteryChargeLimitPresent);
    }

    [Fact]
    public void EmptySchemaWithoutCareCenter_StaysUnknown()
    {
        var snapshot = CapabilityProbe.Probe(Identity(), Catalog(),
            Schema(guids: new Dictionary<string, string>()));

        Assert.Equal(HardwareProbeOutcome.Unknown,
            snapshot.Find(CapabilitySnapshot.WmiBatteryChargeLimit)!.Outcome);
        Assert.Null(snapshot.WmiBatteryChargeLimitPresent);
    }

    [Fact]
    public void CareCenterDetailFallsBackToTheServiceNameWhenTheProbeDidNotSupplyOne()
    {
        var snapshot = CapabilityProbe.Probe(Identity(), Catalog(), Schema(careCenter: true));

        var battery = snapshot.Find(CapabilitySnapshot.WmiBatteryChargeLimit);
        Assert.True(battery!.IsSupported);
        Assert.Contains(CareCenterProbe.ServiceName, battery.Detail);
    }

    // ---------------------------------------------------------------- cache round-trip

    [Fact]
    public void Cache_RoundTripsTheWmiBatterySignal()
    {
        foreach (bool present in new[] { true, false })
        {
            var settings = new AppSettings();
            var snapshot = CapabilityProbe.Probe(Identity(), Catalog(), Schema()) with
            {
                WmiBatteryChargeLimitPresent = present
            };
            CapabilityProbe.Store(settings, snapshot);

            var restored = CapabilityProbe.TryFromCache(settings, Identity(), Catalog());
            Assert.NotNull(restored);
            Assert.Equal(present, restored!.WmiBatteryChargeLimitPresent);
        }
    }

    [Fact]
    public void Cache_FromABuildThatDidNotRecordTheField_ReadsAsUnknown()
    {
        var settings = new AppSettings();
        settings.HardwareCapabilityCache = new HardwareCapabilityCache
        {
            Key = CapabilityProbe.CacheKey(Identity()),
            ProbeVersion = CapabilityProbe.CurrentProbeVersion,
            CapturedUtc = DateTime.UtcNow,
            Capabilities = new List<CachedCapability>
            {
                new()
                {
                    Capability = CapabilitySnapshot.WmiBatteryChargeLimit,
                    Outcome = nameof(HardwareProbeOutcome.Unsupported),
                    Detail = "WMID_GUID5 not registered"
                }
            }
        };

        var restored = CapabilityProbe.TryFromCache(settings, Identity(), Catalog());
        Assert.NotNull(restored);

        // Null, not false: an old cache is "nobody asked", and treating it as a no would take the feature away from a
        // machine that does have the class.
        Assert.Null(restored!.WmiBatteryChargeLimitPresent);
    }

    [Fact]
    public void Cache_FromAnOlderProbeVersion_IsAMiss()
    {
        // The fix changes what the battery answer means, so a cache written before it must not survive: otherwise the
        // walkthrough keeps saying "desteklenmiyor" on a machine where the limit works.
        var settings = new AppSettings();
        var snapshot = CapabilityProbe.Probe(Identity(), Catalog(), Schema());
        CapabilityProbe.Store(settings, snapshot);

        Assert.NotNull(CapabilityProbe.TryFromCache(settings, Identity(), Catalog()));

        settings.HardwareCapabilityCache!.ProbeVersion = CapabilityProbe.CurrentProbeVersion - 1;
        Assert.Null(CapabilityProbe.TryFromCache(settings, Identity(), Catalog()));
    }

    // ---------------------------------------------------------------- the live service check

    [Fact]
    public void CareCenterProbe_ReadNeverThrowsAndAlwaysExplainsItself()
    {
        var result = CareCenterProbe.Read();

        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        if (result.Available) Assert.Contains(CareCenterProbe.ServiceName, result.Detail);
    }

    // ---------------------------------------------------------------- the WMID_GUID5 buffer

    [Fact]
    public void EncodeBatteryLimit_IsTheFourByteBufferTheFirmwareDocuments()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, AcerWmiProtocol.EncodeBatteryLimit(0));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, AcerWmiProtocol.EncodeBatteryLimit(1));
    }

    [Fact]
    public void DecodeBatteryLimit_ReadsByteZeroAndIgnoresTheReservedBytes()
    {
        foreach (var mode in Enum.GetValues<BatteryChargeMode>())
        {
            var buffer = AcerWmiProtocol.EncodeBatteryLimit((byte)mode);
            Assert.Equal(mode, AcerWmiProtocol.DecodeBatteryLimit(buffer));
        }

        // A firmware that fills the reserved bytes must not change the answer.
        Assert.Equal(BatteryChargeMode.Optimized80,
            AcerWmiProtocol.DecodeBatteryLimit(new byte[] { 0, 0xFF, 0xFF, 0xFF }));
    }

    [Fact]
    public void DecodeBatteryLimit_RejectsAnEmptyOrUnknownAnswer()
    {
        Assert.Null(AcerWmiProtocol.DecodeBatteryLimit(null));
        Assert.Null(AcerWmiProtocol.DecodeBatteryLimit(Array.Empty<byte>()));
        Assert.Null(AcerWmiProtocol.DecodeBatteryLimit(new byte[] { 0x7F }));
    }

    [Fact]
    public void BatteryMethodNamesCoverTheDocumentedPair()
    {
        // The pair the Linux acer-wmi-battery driver documents for WMID_GUID5 has to be in the candidate list, or a
        // machine that exposes only that pair reports "no known setter" and the limit stays read-only.
        var source = typeof(AcerWmiExecutor);
        var fields = source.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(f => f.Name is "BatteryGetMethods" or "BatterySetMethods")
            .Select(f => (string[])f.GetValue(null)!)
            .ToList();

        Assert.Equal(2, fields.Count);
        Assert.Contains("GetBatteryHealthControl", fields[0]);
        Assert.Contains("SetBatteryHealthControl", fields[1]);
    }

    // ---------------------------------------------------------------- the real machine

    /// <summary>
    /// Reads the real schema and checks the rule rather than a fixed answer, so it is meaningful on any Acer laptop:
    /// the capability is supported exactly when one of the two interfaces is there, and the detail names which.
    /// </summary>
    [LiveSystemFact]
    public void LiveProbe_AgreesWithTheInterfacesItCanSee()
    {
        var schema = SchemaProbeResult.Read();
        var snapshot = CapabilityProbe.Probe(HardwareDetector.Current, ProfileCatalog.Load(), schema);

        var battery = snapshot.Find(CapabilitySnapshot.WmiBatteryChargeLimit);
        Assert.NotNull(battery);

        bool expected = schema.CareCenterAvailable || snapshot.WmiBatteryChargeLimitPresent == true;
        Assert.True(expected == battery!.IsSupported,
            $"careCenter={schema.CareCenterAvailable} ({schema.CareCenterDetail}), " +
            $"wmiClass={snapshot.WmiBatteryChargeLimitPresent}, outcome={battery.Outcome} ({battery.Detail})");

        if (battery.IsSupported)
            Assert.False(string.IsNullOrWhiteSpace(battery.Detail));
    }
}
