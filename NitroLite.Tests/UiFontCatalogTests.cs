using System.IO;
using System.Linq;
using NitroLite.Core;
using NitroLite.Desktop;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The typeface picker: six faces, every one of them able to draw all eight UI languages.
/// </summary>
/// <remarks>
/// The risk this guards is not cosmetic. A face that carries only Latin leaves a Hindi user with empty boxes, and a
/// face that is not installed leaves the window in whatever the platform falls back to. Both are checked here rather
/// than by opening the app in eight languages.
/// </remarks>
public class UiFontCatalogTests
{
    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"nitrolite-font-{Guid.NewGuid():N}.json");

    [Fact]
    public void SixFacesAreOffered()
    {
        Assert.Equal(6, UiFontCatalog.All.Count);
        Assert.Equal(6, UiFontCatalog.All.Select(f => f.Id).Distinct().Count());
        Assert.Equal(6, UiFontCatalog.All.Select(f => f.DisplayName).Distinct().Count());
    }

    [Fact]
    public void TheDefaultFaceIsTheOneBundledInTheAssembly()
    {
        // The default has to be the bundled face, not a system one: it is the only choice that looks the same on
        // Windows, on Linux and on a machine that has installed nothing, and it is what the design system is drawn
        // against. A bare family name here would silently resolve to nothing, because the face is not installed.
        var face = UiFontCatalog.All[0];

        Assert.Equal(UiFontCatalog.DefaultId, face.Id);
        Assert.StartsWith(UiFontCatalog.BundledSans, face.Stack, System.StringComparison.Ordinal);
        Assert.Contains("IBM Plex Sans", face.Stack, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheBundledStackKeepsItsScriptFallbacks()
    {
        // Avalonia reads a stack that names an asset folder as a composite family: the folder is the source and every
        // name after the hash is a member of it. That is what keeps the Devanagari tail alive behind the bundled face
        // — if the hash swallowed the tail, Hindi would draw empty boxes — so the shape is asserted, not assumed.
        var family = new Avalonia.Media.FontFamily(UiFontCatalog.All[0].Stack);

        Assert.StartsWith(
            $"compositefont:{UiFontCatalog.BundledSans}",
            family.Key?.ToString(),
            System.StringComparison.Ordinal);
        Assert.Contains("IBM Plex Sans", family.FamilyNames);
        Assert.Contains("Nirmala UI", family.FamilyNames);
        Assert.Contains("Noto Sans Devanagari", family.FamilyNames);
        Assert.Contains("sans-serif", family.FamilyNames);
    }

    [Fact]
    public void EveryFaceCarriesADevanagariFallbackAndAGenericFamily()
    {
        // Hindi is the language that breaks a Latin-only stack, and the generic family is what keeps a machine without
        // any of the named faces from rendering nothing at all.
        foreach (var face in UiFontCatalog.All)
        {
            Assert.Contains("Noto Sans Devanagari", face.Stack, System.StringComparison.Ordinal);
            Assert.True(
                face.Stack.Contains("sans-serif", System.StringComparison.Ordinal)
                || face.Stack.Contains("serif", System.StringComparison.Ordinal),
                $"{face.DisplayName} has no generic family: {face.Stack}");
        }
    }

    [Fact]
    public void EveryFaceNamesAFamilyFirst()
    {
        // The first entry is the face itself; the rest is fallback. A stack that started with a generic family would
        // silently ignore the choice.
        foreach (var face in UiFontCatalog.All)
        {
            var first = face.Stack.Split(',')[0].Trim();
            Assert.False(string.IsNullOrWhiteSpace(first));
            Assert.DoesNotContain("sans-serif", first, System.StringComparison.Ordinal);
            Assert.DoesNotContain("serif", first, System.StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("comic-sans")]
    public void AnUnknownIdResolvesToTheDefault(string? id)
    {
        Assert.Equal(UiFontCatalog.DefaultId, UiFontCatalog.Resolve(id).Id);
    }

    [Fact]
    public void TheDefaultIsTheFirstFace()
    {
        Assert.Equal(UiFontCatalog.DefaultId, UiFontCatalog.All[0].Id);
    }

    [Fact]
    public void TheChoiceIsAppliedAndStored()
    {
        var path = TempSettingsPath();
        var applier = new RecordingApplier();

        using (var vm = new MainViewModel(new AppSettings(), null, null, path, themeApplier: applier))
        {
            // The default goes on at construction, before the window exists, so the first frame is already right.
            Assert.Equal(UiFontCatalog.DefaultId, vm.SelectedUiFont?.Id);
            Assert.Single(applier.Fonts);

            var georgia = vm.UiFonts.Single(f => f.Id == "georgia");
            vm.SelectedUiFont = georgia;

            Assert.Equal(georgia.Stack, applier.Fonts[^1]);
            Assert.Equal(vm.Labels.FontApplied, vm.Status);
        }

        Assert.Equal("georgia", SettingsManager.LoadSettingsWithFallback(path).UiFont);
    }

    [Fact]
    public void AStoredChoiceIsRestoredOnTheNextLaunch()
    {
        var path = TempSettingsPath();
        var applier = new RecordingApplier();

        using (var vm = new MainViewModel(new AppSettings(), null, null, path, themeApplier: applier))
        {
            vm.SelectedUiFont = vm.UiFonts.Single(f => f.Id == "verdana");
        }

        // The second launch reads the file the first one wrote, which is what a restart does.
        var stored = SettingsManager.LoadSettingsWithFallback(path);
        using (var vm = new MainViewModel(stored, null, null, path, themeApplier: applier))
        {
            Assert.Equal("verdana", vm.SelectedUiFont?.Id);
            Assert.Equal(UiFontCatalog.Resolve("verdana").Stack, applier.Fonts[^1]);
        }
    }

    [Fact]
    public void AFaceThisBuildNoLongerShipsIsDroppedOnLoad()
    {
        // A settings file from a build that offered a face this one does not must not leave the window asking for a
        // font that is not there.
        var settings = new AppSettings { UiFont = "comic-sans" };
        settings.Normalize();

        Assert.Null(settings.UiFont);
        Assert.Equal(UiFontCatalog.DefaultId, UiFontCatalog.Resolve(settings.UiFont).Id);
    }

    private sealed class RecordingApplier : IThemeApplier
    {
        public List<string> Fonts { get; } = new();

        public void Apply(AppTheme theme)
        {
        }

        public void ApplyFont(string fontStack) => Fonts.Add(fontStack);
    }
}
