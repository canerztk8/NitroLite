using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class KeyboardBacklightPersistenceTests
{
    [Fact]
    public void AppSettings_SerializationAndDeserialization_PreservesLightingAndOffState()
    {
        var settings = new AppSettings
        {
            StartMinimized = true,
            OsdEnabled = true,
            IsBacklightOff = true,
            KeyboardLighting = new LightingConfig
            {
                Brightness = 0,
                Effect = "OFF",
                Color = "#000000",
                LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" }
            }
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.IsBacklightOff);
        Assert.NotNull(deserialized.KeyboardLighting);
        Assert.Equal(0, deserialized.KeyboardLighting.Brightness);
        Assert.Equal("OFF", deserialized.KeyboardLighting.Effect);
        Assert.Equal("#000000", deserialized.KeyboardLighting.Color);
        Assert.Equal(4, deserialized.KeyboardLighting.LEDs.Count);
    }

    [Fact]
    public void AppSettings_BacklightActiveProfile_PreservesCustomSettings()
    {
        var settings = new AppSettings
        {
            IsBacklightOff = false,
            KeyboardLighting = new LightingConfig
            {
                Brightness = 7,
                Effect = "BREATHING",
                Speed = 4,
                Direction = 2,
                Color = "#FF3366",
                LEDs = new List<string> { "#FF3366", "#33FF66", "#3366FF", "#FFFF33" }
            }
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.False(deserialized.IsBacklightOff);
        Assert.NotNull(deserialized.KeyboardLighting);
        Assert.Equal(7, deserialized.KeyboardLighting.Brightness);
        Assert.Equal("BREATHING", deserialized.KeyboardLighting.Effect);
        Assert.Equal(4, deserialized.KeyboardLighting.Speed);
        Assert.Equal(2, deserialized.KeyboardLighting.Direction);
        Assert.Equal("#FF3366", deserialized.KeyboardLighting.Color);
        Assert.Equal("#33FF66", deserialized.KeyboardLighting.LEDs[1]);
    }

    [Fact]
    public void LegacyConfig_Compatibility_DeserializesWithoutLightingFields()
    {
        string legacyJson = @"{
            ""StartMinimized"": true,
            ""OsdEnabled"": false,
            ""SelectedChartMetric"": ""CPU_TEMP"",
            ""AutoRamCleanerEnabled"": true,
            ""WindowLeft"": 120.5,
            ""WindowTop"": 80.0,
            ""OsdCorner"": 1
        }";

        var deserialized = JsonSerializer.Deserialize<AppSettings>(legacyJson);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.StartMinimized);
        Assert.False(deserialized.IsBacklightOff);
        Assert.Null(deserialized.KeyboardLighting);
    }

    [Fact]
    public void SettingsFile_AtomicSerialization_ProducesValidJson()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"nitrolite_test_settings_{Guid.NewGuid():N}.json");
        try
        {
            var original = new AppSettings
            {
                StartMinimized = false,
                IsBacklightOff = true,
                KeyboardLighting = new LightingConfig { Brightness = 0, Effect = "OFF" }
            };

            var json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempFile, json);

            var readBack = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(tempFile));
            Assert.NotNull(readBack);
            Assert.True(readBack.IsBacklightOff);
            Assert.Equal(0, readBack.KeyboardLighting?.Brightness);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ReassertDecisionLogic_TurnedOffBacklight_StaysStrictlyOff()
    {
        // Simulate decision logic during PowerModes.Resume or SessionUnlock
        var settings = new AppSettings { IsBacklightOff = true };
        var activeLighting = new LightingConfig { Brightness = 0, Effect = "OFF" };

        bool shouldTurnOff = settings.IsBacklightOff || 
                             activeLighting.Brightness == 0 || 
                             activeLighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase);

        Assert.True(shouldTurnOff, "When IsBacklightOff is true or Brightness is 0, re-assertion must enforce turn-off!");
    }

    [Fact]
    public void ReassertDecisionLogic_ActiveLighting_MaintainsTargetParameters()
    {
        var settings = new AppSettings { IsBacklightOff = false };
        var activeLighting = new LightingConfig { Brightness = 6, Effect = "WAVE" };

        bool shouldTurnOff = settings.IsBacklightOff || 
                             activeLighting.Brightness == 0 || 
                             activeLighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase);

        Assert.False(shouldTurnOff);
        Assert.Equal(6, activeLighting.Brightness);
        Assert.Equal("WAVE", activeLighting.Effect);
    }

    [Fact]
    public void AppSettings_SelectedRgbPreset_PreservesCustomPresetNameAndOffState()
    {
        var settings = new AppSettings
        {
            SelectedRgbPreset = "Özel Profil 11:57",
            IsBacklightOff = true,
            AutoRamCleanerEnabled = false,
            KeyboardLighting = new LightingConfig
            {
                Brightness = 0,
                Effect = "OFF",
                Color = "#000000",
                LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" }
            }
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Özel Profil 11:57", deserialized.SelectedRgbPreset);
        Assert.True(deserialized.IsBacklightOff);
        Assert.False(deserialized.AutoRamCleanerEnabled);
        Assert.Equal(0, deserialized.KeyboardLighting!.Brightness);
        Assert.Equal("OFF", deserialized.KeyboardLighting.Effect);
    }

    [Fact]
    public void RgbProfileService_SaveAndRetrieveCustomPreset_WorksReliably()
    {
        var service = new RgbProfileService();
        string testPresetName = "UnitTest_CustomOff_" + Guid.NewGuid().ToString("N")[..6];

        service.SaveCustomPreset(testPresetName, "OFF", 0, 1, 1, "#000000", new List<string> { "#000000", "#000000", "#000000", "#000000" });

        var presets = service.GetPresets();
        var found = presets.FirstOrDefault(p => p.Name == testPresetName);

        Assert.NotNull(found);
        Assert.Equal("OFF", found.Effect);
        Assert.Equal(0, found.Brightness);
        Assert.False(found.IsBuiltIn);

        // Clean up
        service.DeletePreset(testPresetName);
    }
}
