using System;
using System.Collections.Generic;
using System.Linq;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The fan curve templates are pure data, so everything about them can be checked without a laptop: the shapes
/// are inside the range the Acer service accepts, they never ask for a slower fan as the temperature rises, and
/// every name and description exists in every catalogue.
/// </summary>
public class FanCurvePresetsTests
{
    private static readonly string[] TranslatedLanguages = { "en", "de", "es", "ru", "pt", "hi", "id" };

    [Fact]
    public void ThereIsAtLeastOnePresetAndTheIdsAreUnique()
    {
        Assert.NotEmpty(FanCurvePresets.All);
        Assert.Equal(FanCurvePresets.All.Count, FanCurvePresets.All.Select(p => p.Id).Distinct().Count());
        Assert.All(FanCurvePresets.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Id)));
    }

    [Fact]
    public void EveryPresetHasOneAnchorPerEditorTemperature()
    {
        foreach (var preset in FanCurvePresets.All)
        {
            Assert.Equal(FanCurve.AnchorTemperatures.Count, preset.Percents.Count);
        }
    }

    [Fact]
    public void EveryPresetStaysInsideTheRangeTheServiceAccepts()
    {
        foreach (var preset in FanCurvePresets.All)
        {
            foreach (int percent in preset.Percents)
            {
                Assert.InRange(percent, FanCurve.MinPercent, FanCurve.MaxPercent);
            }
        }
    }

    [Fact]
    public void NoPresetAsksForASlowerFanAsTheTemperatureRises()
    {
        // A curve that dips would make the fans slow down while the machine heats up, which is the one thing a
        // fan curve must never do.
        foreach (var preset in FanCurvePresets.All)
        {
            for (int i = 1; i < preset.Percents.Count; i++)
            {
                Assert.True(preset.Percents[i] >= preset.Percents[i - 1],
                    $"{preset.Id} drops from {preset.Percents[i - 1]} % to {preset.Percents[i]} % at anchor {i}");
            }
        }
    }

    [Fact]
    public void TheHottestAnchorIsAboveTheSafetyFloor()
    {
        // The editor fills its sliders through PercentAt, which raises anything at or above 85 °C to the safety
        // floor. A preset below that would be silently rewritten the moment it was applied, and would then no
        // longer match itself.
        int last = FanCurve.AnchorTemperatures.Count - 1;
        Assert.True(FanCurve.AnchorTemperatures[last] >= FanCurve.SafetyTemperatureC);

        foreach (var preset in FanCurvePresets.All)
        {
            Assert.True(preset.Percents[last] >= FanCurve.SafetyMinPercent,
                $"{preset.Id} asks for {preset.Percents[last]} % at {FanCurve.AnchorTemperatures[last]} °C");
        }
    }

    [Fact]
    public void ToPointsPutsEachValueOnTheEditorsOwnTemperature()
    {
        foreach (var preset in FanCurvePresets.All)
        {
            var points = preset.ToPoints();
            Assert.Equal(FanCurve.AnchorTemperatures.Count, points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                Assert.Equal(FanCurve.AnchorTemperatures[i], points[i].TemperatureC);
                Assert.Equal(preset.Percents[i], points[i].Percent);
            }
        }
    }

    [Fact]
    public void EveryPresetMatchesItsOwnShape()
    {
        foreach (var preset in FanCurvePresets.All)
        {
            var match = FanCurvePresets.Match(preset.ToPoints());
            Assert.NotNull(match);
            Assert.Equal(preset.Id, match!.Id);
        }
    }

    [Fact]
    public void ACurveThatIsNotAPresetMatchesNothing()
    {
        var custom = new List<FanCurvePoint>
        {
            new(40, 31), new(50, 32), new(60, 36), new(70, 42), new(80, 62), new(90, 100)
        };
        Assert.Null(FanCurvePresets.Match(custom));
    }

    [Fact]
    public void ACurveWithAnAnchorSomewhereElseMatchesNothing()
    {
        // Same values, but one anchor moved: the shape is not the preset any more.
        var moved = new List<FanCurvePoint>
        {
            new(40, 30), new(50, 32), new(60, 36), new(70, 42), new(80, 62), new(95, 100)
        };
        Assert.Null(FanCurvePresets.Match(moved));
    }

    [Fact]
    public void AMissingAnchorMatchesNothing()
    {
        var partial = new List<FanCurvePoint>
        {
            new(40, 30), new(50, 32), new(60, 36), new(70, 42), new(80, 62)
        };
        Assert.Null(FanCurvePresets.Match(partial));
    }

    [Fact]
    public void MatchToleratesNullAndEmptyInput()
    {
        Assert.Null(FanCurvePresets.Match(null));
        Assert.Null(FanCurvePresets.Match(Array.Empty<FanCurvePoint>()));
    }

    [Fact]
    public void ByIdFindsThePresetAndRejectsAnythingElse()
    {
        foreach (var preset in FanCurvePresets.All)
        {
            Assert.Equal(preset.Id, FanCurvePresets.ById(preset.Id)?.Id);
        }

        Assert.Null(FanCurvePresets.ById("no-such-preset"));
        Assert.Null(FanCurvePresets.ById(null));
        Assert.Null(FanCurvePresets.ById(""));
        Assert.Null(FanCurvePresets.ById("   "));
    }

    [Fact]
    public void TheDefaultCurveIsTheBalancedPreset()
    {
        // A fresh install opens the editor with that template already marked, instead of showing a shape that
        // matches nothing.
        var match = FanCurvePresets.Match(FanCurve.DefaultPoints());
        Assert.NotNull(match);
        Assert.Equal("balanced", match!.Id);
    }

    [Fact]
    public void EveryPresetNameAndDescriptionIsTranslated()
    {
        foreach (var code in TranslatedLanguages)
        {
            var catalogue = Loc.LoadCatalog(code);
            Assert.NotEmpty(catalogue);

            foreach (var preset in FanCurvePresets.All)
            {
                Assert.True(catalogue.ContainsKey(preset.NameKey),
                    $"{code} has no name for preset '{preset.Id}' (key: {preset.NameKey})");
                Assert.True(catalogue.ContainsKey(preset.DescriptionKey),
                    $"{code} has no description for preset '{preset.Id}'");

                Assert.False(string.IsNullOrWhiteSpace(catalogue[preset.NameKey]));
                Assert.False(string.IsNullOrWhiteSpace(catalogue[preset.DescriptionKey]));
            }
        }
    }

    [Fact]
    public void TheTemplateLabelIsTranslated()
    {
        foreach (var code in TranslatedLanguages)
        {
            var catalogue = Loc.LoadCatalog(code);
            Assert.True(catalogue.ContainsKey("ŞABLON"), $"{code} has no translation for the template label");
        }
    }
}
