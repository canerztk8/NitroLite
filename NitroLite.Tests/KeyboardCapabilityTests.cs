using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The keyboard capability decides whether the four-zone picker is shown. The payloads below are the shapes the
/// firmware actually returns, including the misspelled "minimun_number_of_colors" that must not be relied on.
/// </summary>
public class KeyboardCapabilityTests
{
    /// <summary>The AN16-41 answer, trimmed to the fields the parser reads.</summary>
    private const string FourZone = """
    { "data": [ { "active_mode_index": 4, "brightness": 5, "device_index": 0,
      "device_name": "AcerECKeyboard Device", "device_type": 5,
      "vector_of_modes": [
        { "maximum_number_of_colors": 4, "minimum_speed": 0, "maximum_speed": 0, "mode_name": "DIRECT" },
        { "maximum_number_of_colors": 1, "minimum_speed": 1, "maximum_speed": 9, "mode_name": "STATIC" },
        { "maximum_number_of_colors": 0, "minimum_speed": 1, "maximum_speed": 9, "mode_name": "WAVE" }
      ] } ] }
    """;

    private const string SingleZone = """
    { "data": [ { "device_name": "AcerECKeyboard Device", "device_type": 5,
      "vector_of_modes": [
        { "maximum_number_of_colors": 1, "minimum_speed": 1, "maximum_speed": 9, "mode_name": "STATIC" },
        { "maximum_number_of_colors": 1, "minimum_speed": 1, "maximum_speed": 9, "mode_name": "BREATHING" }
      ] } ] }
    """;

    private const string NoLighting = """
    { "data": [ { "device_name": "AcerECKeyboard Device", "device_type": 5, "vector_of_modes": [] } ] }
    """;

    /// <summary>The parser takes the response's "data" node, which is what the client hands it.</summary>
    private static JsonNode? Data(string json) => JsonNode.Parse(json)?["data"];

    [Fact]
    public void Parse_ReadsTheFourZoneKeyboard()
    {
        var cap = KeyboardCapability.Parse(Data(FourZone));
        Assert.NotNull(cap);
        Assert.Equal("AcerECKeyboard Device", cap!.DeviceName);
        Assert.Equal(5, cap.DeviceType);
        Assert.Equal(4, cap.ZoneCount);
        Assert.True(cap.HasLighting);
        Assert.True(cap.HasZoneColors);
        Assert.Equal(new[] { "DIRECT", "STATIC", "WAVE" }, cap.Effects.Select(e => e.Name));
        Assert.Equal(9, cap.Effects[1].MaxSpeed);
    }

    [Fact]
    public void Parse_ReadsASingleZoneKeyboard()
    {
        var cap = KeyboardCapability.Parse(Data(SingleZone));
        Assert.NotNull(cap);
        Assert.Equal(1, cap!.ZoneCount);
        Assert.True(cap.HasLighting);
        Assert.False(cap.HasZoneColors);
    }

    [Fact]
    public void Parse_ReadsAKeyboardWithNoLighting()
    {
        var cap = KeyboardCapability.Parse(Data(NoLighting));
        Assert.NotNull(cap);
        Assert.Equal(0, cap!.ZoneCount);
        Assert.False(cap.HasLighting);
        Assert.False(cap.HasZoneColors);
        Assert.Equal("no lighting", cap.Summary);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"keyboard\"")]
    [InlineData("[]")]
    public void Parse_ReturnsNullWhenThePayloadIsNotADeviceList(string json) =>
        Assert.Null(KeyboardCapability.Parse(JsonNode.Parse(json)));

    [Fact]
    public void Parse_HandlesNull() => Assert.Null(KeyboardCapability.Parse(null));

    [Fact]
    public void Parse_PicksTheDeviceThatHasModes()
    {
        // A machine can report a lid logo first; the keyboard is the entry with lighting modes.
        var json = """
        { "data": [
          { "device_name": "Logo", "device_type": 1, "vector_of_modes": [] },
          { "device_name": "AcerECKeyboard Device", "device_type": 5,
            "vector_of_modes": [ { "maximum_number_of_colors": 4, "mode_name": "DIRECT" } ] }
        ] }
        """;
        var cap = KeyboardCapability.Parse(Data(json));
        Assert.NotNull(cap);
        Assert.Equal("AcerECKeyboard Device", cap!.DeviceName);
        Assert.Equal(4, cap.ZoneCount);
    }

    [Fact]
    public void DeriveZoneCount_UsesDirectMode()
    {
        var effects = new[]
        {
            new KeyboardEffect { Name = "STATIC", MaxColors = 1 },
            new KeyboardEffect { Name = "DIRECT", MaxColors = 4 }
        };
        Assert.Equal(4, KeyboardCapability.DeriveZoneCount(effects));
    }

    [Fact]
    public void DeriveZoneCount_IsOneWhenThereIsNoDirectMode()
    {
        var effects = new[] { new KeyboardEffect { Name = "STATIC", MaxColors = 1 } };
        Assert.Equal(1, KeyboardCapability.DeriveZoneCount(effects));
    }

    [Fact]
    public void DeriveZoneCount_IsZeroWhenThereAreNoModes() =>
        Assert.Equal(0, KeyboardCapability.DeriveZoneCount(Array.Empty<KeyboardEffect>()));

    [Fact]
    public void DeriveZoneCount_NeverReturnsZeroForADirectModeThatReportsNoColours()
    {
        var effects = new[] { new KeyboardEffect { Name = "DIRECT", MaxColors = 0 } };
        Assert.Equal(1, KeyboardCapability.DeriveZoneCount(effects));
    }

    [Fact]
    public void Summary_ReadsNaturally()
    {
        var four = new KeyboardCapability { ZoneCount = 4, Effects = new[] { new KeyboardEffect { Name = "DIRECT" } } };
        Assert.Equal("4 zones, 1 effects", four.Summary);
        Assert.Equal("1 zone, 0 effects", new KeyboardCapability { ZoneCount = 1 }.Summary);
    }

    [Fact]
    public async Task GetKeyboardCapability_ReturnsACapabilityOrNull()
    {
        // Read-only; a machine without AASSvc simply answers null.
        using var client = new AcerAgentClient();
        var cap = await client.GetKeyboardCapabilityAsync();
        if (cap == null) return;

        Assert.True(cap.ZoneCount >= 0);
        Assert.All(cap.Effects, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
    }
}
