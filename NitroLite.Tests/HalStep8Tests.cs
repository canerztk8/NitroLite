using System.Text.Json.Nodes;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the third package of the hardware-layer work: the operating-mode bitmask, the WMI MUX signal, the model
/// quirks that switch a write off, and the four extra switches that only some firmwares register
/// (<c>APGeAction</c> CoolBoost and backlight timeout, <c>GetGamingProfile</c> LCD overdrive and Windows-key lock).
/// <para>
/// Every expected value here came from a read-only run on an AN16-41 (BIOS V1.27): <c>GetGamingMiscSetting(0x0A)</c>
/// answered <c>0x7300</c>, <c>(0x09)</c> answered <c>0x0700</c>, <c>GetGamingProfile(0)</c> answered
/// <c>0x01000000</c> and <c>APGeAction.GetFunction(0x207)</c> answered zero. Nothing in this file touches WMI.
/// </para>
/// </summary>
public class HalStep8Tests
{
    // ---------------------------------------------------------------- operating-mode bitmask

    [Fact]
    public void SupportedModes_An16_41Answer_IsEveryModeTheFirmwareAdvertises()
    {
        // 0x73 = bits 0, 1, 4, 5, 6: Quiet, Balanced, Performance, Turbo, Eco.
        var modes = AcerWmiProtocol.DecodeSupportedModes(0x7300);

        Assert.Equal(
            new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo, OperatingMode.Eco },
            modes);
    }

    [Fact]
    public void SupportedModes_Zero_IsEmptyRatherThanNoModes()
    {
        // A firmware that answers zero did not answer. The caller keeps its full set; an empty list is the signal.
        Assert.Empty(AcerWmiProtocol.DecodeSupportedModes(0));
    }

    [Fact]
    public void SupportedModes_UnknownBits_DoNotConjureAMode()
    {
        // Bit 7 is outside KnownModeBits. A firmware that sets it must not make a sixth mode appear.
        Assert.Empty(AcerWmiProtocol.DecodeSupportedModes(0x8000));
    }

    [Fact]
    public void SupportedModes_OnlyTheLowByteOfTheMaskCounts()
    {
        // The status byte sits below the mask and the upper bytes are reserved; neither may leak into the answer.
        Assert.Equal(
            new[] { OperatingMode.Default, OperatingMode.Turbo },
            AcerWmiProtocol.DecodeSupportedModes(0xFF000000_0000_2200UL));
    }

    [Fact]
    public void SupportedModes_ANitroVWithoutTurbo_LosesOnlyTurbo()
    {
        // 0x53 = Quiet, Balanced, Performance, Eco. This is the shape a Nitro V 15 is expected to answer, and the
        // reason the UI has to hide the Turbo button rather than offer one that does nothing.
        var modes = AcerWmiProtocol.DecodeSupportedModes(0x5300);

        Assert.DoesNotContain(OperatingMode.Turbo, modes);
        Assert.Contains(OperatingMode.Eco, modes);
        Assert.Equal(4, modes.Count);
    }

    // ---------------------------------------------------------------- WMI MUX signal

    [Fact]
    public void GpuModeSupport_OnlyThreeMeansSwitchable()
    {
        Assert.True(AcerWmiProtocol.DecodeGpuModeSupport(0x0300));
        Assert.False(AcerWmiProtocol.DecodeGpuModeSupport(0x0700));
        Assert.False(AcerWmiProtocol.DecodeGpuModeSupport(0));
    }

    [Fact]
    public void MuxSwitch_TheWmiSignalCanOnlyAddSupport()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

        // The registry says zero, which on its own is a "no". The firmware saying 3 is the stronger answer.
        var registrySaysNo = Schema(new AcerRegistrySnapshot { KeyPresent = true, DgpuModeCapability = 0 });
        var withSignal = CapabilityProbe.Probe(Identity("AN16-41"), catalog, registrySaysNo, wmiMuxSignal: true);
        Assert.True(withSignal.IsSupported(CapabilitySnapshot.MuxSwitch));
        Assert.True(withSignal.WmiMuxSignal);

        // And the other way round: a selector this build does not recognise is not evidence that the switch is gone.
        var registrySaysYes = Schema(new AcerRegistrySnapshot { KeyPresent = true, DgpuModeCapability = 7 });
        var withNoSignal = CapabilityProbe.Probe(Identity("AN16-41"), catalog, registrySaysYes, wmiMuxSignal: false);
        Assert.True(withNoSignal.IsSupported(CapabilitySnapshot.MuxSwitch));
    }

    [Fact]
    public void MuxSwitch_NoRegistryAndNoSignal_IsUnsupportedRatherThanUnknown()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var snapshot = CapabilityProbe.Probe(
            Identity("AN16-41"), catalog, Schema(new AcerRegistrySnapshot { KeyPresent = true }), wmiMuxSignal: false);

        var mux = snapshot.Find(CapabilitySnapshot.MuxSwitch);
        Assert.NotNull(mux);
        Assert.Equal(HardwareProbeOutcome.Unsupported, mux!.Outcome);
    }

    // ---------------------------------------------------------------- CoolBoost

    [Fact]
    public void CoolBoost_Encode_CarriesTheFlagInBit16()
    {
        Assert.Equal(7UL, AcerWmiProtocol.EncodeCoolBoost(false));
        Assert.Equal(7UL | (1UL << 16), AcerWmiProtocol.EncodeCoolBoost(true));
    }

    [Fact]
    public void CoolBoost_Decode_ReadsTheStateByte()
    {
        Assert.True(AcerWmiProtocol.DecodeCoolBoost(0x0100));
        Assert.False(AcerWmiProtocol.DecodeCoolBoost(0x0000));
    }

    [Fact]
    public void CoolBoost_UnknownStatusByte_IsNullRatherThanOff()
    {
        // A non-zero low byte is the firmware saying it did not understand the selector. Reporting "off" would let
        // the UI show a switch that is not actually being read.
        Assert.Null(AcerWmiProtocol.DecodeCoolBoost(0x0101));
    }

    // ---------------------------------------------------------------- backlight timeout

    [Fact]
    public void BacklightTimeout_GetInput_CarriesTheHotkeyAndTheFlag()
    {
        Assert.Equal(1UL | 0x80000UL, AcerWmiProtocol.EncodeBacklightTimeoutGet(0));
        Assert.Equal(1UL | (3UL << 8) | 0x80000UL, AcerWmiProtocol.EncodeBacklightTimeoutGet(3));
    }

    [Fact]
    public void BacklightTimeout_SetInput_CarriesBrightnessAndTheThirtySecondFlag()
    {
        var on = AcerWmiProtocol.EncodeBacklightTimeoutSet(0, 50, enabled: true);
        Assert.Equal(2UL | 0x80000UL | (50UL << 32) | (30UL << 40), on);

        var off = AcerWmiProtocol.EncodeBacklightTimeoutSet(0, 50, enabled: false);
        Assert.Equal(2UL | 0x80000UL | (50UL << 32), off);
    }

    [Fact]
    public void BacklightTimeout_SetInput_ClampsBrightnessToTheByteRange()
    {
        Assert.Equal(100UL, (AcerWmiProtocol.EncodeBacklightTimeoutSet(0, 140, false) >> 32) & 0xFF);
        Assert.Equal(0UL, (AcerWmiProtocol.EncodeBacklightTimeoutSet(0, -5, false) >> 32) & 0xFF);
    }

    [Fact]
    public void DedicatedTimeoutSelectorAcceptsBothStatesAndUsesTimeoutOnlyPayload()
    {
        foreach (bool enabled in new[] { false, true })
        {
            var state = AcerWmiExecutor.ReadTimeout(input =>
            {
                Assert.Equal(0x88401UL, input);
                return enabled ? 0x1E0000080000UL : 0x80000UL;
            });
            Assert.NotNull(state);
            Assert.Equal(enabled, state.Enabled);
            Assert.Equal(enabled ? 0x1E0000088402UL : 0x88402UL,
                AcerWmiProtocol.EncodeBacklightTimeoutSet(state.Selector, state.Brightness, enabled));
        }
    }

    [Fact]
    public void UnrecognizedDedicatedReplyFallsBackToTheCombinedBrightnessSelector()
    {
        var state = AcerWmiExecutor.ReadTimeout(input => input == 0x80001 ? 50UL << 32 : 0UL);
        Assert.NotNull(state);
        Assert.Equal(0, state.Selector);
        Assert.Equal(50, state.Brightness);
        Assert.Null(AcerWmiExecutor.ReadTimeout(_ => 1UL));
    }

    [Fact]
    public void BacklightTimeout_Decode_RoundTripsBothFields()
    {
        // Status 0, brightness 50 in bits 32..39, timeout 30 in bits 40..47.
        var answer = (30UL << 40) | (50UL << 32);
        var state = AcerWmiProtocol.DecodeBacklightTimeout(answer);

        Assert.NotNull(state);
        Assert.Equal(50, state!.Value.BrightnessPercent);
        Assert.True(state.Value.Enabled);

        var disabled = AcerWmiProtocol.DecodeBacklightTimeout(50UL << 32);
        Assert.NotNull(disabled);
        Assert.Equal(50, disabled!.Value.BrightnessPercent);
        Assert.False(disabled.Value.Enabled);
    }

    [Fact]
    public void BacklightTimeout_UnknownStatusByte_IsNull()
    {
        Assert.Null(AcerWmiProtocol.DecodeBacklightTimeout((30UL << 40) | (50UL << 32) | 1UL));
    }

    // ---------------------------------------------------------------- LCD overdrive

    [Fact]
    public void LcdOverdrive_Decode_KnowsExactlyTwoValues()
    {
        Assert.True(AcerWmiProtocol.DecodeLcdOverdrive(0x1000001000000UL));
        Assert.False(AcerWmiProtocol.DecodeLcdOverdrive(0x1000000UL));

        // Anything else is "this panel has no overdrive", not a guess at which bit means on.
        Assert.Null(AcerWmiProtocol.DecodeLcdOverdrive(0));
        Assert.Null(AcerWmiProtocol.DecodeLcdOverdrive(0x02));
    }

    [Fact]
    public void LcdOverdrive_Encode_UsesSelectorSixteenAndBit48()
    {
        Assert.Equal(0x10UL, AcerWmiProtocol.EncodeLcdOverdrive(false));
        Assert.Equal(0x10UL | (1UL << 48), AcerWmiProtocol.EncodeLcdOverdrive(true));
    }

    // ---------------------------------------------------------------- Windows-key lock

    [Fact]
    public void WinKeyLock_Encode_UsesSelectorTwoAndBit24()
    {
        Assert.Equal(2UL, AcerWmiProtocol.EncodeWinKeyLock(false));
        Assert.Equal(2UL | (1UL << 24), AcerWmiProtocol.EncodeWinKeyLock(true));
    }

    [Fact]
    public void WinKeyLock_DoesNotCollideWithTheOverdriveWord()
    {
        // GetGamingProfile(0) answers the overdrive word, whose bit 24 means "overdrive is supported". If the lock
        // ever shared that bit the two switches would fight each other.
        Assert.Equal(0UL, AcerWmiProtocol.EncodeWinKeyLock(true) & AcerWmiProtocol.GamingProfileOverdriveBit);
        Assert.Equal(0UL, AcerWmiProtocol.EncodeLcdOverdrive(true) & AcerWmiProtocol.GamingProfileWinKeyLockBit);
    }

    // ---------------------------------------------------------------- model quirks

    [Fact]
    public void Profiles_EcMmioAndZoneArmQuirks_RoundTrip()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out var rejections);
        Assert.Empty(rejections);

        var ecMmio = catalog.Match(Identity("ANV15-52"));
        Assert.Equal("nitro-v-15-anv15-52", ecMmio.Id);
        Assert.True(ecMmio.EcMmioProfile);

        var zoneArm = catalog.Match(Identity("PH315-53"));
        Assert.Equal("predator-helios-300-ph315-53", zoneArm.Id);
        Assert.True(zoneArm.KbZoneArm);

        // The flags are per-model, not global: a plain Nitro 16 has neither.
        var plain = catalog.Match(Identity("AN16-41"));
        Assert.False(plain.EcMmioProfile);
        Assert.False(plain.KbZoneArm);
    }

    [Fact]
    public void Profiles_UserFile_AcceptsTheQuirkFlags()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "anv15-52-user",
              "match": { "modelCodes": [ "ANV15-52" ] },
              "ecMmioProfile": true,
              "kbZoneArm": true
            }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);
        Assert.Empty(rejections);

        var profile = catalog.Match(Identity("ANV15-52"));
        Assert.Equal("anv15-52-user", profile.Id);
        Assert.True(profile.EcMmioProfile);
        Assert.True(profile.KbZoneArm);
    }

    [Fact]
    public void Profiles_UserFile_RejectsAnUnknownQuirkField()
    {
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "an16-41-bad",
              "match": { "modelCodes": [ "AN16-41" ] },
              "ecMmioProfile": true,
              "disableThermalFloors": true
            }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);

        // The record is dropped rather than half-applied, and the shipped profile still answers for the machine.
        Assert.NotEmpty(rejections);
        Assert.Equal("nitro-16-an16-41", catalog.Match(Identity("AN16-41")).Id);
    }

    [Fact]
    public void Profiles_EveryNewModelCode_MatchesItsOwnRecord()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

        var expected = new (string Code, string Id)[]
        {
            ("AN16-42", "nitro-16-an16-42"),
            ("AN16-43", "nitro-16-an16-43"),
            ("AN17-51", "nitro-17-an17-51"),
            ("AN515-44", "nitro-5-an515-44"),
            ("AN515-45", "nitro-5-an515-45"),
            ("AN517-41", "nitro-5-an517-41"),
            ("AN517-54", "nitro-5-an517-54"),
            ("ANV15-41", "nitro-v-15-anv15-41"),
            ("ANV16-71", "nitro-v-16-anv16-71"),
            ("PH315-52", "predator-helios-300-ph315-52"),
            ("PH315-54", "predator-helios-300-ph315-54"),
            ("PH16-72", "predator-helios-16-ph16-72"),
            ("PH18-71", "predator-helios-18-ph18-71"),
            ("PH18-73", "predator-helios-18-ph18-73"),
            ("PHN16-72", "predator-helios-neo-16-phn16-72"),
            ("PHN16-73", "predator-helios-neo-16-phn16-73"),
            ("PHN16S-71", "predator-helios-neo-16-phn16s-71"),
            ("PHIN16-71", "predator-triton-phin16-71"),
            ("PHIN16S-71", "predator-triton-phin16s-71"),
            ("PHN14-51", "predator-helios-neo-14-phn14-51"),
            ("PTX17-71", "predator-triton-ptx17-71")
        };

        foreach (var (code, id) in expected)
        {
            Assert.Equal(id, catalog.Match(Identity(code)).Id);
        }
    }

    [Fact]
    public void Profiles_UnverifiedRecords_SaySoInTheirNotes()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

        // A record nobody has confirmed on hardware must not read like a measured one.
        Assert.Contains("unverified", catalog.Match(Identity("AN17-42")).Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unverified", catalog.Match(Identity("AN515-56")).Notes, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- worker dispatch

    private static WmiWorkerServer ServerWith(FakeExtraExecutor executor) =>
        new("nitrolite-test-pipe", executor);

    [Fact]
    public void Dispatch_GetCoolBoost_ForwardsToTheExecutor()
    {
        var executor = new FakeExtraExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest { Command = WmiWorkerCommand.GetCoolBoost });

        Assert.True(response.Ok);
        Assert.Equal(1, executor.GetCoolBoostCalls);
    }

    [Fact]
    public void Dispatch_SetCoolBoost_ForwardsTheFlag()
    {
        var executor = new FakeExtraExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetCoolBoost,
            Args = new JsonObject { ["on"] = true }
        });

        Assert.True(response.Ok);
        Assert.True(executor.LastCoolBoost);
    }

    [Fact]
    public void Dispatch_SetCoolBoost_RejectsAMissingFlag()
    {
        var executor = new FakeExtraExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetCoolBoost,
            Args = new JsonObject()
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCoolBoostCalls);
    }

    [Fact]
    public void Dispatch_SetCoolBoost_RejectsAStringFlag()
    {
        // "true" as a JSON string is what a hand-written client sends. Accepting it would mean the worker parses
        // booleans loosely, which is the kind of leniency that turns a typo into a hardware write.
        var executor = new FakeExtraExecutor();
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetCoolBoost,
            Args = new JsonObject { ["on"] = "true" }
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, executor.SetCoolBoostCalls);
    }

    [Fact]
    public void Dispatch_LcdOverdrive_ReadsAndWrites()
    {
        var executor = new FakeExtraExecutor();
        using var server = ServerWith(executor);

        Assert.True(server.Dispatch(new WmiWorkerRequest { Command = WmiWorkerCommand.GetLcdOverdrive }).Ok);
        Assert.Equal(1, executor.GetLcdOverdriveCalls);

        var write = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetLcdOverdrive,
            Args = new JsonObject { ["on"] = false }
        });

        Assert.True(write.Ok);
        Assert.False(executor.LastLcdOverdrive);
    }

    [Fact]
    public void Dispatch_WinKeyLock_RequiresTheEnabledFlag()
    {
        var executor = new FakeExtraExecutor();
        using var server = ServerWith(executor);

        var missing = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetWinKeyLock,
            Args = new JsonObject()
        });

        Assert.False(missing.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, missing.ErrorCode);
        Assert.Equal(0, executor.SetWinKeyLockCalls);

        var ok = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetWinKeyLock,
            Args = new JsonObject { ["enabled"] = true }
        });

        Assert.True(ok.Ok);
        Assert.True(executor.LastWinKeyLock);
    }

    [Fact]
    public void Dispatch_BacklightTimeout_ReadsAndWrites()
    {
        var executor = new FakeExtraExecutor();
        using var server = ServerWith(executor);

        Assert.True(server.Dispatch(new WmiWorkerRequest { Command = WmiWorkerCommand.GetBacklightTimeout }).Ok);
        Assert.Equal(1, executor.GetBacklightTimeoutCalls);

        var write = server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetBacklightTimeout,
            Args = new JsonObject { ["enabled"] = true }
        });

        Assert.True(write.Ok);
        Assert.True(executor.LastBacklightTimeout);
    }

    [Fact]
    public void Dispatch_NotSupported_IsReportedAsSuchRatherThanAsAFailure()
    {
        var executor = new FakeExtraExecutor { CoolBoostSupported = false };
        using var server = ServerWith(executor);

        var response = server.Dispatch(new WmiWorkerRequest { Command = WmiWorkerCommand.GetCoolBoost });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeNotSupported, response.ErrorCode);
    }

    // ---------------------------------------------------------------- EC MMIO quirk

    [Fact]
    public async Task WmiController_EcMmioProfile_RefusesTheModeWriteWithoutAskingTheWorker()
    {
        var snapshot = new CapabilitySnapshot
        {
            CacheKey = "ANV15-52|1.10",
            ProfileId = "nitro-v-15-anv15-52",
            EcMmioProfile = true
        };

        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new InvalidOperationException("the worker must not be started for a refused write"));
        using var controller = new WmiHardwareController(
            launcher, new ReadBackPolicy(new AppSettings(), _ => Task.CompletedTask), () => snapshot);

        // The firmware accepts the WMI write and ignores it, so reporting not-supported is the honest answer and
        // the caller falls back to the Windows power plan.
        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Turbo));
        Assert.Equal(0, launcher.Attempts);
    }

    // ---------------------------------------------------------------- helpers

    private static DeviceIdentity Identity(string modelCode, string bios = "1.27") => new()
    {
        Manufacturer = "Acer",
        Model = $"Nitro {modelCode}",
        ModelCode = modelCode,
        ModelFamily = "",
        BiosVersion = bios,
        IsAcer = true
    };

    private static SchemaProbeResult Schema(AcerRegistrySnapshot registry) => new()
    {
        Wmi = new AcerWmiProbeResult { ClassPresent = true, DynamicClass = true, InstanceAccessDenied = true },
        AcerClassGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AcerGamingFunction"] = "{7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56}"
        },
        Registry = registry
    };

    /// <summary>
    /// Answers the seven new commands and records what it was asked. Nothing here touches WMI, so the dispatch tests
    /// run on any machine.
    /// </summary>
    private sealed class FakeExtraExecutor : IWmiExecutor
    {
        public bool CoolBoostSupported { get; init; } = true;

        public int GetCoolBoostCalls { get; private set; }
        public int SetCoolBoostCalls { get; private set; }
        public int GetLcdOverdriveCalls { get; private set; }
        public int SetWinKeyLockCalls { get; private set; }
        public int GetBacklightTimeoutCalls { get; private set; }

        public bool LastCoolBoost { get; private set; }
        public bool LastLcdOverdrive { get; private set; }
        public bool LastWinKeyLock { get; private set; }
        public bool LastBacklightTimeout { get; private set; }

        public bool IsAvailable => true;

        public WmiOperationResult Ping() => WmiOperationResult.Success(new JsonObject { ["pong"] = true });

        public WmiOperationResult GetCapabilities() => WmiOperationResult.Success(new JsonObject { ["gamingClass"] = true });

        public WmiOperationResult ReadTelemetry(SensorQuery query) => WmiOperationResult.Success(new JsonObject());

        public WmiOperationResult ReadDeviceState() => WmiOperationResult.Success(new JsonObject());

        public WmiOperationResult SetFanBehavior(FanMode cpu, FanMode gpu) => WmiOperationResult.Success();

        public WmiOperationResult SetFanSpeed(int cpuPercent, int gpuPercent) => WmiOperationResult.Success();

        public WmiOperationResult SetOperatingMode(OperatingMode mode) => WmiOperationResult.Success();

        public WmiOperationResult SetMuxMode(GpuOperatingMode mode) => WmiOperationResult.Success();

        public WmiOperationResult SetBatteryLimit(BatteryChargeMode mode) => WmiOperationResult.Success();

        public WmiOperationResult GetKeyboardBacklight() => WmiOperationResult.Success(new JsonObject());

        public WmiOperationResult SetKeyboardLighting(KeyboardLightingRequest request) => WmiOperationResult.Success();

        public WmiOperationResult GetCoolBoost()
        {
            GetCoolBoostCalls++;
            return CoolBoostSupported
                ? WmiOperationResult.Success(new JsonObject { ["on"] = false })
                : WmiOperationResult.NotSupported("APGeAction is not registered");
        }

        public WmiOperationResult SetCoolBoost(bool on)
        {
            SetCoolBoostCalls++;
            LastCoolBoost = on;
            return CoolBoostSupported
                ? WmiOperationResult.Success()
                : WmiOperationResult.NotSupported("APGeAction is not registered");
        }

        public WmiOperationResult GetLcdOverdrive()
        {
            GetLcdOverdriveCalls++;
            return WmiOperationResult.Success(new JsonObject { ["on"] = false });
        }

        public WmiOperationResult SetLcdOverdrive(bool on)
        {
            LastLcdOverdrive = on;
            return WmiOperationResult.Success();
        }

        public WmiOperationResult SetWinKeyLock(bool enabled)
        {
            SetWinKeyLockCalls++;
            LastWinKeyLock = enabled;
            return WmiOperationResult.Success();
        }

        public WmiOperationResult GetBacklightTimeout()
        {
            GetBacklightTimeoutCalls++;
            return WmiOperationResult.Success(new JsonObject { ["enabled"] = false, ["brightness"] = 100 });
        }

        public WmiOperationResult SetBacklightTimeout(bool enabled)
        {
            LastBacklightTimeout = enabled;
            return WmiOperationResult.Success();
        }

        public void Dispose() { }
    }
}
