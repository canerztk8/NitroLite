using System.Text.Json.Nodes;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// With the Acer service answering, what a machine shows (MUX, mode cards, fan control) comes from the service's own
/// SUPPORT_* answers, the way NitroSense does it, not from guessing at values. Reference: AN16-41.
/// </summary>
public class AgentServiceCapabilityTests
{
    // ---------------------------------------------------------------- MUX

    [Fact]
    public async Task Mux_AnswerOfZero_ShowsNoMuxControl()
    {
        var caps = await CapabilitiesAsync(new FakeAgent { GpuCapability = 0 });
        Assert.False(caps.MuxSwitch);
    }

    [Fact]
    public async Task Mux_AnswerOfSeven_ShowsTheMuxControl()
    {
        var caps = await CapabilitiesAsync(new FakeAgent { GpuCapability = 7 });
        Assert.True(caps.MuxSwitch);
    }

    // ---------------------------------------------------------------- operating modes

    [Fact]
    public async Task Modes_FollowTheServiceList()
    {
        var caps = await CapabilitiesAsync(new FakeAgent
        {
            Modes = new[]
            {
                OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo, OperatingMode.Eco
            }
        });

        Assert.Contains(OperatingMode.Eco, caps.SupportedOperatingModes);
        Assert.Equal(5, caps.SupportedOperatingModes.Count);
        Assert.True(ModePolicy.IsModeSupported(caps.SupportedOperatingModes, OperatingMode.Eco));
    }

    [Fact]
    public async Task Modes_ListWithoutTurbo_HidesTurbo()
    {
        var caps = await CapabilitiesAsync(new FakeAgent
        {
            Modes = new[] { OperatingMode.Quiet, OperatingMode.Default }
        });
        Assert.False(ModePolicy.IsModeSupported(caps.SupportedOperatingModes, OperatingMode.Turbo));
    }

    [Fact]
    public async Task Modes_NoAnswer_KeepTheDefault()
    {
        var caps = await CapabilitiesAsync(new FakeAgent { Modes = null });
        Assert.Equal(new HardwareCapabilities().SupportedOperatingModes, caps.SupportedOperatingModes);
        Assert.True(ModePolicy.IsModeSupported(caps.SupportedOperatingModes, OperatingMode.Turbo));
    }

    [Fact]
    public async Task Modes_EmptyAnswer_KeepTheDefault()
    {
        var caps = await CapabilitiesAsync(new FakeAgent { Modes = Array.Empty<OperatingMode>() });
        Assert.Empty(caps.SupportedOperatingModes);
        Assert.True(ModePolicy.IsModeSupported(caps.SupportedOperatingModes, OperatingMode.Turbo));
    }

    // ---------------------------------------------------------------- fans

    [Fact]
    public async Task Fans_EmptyList_ShowsNoFanControl()
    {
        var caps = await CapabilitiesAsync(new FakeAgent { Fans = Array.Empty<AgentFan>(), FanMode = FanMode.Auto });
        Assert.False(caps.FanControl);
    }

    [Fact]
    public async Task Fans_TwoFans_ShowFanControl()
    {
        var caps = await CapabilitiesAsync(new FakeAgent
        {
            Fans = new[] { new AgentFan(0, "CPU", 0, 100), new AgentFan(1, "GPU", 0, 100) },
            FanMode = null
        });
        Assert.True(caps.FanControl);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Fans_NoAnswer_FallsBackToTheFanModeRead(bool modeReadable, bool expected)
    {
        var caps = await CapabilitiesAsync(new FakeAgent
        {
            Fans = null,
            FanMode = modeReadable ? Core.FanMode.Auto : null
        });
        Assert.Equal(expected, caps.FanControl);
    }

    [Fact]
    public void FanList_ReadsNumbersAndStringsAlike()
    {
        var fans = AcerAgentClient.ParseFanList(JsonNode.Parse(
            "[{\"fan_index\":\"0\",\"fan_name\":\"CPU\",\"fan_custom_min_speed\":\"20\",\"fan_custom_max_speed\":\"100\"}," +
            "{\"fan_index\":1,\"fan_name\":\"GPU\",\"fan_custom_min_speed\":0,\"fan_custom_max_speed\":90}]"));

        Assert.Equal(new[] { new AgentFan(0, "CPU", 20, 100), new AgentFan(1, "GPU", 0, 90) }, fans);
    }

    [Fact]
    public void FanList_AnythingThatIsNotAnArrayOfObjects_IsEmpty()
    {
        Assert.Empty(AcerAgentClient.ParseFanList(null));
        Assert.Empty(AcerAgentClient.ParseFanList(JsonNode.Parse("{}")));
        Assert.Empty(AcerAgentClient.ParseFanList(JsonNode.Parse("[1, \"x\"]")));
    }

    [Fact]
    public async Task FanList_OverTheWire_ParsesStringNumbers_AndNoAnswerIsNull()
    {
        using (var service = new AgentCapabilityDumpTests.EchoService(function => function == "SUPPORT_FAN_CONTROL"
            ? "{\"request\":\"SUPPORT_FAN_CONTROL\",\"result\":\"0\",\"data\":[{\"fan_index\":\"0\",\"fan_name\":\"CPU\",\"fan_custom_min_speed\":\"0\",\"fan_custom_max_speed\":\"100\"}]}"
            : null))
        using (var client = NewClient(service))
        {
            Assert.Equal(new[] { new AgentFan(0, "CPU", 0, 100) }, await client.GetFanListAsync());
        }

        // result "1" is "not supported"/failed: no answer, never an empty list.
        using (var service = new AgentCapabilityDumpTests.EchoService(function =>
            $"{{\"request\":\"{function}\",\"result\":\"1\"}}"))
        using (var client = NewClient(service))
        {
            Assert.Null(await client.GetFanListAsync());
        }
    }

    // ---------------------------------------------------------------- fan range on the write path

    [Theory]
    [InlineData(10, 95, 20, 90)]   // both outside: pulled into the range
    [InlineData(50, 60, 50, 60)]   // inside: untouched
    [InlineData(20, 90, 20, 90)]   // exactly on the edges: untouched
    [InlineData(0, 100, 20, 90)]
    public async Task FanWrite_Custom_IsClampedIntoTheServiceRange(int cpu, int gpu, int expectedCpu, int expectedGpu)
    {
        var agent = new FakeAgent { Fans = new[] { new AgentFan(0, "CPU", 20, 90), new AgentFan(1, "GPU", 20, 90) } };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());

        Assert.True(await controller.SetFanModeAsync(FanMode.Custom, cpu, gpu));

        Assert.Equal(expectedCpu, agent.LastFanWrite.Cpu);
        Assert.Equal(expectedGpu, agent.LastFanWrite.Gpu);
        Assert.Equal(FanMode.Custom, agent.LastFanWrite.Mode);
    }

    [Fact]
    public async Task FanWrite_Custom_NeverGoesAboveTheServiceMaximum()
    {
        var agent = new FakeAgent { Fans = new[] { new AgentFan(0, "CPU", 20, 80), new AgentFan(1, "GPU", 10, 70) } };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());

        await controller.SetFanModeAsync(FanMode.Custom, 100, 100);

        Assert.Equal((FanMode.Custom, 80, 70), agent.LastFanWrite);
    }

    [Fact]
    public async Task FanWrite_Custom_WithoutAServiceRange_IsPassedThroughUnchanged()
    {
        foreach (var fans in new IReadOnlyList<AgentFan>?[]
                 {
                     null,                                                       // no answer
                     Array.Empty<AgentFan>(),                                    // nothing to control
                     new[] { new AgentFan(0, "CPU", 0, 0), new AgentFan(1, "GPU", 0, 0) } // fields absent
                 })
        {
            var agent = new FakeAgent { Fans = fans };
            using var controller = new AgentHardwareController(agent, new FakeCareCenter());

            await controller.SetFanModeAsync(FanMode.Custom, 5, 100);

            Assert.Equal((FanMode.Custom, 5, 100), agent.LastFanWrite);
        }
    }

    [Fact]
    public async Task FanWrite_Custom_RealAn1641Shape_GpuListedFirstWithTheSameIndex_IsMatchedByName()
    {
        // The AN16-41 answers GPU first and gives both fans index 0: only the name tells them apart.
        var agent = new FakeAgent { Fans = new[] { new AgentFan(0, "GPU", 40, 70), new AgentFan(0, "CPU", 20, 90) } };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());

        await controller.SetFanModeAsync(FanMode.Custom, 10, 100);

        Assert.Equal((FanMode.Custom, 20, 70), agent.LastFanWrite);
    }

    [Fact]
    public async Task FanWrite_Custom_UnnamedFansWithDistinctIndices_AreMatchedByPosition()
    {
        var agent = new FakeAgent { Fans = new[] { new AgentFan(0, "", 20, 90), new AgentFan(1, "", 40, 70) } };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());

        await controller.SetFanModeAsync(FanMode.Custom, 10, 100);

        Assert.Equal((FanMode.Custom, 20, 70), agent.LastFanWrite);
    }

    [Fact]
    public async Task FanWrite_Custom_UnnamedFansWithTheSameIndex_AreNotGuessedAt()
    {
        // Two unnamed fans that share an index: which one is the CPU fan is unknowable, so nothing is clamped.
        var agent = new FakeAgent { Fans = new[] { new AgentFan(0, "", 40, 70), new AgentFan(0, "", 20, 90) } };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());

        await controller.SetFanModeAsync(FanMode.Custom, 10, 100);

        Assert.Equal((FanMode.Custom, 10, 100), agent.LastFanWrite);
    }

    [Fact]
    public async Task FanWrite_NotCustom_IsNotTouched()
    {
        var agent = new FakeAgent { Fans = new[] { new AgentFan(0, "CPU", 20, 80), new AgentFan(1, "GPU", 20, 80) } };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());

        await controller.SetFanModeAsync(FanMode.Max, 0, 100);

        Assert.Equal((FanMode.Max, 0, 100), agent.LastFanWrite);
    }

    [Fact]
    public async Task FanWrite_Custom_DefaultSpeeds_AreClampedToo()
    {
        var agent = new FakeAgent { Fans = new[] { new AgentFan(0, "CPU", 30, 80), new AgentFan(1, "GPU", 30, 80) } };
        agent.CustomFanCpuPercent = 10;
        agent.CustomFanGpuPercent = 95;
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());

        await controller.SetFanModeAsync(FanMode.Custom);

        Assert.Equal((FanMode.Custom, 30, 80), agent.LastFanWrite);
    }

    [Fact]
    public async Task FanWrite_MinusOne_UsesTheSavedSpeedsHandedToTheController()
    {
        var agent = new FakeAgent { Fans = new[] { new AgentFan(0, "CPU", 0, 100), new AgentFan(1, "GPU", 0, 100) } };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter(), customFanDefaults: () => (70, 65));

        await controller.SetFanModeAsync(FanMode.Custom);

        Assert.Equal((FanMode.Custom, 70, 65), agent.LastFanWrite);
        Assert.Equal((70, 65), agent.SavedCustomFanPercents());
    }

    // ---------------------------------------------------------------- per-session capability cache

    [Fact]
    public async Task Capabilities_SecondCall_DoesNotAskTheServiceAgain()
    {
        var agent = new FakeAgent
        {
            GpuCapability = 7,
            Modes = new[] { OperatingMode.Quiet, OperatingMode.Default },
            Fans = new[] { new AgentFan(0, "CPU", 0, 100) }
        };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());

        var first = await controller.GetCapabilitiesAsync();
        var second = await controller.GetCapabilitiesAsync();

        Assert.Equal(1, agent.FanListCalls);
        Assert.Equal(1, agent.ModeListCalls);
        Assert.Equal(1, agent.MuxCalls);
        Assert.Equal(first.MuxSwitch, second.MuxSwitch);
        Assert.Equal(first.SupportedOperatingModes, second.SupportedOperatingModes);
        Assert.True(second.FanControl);
    }

    [Fact]
    public async Task Capabilities_AnUnansweredRead_IsNotCached_AndIsRetried()
    {
        var agent = new FakeAgent { Fans = null, Modes = null, GpuCapability = null, FanMode = null };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());

        var first = await controller.GetCapabilitiesAsync();
        Assert.False(first.FanControl);
        Assert.False(first.MuxSwitch);

        agent.Fans = new[] { new AgentFan(0, "CPU", 0, 100) };
        agent.GpuCapability = 7;
        agent.Modes = new[] { OperatingMode.Quiet };
        var second = await controller.GetCapabilitiesAsync();

        Assert.True(second.FanControl);
        Assert.True(second.MuxSwitch);
        Assert.Single(second.SupportedOperatingModes);
        Assert.Equal(2, agent.FanListCalls);
        Assert.Equal(2, agent.ModeListCalls);
        Assert.Equal(2, agent.MuxCalls);
    }

    [Fact]
    public async Task Capabilities_Invalidate_AsksAgain()
    {
        var agent = new FakeAgent { GpuCapability = 7, Modes = new[] { OperatingMode.Quiet }, Fans = new[] { new AgentFan(0, "CPU", 0, 100) } };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());

        await controller.GetCapabilitiesAsync();
        controller.InvalidateCapabilityCache();
        agent.GpuCapability = 0;
        var again = await controller.GetCapabilitiesAsync();

        Assert.Equal(2, agent.FanListCalls);
        Assert.False(again.MuxSwitch);
    }

    [Fact]
    public async Task Rescan_ClearsTheSettingsCache_AndTheServiceIsAskedAgain()
    {
        var agent = new FakeAgent { GpuCapability = 7, Modes = new[] { OperatingMode.Quiet }, Fans = new[] { new AgentFan(0, "CPU", 0, 100) } };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());
        var settings = new AppSettings { HardwareCapabilityCache = new HardwareCapabilityCache { Key = "A|1" } };

        await controller.GetCapabilitiesAsync();
        await controller.GetCapabilitiesAsync();
        Assert.Equal(1, agent.FanListCalls);

        CapabilityRescan.Reset(settings, controller);
        agent.GpuCapability = 0;
        var again = await controller.GetCapabilitiesAsync();

        Assert.Null(settings.HardwareCapabilityCache);
        Assert.Equal(2, agent.FanListCalls);
        Assert.Equal(2, agent.ModeListCalls);
        Assert.Equal(2, agent.MuxCalls);
        Assert.False(again.MuxSwitch);
    }

    [Fact]
    public async Task Capabilities_ServiceGoingAway_DropsTheCache()
    {
        var agent = new FakeAgent { GpuCapability = 7 };
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());

        await controller.GetCapabilitiesAsync();
        agent.Connected = false;
        Assert.False((await controller.GetCapabilitiesAsync()).ServiceAvailable);
        agent.Connected = true;
        agent.GpuCapability = 0;

        Assert.False((await controller.GetCapabilitiesAsync()).MuxSwitch);
    }

    // ---------------------------------------------------------------- helpers

    private static AcerAgentClient NewClient(AgentCapabilityDumpTests.EchoService service) =>
        new("127.0.0.1", service.Port,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

    private static async Task<HardwareCapabilities> CapabilitiesAsync(FakeAgent agent)
    {
        using var controller = new AgentHardwareController(agent, new FakeCareCenter());
        return await controller.GetCapabilitiesAsync();
    }

    /// <summary>Answers from properties, never from the network: every read the controller probes is overridden.</summary>
    private sealed class FakeAgent : AcerAgentClient
    {
        public FakeAgent()
            : base("127.0.0.1", DeadEndService.Port, DeadEndService.Budget, DeadEndService.Budget, DeadEndService.Budget,
                registry: () => AcerRegistrySnapshot.Empty)
        {
        }

        public int? GpuCapability { get; set; }
        public IReadOnlyList<OperatingMode>? Modes { get; set; }
        public IReadOnlyList<AgentFan>? Fans { get; set; }
        public FanMode? FanMode { get; set; } = Core.FanMode.Auto;

        public bool Connected { get; set; } = true;
        public int FanListCalls { get; private set; }
        public int ModeListCalls { get; private set; }
        public int MuxCalls { get; private set; }
        public (FanMode Mode, int Cpu, int Gpu) LastFanWrite { get; private set; }

        public override bool IsConnected => Connected;
        public override Task<bool> EnsureConnectedAsync() => Task.FromResult(Connected);
        public override Task<FanMode?> GetFanModeAsync() => Task.FromResult(FanMode);
        public override Task<OperatingMode?> TryGetOperatingModeAsync() => Task.FromResult<OperatingMode?>(OperatingMode.Default);

        public override Task<int?> GetGpuModeCapabilityAsync()
        {
            MuxCalls++;
            return Task.FromResult(GpuCapability);
        }

        public override Task<IReadOnlyList<OperatingMode>?> GetOperatingModesAsync()
        {
            ModeListCalls++;
            return Task.FromResult(Modes);
        }

        public override Task<IReadOnlyList<AgentFan>?> GetFanListAsync()
        {
            FanListCalls++;
            return Task.FromResult(Fans);
        }

        /// <summary>Records what would have gone to the service, with the stored defaults filled in like the real client does.</summary>
        public override Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
        {
            LastFanWrite = (mode, cpuPercent < 0 ? CustomFanCpuPercent : cpuPercent, gpuPercent < 0 ? CustomFanGpuPercent : gpuPercent);
            return Task.FromResult(true);
        }
        public override Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(null);
        public override Task<bool?> GetWinKeyEnabledAsync() => Task.FromResult<bool?>(null);
        public override Task<KeyboardCapability?> GetKeyboardCapabilityAsync() => Task.FromResult<KeyboardCapability?>(null);
    }

    private sealed class FakeCareCenter : CareCenterClient
    {
        public override Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => Task.FromResult<BatteryChargeMode?>(null);
    }
}
