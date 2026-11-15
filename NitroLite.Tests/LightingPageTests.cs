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

    // The AN16-41 reports maximum_number_of_colors = 1 for STATIC and 4 for DIRECT. NitroLite's "static" means a
    // fixed colour per zone, so four different zone colours have to leave as DIRECT; sending them under STATIC is
    // what made only one of the four colours light up on the keyboard.
    [Fact]
    public void FourDistinctZoneColours_GoOutAsDirect()
    {
        var zones = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFFFF" };
        Assert.Equal("DIRECT", LightingPolicy.DeviceEffectFor("STATIC", zones));
    }

    [Fact]
    public void OneColourForEveryZone_StaysStatic()
    {
        var zones = new[] { "#00E5FF", "#00E5FF", "#00E5FF", "#00E5FF" };
        Assert.Equal("STATIC", LightingPolicy.DeviceEffectFor("STATIC", zones));
    }

    [Fact]
    public void TwoZonesThatDiffer_AreEnoughToNeedDirect()
    {
        var zones = new[] { "#FF0000", "#FF0000", "#FF0000", "#0000FF" };
        Assert.Equal("DIRECT", LightingPolicy.DeviceEffectFor("STATIC", zones));
    }

    [Fact]
    public void ZoneColourComparison_IgnoresCaseAndSurroundingSpace()
    {
        var zones = new[] { "#00e5ff", " #00E5FF ", "#00E5FF", "#00E5FF" };
        Assert.Equal("STATIC", LightingPolicy.DeviceEffectFor("STATIC", zones));
    }

    [Theory]
    [InlineData("WAVE")]
    [InlineData("BREATHING")]
    [InlineData("OFF")]
    public void AnAnimatedEffect_IsNeverRewrittenToDirect(string effect)
    {
        // Only STATIC is translated: the other modes take a single colour by design, and rewriting them would
        // silently change an animation the user picked.
        var zones = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFFFF" };
        Assert.Equal(effect, LightingPolicy.DeviceEffectFor(effect, zones));
    }

    [Fact]
    public void NoZoneColours_LeavesTheEffectAlone()
    {
        Assert.Equal("STATIC", LightingPolicy.DeviceEffectFor("STATIC", null));
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

/// <summary>
/// The preset combo shows <see cref="RgbPreset.DisplayName"/> while settings keep <see cref="RgbPreset.Name"/>.
/// If the two ever collapse into one, switching the UI language orphans the user's saved selection.
/// </summary>
public class RgbPresetDisplayNameTests
{
    [Fact]
    public void BuiltInPreset_TranslatesItsDisplayNameButKeepsItsIdentity()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage("en");
            var wave = new RgbProfileService().GetPresets().Single(p => p.Effect == "WAVE");
            Assert.Equal("Gökkuşağı Dalga", wave.Name);
            Assert.Equal("Rainbow Wave", wave.DisplayName);
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void CustomPreset_KeepsTheNameItsUserTyped()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage("en");
            var custom = new RgbPreset { Name = "Benim Profilim", IsBuiltIn = false };
            Assert.Equal("Benim Profilim", custom.DisplayName);
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void EveryBuiltInPreset_HasATranslatedDisplayName()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage("en");
            var untranslated = new RgbProfileService().GetPresets()
                .Where(p => p.IsBuiltIn)
                .Where(p => p.DisplayName == p.Name)
                .Select(p => p.Name)
                .ToList();
            Assert.True(untranslated.Count == 0,
                $"built-in presets with no English name: {string.Join(" / ", untranslated)}");
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }
}
