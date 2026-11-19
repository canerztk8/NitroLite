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
    public async Task AKeyboardTheUserTurnedOff_StaysOff()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 80;

        var settings = new AppSettings { IsBacklightOff = true };
        using var vm = Create(hardware, settings);
        await vm.InitializeAsync();

        vm.ReactiveLightingEnabled = true;
        await vm.RefreshTelemetryAsync();
        await vm.ReactiveLightingTask;

        // The mode is a colour, not a way to switch the lights back on.
        Assert.Empty(ReactiveWrites(hardware, ReactiveLighting.ColorFor(80)));
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
    public void TheCardComesBeforeTheColourControls()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml"));

        // The mode overrides every colour under it, so it has to be the first card on the page. It was written below the
        // effect sliders first, and the one person who went looking for it could not find it without scrolling.
        var card = markup.IndexOf("Labels.ReactiveLighting", StringComparison.Ordinal);
        var deck = markup.IndexOf("Labels.ZonePreview", StringComparison.Ordinal);
        var palette = markup.IndexOf("Labels.ColorPalette", StringComparison.Ordinal);

        Assert.True(card > 0, "the lighting page has no reactive card");
        Assert.True(deck > 0, "the lighting page has no zone deck");
        Assert.True(card < deck, "the reactive card is below the zone deck");
        Assert.True(palette > 0, "the lighting page has no palette");
        Assert.True(card < palette, "the reactive card is below the palette");
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
