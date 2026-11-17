using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
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

    // Every red NitroLite used to ship is now #FF0000. A keyboard left on one of the old values has to come back on
    // the new one, otherwise the fix is only visible to someone who re-picks the colour by hand.
    [Theory]
    [InlineData("#D93829")]
    [InlineData("#E01B1B")]
    [InlineData("#FF2A55")]
    [InlineData("#FF1A1A")]
    [InlineData("#FF3D00")]
    [InlineData("#e01b1b")]
    public void EveryRedTheAppUsedToShip_BecomesPureRed(string legacy)
    {
        Assert.Equal("#FF0000", LightingPolicy.MigrateLegacyRed(legacy));
    }

    // The migration is not a rule about reds in general: a colour the user chose is theirs.
    [Theory]
    [InlineData("#00E5FF")]
    [InlineData("#FF6D00")]
    [InlineData("#FFD600")]
    [InlineData("#123456")]
    public void AColourTheUserChose_IsLeftAlone(string chosen)
    {
        Assert.Equal(chosen, LightingPolicy.MigrateLegacyRed(chosen));
    }

    [Fact]
    public void TheMigration_CoversThePrimaryColourAndEveryZone()
    {
        var legacy = new LightingConfig
        {
            Effect = "STATIC",
            Brightness = 8,
            Speed = 4,
            Direction = 1,
            Color = "#E01B1B",
            LEDs = new() { "#E01B1B", "#FF2A55", "#00E5FF", "#FF1A1A" }
        };

        var migrated = LightingPolicy.MigrateLegacyReds(legacy)!;

        Assert.Equal("#FF0000", migrated.Color);
        Assert.Equal(new[] { "#FF0000", "#FF0000", "#00E5FF", "#FF0000" }, migrated.LEDs);
        Assert.Equal("STATIC", migrated.Effect);
        Assert.Equal(8, migrated.Brightness);
    }

    [Fact]
    public void TheMigration_LeavesAMissingConfigurationMissing()
    {
        Assert.Null(LightingPolicy.MigrateLegacyReds(null));
    }

    // The file the re-assert after a resume reads. A keyboard left on the old red has to come back on the new one,
    // otherwise the fix only lands for someone who re-picks the colour by hand.
    [Fact]
    public void TheActiveRgbStateFile_IsMigratedOnLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nitrolite-rgb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            File.WriteAllText(ActiveRgbStateStore.PathFor(settingsPath),
                "{ \"Effect\": \"STATIC\", \"Brightness\": 8, \"Speed\": 4, \"Direction\": 1, " +
                "\"Color\": \"#E01B1B\", \"LEDs\": [\"#E01B1B\", \"#FF2A55\", \"#00E5FF\", \"#FF1A1A\"] }");

            var loaded = ActiveRgbStateStore.Load(settingsPath);

            Assert.NotNull(loaded);
            Assert.Equal("#FF0000", loaded!.Color);
            Assert.Equal(new[] { "#FF0000", "#FF0000", "#00E5FF", "#FF0000" }, loaded.LEDs);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
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

/// <summary>
/// Page 3: the nine effects, the four-zone deck, the palette and the optional switches beside it.
/// </summary>
/// <remarks>
/// The deck is the one place where the page has to know something about the effect rather than just display it: WAVE,
/// NEON and OFF paint the keyboard's own colours, so a zone bar showing the chosen colour would be a promise the
/// firmware does not keep. <see cref="LightingPolicy"/> owns that rule and these tests pin the page to it.
/// </remarks>
public class LightingPageTests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>A backend that answers the capability flags the test sets and records every write.</summary>
    private sealed class StubHardware : IHardwareController
    {
        public string BackendName => "Test";

        public bool IsAvailable => true;

        public HardwareCapabilities Capabilities { get; set; } = new()
        {
            Backend = "Test",
            ServiceAvailable = true,
            KeyboardLighting = true,
            BacklightTimeout = true,
            CoolBoost = true,
            LcdOverdrive = true,
            WinKeyLock = true
        };

        public LightingConfig? Lighting { get; set; } = new()
        {
            Effect = "STATIC",
            Brightness = 5,
            Speed = 3,
            Direction = 1,
            Color = "#00E5FF",
            LEDs = new List<string> { "#00E5FF", "#00E5FF", "#00E5FF", "#00E5FF" }
        };

        public bool? BacklightTimeout { get; set; } = false;

        public bool? CoolBoost { get; set; } = false;

        public bool? LcdOverdrive { get; set; } = false;

        public bool WinKeyLock { get; private set; }

        public int WinKeyLockWrites { get; private set; }

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

        public Task<HardwareCapabilities> GetCapabilitiesAsync() => Task.FromResult(Capabilities);

        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(null);

        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(OperatingMode.Default);

        public Task<bool> SetOperatingModeAsync(OperatingMode mode) => Task.FromResult(true);

        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(null);

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(true);

        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);

        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(FanMode.Auto);

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1) => Task.FromResult(true);

        public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult(Lighting);

        public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction,
            string primaryColor, string[]? zoneColors)
        {
            Lighting = new LightingConfig
            {
                Effect = effect,
                Brightness = brightness,
                Speed = speed,
                Direction = direction,
                Color = primaryColor,
                LEDs = zoneColors?.ToList() ?? new List<string>()
            };
            return Task.FromResult(true);
        }

        public Task<bool> TurnOffLightingAsync()
        {
            Lighting = null;
            return Task.FromResult(true);
        }

        public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult(BacklightTimeout);

        public Task<bool> SetBacklightTimeoutAsync(bool enable30s)
        {
            BacklightTimeout = enable30s;
            return Task.FromResult(true);
        }

        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => Task.FromResult<BatteryChargeMode?>(null);

        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => Task.FromResult(true);

        public Task<bool?> GetCoolBoostAsync() => Task.FromResult(CoolBoost);

        public Task<bool> SetCoolBoostAsync(bool on)
        {
            CoolBoost = on;
            return Task.FromResult(true);
        }

        public Task<bool?> GetLcdOverdriveAsync() => Task.FromResult(LcdOverdrive);

        public Task<bool> SetLcdOverdriveAsync(bool on)
        {
            LcdOverdrive = on;
            return Task.FromResult(true);
        }

        public Task<bool> SetWinKeyLockAsync(bool enabled)
        {
            WinKeyLock = enabled;
            WinKeyLockWrites++;
            return Task.FromResult(true);
        }

        public void Dispose() { }
    }

    /// <summary>An OpenRGB provider that answers a fixed line, so the status row can be asserted without a socket.</summary>
    private sealed class StubOpenRgb : IOpenRgbStatusProvider
    {
        public string? OpenRgbStatus { get; init; }
    }

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-lighting-" + Guid.NewGuid().ToString("N") + ".json");

    private static MainViewModel Create(
        IHardwareController? hardware = null,
        IOpenRgbStatusProvider? openRgb = null,
        AppSettings? settings = null) =>
        new(settings ?? new AppSettings(),
            hardware,
            null,
            TempSettingsPath(),
            openRgb: openRgb);

    private static EffectChoice Effect(MainViewModel vm, string id) =>
        vm.Effects.First(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- 1. the nine effects

    [Fact]
    public void AllNineEffects_Listed()
    {
        using var vm = Create(new StubHardware());

        // The page offers the policy's list, in its order, so an effect added there cannot be missing here.
        Assert.Equal(9, vm.Effects.Count);
        Assert.Equal(LightingPolicy.AllEffects, vm.Effects.Select(e => e.Id).ToArray());

        // Every one of them carries a translated name, and no two share one: a list of nine identical rows would be
        // worse than a list of none.
        Assert.All(vm.Effects, e => Assert.False(string.IsNullOrWhiteSpace(e.Label)));
        Assert.Equal(9, vm.Effects.Select(e => e.Label).Distinct().Count());

        // OFF is last because it is NitroLite's own "brightness zero", not one more animation.
        Assert.Equal("OFF", vm.Effects[^1].Id);

        // The page opens on the first effect, which is the one the keyboard is already in.
        Assert.Equal("STATIC", vm.SelectedEffect?.Id);
    }

    // ---------------------------------------------------------------- 2. the deck

    [Fact]
    public void RainbowGradient_ForColorlessEffects()
    {
        using var vm = Create(new StubHardware());

        // STATIC paints the chosen colour, so the deck shows it and the note stays away.
        vm.SelectedEffect = Effect(vm, "STATIC");
        Assert.False(vm.ZoneRainbow);
        Assert.True(vm.ZoneSolid);
        Assert.False(vm.HasZonePreviewNote);
        Assert.Equal("", vm.ZonePreviewNote);

        // WAVE and NEON cycle the keyboard's own colours: the deck shows a rainbow and says why.
        foreach (var id in new[] { "WAVE", "NEON" })
        {
            vm.SelectedEffect = Effect(vm, id);

            Assert.True(vm.ZoneRainbow);
            Assert.False(vm.ZoneSolid);
            Assert.True(vm.HasZonePreviewNote);
            Assert.Equal(vm.Labels.ZonePreviewNote, vm.ZonePreviewNote);
            Assert.False(LightingPolicy.EffectUsesColor(id));
        }

        // OFF takes no colour either, but it is not a rainbow: the keyboard is dark, so the deck shows the black the
        // zones actually hold instead of four coloured bars over a keyboard that is off.
        vm.SelectedEffect = Effect(vm, "OFF");
        Assert.True(vm.IsLightingOff);
        Assert.False(vm.ZoneRainbow);
        Assert.True(vm.ZoneSolid);
        Assert.False(vm.HasZonePreviewNote);

        // The remaining five take a colour, so the rainbow is not a blanket rule.
        foreach (var id in new[] { "BREATHING", "SHIFTING", "ZOOM", "METEOR", "TWINKLING" })
        {
            vm.SelectedEffect = Effect(vm, id);
            Assert.False(vm.ZoneRainbow);
            Assert.True(vm.ZoneSolid);
        }
    }

    [Fact]
    public void ZoneClick_OpensPicker()
    {
        using var vm = Create(new StubHardware());

        Assert.False(vm.ZonePickerOpen);
        Assert.True(vm.IsPrimaryZoneActive);

        // A click on the third bar points the palette at that zone and brings the picker into view.
        vm.SelectZoneForPicker(3);

        Assert.True(vm.ZonePickerOpen);
        Assert.Equal(3, vm.ActiveZone);
        Assert.True(vm.IsZone3Active);
        Assert.False(vm.IsZone1Active);
        Assert.False(vm.IsPrimaryZoneActive);
        Assert.Equal(vm.Labels.Zone(3), vm.ActiveZoneText);

        // A click on the deck is not an event worth a banner: the picker opening beside it is the feedback.
        Assert.False(vm.HasStatus);

        // The primary colour is zone 0, which is the same numbering the palette's own buttons use.
        vm.SelectZoneForPicker(0);
        Assert.Equal(0, vm.ActiveZone);
        Assert.True(vm.IsPrimaryZoneActive);
        Assert.Equal(vm.Labels.PrimaryColor, vm.ActiveZoneText);

        // A bar that is not there cannot be selected: the deck has four, and a fifth click is a bug in the view.
        vm.SelectZoneForPicker(9);
        Assert.Equal(LightingLimits.ZoneCount, vm.ActiveZone);
        Assert.True(vm.IsZone4Active);
    }

    // ---------------------------------------------------------------- 2b. the reds

    /// <summary>
    /// Every red the app ships has to arrive on the keyboard as red — pure #FF0000, not a red that leans pink.
    /// </summary>
    /// <remarks>
    /// The keyboard's diffuser mixes the three LEDs, so a red that carries any green or blue lights the keys up
    /// salmon or pink: the swatch on screen was red and the keyboard was not. The palette's red was #D93829 — a coral
    /// with a fifth of its red in blue — and every built-in preset shipped #FF2A55, a crimson with a third of its red
    /// in blue, which is why the preset called "Kırmızı Nefes" breathed pink. The reds are now #FF0000 exactly, so
    /// there is nothing left for the diffuser to wash out.
    /// </remarks>
    [Fact]
    public void TheRedsTheAppShips_AreRedNotPink()
    {
        using var vm = Create(new StubHardware());
        var presets = new RgbProfileService().GetPresets();

        var reds = new Dictionary<string, string>
        {
            ["the palette's red"] = vm.ColorPalette[0].Hex,
            ["Kırmızı Nefes"] = presets.Single(p => p.Name == "Kırmızı Nefes").Color,
            ["Inferno's red zones"] = presets.Single(p => p.Name == "Kırmızı & Turuncu (Inferno)").LEDs[0],
            ["Cyberpunk's red zones"] = presets.Single(p => p.Name == "Cyberpunk (Cyan & Kırmızı)").LEDs[1]
        };

        foreach (var (where, hex) in reds)
        {
            Assert.Equal("#FF0000", hex.ToUpperInvariant());

            var (red, green, blue) = Channels(hex);
            Assert.True(green * 5 <= red, $"{where} is {hex}: green {green} against red {red} reads as salmon");
            Assert.True(blue * 5 <= red, $"{where} is {hex}: blue {blue} against red {red} reads as pink");
        }
    }

    private static (int Red, int Green, int Blue) Channels(string hex) =>
        (Convert.ToInt32(hex.Substring(1, 2), 16),
         Convert.ToInt32(hex.Substring(3, 2), 16),
         Convert.ToInt32(hex.Substring(5, 2), 16));

    // ---------------------------------------------------------------- 3. the optional switches

    [Fact]
    public async Task WinKeyLock_Toggles()
    {
        var hardware = new StubHardware();
        using var vm = Create(hardware);

        // The capability flags arrive with the first probe, not with the constructor: a window that guessed before the
        // backend answered would offer switches the machine does not have.
        await vm.InitializeAsync();

        Assert.True(vm.WinKeyLockAvailable);
        Assert.False(vm.WinKeyLockOn);

        // There is no getter in the contract, so the switch is the state the user asked for and the write is the only
        // thing that can confirm it.
        vm.WinKeyLockOn = true;
        Assert.True(vm.WinKeyLockOn);
        Assert.True(hardware.WinKeyLock);
        Assert.Equal(1, hardware.WinKeyLockWrites);

        vm.WinKeyLockOn = false;
        Assert.False(vm.WinKeyLockOn);
        Assert.False(hardware.WinKeyLock);
        Assert.Equal(2, hardware.WinKeyLockWrites);

        // A machine whose firmware has no lock hides the switch rather than offering one that answers not-supported.
        var withoutLock = new StubHardware
        {
            Capabilities = new HardwareCapabilities { Backend = "Test", KeyboardLighting = true }
        };
        using var other = Create(withoutLock);
        await other.InitializeAsync();
        Assert.False(other.WinKeyLockAvailable);
    }

    [Fact]
    public void OpenRgbStatus_ShowsOnlyWhenThePlatformSpeaksIt()
    {
        using var silent = Create(new StubHardware());
        Assert.False(silent.HasOpenRgbStatus);
        Assert.Equal("", silent.OpenRgbStatus);

        using var speaking = Create(new StubHardware(), new StubOpenRgb { OpenRgbStatus = "OpenRGB: 1 istemci bağlı" });
        Assert.True(speaking.HasOpenRgbStatus);
        Assert.Contains("OpenRGB", speaking.OpenRgbStatus);
    }

    // ---------------------------------------------------------------- 4. the extras card

    [Fact]
    public async Task ExtrasCard_HidesOnlyWhenNothingIsSupported()
    {
        using var full = Create(new StubHardware());
        await full.InitializeAsync();
        Assert.True(full.HasAnyExtra);
        Assert.False(full.NoExtrasAvailable);

        // A machine with no CoolBoost, no overdrive and no backlight timeout says so once instead of drawing three
        // empty grey boxes.
        var bare = new StubHardware
        {
            Capabilities = new HardwareCapabilities { Backend = "Test", KeyboardLighting = true }
        };
        using var empty = Create(bare);
        await empty.InitializeAsync();
        Assert.False(empty.HasAnyExtra);
        Assert.True(empty.NoExtrasAvailable);
    }
}
