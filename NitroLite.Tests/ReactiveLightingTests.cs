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

/// <summary>
/// The keyboard colour that follows the processor: the ramp, the throttle that keeps it off the embedded controller,
/// and the view model's wiring.
/// </summary>
/// <remarks>
/// The ramp is a pure function, so most of this needs no backend at all. The four view-model tests use the shared
/// <see cref="Phase3Fakes.StubHardware"/>, which records every lighting write, so "did it write" is a count rather than
/// a guess.
/// </remarks>
public class ReactiveLightingTests
{
    // =============================================================================================================
    // 1. The ramp.
    // =============================================================================================================

    [Theory]
    [InlineData(-40)]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(45)]
    public void BelowTheColdEnd_TheColourHolds(double celsius)
    {
        // A keyboard that went dark at idle would read as a fault, so the cold end is a colour and not black.
        Assert.Equal(ReactiveLighting.ColdColor, ReactiveLighting.ColorFor(celsius));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(95)]
    [InlineData(127)]
    [InlineData(500)]
    public void AboveTheHotEnd_TheColourHolds(double celsius)
    {
        Assert.Equal(ReactiveLighting.HotColor, ReactiveLighting.ColorFor(celsius));
    }

    [Theory]
    [InlineData(60, "#00E5A0")]
    [InlineData(72, "#FFC400")]
    public void TheStopsAreTheColoursTheySayTheyAre(double celsius, string expected)
    {
        Assert.Equal(expected, ReactiveLighting.ColorFor(celsius));
    }

    [Fact]
    public void BetweenTwoStops_TheColourIsBetweenThem()
    {
        // 52.5 is a quarter of the way from 45 to 60, so the red channel has to have started to move and the blue
        // channel has to have started to fall.
        var color = ReactiveLighting.ColorFor(52.5);

        Assert.StartsWith("#", color, StringComparison.Ordinal);
        Assert.Equal(7, color.Length);

        var r = Convert.ToInt32(color.Substring(1, 2), 16);
        var g = Convert.ToInt32(color.Substring(3, 2), 16);
        var b = Convert.ToInt32(color.Substring(5, 2), 16);

        Assert.InRange(r, 0, 0x10);
        Assert.InRange(g, 0xB4, 0xE5);
        Assert.InRange(b, 0xA0, 0xFF);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonsenseReading_FallsBackToTheColdColour(double celsius)
    {
        // The chaos tests inject NaN and infinities into every sensor. A keyboard is not the place to discover that a
        // division went wrong.
        Assert.Equal(ReactiveLighting.ColdColor, ReactiveLighting.ColorFor(celsius));
    }

    [Fact]
    public void TheTemperatureIsQuantised_SoATenthOfADegreeIsNotANewColour()
    {
        // Every colour change is a write to the embedded controller, so a sensor wobbling between 61.2 and 61.4 must
        // not produce two of them.
        Assert.Equal(ReactiveLighting.ColorFor(61.1), ReactiveLighting.ColorFor(61.2));
        Assert.Equal(ReactiveLighting.ColorFor(60.1), ReactiveLighting.ColorFor(61.2));
    }

    [Fact]
    public void Quantise_ClampsAndRounds()
    {
        Assert.Equal(45, ReactiveLighting.Quantize(-10));
        Assert.Equal(90, ReactiveLighting.Quantize(400));
        Assert.Equal(45, ReactiveLighting.Quantize(double.NaN));
        Assert.Equal(60, ReactiveLighting.Quantize(61.2));
    }

    // =============================================================================================================
    // 2. The throttle.
    // =============================================================================================================

    [Fact]
    public void TheFirstColour_IsAlwaysWritten()
    {
        var throttle = new ReactiveLightingThrottle();
        Assert.True(throttle.TryBeginWrite("#00B4FF", DateTime.UtcNow));
    }

    [Fact]
    public void TheSameColour_IsNeverWrittenTwice()
    {
        var throttle = new ReactiveLightingThrottle();
        var now = DateTime.UtcNow;

        Assert.True(throttle.TryBeginWrite("#00B4FF", now));
        Assert.False(throttle.TryBeginWrite("#00B4FF", now.AddMinutes(5)));
    }

    [Fact]
    public void AChangedColour_WaitsForTheInterval()
    {
        var throttle = new ReactiveLightingThrottle();
        var now = DateTime.UtcNow;

        Assert.True(throttle.TryBeginWrite("#00B4FF", now));

        // A temperature oscillating across a bucket boundary would otherwise write on every tick.
        Assert.False(throttle.TryBeginWrite("#00E5A0", now.AddSeconds(1)));
        Assert.True(throttle.TryBeginWrite("#00E5A0", now + ReactiveLighting.MinInterval));
    }

    [Fact]
    public void Reset_ForgetsTheLastWrite()
    {
        var throttle = new ReactiveLightingThrottle();
        var now = DateTime.UtcNow;

        Assert.True(throttle.TryBeginWrite("#00B4FF", now));
        throttle.Reset();

        // Turning the mode back on should paint the keyboard on the next tick rather than wait for a change.
        Assert.True(throttle.TryBeginWrite("#00B4FF", now));
    }

    [Fact]
    public void AnEmptyColour_IsNeverWritten()
    {
        var throttle = new ReactiveLightingThrottle();
        Assert.False(throttle.TryBeginWrite("", DateTime.UtcNow));
        Assert.False(throttle.TryBeginWrite("   ", DateTime.UtcNow));
    }

    // =============================================================================================================
    // 3. The view model.
    // =============================================================================================================

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"nitrolite_reactive_{Guid.NewGuid():N}.json");

    private static MainViewModel Create(Phase3Fakes.StubHardware hardware, AppSettings? settings = null, string? path = null) =>
        new(settings ?? new AppSettings(), hardware, null, path ?? TempSettingsPath());

    /// <summary>
    /// The writes that carry a reactive colour. The page's own debounced write fires 350 ms after the constructor sets
    /// the effect, so a test that counted every write would be racing it; counting the ones that carry the colour under
    /// test is the question actually being asked.
    /// </summary>
    private static List<string> ReactiveWrites(Phase3Fakes.StubHardware hardware, string color) =>
        hardware.LightingWrites.Where(w => w.Contains(color, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task TheModeIsOffByDefault_AndWritesNothing()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 78;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        Assert.False(vm.ReactiveLightingEnabled);

        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        Assert.Empty(ReactiveWrites(hardware, ReactiveLighting.ColorFor(78)));
    }

    [Fact]
    public async Task TurningItOn_PaintsTheKeyboardOnTheNextTick()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 78;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        var expected = ReactiveLighting.ColorFor(78);
        var write = Assert.Single(ReactiveWrites(hardware, expected));

        // STATIC with one colour in every zone: the same shape the page writes when the user picks a colour.
        Assert.StartsWith("STATIC|", write, StringComparison.Ordinal);
        Assert.Equal(expected, vm.ReactiveLightingColor);
    }

    [Fact]
    public async Task ASecondTickAtTheSameTemperature_WritesNothing()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 78;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        // One write for one colour. This is the whole point of the throttle: the telemetry loop runs every second and
        // the embedded controller is not a place to spend that budget.
        Assert.Single(ReactiveWrites(hardware, ReactiveLighting.ColorFor(78)));
    }

    [Fact]
    public async Task TurningItOnWhileTheLightsAreOff_BringsThemBackAndPaints()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 80;

        var settings = new AppSettings { IsBacklightOff = true };
        using var vm = Create(hardware, settings);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;

        // Asking for the temperature colour is asking to see it. The switch used to leave the off state in place, the
        // tick then refused to paint a keyboard the user had turned off, and the mode looked broken: the keyboard
        // stayed black with the switch visibly on.
        Assert.False(settings.IsBacklightOff);
        Assert.True(vm.BrightnessLevel > 0);

        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        var write = Assert.Single(ReactiveWrites(hardware, ReactiveLighting.ColorFor(80)));

        // The mode's own level, not zero: the firmware ignores a colour written at brightness zero, and the page's
        // slider is not this mode's to borrow.
        Assert.StartsWith($"STATIC|{vm.ReactiveBrightness}|", write, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurningItOnAfterTheBlackoutPreset_MovesTheListToTheCustomEntry()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 80;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        // The state from the report: "Lights Off (Blackout)" was the selected profile when the mode was turned on, so
        // the list kept claiming a preset over colours the processor was choosing.
        vm.SelectedRgbProfile = vm.RgbProfiles.First(p => string.Equals(p.Effect, "OFF", StringComparison.OrdinalIgnoreCase));
        Assert.True(vm.IsLightingOff);

        vm.ReactiveLightingEnabled = true;

        Assert.Same(vm.CustomProfile, vm.SelectedRgbProfile);
        Assert.False(vm.IsLightingOff);

        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        Assert.Single(ReactiveWrites(hardware, ReactiveLighting.ColorFor(80)));
    }

    [Fact]
    public async Task TurningTheLightsOffWhileItRuns_TakesTheModeDownWithThePage()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 80;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        // The mode was on first, and the user turned the keyboard off afterwards: the page goes dark with it. The
        // switch has to come down too — a mode left visibly on over a black keyboard was the reported bug, and the
        // only way to make it work again was to switch it off and on. The preference stays in the settings file.
        vm.TurnOffLightingCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => vm.IsLightingBlackedOut, 3000);
        hardware.LightingWrites.Clear();

        Assert.False(vm.ReactiveLightingEnabled);
        Assert.True(vm.IsLightingBlackedOut);
        Assert.False(vm.LightingControlsEnabled);

        hardware.Sample.CpuTemperature = 92;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        Assert.Empty(ReactiveWrites(hardware, ReactiveLighting.ColorFor(92)));
    }

    [Fact]
    public async Task BringingTheLightsBack_RevivesTheModeWithoutTogglingIt()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 80;

        var settings = new AppSettings();
        using var vm = Create(hardware, settings);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        vm.TurnOffLightingCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => vm.IsLightingBlackedOut, 3000);

        // The blackout is the only thing standing between the user and their mode: the settings still say they want
        // it, so the button that brings the lights back brings the mode back with them.
        Assert.True(settings.ReactiveLightingEnabled);

        vm.ToggleLightingBlackoutCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => !vm.IsLightingBlackedOut, 3000);
        await Phase3Fakes.WaitUntilAsync(() => vm.ReactiveLightingEnabled, 3000);

        hardware.Sample.CpuTemperature = 86;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        Assert.Single(ReactiveWrites(hardware, ReactiveLighting.ColorFor(86)));
    }

    [Fact]
    public async Task ACapabilityFlagThatSaysNoKeyboard_DoesNotStopTheWrite()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 80;
        hardware.Capabilities = new HardwareCapabilities
        {
            Backend = "Test",
            ServiceAvailable = true,
            FanControl = true,
            KeyboardLighting = false
        };

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        // The flag comes from the WMI capability probe, which opens an instance of AcerGamingFunction and needs
        // elevation. Without it the probe answers "no keyboard" on a machine whose keyboard the write path paints
        // perfectly - the reference AN16-41 does exactly that - so the mode must not be gated on it. Gating it hid the
        // whole card on the one machine it was written for.
        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        Assert.Single(ReactiveWrites(hardware, ReactiveLighting.ColorFor(80)));
    }

    [Fact]
    public async Task TheModeIsPersisted()
    {
        var path = TempSettingsPath();
        try
        {
            var hardware = new Phase3Fakes.StubHardware();
            using (var vm = Create(hardware, path: path))
            {
                await vm.InitializeAsync();
                vm.ReactiveLightingEnabled = true;
            }

            Assert.True(SettingsManager.LoadSettingsWithFallback(path).ReactiveLightingEnabled);

            // And it comes back on the next launch, because it is a preference and not a moment. The view model is
            // handed the settings it should start from, so the second one is given what the file now holds.
            using var again = Create(new Phase3Fakes.StubHardware(),
                SettingsManager.LoadSettingsWithFallback(path), path);
            Assert.True(again.ReactiveLightingEnabled);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task AManualColour_TurnsTheModeOff()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 70;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        // Someone who reaches for a swatch is asking for that colour, not for the temperature's. The command runs the
        // same write the page runs, and every await inside it is on a completed task, so it has finished on return.
        vm.ApplyLightingCommand.Execute(null);

        Assert.False(vm.ReactiveLightingEnabled);
    }

    [Fact]
    public void TheCardIsAlwaysOnThePage()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml"));

        Assert.Contains("Labels.ReactiveLighting", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveLightingEnabled", markup, StringComparison.Ordinal);

        // The card is not hidden behind a capability flag. It was, and the flag answered "no keyboard" on the machine
        // this was written on, so the feature existed and could not be found.
        var start = markup.IndexOf("Labels.ReactiveLighting", StringComparison.Ordinal);
        var card = markup[markup.LastIndexOf("<Border", start, StringComparison.Ordinal)..];
        card = card[..(card.IndexOf('>') + 1)];

        Assert.DoesNotContain("IsVisible", card, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCardSitsWithTheOtherAutomaticColourAfterTheEffectCard()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml"));

        // The automatic colours answer "which colour does the keyboard show" without the user choosing one, so they
        // sit after the controls that hold a colour of their own — the palette, the profiles and the effect — and the
        // charging colour sits directly under the temperature ramp. The card used to sit at the very top of the page,
        // which made it a fifth subject rather than a way of choosing a colour.
        var palette = markup.IndexOf("Labels.ColorPalette", StringComparison.Ordinal);
        var profiles = markup.IndexOf("Labels.RgbProfiles", StringComparison.Ordinal);
        var effects = markup.IndexOf("Labels.Effects", StringComparison.Ordinal);
        var card = markup.IndexOf("Labels.ReactiveLighting", StringComparison.Ordinal);
        var charging = markup.IndexOf("Labels.ChargingColor}", StringComparison.Ordinal);

        Assert.True(palette > 0, "the lighting page has no palette");
        Assert.True(profiles > palette, "the palette is below the saved profiles");
        Assert.True(effects > profiles, "the effect card is above the saved profiles");
        Assert.True(card > effects, "the reactive card is above the effect card");
        Assert.True(charging > card, "the charging card is above the temperature ramp");

        // The extras card is gone: its 30-second switch moved into the header, and the category with it.
        Assert.DoesNotContain("Labels.Extras", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowExtrasCard", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCardOffersTheThreeRampsAndTheCustomEditor()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml"));

        // A picker the user cannot see is a picker that does not exist: the view model carried all three ramps and the
        // stop editor for a while before the view was told about them.
        Assert.Contains("ReactiveProfileChoices", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveCustomEditorVisible", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveStops", markup, StringComparison.Ordinal);
        Assert.Contains("AddReactiveStopCommand", markup, StringComparison.Ordinal);
        Assert.Contains("OnPickReactiveStopColor", markup, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 4. The three ramps, and the one the user builds.
    // =============================================================================================================

    [Fact]
    public void ThreeRampsAreOffered_EachWithAUsableShape()
    {
        Assert.Equal(3, ReactiveLightingProfiles.BuiltIn.Count);

        // Three chips with the same gradient would be one chip with three names, so every ramp has to be its own.
        Assert.Equal(3, ReactiveLightingProfiles.BuiltIn.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, ReactiveLightingProfiles.BuiltIn.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, ReactiveLightingProfiles.BuiltIn
            .Select(p => ReactiveLighting.ColorFor(p.Stops, 95))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());

        foreach (var profile in ReactiveLightingProfiles.BuiltIn)
        {
            var stops = ReactiveLightingProfiles.SanitizeStops(profile.Stops);

            // Nothing is dropped as invalid: a shipped ramp is one the evaluator can walk end to end.
            Assert.True(stops.Count >= 2, $"{profile.Id} has {stops.Count} usable stops");
            Assert.Equal(profile.Stops.Count, stops.Count);
            Assert.True(profile.IsBuiltIn);
        }

        // The first ramp is the one the evaluator has always used, so a settings file that names no profile keeps the
        // colour it had before the picker existed.
        Assert.Equal(ReactiveLighting.ColorFor(20), ReactiveLighting.ColorFor(ReactiveLightingProfiles.Default.Stops, 20));
        Assert.Equal(ReactiveLighting.ColdColor, ReactiveLightingProfiles.Default.Stops[0].Color);
    }

    [Fact]
    public void AProfileIsFoundByIdOrByTheNameAnOlderFileStored()
    {
        var inferno = ReactiveLightingProfiles.BuiltIn.First(p => p.Id == "inferno");

        Assert.Same(inferno, ReactiveLightingProfiles.FindBuiltIn("inferno"));
        Assert.Same(inferno, ReactiveLightingProfiles.FindBuiltIn("INFERNO"));
        Assert.Same(inferno, ReactiveLightingProfiles.FindBuiltIn(inferno.Name));

        // The build before the rename had no ids and stored the name instead, so an upgraded settings file carries
        // that text and nowhere else.
        Assert.Same(inferno, ReactiveLightingProfiles.FindBuiltIn("Sıcak Ateş (Sarı → Kırmızı)"));
        Assert.Equal("inferno", ReactiveLightingProfiles.CanonicalId("Sıcak Ateş (Sarı → Kırmızı)"));

        Assert.Equal("inferno", ReactiveLightingProfiles.CanonicalId(inferno.Name));
        Assert.Equal(ReactiveLightingProfiles.CustomId, ReactiveLightingProfiles.CanonicalId("custom"));
        Assert.Null(ReactiveLightingProfiles.CanonicalId("a profile that no build knows"));
        Assert.Null(ReactiveLightingProfiles.CanonicalId(null));
    }

    [Theory]
    [InlineData("Nebula (Turkuaz → Mor)", "nebula")]
    [InlineData("Buz → Mor (Soğuk Palet)", "nebula")]   // what the build before the rename stored
    [InlineData("Klasik (Mavi → Kırmızı)", "aurora")]
    public void ASettingsFileThatStoredAName_IsRewrittenAsTheId(string stored, string expected)
    {
        var path = TempSettingsPath();
        try
        {
            var settings = new AppSettings
            {
                ReactiveLightingProfile = stored
            };

            SettingsManager.SaveSettingsAtomic(path, settings);
            var loaded = SettingsManager.LoadSettingsWithFallback(path);

            // The id is the identity, so a language change cannot lose the choice, and the rename cannot lose the
            // ramp an existing user picked: the name is looked up, and what is stored is the id.
            Assert.Equal(expected, loaded.ReactiveLightingProfile);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task PickingARamp_PaintsItOnTheNextTickAndStoresTheId()
    {
        var path = TempSettingsPath();
        try
        {
            var hardware = new Phase3Fakes.StubHardware();
            hardware.Sample.CpuTemperature = 95;

            using var vm = Create(hardware, path: path);
            await vm.InitializeAsync();

            vm.ReactiveLightingEnabled = true;
            await vm.RefreshTelemetryAsync();
            await vm.ReactiveLightingTask;

            hardware.LightingWrites.Clear();
            vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.Id == "inferno");

            // Choosing a ramp is not a write of its own: the colour it produces at this temperature is, and that is
            // the next tick's business.
            Assert.Empty(hardware.LightingWrites);

            await vm.RefreshTelemetryAsync();
            await vm.ReactiveLightingTask;

            // 95 °C is the hot end of every ramp, and Inferno's hot end is its own deep red.
            var write = Assert.Single(ReactiveWrites(hardware, "#D50000"));
            Assert.StartsWith("STATIC|", write, StringComparison.Ordinal);
            Assert.Equal("#D50000", vm.ReactiveLightingColor);

            Assert.Equal("inferno", SettingsManager.LoadSettingsWithFallback(path).ReactiveLightingProfile);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task ChoosingARampWhileTheModeIsOff_WritesNothing()
    {
        var hardware = new Phase3Fakes.StubHardware();

        using var vm = Create(hardware);
        await vm.InitializeAsync();
        hardware.LightingWrites.Clear();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.Id == "nebula");

        // The mode is off, so the choice is only a preference: a profile picked while the keyboard is following the
        // user's own colour must not repaint it.
        Assert.False(vm.ReactiveLightingEnabled);
        Assert.Empty(hardware.LightingWrites);
    }

    [Fact]
    public async Task TheCustomEntry_StartsFromTheRampThatWasActive()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.Id == "nebula");
        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);

        Assert.True(vm.ReactiveCustomEditorVisible);
        Assert.Equal(4, vm.ReactiveStops.Count);
        Assert.Equal("#00E5FF", vm.ReactiveStops[0].Color);
        Assert.Equal(90, vm.ReactiveStops[^1].TemperatureC);
    }

    [Fact]
    public async Task EditingTheCustomRamp_IsWhatTheNextTickPaints()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 95;

        using var vm = Create(hardware);
        await vm.InitializeAsync();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);
        vm.ReactiveStops[0].Color = "#123456";
        vm.ReactiveStops[^1].Color = "#654321";

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        // 95 °C is above the last stop, so the ramp holds its hottest colour — the one the user just set. A custom
        // ramp that painted the built-in amber instead is the failure this pins down.
        Assert.Equal("#654321", vm.ReactiveLightingColor);
        Assert.NotEmpty(ReactiveWrites(hardware, "#654321"));
    }

    [Fact]
    public async Task TheCustomRampStopsAtTheLimits()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);

        // The editor starts from a four-stop ramp seeded from the one that was active.
        Assert.Equal(4, vm.ReactiveStops.Count);
        Assert.True(vm.CanRemoveReactiveStop);

        while (vm.CanAddReactiveStop)
        {
            Assert.True(vm.AddReactiveStopCommand.CanExecute(null));
            vm.AddReactiveStopCommand.Execute(null);
        }

        Assert.Equal(ReactiveLighting.MaxStops, vm.ReactiveStops.Count);
        Assert.False(vm.AddReactiveStopCommand.CanExecute(null));
        Assert.True(vm.ReactiveStops[0].RemoveCommand.CanExecute(null));

        // Each stop landed in its own place rather than at the end, so the editor reads cold to hot the way the
        // keyboard blends, and no two rows ask for the same temperature.
        var temperatures = vm.ReactiveStops.Select(s => s.TemperatureC).ToList();
        Assert.Equal(temperatures.OrderBy(t => t), temperatures);
        Assert.Equal(temperatures.Count, temperatures.Distinct().Count());

        while (vm.CanRemoveReactiveStop)
        {
            vm.ReactiveStops[^1].RemoveCommand.Execute(null);
        }

        // A ramp needs a cold end and a hot end; two stops is the floor.
        Assert.Equal(2, vm.ReactiveStops.Count);
        Assert.False(vm.CanRemoveReactiveStop);
        Assert.False(vm.ReactiveStops[0].RemoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheCustomChipShowsWhatTheEditorHolds()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.IsCustom);
        var chip = vm.ReactiveProfileChoices.First(c => c.IsCustom);

        var before = chip.Stops;
        vm.ReactiveStops[0].Color = "#ABCDEF";

        // The chip is the preview the user judges, so it is the same list the evaluator would walk, not a copy taken
        // when the picker was built.
        Assert.NotSame(before, chip.Stops);
        Assert.Equal("#ABCDEF", chip.Stops[0].Color);
        Assert.Equal(vm.ReactiveStops.Count, chip.Stops.Count);
    }

    [Fact]
    public async Task TheBuiltInChipsCarryTheirOwnRamp()
    {
        using var vm = Create(new Phase3Fakes.StubHardware());
        await vm.InitializeAsync();

        foreach (var profile in ReactiveLightingProfiles.BuiltIn)
        {
            var chip = vm.ReactiveProfileChoices.First(c => c.Id == profile.Id);

            Assert.Equal(profile.Stops.Count, chip.Stops.Count);
            Assert.Equal(profile.Stops[0].Color, chip.Stops[0].Color);
            Assert.False(chip.IsCustom);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
