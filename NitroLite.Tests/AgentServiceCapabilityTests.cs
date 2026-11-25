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
            : base("127.0.0.1", DeadEndService.Port, DeadEndService.Budget, DeadEndService.Budget, DeadEndService.Budget)
        {
        }

        public int? GpuCapability { get; set; }
        public IReadOnlyList<OperatingMode>? Modes { get; set; }
        public IReadOnlyList<AgentFan>? Fans { get; set; }
        public FanMode? FanMode { get; set; } = Core.FanMode.Auto;

        public override bool IsConnected => true;
        public override Task<bool> EnsureConnectedAsync() => Task.FromResult(true);
        public override Task<FanMode?> GetFanModeAsync() => Task.FromResult(FanMode);
        public override Task<OperatingMode?> TryGetOperatingModeAsync() => Task.FromResult<OperatingMode?>(OperatingMode.Default);
        public override Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult(GpuCapability);
        public override Task<IReadOnlyList<OperatingMode>?> GetOperatingModesAsync() => Task.FromResult(Modes);
        public override Task<IReadOnlyList<AgentFan>?> GetFanListAsync() => Task.FromResult(Fans);
        public override Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(null);
        public override Task<bool?> GetWinKeyEnabledAsync() => Task.FromResult<bool?>(null);
        public override Task<KeyboardCapability?> GetKeyboardCapabilityAsync() => Task.FromResult<KeyboardCapability?>(null);
    }

    private sealed class FakeCareCenter : CareCenterClient
    {
        public override Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => Task.FromResult<BatteryChargeMode?>(null);
    }
}
