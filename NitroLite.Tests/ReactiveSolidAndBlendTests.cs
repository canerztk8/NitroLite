using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The three ways the reactive card was made more customisable: one colour per half whatever the heat, a split that
/// crosses the deck instead of meeting in the middle, and two more ramps to choose from.
/// </summary>
/// <remarks>
/// The request was "make temp-reactive lighting more customisable — a different colour for the processor than for the
/// graphics chip, and so on". A second colour per half is only worth anything if the two halves stay independent, so
/// the tests below check the shapes that keep them apart: the flat ramp the solid entry stands on, the four zones a
/// blend produces, and the two settings keys the choice is stored in.
/// </remarks>
public class ReactiveSolidAndBlendTests
{
    // =============================================================================================================
    // 1. The solid entry is a flat ramp, so no code path below it needs a second branch.
    // =============================================================================================================

    [Fact]
    public void TheSolidEntry_IsOneColourAtEveryTemperature()
    {
        var stops = ReactiveLightingProfiles.SolidStops("#22AA44");

        // Two stops at the ends of the scale, both the same colour: the evaluator answers it for every temperature,
        // which is what lets a solid half share the painting path with a ramp.
        Assert.Equal(2, stops.Count);
        Assert.Equal(ReactiveLighting.MinStopCelsius, stops[0].AtCelsius);
        Assert.Equal(ReactiveLighting.MaxStopCelsius, stops[1].AtCelsius);

        foreach (var celsius in new double[] { 20, 45, 58, 78, 110 })
        {
            Assert.Equal("#22AA44", ReactiveLighting.ColorFor(stops, celsius));
        }

        // Text that is not a colour falls back to the cold end rather than throwing on a telemetry tick.
        Assert.Equal(ReactiveLighting.ColdColor, ReactiveLightingProfiles.SolidStops("nonsense")[0].Color);
    }

    [Fact]
    public void TheSolidEntry_IsRecognisedByItsIdAndStoredUnderIt()
    {
        Assert.True(ReactiveLightingProfiles.IsSolid("solid"));
        Assert.True(ReactiveLightingProfiles.IsSolid(" Solid "));
        Assert.False(ReactiveLightingProfiles.IsSolid("inferno"));
        Assert.False(ReactiveLightingProfiles.IsSolid(null));

        // A settings file that names the entry reaches the id the picker compares against.
        Assert.Equal(ReactiveLightingProfiles.SolidId, ReactiveLightingProfiles.CanonicalId("solid"));
    }

    // =============================================================================================================
    // 2. Two blocks or one gradient, and never a zone in the wrong half.
    // =============================================================================================================

    [Fact]
    public void TheBlocksSplit_IsTwoBlocksAndNeverBleeds()
    {
        var zones = ReactiveLighting.ZonesFor("#FF0000", "#0000FF", blend: false);

        Assert.Equal(new[] { "#FF0000", "#FF0000", "#0000FF", "#0000FF" }, zones);
        Assert.Equal(4, zones.Length);
    }

    [Fact]
    public void TheBlendedSplit_CrossesFromOneSensorToTheOther()
    {
        var zones = ReactiveLighting.ZonesFor("#FF0000", "#0000FF", blend: true);

        Assert.Equal(4, zones.Length);

        // The ends are exactly the two halves' colours: the gradient is how the deck gets from one to the other, not a
        // third colour that leaves both sensors unrecognisable.
        Assert.Equal("#FF0000", zones[0]);
        Assert.Equal("#0000FF", zones[3]);

        // The two middle zones are the straight line between them, one third and two thirds of the way across.
        Assert.Equal(ReactiveLighting.Blend("#FF0000", "#0000FF", 1 / 3.0), zones[1]);
        Assert.Equal(ReactiveLighting.Blend("#FF0000", "#0000FF", 2 / 3.0), zones[2]);

        // Neither middle zone is a copy of a half's colour, which is the failure a stale index would produce.
        Assert.NotEqual(zones[0], zones[1]);
        Assert.NotEqual(zones[3], zones[2]);
    }

    [Fact]
    public void TheBlend_KeepsTwoMatchingHalvesAsBlocks()
    {
        // Same colour on both halves: a gradient between a colour and itself is that colour, so a blend with one sensor
        // on is not a special case and the deck does not drift as the temperature moves.
        var zones = ReactiveLighting.ZonesFor("#00B4FF", "#00B4FF", blend: true);

        Assert.All(zones, zone => Assert.Equal("#00B4FF", zone));
    }

    [Fact]
    public void TheBlend_IsClampedAtBothEnds()
    {
        Assert.Equal("#FF0000", ReactiveLighting.Blend("#FF0000", "#0000FF", -5));
        Assert.Equal("#FF0000", ReactiveLighting.Blend("#FF0000", "#0000FF", 0));
        Assert.Equal("#0000FF", ReactiveLighting.Blend("#FF0000", "#0000FF", 1));
        Assert.Equal("#0000FF", ReactiveLighting.Blend("#FF0000", "#0000FF", 5));

        // Halfway is the midpoint of each channel, so the mix is a straight line rather than a weighted one.
        Assert.Equal("#800080", ReactiveLighting.Blend("#FF0000", "#0000FF", 0.5));
    }

    // =============================================================================================================
    // 3. Two more ramps, appended so the shipped default stays the default.
    // =============================================================================================================

    [Fact]
    public void TwoMoreRamps_JoinTheShippedThree()
    {
        var forest = ReactiveLightingProfiles.FindBuiltIn("forest");
        var glacier = ReactiveLightingProfiles.FindBuiltIn("glacier");

        Assert.NotNull(forest);
        Assert.NotNull(glacier);

        // Four stops each, between the two ends of the scale, so both are editable like every other ramp.
        foreach (var profile in new[] { forest!, glacier! })
        {
            Assert.True(profile.IsBuiltIn);
            Assert.Equal(4, profile.Stops.Count);
            Assert.All(profile.Stops, stop => Assert.InRange(stop.AtCelsius,
                ReactiveLighting.MinStopCelsius, ReactiveLighting.MaxStopCelsius));
        }

        // The green one ends warmer than it starts and the ice one ends paler, which is the reason each was added.
        Assert.Equal("#00C853", ReactiveLighting.ColorFor(forest!.Stops, 45));
        Assert.Equal("#FF6D00", ReactiveLighting.ColorFor(forest.Stops, 90));
        Assert.Equal("#1E3A8A", ReactiveLighting.ColorFor(glacier!.Stops, 45));
        Assert.Equal("#F8FAFC", ReactiveLighting.ColorFor(glacier.Stops, 90));

        // Appended, not prepended: the entry a fresh settings file lands on is still the first one, and the two new
        // ramps are the last two chips in the picker.
        Assert.Same(ReactiveLightingProfiles.Default, ReactiveLightingProfiles.BuiltIn[0]);
        Assert.Equal(5, ReactiveLightingProfiles.BuiltIn.Count);
        Assert.Same(forest, ReactiveLightingProfiles.BuiltIn[3]);
        Assert.Same(glacier, ReactiveLightingProfiles.BuiltIn[4]);

        // The picker reaches them by name too, which is what a settings file written before the ids means.
        Assert.Equal("forest", ReactiveLightingProfiles.CanonicalId("Orman (Yeşil → Turuncu)"));
        Assert.Equal("glacier", ReactiveLightingProfiles.CanonicalId("Buzul (Lacivert → Beyaz)"));
    }

    // =============================================================================================================
    // 4. The card: a solid half paints one colour, a blend is remembered, and the chips are where the picker is.
    // =============================================================================================================

    [Fact]
    public async Task TheSolidHalfPaintsItsColourWhateverTheHeat()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 58;

        var path = TempSettingsPath();
        try
        {
            var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = false };
            using var vm = new MainViewModel(settings, hardware, null, path, powerState: () => (false, 50));

            await vm.InitializeAsync();
            vm.ReactiveLightingEnabled = true;
            await vm.RefreshTelemetryAsync();
            await vm.ReactiveLightingTask;

            hardware.LightingWrites.Clear();

            // The solid entry, and a colour no ramp in the app contains at any temperature.
            vm.SelectedReactiveProfile = vm.ReactiveProfileChoices
                .First(c => c.Id == ReactiveLightingProfiles.SolidId);

            Assert.True(vm.ReactiveSolidVisible);

            vm.ReactiveSolidColor = "#22AA44";
            await vm.ReactiveLightingTask;

            var zones = Zones(Assert.Single(hardware.LightingWrites));

            // Four zones, all the chosen colour: a solid half is not a ramp that happens to be flat at this temperature.
            Assert.Equal(4, zones.Length);
            Assert.All(zones, zone => Assert.Equal("#22AA44", zone));

            // And the choice is stored against the half that was being edited.
            Assert.Equal("#22AA44", settings.ReactiveLightingSolidCpu);
            Assert.Equal(ReactiveLightingProfiles.SolidId, settings.ReactiveLightingProfile);
            Assert.Null(settings.ReactiveLightingSolidGpu);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task TheSolidChip_SitsBesideTheRampsAndPaintsTheHeldColour()
    {
        var hardware = new Phase3Fakes.StubHardware();

        var path = TempSettingsPath();
        try
        {
            var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = false };
            using var vm = new MainViewModel(settings, hardware, null, path, powerState: () => (false, 50));

            await vm.InitializeAsync();

            // The order of the picker: the shipped ramps, then the solid entry, then the user's own. The solid entry is
            // a colour rather than a ramp, but it answers the same question, so it sits with the answers.
            Assert.Equal(ReactiveLightingProfiles.SolidId, vm.ReactiveProfileChoices[^2].Id);
            Assert.True(vm.ReactiveProfileChoices[^1].IsCustom);

            var solid = vm.ReactiveProfileChoices[^2];
            Assert.False(solid.IsCustom);
            Assert.False(string.IsNullOrWhiteSpace(solid.Label));

            // Its chip paints the colour the half holds, as two identical stops, so it reads as one colour beside ramps
            // that read as gradients.
            Assert.Equal(2, solid.Stops.Count);
            Assert.Equal(solid.Stops[0].Color, solid.Stops[1].Color);

            // The colour row is only on screen while the solid entry is the one selected.
            vm.SelectedReactiveProfile = vm.ReactiveProfileChoices.First(c => c.Id == "inferno");
            Assert.False(vm.ReactiveSolidVisible);

            vm.SelectedReactiveProfile = solid;
            Assert.True(vm.ReactiveSolidVisible);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task TheBlendIsRememberedAndPaintsTheGradient()
    {
        var hardware = new Phase3Fakes.StubHardware();
        hardware.Sample.CpuTemperature = 45;
        hardware.Sample.GpuTemperature = 45;

        var path = TempSettingsPath();
        try
        {
            // Two ramps whose cold ends are nothing alike, at the same temperature: the only difference between the
            // halves is the ramp, which is exactly what the split is for.
            var settings = new AppSettings
            {
                ReactiveLightingUseCpu = true,
                ReactiveLightingUseGpu = true,
                ReactiveLightingProfile = "forest",
                ReactiveLightingProfileGpu = "glacier"
            };

            using var vm = new MainViewModel(settings, hardware, null, path, powerState: () => (false, 50));

            await vm.InitializeAsync();
            vm.ReactiveLightingEnabled = true;
            await vm.RefreshTelemetryAsync();
            await vm.ReactiveLightingTask;

            // Blocks first: two zones of each colour with nothing in between.
            var blocks = Zones(hardware.LightingWrites.Last());
            Assert.Equal("#00C853", blocks[0]);
            Assert.Equal("#00C853", blocks[1]);
            Assert.Equal("#1E3A8A", blocks[2]);
            Assert.Equal("#1E3A8A", blocks[3]);

            hardware.LightingWrites.Clear();

            // The blended chip repaints at once: the shape changed while the pair of colours did not, so a write that
            // waited for a new temperature would leave the keys in the old shape.
            Assert.True(vm.ReactiveSplitEven);
            vm.ReactiveSplitBlend = true;

            Assert.False(vm.ReactiveSplitEven);
            Assert.True(settings.ReactiveLightingBlend);

            await vm.ReactiveLightingTask;

            var zones = Zones(Assert.Single(hardware.LightingWrites));

            Assert.Equal("#00C853", zones[0]);
            Assert.Equal("#1E3A8A", zones[3]);
            Assert.Equal(ReactiveLighting.Blend("#00C853", "#1E3A8A", 1 / 3.0), zones[1]);
            Assert.Equal(ReactiveLighting.Blend("#00C853", "#1E3A8A", 2 / 3.0), zones[2]);

            // The choice is in the settings file, so the deck keeps its shape on the next launch.
            Assert.True(SettingsManager.LoadSettingsWithFallback(path).ReactiveLightingBlend);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task TheSplitChoice_OnlyAppearsWithBothHalvesLive()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = false };
            using var vm = new MainViewModel(settings, null, null, path, powerState: () => (false, 50));

            await vm.InitializeAsync();

            // Nothing to mix with one sensor on: the chips are hidden rather than offering a shape the keyboard cannot
            // show.
            Assert.False(vm.ReactiveBlendVisible);

            vm.ReactiveGpuSensor = true;
            Assert.True(vm.ReactiveBlendVisible);

            // The two chips are one setting seen twice, so switching the left one on switches the right one off.
            vm.ReactiveLightingBlend = true;
            Assert.True(vm.ReactiveSplitBlend);
            Assert.False(vm.ReactiveSplitEven);

            vm.ReactiveSplitEven = true;
            Assert.False(vm.ReactiveSplitBlend);
            Assert.False(settings.ReactiveLightingBlend);

            // A chip that is switched off does not switch itself on: unchecking the blended chip leaves the setting
            // alone, because the pair is a choice between two shapes rather than a checkbox with an empty state.
            vm.ReactiveSplitBlend = false;
            Assert.False(settings.ReactiveLightingBlend);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task TheSolidColour_IsStoredPerHalfAndSurvivesARestart()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = new AppSettings { ReactiveLightingUseCpu = true, ReactiveLightingUseGpu = true };

            using (var vm = new MainViewModel(settings, null, null, path, powerState: () => (false, 50)))
            {
                await vm.InitializeAsync();

                // The tiles point the card: switching the graphics half off hands it to the processor.
                vm.ReactiveGpuSensor = false;
                Assert.False(vm.ReactiveRampTargetGpu);

                vm.SelectedReactiveProfile = vm.ReactiveProfileChoices
                    .First(c => c.Id == ReactiveLightingProfiles.SolidId);
                vm.ReactiveSolidColor = "#AABBCC";

                Assert.True(vm.ReactiveSolidVisible);
                Assert.Equal("#AABBCC", settings.ReactiveLightingSolidCpu);

                // Both halves live again, and the graphics tile takes the card: its colour is its own.
                vm.ReactiveGpuSensor = true;
                Assert.True(vm.ReactiveRampTargetGpu);

                vm.ReactiveSolidColor = "#112233";
                Assert.Equal("#112233", settings.ReactiveLightingSolidGpu);

                // Editing one half's colour left the other alone, which is the whole point of two keys.
                Assert.Equal("#AABBCC", settings.ReactiveLightingSolidCpu);
            }

            var reloaded = SettingsManager.LoadSettingsWithFallback(path);

            Assert.Equal("#AABBCC", reloaded.ReactiveLightingSolidCpu);
            Assert.Equal("#112233", reloaded.ReactiveLightingSolidGpu);
            Assert.Equal(ReactiveLightingProfiles.SolidId, reloaded.ReactiveLightingProfile);
        }
        finally
        {
            Delete(path);
        }
    }

    // =============================================================================================================
    // 5. The page itself.
    // =============================================================================================================

    [Fact]
    public void TheCardOffersASolidColourRowAndASplitChoice()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml"));

        // The solid row: the page's own eight swatches, the picker and a hex box, so a colour is chosen here the way it
        // is chosen everywhere else rather than through a second kind of control.
        Assert.Contains("ReactiveSolidChoices", markup, StringComparison.Ordinal);
        Assert.Contains("PickReactiveSolidColor", markup, StringComparison.Ordinal);
        Assert.Contains("ReactiveSolidColorHex", markup, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ReactiveSolidVisible}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Labels.ReactiveSolidHint", markup, StringComparison.Ordinal);

        // The two shapes, offered only while both halves are live.
        Assert.Contains("IsVisible=\"{Binding ReactiveBlendVisible}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Labels.ReactiveSplitMode", markup, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ReactiveSplitEven\"", markup, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ReactiveSplitBlend\"", markup, StringComparison.Ordinal);

        // The picker draws a swatch, which is a file dialog on the code-behind: the button that opens it is wired there.
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "LightingView.axaml.cs"));
        Assert.Contains("OnPickReactiveSolidColor", code, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // harness
    // =============================================================================================================

    /// <summary>The four zone colours of a recorded lighting write.</summary>
    private static string[] Zones(string write) => write.Split('|')[^1].Split(',');

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-solid-blend-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A test that cannot clean up is still a test that passed.
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
}
