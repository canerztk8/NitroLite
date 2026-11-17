using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The sliders and the rail: the two controls that could be seen but not moved.
/// </summary>
/// <remarks>
/// Both failures were silent. The slider's template never told its <c>Track</c> which way it ran, so the track laid its
/// two click areas out with no size and a press on the track did nothing; and the rail's width was bound to a
/// <c>ColumnDefinition</c>, which has no data context, so the binding never resolved and the splitter had nothing to
/// write to. The tests below pin the two fixes at the level they failed at — the template and the column — and then
/// check that a moved slider actually reaches the settings file, because "it moves but forgets" was the other half of
/// the report.
/// </remarks>
public class SliderAndRailInteractionTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    private static string ReadAppXaml() => File.ReadAllText(RepoFile("NitroLite.Desktop", "App.axaml"));

    private static string ReadView(string name) =>
        File.ReadAllText(RepoFile("NitroLite.Desktop", "Views", name));

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* a leftover temp file is not a failure */ }
    }

    // ---------------------------------------------------------------- the template

    [Fact]
    public void SliderTemplate_TellsTheTrackWhichWayItRuns()
    {
        // Track's default orientation is Vertical. A vertical track arranges the two RepeatButtons with zero height, so
        // the slider has no hit area at all: neither a click nor a drag reaches it. The binding is the fix.
        var xaml = ReadAppXaml();

        Assert.Contains("Orientation=\"{TemplateBinding Orientation}\"", xaml);
        Assert.Contains("IsDirectionReversed=\"{TemplateBinding IsDirectionReversed}\"", xaml);
    }

    [Fact]
    public void SliderTemplate_GivesTheTrackButtonsTheirOwnPaint()
    {
        // The Fluent RepeatButton theme paints nothing inside this template, and a control that paints nothing cannot
        // be hit. Each button therefore carries a transparent Border of its own.
        var xaml = ReadAppXaml();

        Assert.Contains("Name=\"PART_DecreaseButton\"", xaml);
        Assert.Contains("Name=\"PART_IncreaseButton\"", xaml);
        Assert.Contains("<Border Background=\"Transparent\" />", xaml);
    }

    [Fact]
    public void SliderTheme_MatchesTheDerivedControl()
    {
        // The pages use NitroSlider, which adds the track's click area. A plain "Slider" type selector matches the
        // exact type only, so the theme would silently stop applying to every slider on every page.
        var xaml = ReadAppXaml();

        Assert.Contains("<Style Selector=\":is(Slider)\">", xaml);
        Assert.DoesNotContain("<Style Selector=\"Slider\">", xaml);
    }

    [Theory]
    [InlineData("DashboardView.axaml")]
    [InlineData("LightingView.axaml")]
    [InlineData("BatteryView.axaml")]
    public void EveryPage_UsesTheSliderThatCanBeClicked(string view)
    {
        var xaml = ReadView(view);

        Assert.DoesNotContain("<Slider ", xaml);
        Assert.Contains("<controls:NitroSlider ", xaml);
    }

    // ---------------------------------------------------------------- the rail

    [Fact]
    public void RailColumn_IsClampedAndNotBound()
    {
        // A ColumnDefinition is an AvaloniaObject with no data context, so a binding on its Width never resolves and
        // the rail falls back to the width of its own content. The width is written by the window instead, and the
        // splitter clamps against MinWidth/MaxWidth itself.
        var xaml = ReadView("MainWindow.axaml");

        Assert.Contains("x:Name=\"RailGrid\"", xaml);
        Assert.Contains($"MinWidth=\"{AppSettings.MinSidebarWidth}\"", xaml);
        Assert.Contains($"MaxWidth=\"{AppSettings.MaxSidebarWidth}\"", xaml);
        Assert.DoesNotContain("Width=\"{Binding SidebarWidth", xaml);
    }

    [Fact]
    public void RailWidth_IsPersisted()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            using var vm = new MainViewModel(new AppSettings(), null, null, path);

            vm.SidebarWidth = 250;

            Assert.Equal(250, SettingsManager.LoadSettingsWithFallback(path).SidebarWidth);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void RailWidth_IsClampedBeforeItIsStored()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            using var vm = new MainViewModel(new AppSettings(), null, null, path);

            // A drag cannot go past the column's MaxWidth, but the setter is also reachable from a hand-edited file.
            vm.SidebarWidth = 5000;

            Assert.Equal(AppSettings.MaxSidebarWidth, vm.SidebarWidth);
            Assert.Equal(AppSettings.MaxSidebarWidth, SettingsManager.LoadSettingsWithFallback(path).SidebarWidth);
        }
        finally
        {
            Delete(path);
        }
    }

    // ---------------------------------------------------------------- the sliders that write

    [Fact]
    public async Task LightingSlider_AppliesAndSaves()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            using var vm = new MainViewModel(new AppSettings(), new Phase3Fakes.StubHardware(), null, path);
            await vm.InitializeAsync();

            vm.BrightnessLevel = 8;

            // The page has no Apply button: the slider is the write, and the write is what stores the level.
            await Phase3Fakes.WaitUntilAsync(
                () => SettingsManager.LoadSettingsWithFallback(path).KeyboardLighting?.Brightness == 8);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task SpeedSlider_AppliesAndSaves()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            using var vm = new MainViewModel(new AppSettings(), new Phase3Fakes.StubHardware(), null, path);
            await vm.InitializeAsync();

            vm.SpeedLevel = 7;

            await Phase3Fakes.WaitUntilAsync(
                () => SettingsManager.LoadSettingsWithFallback(path).KeyboardLighting?.Speed == 7);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task CustomFanSliders_AreSaved()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            using var vm = new MainViewModel(new AppSettings(), new Phase3Fakes.StubHardware(), null, path);
            await vm.InitializeAsync();

            vm.CustomCpuPercent = 75;
            vm.CustomGpuPercent = 65;

            await Phase3Fakes.WaitUntilAsync(() =>
            {
                var saved = SettingsManager.LoadSettingsWithFallback(path);
                return saved.CustomFanCpuPercent == 75 && saved.CustomFanGpuPercent == 65;
            });
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void CustomFanSliders_OpenAtTheStoredSpeeds()
    {
        // The sliders used to open at their defaults whatever the file said, so the first drag wrote those defaults
        // over the user's own numbers.
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            var settings = new AppSettings { CustomFanCpuPercent = 70, CustomFanGpuPercent = 45 };
            using var vm = new MainViewModel(settings, null, null, path);

            Assert.Equal(70, vm.CustomCpuPercent);
            Assert.Equal(45, vm.CustomGpuPercent);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task FanCurveAnchors_AreSavedAsTheyMove()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            using var vm = new MainViewModel(new AppSettings(), new Phase3Fakes.StubHardware(), null, path);
            await vm.InitializeAsync();

            var anchor = vm.CurveAnchors[0];
            anchor.Percent = 55;

            await Phase3Fakes.WaitUntilAsync(() =>
                SettingsManager.LoadSettingsWithFallback(path).FanCurvePoints
                    ?.Any(p => p.TemperatureC == anchor.TemperatureC && p.Percent == 55) == true);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task FanCurveAnchors_DoNotWriteTheFileOncePerPixel()
    {
        // A drag raises a change per pixel. Each one used to be a whole settings file; the pause collapses the burst,
        // so a hundred moves leave the file holding the last shape rather than a hundred intermediate ones.
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            using var vm = new MainViewModel(new AppSettings(), new Phase3Fakes.StubHardware(), null, path);
            await vm.InitializeAsync();

            for (var percent = 30; percent <= 90; percent++)
            {
                vm.CurveAnchors[0].Percent = percent;
            }

            await Phase3Fakes.WaitUntilAsync(() =>
                SettingsManager.LoadSettingsWithFallback(path).FanCurvePoints
                    ?.Any(p => p.TemperatureC == vm.CurveAnchors[0].TemperatureC && p.Percent == 90) == true);
        }
        finally
        {
            Delete(path);
        }
    }
}
