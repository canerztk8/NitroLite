using System.Linq;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class LightingPolicyTests
{
    [Theory]
    [InlineData("WAVE", false)]
    [InlineData("NEON", false)]
    [InlineData("STATIC", true)]
    [InlineData("BREATHING", true)]
    [InlineData("SHIFTING", true)]
    [InlineData("TWINKLING", true)]
    public void ColourSupport_MatchesWhatTheKeyboardReports(string effect, bool usesColor)
    {
        Assert.Equal(usesColor, LightingPolicy.EffectUsesColor(effect));
    }

    [Fact]
    public void OnlyStatic_TakesZoneColours()
    {
        Assert.True(LightingPolicy.EffectUsesZoneColors("STATIC"));
        Assert.False(LightingPolicy.EffectUsesZoneColors("BREATHING"));
        Assert.False(LightingPolicy.EffectUsesZoneColors("WAVE"));
    }

    [Fact]
    public void Directions_FollowNitroSenseEnum()
    {
        Assert.Equal(new[] { 1, 2, 3, 4 }, LightingPolicy.AllowedDirections("WAVE"));
        Assert.Equal(new[] { 1, 2 }, LightingPolicy.AllowedDirections("SHIFTING"));
        Assert.Empty(LightingPolicy.AllowedDirections("STATIC"));
        Assert.Equal("Sola", LightingPolicy.DirectionName(2));
    }

    [Theory]
    [InlineData(0, 1)]   // 0 is not a direction (was sent for "left" before)
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(9, 1)]
    public void ClientNormalizesDirection(int input, int expected)
    {
        Assert.Equal(expected, AcerAgentClient.NormalizeDirection(input));
    }

    [Fact]
    public void ResumeFromOff_RestoresLastLitSettings_WithRequestedBrightness()
    {
        var last = new LightingConfig { Effect = "BREATHING", Brightness = 7, Speed = 2, Color = "#FF2A55",
            LEDs = new() { "#FF2A55", "#FF2A55", "#FF2A55", "#FF2A55" } };
        var resumed = LightingPolicy.ResumeFromOff(last, brightness: 3);
        Assert.Equal("BREATHING", resumed.Effect);
        Assert.Equal(3, resumed.Brightness);
        Assert.Equal("#FF2A55", resumed.Color);
        Assert.False(LightingPolicy.IsOff(resumed));
    }

    [Fact]
    public void ResumeFromOff_WithoutHistory_IsVisibleStaticCyan_AndNeverBlack()
    {
        var resumed = LightingPolicy.ResumeFromOff(null, effect: "ZOOM");
        Assert.Equal("ZOOM", resumed.Effect);
        Assert.True(resumed.Brightness > 0);
        Assert.Equal(LightingPolicy.DefaultColor, resumed.Color);
        Assert.All(resumed.LEDs, l => Assert.NotEqual("#000000", l));

        var blackHistory = new LightingConfig { Effect = "STATIC", Brightness = 5, Color = "#000000",
            LEDs = new() { "#000000", "#000000", "#000000", "#000000" } };
        Assert.Equal(LightingPolicy.DefaultColor, LightingPolicy.ResumeFromOff(blackHistory).Color);
    }

    [Fact]
    public void ResumeFromOff_IgnoresOffAsEffect()
    {
        var resumed = LightingPolicy.ResumeFromOff(null, effect: "OFF");
        Assert.Equal("STATIC", resumed.Effect);
    }
}

public class RgbPresetCapabilityTests
{
    [Fact]
    public void BuiltInPresets_NeverPromiseColoursTheEffectCannotShow()
    {
        var presets = new RgbProfileService().GetPresets().Where(p => p.IsBuiltIn).ToList();
        Assert.NotEmpty(presets);
        foreach (var p in presets)
        {
            bool multiColour = p.LEDs.Distinct(System.StringComparer.OrdinalIgnoreCase).Count() > 1;
            if (multiColour)
            {
                // Four different zone colours only exist in STATIC.
                Assert.True(LightingPolicy.EffectUsesZoneColors(p.Effect), $"'{p.Name}' uses zone colours with {p.Effect}");
            }
        }
    }

    [Fact]
    public void RainbowPresets_AreNamedAsSuch()
    {
        var presets = new RgbProfileService().GetPresets().Where(p => p.IsBuiltIn).ToList();
        var wave = presets.Single(p => p.Effect == "WAVE");
        Assert.Contains("Gökkuşağı", wave.Name);
        Assert.Contains(presets, p => p.Effect == "NEON");
        Assert.Equal("STATIC", presets.Single(p => p.Name == "Okyanus Mavisi").Effect);
    }
}
