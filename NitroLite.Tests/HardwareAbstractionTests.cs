using System.Text.Json;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the first step of the hardware-layer migration: the role parser that has to run before the single-instance
/// mutex, the wrapper that forwards to the Acer clients without changing what they do, the rollback setting, and the
/// WMI probe's diagnosis.
/// </summary>
public class HardwareAbstractionTests
{
    private const string ValidNonce = "0123456789abcdef0123456789abcdef";

    // ---------------------------------------------------------------- StartupMode.Parse

    [Fact]
    public void Parse_NoArguments_IsNormalStartup()
    {
        var mode = StartupMode.Parse(null);
        Assert.False(mode.IsWmiWorker);
        Assert.Equal(StartupModeKind.Normal, mode.Kind);
        Assert.False(mode.IsValidWorkerRequest);
    }

    [Fact]
    public void Parse_OrdinaryFlags_StayNormal()
    {
        var mode = StartupMode.Parse(new[] { "--autostart", "--minimized" });
        Assert.False(mode.IsWmiWorker);
    }

    [Fact]
    public void Parse_WorkerFlagWinsOverTheOtherRoles()
    {
        // The helper is a second process of this executable, so the role has to be decided before the
        // single-instance guard classifies it as a duplicate. The parser is the only place that decision is made,
        // and it must not be swayed by the flags the window also understands.
        var mode = StartupMode.Parse(new[]
        {
            "--autostart", "--minimized", "--wmi-worker", "--pipe", "p", "--parent-pid", "7"
        });

        Assert.True(mode.IsWmiWorker);
        Assert.True(mode.IsValidWorkerRequest);
    }

    [Fact]
    public void Parse_WorkerWithEveryArgument_IsAValidRequest()
    {
        var mode = StartupMode.Parse(new[]
        {
            "--wmi-worker", "--pipe", "nitrolite-wmi-1", "--parent-pid", "4242"
        });

        Assert.True(mode.IsWmiWorker);
        Assert.Equal("nitrolite-wmi-1", mode.PipeName);
        Assert.Equal(4242, mode.ParentProcessId);
        Assert.True(mode.IsValidWorkerRequest);
    }

    [Fact]
    public void Parse_AcceptsTheEqualsForm()
    {
        var mode = StartupMode.Parse(new[]
        {
            "--wmi-worker", "--pipe=nitrolite-wmi-2", "--parent-pid=99"
        });

        Assert.True(mode.IsValidWorkerRequest);
        Assert.Equal("nitrolite-wmi-2", mode.PipeName);
        Assert.Equal(99, mode.ParentProcessId);
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        var mode = StartupMode.Parse(new[]
        {
            "--WMI-WORKER", "--PIPE", "p", "--PARENT-PID", "7"
        });

        Assert.True(mode.IsValidWorkerRequest);
    }

    [Fact]
    public void Parse_IgnoresALegacyNonceArgument()
    {
        // The handshake value no longer travels on the command line. An older launcher that still passes one must
        // not break the parse, and the value must not be picked up from anywhere.
        var mode = StartupMode.Parse(new[]
        {
            "--wmi-worker", "--pipe", "p", "--parent-pid", "7", "--nonce", ValidNonce
        });

        Assert.True(mode.IsValidWorkerRequest);
        Assert.Equal("p", mode.PipeName);
        Assert.Equal(7, mode.ParentProcessId);
    }

    [Fact]
    public void Parse_WorkerWithoutAPipe_IsRejected()
    {
        var mode = StartupMode.Parse(new[] { "--wmi-worker", "--parent-pid", "7" });
        Assert.True(mode.IsWmiWorker);
        Assert.False(mode.IsValidWorkerRequest);
    }

    [Fact]
    public void Parse_WorkerWithoutParent_IsRejected()
    {
        var mode = StartupMode.Parse(new[] { "--wmi-worker", "--pipe", "p" });
        Assert.False(mode.IsValidWorkerRequest);
    }

    [Fact]
    public void Parse_UnparsableParentPid_IsRejected()
    {
        var mode = StartupMode.Parse(new[] { "--wmi-worker", "--pipe", "p", "--parent-pid", "abc" });
        Assert.Equal(0, mode.ParentProcessId);
        Assert.False(mode.IsValidWorkerRequest);
    }

    [Fact]
    public void Parse_FlagFollowedByAnotherFlag_DoesNotSwallowItAsAValue()
    {
        // "--pipe --parent-pid 7" must leave the pipe empty: naming the pipe "--parent-pid" would be worse than
        // failing.
        var mode = StartupMode.Parse(new[] { "--wmi-worker", "--pipe", "--parent-pid", "7" });
        Assert.Equal("", mode.PipeName);
        Assert.Equal(7, mode.ParentProcessId);
        Assert.False(mode.IsValidWorkerRequest);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcde", false)]   // 31 characters
    [InlineData("0123456789abcdef0123456789abcdeg", false)]  // not hex
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHexNonce_RequiresAtLeast128BitsOfHex(string? nonce, bool expected) =>
        Assert.Equal(expected, StartupMode.IsHexNonce(nonce));

    // ---------------------------------------------------------------- AgentHardwareController

    [Fact]
    public void Controller_ReportsTheServiceBackendName()
    {
        using var controller = new AgentHardwareController(new RecordingAgentClient(), new RecordingCareCenterClient());
        Assert.Equal("AASSvc", controller.BackendName);
    }

    [Fact]
    public async Task Controller_ForwardsTheOperatingModeWrite()
    {
        var agent = new RecordingAgentClient();
        using var controller = new AgentHardwareController(agent, new RecordingCareCenterClient());

        Assert.True(await controller.SetOperatingModeAsync(OperatingMode.Turbo));
        Assert.Equal(new[] { OperatingMode.Turbo }, agent.ModeWrites);
    }

    [Fact]
    public async Task Controller_ForwardsTheFanWriteWithBothPercentages()
    {
        var agent = new RecordingAgentClient();
        using var controller = new AgentHardwareController(agent, new RecordingCareCenterClient());

        Assert.True(await controller.SetFanModeAsync(FanMode.Custom, 42, 63));
        Assert.Equal((FanMode.Custom, 42, 63), agent.LastFanWrite);
    }

    [Fact]
    public async Task Controller_ForwardsTheGpuModeWrite()
    {
        var agent = new RecordingAgentClient();
        using var controller = new AgentHardwareController(agent, new RecordingCareCenterClient());

        Assert.True(await controller.SetGpuModeAsync(GpuOperatingMode.DiscreteGpu));
        Assert.Equal(new[] { GpuOperatingMode.DiscreteGpu }, agent.GpuWrites);
    }

    [Fact]
    public async Task Controller_ForwardsTheBatteryChargeLimitToCareCenter()
    {
        var careCenter = new RecordingCareCenterClient();
        using var controller = new AgentHardwareController(new RecordingAgentClient(), careCenter);

        Assert.True(await controller.SetBatteryChargeModeAsync(BatteryChargeMode.Optimized80));
        Assert.Equal(new[] { BatteryChargeMode.Optimized80 }, careCenter.Writes);
    }

    [Fact]
    public async Task Controller_ForwardsReadsUnchanged()
    {
        var agent = new RecordingAgentClient { Mode = OperatingMode.Extreme, Fan = FanMode.Max, GpuCapability = 1 };
        using var controller = new AgentHardwareController(agent, new RecordingCareCenterClient());

        Assert.Equal(OperatingMode.Extreme, await controller.GetOperatingModeAsync());
        Assert.Equal(FanMode.Max, await controller.GetFanModeAsync());
        Assert.Equal(1, await controller.GetGpuModeCapabilityAsync());
        Assert.Null(await controller.GetMonitorDataAsync());
    }

    [Fact]
    public async Task Controller_SerialisesConcurrentWrites()
    {
        // The fan curve, the thermal floor and a click can all write at once. Two writes overlapping is what leaves
        // the EC in a state nobody asked for, so the gate has to hold under real concurrency.
        var agent = new RecordingAgentClient { WriteDelay = TimeSpan.FromMilliseconds(25) };
        using var controller = new AgentHardwareController(agent, new RecordingCareCenterClient());

        var writes = Enumerable.Range(0, 6)
            .Select(i => controller.SetFanModeAsync(FanMode.Custom, 30 + i, 30 + i))
            .ToArray();
        await Task.WhenAll(writes);

        Assert.Equal(1, agent.MaxConcurrentWrites);
        Assert.Equal(6, agent.FanWriteCount);
    }

    [Fact]
    public async Task Controller_ReleasesTheGateWhenAWriteThrows()
    {
        var agent = new RecordingAgentClient { ThrowOnWrite = true };
        using var controller = new AgentHardwareController(agent, new RecordingCareCenterClient());

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SetFanModeAsync(FanMode.Max));

        // A thrown write must not wedge every later write for the rest of the session.
        agent.ThrowOnWrite = false;
        Assert.True(await controller.SetFanModeAsync(FanMode.Auto));
    }

    [Fact]
    public async Task Controller_ReportsNoCapabilitiesWhenTheServiceIsDown()
    {
        var agent = new RecordingAgentClient { Connected = false };
        using var controller = new AgentHardwareController(agent, new RecordingCareCenterClient());

        var capabilities = await controller.GetCapabilitiesAsync();

        Assert.False(capabilities.ServiceAvailable);
        Assert.False(capabilities.FanControl);
        Assert.False(capabilities.OperatingModeControl);
        Assert.False(capabilities.MuxSwitch);
        Assert.False(capabilities.BatteryChargeLimit);
        Assert.False(capabilities.KeyboardLighting);
        Assert.Equal("AASSvc", capabilities.Backend);
    }

    [Fact]
    public async Task Controller_ReportsOnlyTheCapabilitiesThatAnswered()
    {
        var agent = new RecordingAgentClient { Mode = OperatingMode.Default, Fan = FanMode.Auto, GpuCapability = null };
        using var controller = new AgentHardwareController(agent, new RecordingCareCenterClient { Mode = null });

        var capabilities = await controller.GetCapabilitiesAsync();

        Assert.True(capabilities.ServiceAvailable);
        Assert.True(capabilities.FanControl);
        Assert.True(capabilities.OperatingModeControl);
        Assert.False(capabilities.MuxSwitch);          // the machine has no switch
        Assert.False(capabilities.BatteryChargeLimit); // Care Center did not answer
    }

    // ---------------------------------------------------------------- UseDirectWmiBackend

    [Fact]
    public void UseDirectWmiBackend_DefaultsToOn()
    {
        Assert.True(new AppSettings().UseDirectWmiBackend);
    }

    [Fact]
    public void UseDirectWmiBackend_SurvivesASettingsRoundTrip()
    {
        var settings = new AppSettings { UseDirectWmiBackend = false };
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("use_direct_wmi_backend", json);

        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.False(restored!.UseDirectWmiBackend);
    }

    [Fact]
    public void UseDirectWmiBackend_IsOnWhenAnOlderFileDoesNotCarryIt()
    {
        // A settings file written before the switch existed must not silently pin the old path.
        var restored = JsonSerializer.Deserialize<AppSettings>("{\"start_minimized\":true}");
        Assert.NotNull(restored);
        Assert.True(restored!.UseDirectWmiBackend);
    }

    // ---------------------------------------------------------------- AcerWmiProbe diagnosis

    [Fact]
    public void ProbeSummary_SaysInstanceAccessIsDeniedRatherThanBlamingTheProvider()
    {
        var result = new AcerWmiProbeResult
        {
            ClassPresent = true,
            DynamicClass = true,
            InstanceAccessDenied = true,
            Methods = new[] { "GetGamingFanSpeed", "SetGamingFanSpeed" },
            CallMethod = "GetGamingFanSpeed",
            CallSucceeded = false,
            CallError = "InvalidMethodParameters (0x8004102F)"
        };

        Assert.Contains("instance access denied", result.Summary);
        Assert.Contains("needs elevation", result.Summary);
        Assert.DoesNotContain("failed", result.Summary);
    }

    [Fact]
    public void ProbeSummary_StillNamesTheFailureWhenTheClassIsNotDynamic()
    {
        var result = new AcerWmiProbeResult
        {
            ClassPresent = true,
            Methods = new[] { "GetGamingFanSpeed" },
            CallMethod = "GetGamingFanSpeed",
            CallSucceeded = false,
            CallError = "InvalidMethodParameters (0x8004102F)"
        };

        Assert.Contains("GetGamingFanSpeed failed", result.Summary);
        Assert.Contains("0x8004102F", result.Summary);
    }

    [Fact]
    public void ProbeSummary_ReportsASuccessfulCallEvenOnADynamicClass()
    {
        var result = new AcerWmiProbeResult
        {
            ClassPresent = true,
            DynamicClass = true,
            Methods = new[] { "GetGamingFanSpeed" },
            CallMethod = "GetGamingFanSpeed",
            CallSucceeded = true
        };

        Assert.Equal("registered, GetGamingFanSpeed answered", result.Summary);
    }

    // ---------------------------------------------------------------- HardwareProbeResult

    [Fact]
    public void ProbeResult_KeepsAccessDeniedApartFromUnsupported()
    {
        var denied = HardwareProbeResult.AccessDenied("fan.speed", "0x8004102F");
        var missing = HardwareProbeResult.Unsupported("fan.speed", "no method");

        Assert.False(denied.IsSupported);
        Assert.False(missing.IsSupported);
        Assert.True(denied.IsInconclusive);   // worth retrying elevated
        Assert.False(missing.IsInconclusive); // the hardware said no
    }

    [Fact]
    public void ProbeResult_SummaryCarriesTheEvidence()
    {
        Assert.Equal("fan.speed: supported", HardwareProbeResult.Supported("fan.speed").Summary);
        Assert.Equal("mux: access denied (0x8004102F)", HardwareProbeResult.AccessDenied("mux", "0x8004102F").Summary);
        Assert.Equal("capability: unknown", new HardwareProbeResult().Summary);
    }

    // ---------------------------------------------------------------- fakes

    private sealed class RecordingAgentClient : AcerAgentClient
    {
        private int _activeWrites;
        private int _maxConcurrentWrites;

        public bool Connected { get; set; } = true;
        public OperatingMode? Mode { get; set; } = OperatingMode.Default;
        public FanMode? Fan { get; set; } = FanMode.Auto;
        public int? GpuCapability { get; set; }
        public TimeSpan WriteDelay { get; set; } = TimeSpan.Zero;
        public bool ThrowOnWrite { get; set; }

        public List<OperatingMode> ModeWrites { get; } = new();
        public List<GpuOperatingMode> GpuWrites { get; } = new();
        public (FanMode Mode, int Cpu, int Gpu) LastFanWrite { get; private set; }
        public int FanWriteCount { get; private set; }
        public int MaxConcurrentWrites => _maxConcurrentWrites;

        public override bool IsConnected => Connected;

        public override Task<bool> EnsureConnectedAsync() => Task.FromResult(Connected);

        public override Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(null);

        public override Task<OperatingMode?> TryGetOperatingModeAsync() => Task.FromResult(Mode);

        public override Task<bool> SetOperatingModeAsync(OperatingMode mode)
        {
            ModeWrites.Add(mode);
            return Task.FromResult(true);
        }

        public override Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(GpuOperatingMode.Optimus);

        public override Task<bool> SetGpuModeAsync(GpuOperatingMode mode)
        {
            GpuWrites.Add(mode);
            return Task.FromResult(true);
        }

        public override Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult(GpuCapability);

        public override Task<FanMode?> GetFanModeAsync() => Task.FromResult(Fan);

        public override async Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
        {
            int active = Interlocked.Increment(ref _activeWrites);
            try
            {
                // Recorded after the increment so an overlap is visible even if the delay is short.
                int seen;
                while (active > (seen = Volatile.Read(ref _maxConcurrentWrites)))
                {
                    Interlocked.CompareExchange(ref _maxConcurrentWrites, active, seen);
                }

                if (WriteDelay > TimeSpan.Zero) await Task.Delay(WriteDelay);
                if (ThrowOnWrite) throw new InvalidOperationException("write refused");

                LastFanWrite = (mode, cpuPercent, gpuPercent);
                FanWriteCount++;
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }
        }

        public override Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(null);
    }

    private sealed class RecordingCareCenterClient : CareCenterClient
    {
        public BatteryChargeMode? Mode { get; set; } = BatteryChargeMode.Optimized80;
        public List<BatteryChargeMode> Writes { get; } = new();

        public override Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => Task.FromResult(Mode);

        public override Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode)
        {
            Writes.Add(mode);
            return Task.FromResult(true);
        }
    }
}
