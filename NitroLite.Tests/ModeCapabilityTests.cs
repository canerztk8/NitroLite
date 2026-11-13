using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Capability gating decides which mode buttons a model shows. The rule that matters most is the fallback: a
/// device that never answered must keep every mode, because hiding a working control is worse than showing one
/// that turns out to be a no-op.
/// </summary>
public class ModeCapabilityTests
{
    [Fact]
    public void ParseOperatingModes_ReadsTheAn16List()
    {
        var modes = AcerAgentClient.ParseOperatingModes(JsonNode.Parse("[0, 1, 4, 5, 6]"));
        Assert.Equal(
            new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo, OperatingMode.Eco },
            modes);
    }

    [Fact]
    public void ParseOperatingModes_DropsTurboWhenTheModelDoesNotOfferIt()
    {
        var modes = AcerAgentClient.ParseOperatingModes(JsonNode.Parse("[0, 1, 4, 6]"));
        Assert.DoesNotContain(OperatingMode.Turbo, modes);
        Assert.Contains(OperatingMode.Extreme, modes);
    }

    [Fact]
    public void ParseOperatingModes_DropsNumbersThatAreNotAMode()
    {
        // A future firmware mode must not be cast into a random existing one.
        var modes = AcerAgentClient.ParseOperatingModes(JsonNode.Parse("[0, 7, 8, 99]"));
        Assert.Equal(new[] { OperatingMode.Quiet }, modes);
    }

    [Fact]
    public void ParseOperatingModes_RemovesDuplicates()
    {
        var modes = AcerAgentClient.ParseOperatingModes(JsonNode.Parse("[1, 1, 1]"));
        Assert.Equal(new[] { OperatingMode.Default }, modes);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"0,1\"")]
    [InlineData("{}")]
    [InlineData("[\"0\", \"1\"]")]
    public void ParseOperatingModes_ReturnsEmptyForAnythingUnexpected(string json)
    {
        var modes = AcerAgentClient.ParseOperatingModes(JsonNode.Parse(json));
        Assert.Empty(modes);
    }

    [Fact]
    public void ParseOperatingModes_HandlesNull() =>
        Assert.Empty(AcerAgentClient.ParseOperatingModes(null));

    [Fact]
    public void IsModeSupported_AllowsEverythingWhenTheDeviceNeverAnswered()
    {
        Assert.True(ModePolicy.IsModeSupported(null, OperatingMode.Turbo));
        Assert.True(ModePolicy.IsModeSupported(Array.Empty<OperatingMode>(), OperatingMode.Turbo));
    }

    [Fact]
    public void IsModeSupported_FollowsTheReportedList()
    {
        IReadOnlyList<OperatingMode> supported = new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Eco };
        Assert.True(ModePolicy.IsModeSupported(supported, OperatingMode.Quiet));
        Assert.True(ModePolicy.IsModeSupported(supported, OperatingMode.Eco));
        Assert.False(ModePolicy.IsModeSupported(supported, OperatingMode.Turbo));
        Assert.False(ModePolicy.IsModeSupported(supported, OperatingMode.Extreme));
    }

    [Fact]
    public async Task GetOperatingModes_ReturnsKnownModesOrNull()
    {
        // Read-only, so it is safe to run anywhere; a machine without AASSvc simply answers null.
        using var client = new AcerAgentClient();
        var modes = await client.GetOperatingModesAsync();
        if (modes == null) return;

        Assert.NotEmpty(modes);
        Assert.All(modes, m => Assert.True(Enum.IsDefined(typeof(OperatingMode), m)));
        Assert.Equal(modes.Distinct().Count(), modes.Count);
    }
}
