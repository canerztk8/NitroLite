using System.Text.Json.Nodes;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the universal-discovery package: the plausibility window every firmware reading has to pass, the live sensor
/// probe that lets an Acer laptop the profile table does not name still report real temperatures, and the Markdown
/// hardware report a user pastes into a GitHub issue.
/// <para>
/// The privacy tests are the important ones here. The report is designed to be published, so a regression that starts
/// including a serial number or a user name is a data leak, not a formatting bug — and it would be invisible in a
/// manual check because the report still looks fine.
/// </para>
/// </summary>
public class HalStep9Tests
{
    // ---------------------------------------------------------------- plausibility window

    [Theory]
    [InlineData(1, true)]
    [InlineData(45, true)]
    [InlineData(95, true)]
    [InlineData(130, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(131, false)]
    [InlineData(65535, false)]
    public void Plausibility_Temperature_AcceptsOnlyTheDieWindow(int value, bool expected)
    {
        Assert.Equal(expected, SensorPlausibility.IsPlausibleTemperature(value));
    }

    [Theory]
    [InlineData(0, true)]      // a stopped fan is a real, alarming reading
    [InlineData(1, true)]
    [InlineData(2436, true)]
    [InlineData(12000, true)]
    [InlineData(12001, false)]
    [InlineData(-1, false)]
    [InlineData(32767, false)]
    [InlineData(32768, false)]
    [InlineData(57005, false)] // 0xDEAD
    [InlineData(48879, false)] // 0xBEEF
    [InlineData(65535, false)]
    public void Plausibility_FanRpm_AcceptsZeroButRejectsSentinels(int value, bool expected)
    {
        Assert.Equal(expected, SensorPlausibility.IsPlausibleFanRpm(value));
    }

    [Fact]
    public void Plausibility_ZeroIsAMeasurementButNotEvidenceOfASensor()
    {
        // The two questions deliberately disagree. Rejecting zero as a measurement would blind the stall watchdog on
        // exactly the sample it exists to catch; accepting zero as discovery evidence would let a firmware that
        // answers nothing look like a working tachometer.
        Assert.True(SensorPlausibility.IsPlausibleFanRpm(0));
        Assert.False(SensorPlausibility.IsPresentFanRpm(0));
        Assert.True(SensorPlausibility.IsPresentFanRpm(1));
    }

    [Fact]
    public void Plausibility_Describe_NamesTheWindow()
    {
        var text = SensorPlausibility.Describe();

        Assert.Contains("1..130", text);
        Assert.Contains("0..12000", text);
    }

    // ---------------------------------------------------------------- live sensor discovery

    private static Dictionary<string, int?> Answered(
        int? cpuTemp = 63, int? cpuRpm = 2436, int? sys1 = 55, int? gpuRpm = 2409, int? gpuTemp = 57) => new()
    {
        [SensorDiscovery.CpuTemp] = cpuTemp,
        [SensorDiscovery.CpuFanRpm] = cpuRpm,
        [SensorDiscovery.Sys1Temp] = sys1,
        [SensorDiscovery.GpuFanRpm] = gpuRpm,
        [SensorDiscovery.GpuTemp] = gpuTemp
    };

    [Fact]
    public void Discovery_PlausibleAnswer_ConfirmsTheLayoutWithoutClaimingItWasMeasured()
    {
        var layout = SensorDiscovery.Resolve(Answered(), SensorLayout.Default);

        Assert.NotNull(layout);
        Assert.True(layout!.Supported);
        // Confirmed live, but nobody compared it against a reference, so it is not "verified".
        Assert.False(layout.Verified);
    }

    [Fact]
    public void Discovery_MissingCpuTemperature_IsRejected()
    {
        // The die temperature is the one reading every Acer gaming firmware answers. Without it the call is not
        // being served the way this build understands it.
        Assert.Null(SensorDiscovery.Resolve(Answered(cpuTemp: null), SensorLayout.Default));
    }

    [Fact]
    public void Discovery_ImplausibleCpuTemperature_IsRejected()
    {
        Assert.Null(SensorDiscovery.Resolve(Answered(cpuTemp: 65535), SensorLayout.Default));
        Assert.Null(SensorDiscovery.Resolve(Answered(cpuTemp: 0), SensorLayout.Default));
        Assert.Null(SensorDiscovery.Resolve(Answered(cpuTemp: 200), SensorLayout.Default));
    }

    [Fact]
    public void Discovery_BothFansAtZero_IsRejected()
    {
        // Either the machine is off or the firmware is answering a sentinel for ids it does not have. Neither is
        // evidence that the layout is right.
        Assert.Null(SensorDiscovery.Resolve(Answered(cpuRpm: 0, gpuRpm: 0), SensorLayout.Default));
    }

    [Fact]
    public void Discovery_OneTachometerTurning_IsEnough()
    {
        var layout = SensorDiscovery.Resolve(Answered(cpuRpm: 0, gpuRpm: 2409), SensorLayout.Default);

        Assert.NotNull(layout);
        Assert.True(layout!.Supported);
    }

    [Fact]
    public void Discovery_EchoedValue_IsRejected()
    {
        // A provider that answers the same word to every query is not measuring anything.
        var echoed = Answered(cpuTemp: 55, cpuRpm: 55, sys1: 55, gpuRpm: 55, gpuTemp: 55);

        Assert.Null(SensorDiscovery.Resolve(echoed, SensorLayout.Default));
    }

    [Fact]
    public void Discovery_EmptyAnswer_IsRejected()
    {
        Assert.Null(SensorDiscovery.Resolve(new Dictionary<string, int?>(), SensorLayout.Default));
    }

    [Fact]
    public void Discovery_KeepsTheCandidateSensorIds()
    {
        var candidate = new SensorLayout { CpuTempSensor = 1, GpuTempSensor = 10, Sys1TempSensor = 3, CpuFanRpmSensor = 2, GpuFanRpmSensor = 6 };
        var layout = SensorDiscovery.Resolve(Answered(), candidate);

        Assert.NotNull(layout);
        Assert.Equal(1, layout!.CpuTempSensor);
        Assert.Equal(10, layout.GpuTempSensor);
        Assert.Equal(6, layout.GpuFanRpmSensor);
    }

    // ---------------------------------------------------------------- telemetry mapping

    private static JsonObject Sensor(int id, int value) => new()
    {
        ["sensor"] = id,
        ["status"] = 0,
        ["value"] = value
    };

    private static JsonObject Telemetry() => new()
    {
        ["sensors"] = new JsonObject
        {
            ["cpuTemp"] = Sensor(1, 63),
            ["cpuFanRpm"] = Sensor(2, 2436),
            ["sys1Temp"] = Sensor(3, 55),
            ["gpuFanRpm"] = Sensor(6, 2409),
            ["gpuTemp"] = Sensor(10, 57)
        },
        ["cpuFanPercent"] = 34,
        ["gpuFanPercent"] = 34
    };

    private static SensorLayout VerifiedLayout() => new() { Supported = true, Verified = true };

    private static SensorLayout UnverifiedLayout() => new() { Supported = true, Verified = false };

    [Fact]
    public void Telemetry_SentinelTemperature_IsReportedAsNoReading()
    {
        // 65535 is the classic "unset unsigned word". Publishing it would show a 6553.5 °C die.
        var answer = Telemetry();
        answer["sensors"]!["cpuTemp"] = Sensor(1, 65535);

        var data = WmiHardwareController.BuildMonitorData(answer, VerifiedLayout(), 6200, 6000);

        Assert.Equal(0, data.CpuTemperature);
        Assert.Equal(57, data.GpuTemperature);
    }

    [Fact]
    public void Telemetry_SentinelRpm_FallsBackToDerivedAndLeavesTheWatchdogInStandby()
    {
        var answer = Telemetry();
        answer["sensors"]!["cpuFanRpm"] = Sensor(2, 65535);

        var data = WmiHardwareController.BuildMonitorData(answer, VerifiedLayout(), 6200, 6000);

        Assert.False(data.IsFanRpmMeasured);
        Assert.Equal(WmiHardwareController.DeriveRpm(34, 6200), data.CpuFanSpeed);
    }

    [Fact]
    public void Telemetry_StoppedFanOnAVerifiedLayout_IsStillAMeasurement()
    {
        // The whole point of accepting zero: a hot die with a stopped fan has to reach the watchdog.
        var answer = Telemetry();
        answer["sensors"]!["cpuFanRpm"] = Sensor(2, 0);
        answer["sensors"]!["gpuFanRpm"] = Sensor(6, 0);

        var data = WmiHardwareController.BuildMonitorData(answer, VerifiedLayout(), 6200, 6000);

        Assert.True(data.IsFanRpmMeasured);
        Assert.Equal(0, data.CpuFanSpeed);
    }

    [Fact]
    public void Telemetry_UnverifiedLayout_ShowsTemperaturesButDoesNotClaimAMeasurement()
    {
        // An unlisted model: the readings are useful, but nobody has confirmed the ids, so the watchdog must not
        // judge a real die against a number that might be a different sensor.
        var data = WmiHardwareController.BuildMonitorData(Telemetry(), UnverifiedLayout(), 6200, 6000);

        Assert.Equal(63, data.CpuTemperature);
        Assert.Equal(57, data.GpuTemperature);
        Assert.False(data.IsFanRpmMeasured);
        Assert.Equal(WmiHardwareController.DeriveRpm(34, 6200), data.CpuFanSpeed);
    }

    [Fact]
    public void Telemetry_ConfirmedProbe_ReEnablesTheMeasurementOnAnUnverifiedLayout()
    {
        var data = WmiHardwareController.BuildMonitorData(
            Telemetry(), UnverifiedLayout(), 6200, 6000, tachometerConfirmed: true);

        Assert.True(data.IsFanRpmMeasured);
        Assert.Equal(2436, data.CpuFanSpeed);
        Assert.Equal(2409, data.GpuFanSpeed);
    }

    [Fact]
    public void Telemetry_ExplicitRefusal_OverridesAVerifiedLayout()
    {
        // The parameter is the caller's verdict, not a hint: a probe that ran and failed must win over the profile.
        var data = WmiHardwareController.BuildMonitorData(
            Telemetry(), VerifiedLayout(), 6200, 6000, tachometerConfirmed: false);

        Assert.False(data.IsFanRpmMeasured);
    }

    // ---------------------------------------------------------------- capability snapshot

    [Fact]
    public void Probe_UnlistedModel_FallsToTheCatchAllAndStillCarriesTheSchema()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var schema = new SchemaProbeResult
        {
            Wmi = new AcerWmiProbeResult { ClassPresent = true, DynamicClass = true, InstanceAccessDenied = true },
            AcerClassGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AcerGamingFunction"] = "{7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56}"
            },
            Registry = new AcerRegistrySnapshot { KeyPresent = true, DgpuModeCapability = 7, KeyboardArea = 4 }
        };

        var snapshot = CapabilityProbe.Probe(Identity("ANV15-99"), catalog, schema);

        Assert.Equal("acer-gaming-default", snapshot.ProfileId);
        Assert.True(snapshot.Sensors.Supported);
        Assert.False(snapshot.Sensors.Verified);
        Assert.Single(snapshot.AcerClassGuids);
    }

    [Fact]
    public void Probe_UserProfile_OverridesTheCatchAllButStillRunsLiveDiscovery()
    {
        // A user profile still wins the match over the catch-all, but "verified" (which skips live sensor discovery)
        // is honoured only for an id the embedded table verifies with identical sensor ids (SEC-07). This id is new.
        const string user = """
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "anv15-99-user",
              "match": { "modelCodes": [ "ANV15-99" ] },
              "sensors": {
                "supported": true,
                "verified": true,
                "cpuTempSensor": 1,
                "cpuFanRpmSensor": 2,
                "sys1TempSensor": 3,
                "gpuFanRpmSensor": 6,
                "gpuTempSensor": 10
              },
              "maxFanRpm": { "cpu": 5400, "gpu": 5200 }
            }
          ]
        }
        """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);
        Assert.Empty(rejections);

        var schema = new SchemaProbeResult
        {
            Wmi = new AcerWmiProbeResult { ClassPresent = true },
            AcerClassGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AcerGamingFunction"] = "{7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56}"
            },
            Registry = new AcerRegistrySnapshot { KeyPresent = true }
        };

        var snapshot = CapabilityProbe.Probe(Identity("ANV15-99"), catalog, schema);

        Assert.Equal("anv15-99-user", snapshot.ProfileId);
        Assert.Equal("user", snapshot.ProfileSource);
        Assert.False(snapshot.Sensors.Verified);
        Assert.Equal(5400, snapshot.MaxCpuFanRpm);
    }

    // ---------------------------------------------------------------- Markdown report

    private static CapabilitySnapshot ReportSnapshot() => new()
    {
        CacheKey = "ANV15-99|1.10",
        ProfileId = "acer-gaming-default",
        ProfileSource = "embedded",
        ProfileNotes = "Fallback for any Acer machine the table does not name.",
        Sensors = new SensorLayout { Supported = true, Verified = false },
        MaxCpuFanRpm = 7000,
        MaxGpuFanRpm = 7000,
        WmiMuxSignal = false,
        Capabilities = new[]
        {
            new HardwareProbeResult
            {
                Capability = CapabilitySnapshot.WmiGamingFunction,
                Outcome = HardwareProbeOutcome.AccessDenied,
                Detail = "dynamic class, instance needs elevation"
            },
            new HardwareProbeResult
            {
                Capability = CapabilitySnapshot.MuxSwitch,
                Outcome = HardwareProbeOutcome.Supported,
                Detail = "dgpu_mode_capability=7"
            }
        },
        Wmi = new AcerWmiProbeResult
        {
            ClassPresent = true,
            DynamicClass = true,
            InstanceAccessDenied = true,
            Methods = new[] { "GetGamingMiscSetting", "SetGamingMiscSetting" },
            Signatures = new[] { "GetGamingMiscSetting(gmInput:UInt32) -> gmOutput:UInt64" }
        },
        AcerClassGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AcerGamingFunction"] = "{7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56}"
        },
        Registry = new AcerRegistrySnapshot
        {
            KeyPresent = true,
            DgpuModeCapability = 7,
            KeyboardArea = 4,
            GpuOverclockSupport = 1
        }
    };

    private static DeviceIdentity ReportIdentity() => new()
    {
        Manufacturer = "Acer",
        Model = "Nitro ANV15-99",
        ModelCode = "ANV15-99",
        ModelFamily = "Nitro V 15",
        BoardProduct = "ZYP",
        BiosVendor = "INSYDE Corp.",
        BiosVersion = "1.10",
        CpuVendor = CpuVendor.Intel,
        IsAcer = true
    };

    [Fact]
    public void Report_ContainsTheFactsATriagerNeeds()
    {
        var markdown = HardwareReport.BuildMarkdown(
            ReportSnapshot(), identity: ReportIdentity());

        Assert.Contains("### NitroLite hardware report", markdown);
        Assert.Contains("ANV15-99", markdown);
        Assert.Contains("1.10", markdown);
        Assert.Contains("acer-gaming-default", markdown);
        Assert.Contains("GetGamingMiscSetting", markdown);
        Assert.Contains("dgpu_mode_capability", markdown);
        Assert.Contains("KeyBoardArea", markdown);
        Assert.Contains(CapabilitySnapshot.WmiGamingFunction, markdown);
        Assert.Contains("AccessDenied", markdown);
    }

    [Fact]
    public void Report_SaysNotReadRatherThanOmittingTheElevatedHalf()
    {
        // A report that silently dropped the WMI half would look like a machine without WMI, which is the opposite
        // of the truth on a machine where the worker was simply never started.
        var markdown = HardwareReport.BuildMarkdown(ReportSnapshot(), identity: ReportIdentity());

        Assert.Contains("not read (needs elevation)", markdown);
        Assert.Contains("(direct-WMI backend not started)", markdown);
    }

    [Fact]
    public void Report_WithCapabilities_FillsInTheLiveSelectors()
    {
        var capabilities = new HardwareCapabilities
        {
            Backend = "WMI",
            ServiceAvailable = true,
            CoolBoost = true,
            BacklightTimeout = true,
            WinKeyLock = true,
            SupportedModeMask = 0x7300,
            SupportedOperatingModes = new[]
            {
                OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo, OperatingMode.Eco
            }
        };

        var markdown = HardwareReport.BuildMarkdown(
            ReportSnapshot(), capabilities, identity: ReportIdentity());

        Assert.Contains("0x7300", markdown);
        Assert.Contains("Turbo", markdown);
        Assert.Contains("present", markdown);
        Assert.DoesNotContain("not read (needs elevation)", markdown);
    }

    [Fact]
    public void Report_ShowsTheLiveMuxSelector_EvenThoughTheProbeRanBeforeTheWorker()
    {
        // The probe runs at launch, before any elevated worker exists, so its own WmiMuxSignal is always null. The
        // capability snapshot taken later is the only place the 0x09 answer survives, and a report that ignores it
        // says "not read (needs elevation)" on a session that was already elevated — which is what an AN16-61
        // reporter saw and had to work around by reading the log.
        var capabilities = new HardwareCapabilities { Backend = "WMI", ServiceAvailable = true, MuxSwitchOverWmi = true };

        var markdown = HardwareReport.BuildMarkdown(ReportSnapshot(), capabilities, identity: ReportIdentity());

        Assert.Contains("| GetGamingMiscSetting(0x09) MUX | 3 (switchable over WMI) |", markdown);
        Assert.DoesNotContain("MUX | not read", markdown);
    }

    [Fact]
    public void Report_SaysTheWorkerDidNotReportTheMux_WhenItReallyDidNot()
    {
        // A worker that answered without a 0x09 value is not the same as no elevation, and the two must not share a
        // sentence: the first is a firmware answer this build could not decode, the second is a session that never
        // asked.
        var capabilities = new HardwareCapabilities { Backend = "WMI", ServiceAvailable = true };
        var probe = ReportSnapshot() with { WmiMuxSignal = null };

        var markdown = HardwareReport.BuildMarkdown(probe, capabilities, identity: ReportIdentity());

        Assert.Contains("| GetGamingMiscSetting(0x09) MUX | not read (the worker did not report it) |", markdown);
    }

    [Fact]
    public void Report_ListsEveryReadBackCapability()
    {
        var settings = new AppSettings();
        settings.CapabilitySessionFailures[ReadBackPolicy.FanSpeed] = 2;
        settings.DisabledCapabilities.Add(ReadBackPolicy.MuxMode);
        var policy = new ReadBackPolicy(settings, _ => Task.CompletedTask);

        var markdown = HardwareReport.BuildMarkdown(
            ReportSnapshot(), readBack: policy, identity: ReportIdentity());

        foreach (var capability in ReadBackPolicy.AllCapabilities)
            Assert.Contains(capability, markdown);

        Assert.Contains("| 2 | active |", markdown);
        Assert.Contains("permanently disabled", markdown);
    }

    [Fact]
    public void Report_DescribesTheSensorProbeOutcome()
    {
        var confirmed = HardwareReport.BuildMarkdown(
            ReportSnapshot(),
            discoveredSensors: new SensorLayout { Supported = true, Verified = false },
            sensorProbeRan: true,
            identity: ReportIdentity());
        Assert.Contains("ran, confirmed", confirmed);

        var rejected = HardwareReport.BuildMarkdown(
            ReportSnapshot(), sensorProbeRan: true, identity: ReportIdentity());
        Assert.Contains("ran, rejected", rejected);

        var notRun = HardwareReport.BuildMarkdown(ReportSnapshot(), identity: ReportIdentity());
        Assert.Contains("| Live probe | not run |", notRun);
    }

    [Fact]
    public void Report_EscapesPipesSoTheTableSurvivesAFreeFormNote()
    {
        var snapshot = ReportSnapshot() with { ProfileNotes = "a|b" };

        var markdown = HardwareReport.BuildMarkdown(snapshot, identity: ReportIdentity());

        Assert.Contains("a\\|b", markdown);
    }

    // ---------------------------------------------------------------- privacy

    [Fact]
    public void Report_ContainsNoSerialNumberUuidUserNameOrPath()
    {
        var markdown = HardwareReport.BuildMarkdown(
            ReportSnapshot(),
            new HardwareCapabilities { Backend = "WMI", CoolBoost = true },
            new ReadBackPolicy(new AppSettings(), _ => Task.CompletedTask),
            new SensorLayout { Supported = true },
            sensorProbeRan: true,
            identity: ReportIdentity());

        // The closing guarantee names the categories it excludes, so it is removed before the scan — otherwise the
        // sentence that promises privacy would be the thing that fails the privacy test.
        var body = markdown.Replace(
            "_No serial number, machine UUID, user name, file path or log content is included._", "");

        // The words themselves must not appear, in any casing: a field named "SerialNumber" would be as much of a
        // leak as the value.
        foreach (var forbidden in new[]
                 {
                     "serial", "uuid", "username", "user name", "machineguid", "machine guid",
                     "C:\\", "\\\\Users", "/home/", "AppData", "SID"
                 })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Report_StatesThePrivacyGuarantee()
    {
        var markdown = HardwareReport.BuildMarkdown(ReportSnapshot(), identity: ReportIdentity());

        Assert.Contains("No serial number, machine UUID, user name, file path or log content", markdown);
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
}
