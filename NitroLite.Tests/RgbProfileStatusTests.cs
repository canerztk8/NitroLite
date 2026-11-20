using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The line under the presets list: the list had no answer for its own entries beyond "Applied", and the entry that
/// is not a preset at all answered with silence — the dropdown moved, the keyboard did not, and the page looked broken.
/// </summary>
public class RgbProfileStatusTests
{
    [Fact]
    public async Task PickingAPreset_SaysWhatWasApplied()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = Create(hardware);
        await vm.InitializeAsync();

        var preset = vm.RgbProfiles.First(p => !ReferenceEquals(p, vm.CustomProfile));
        vm.SelectedRgbProfile = preset;

        await Phase3Fakes.WaitUntilAsync(() => vm.HasRgbProfileStatus, 3000);

        Assert.True(vm.HasRgbProfileStatus);
        Assert.Contains(preset.DisplayName, vm.RgbProfileStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// The custom entry is what the list shows on a fresh install, so picking it again is not a change and says
    /// nothing — the sentence belongs to the case where the user comes back from a preset, which is the only way the
    /// choice can surprise them.
    /// </summary>
    [Fact]
    public async Task ComingBackToTheCustomEntry_ExplainsWhatItMeans()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = Create(hardware);
        await vm.InitializeAsync();

        var preset = vm.RgbProfiles.First(p => !ReferenceEquals(p, vm.CustomProfile));
        vm.SelectedRgbProfile = preset;
        await Phase3Fakes.WaitUntilAsync(() => vm.HasRgbProfileStatus, 3000);

        vm.SelectedRgbProfile = vm.CustomProfile;
        await Phase3Fakes.WaitUntilAsync(() => vm.RgbProfileStatus == vm.Labels.CustomProfileStatus, 3000);

        Assert.True(vm.HasRgbProfileStatus);
        Assert.Equal(vm.Labels.CustomProfileStatus, vm.RgbProfileStatus);
    }

    /// <summary>
    /// The sentence has to exist in every language the app ships: an empty label would have left the line blank while
    /// the flags said it was visible, which is the same silence with more code.
    /// </summary>
    [Fact]
    public void TheSentenceIsTranslatedEverywhere()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Lang");
        if (!Directory.Exists(directory)) return;

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            var text = File.ReadAllText(file);
            Assert.Contains("klavyede kayıtlı bir profil değil", text, StringComparison.Ordinal);
        }
    }

    private static MainViewModel Create(Phase3Fakes.StubHardware hardware) =>
        new(new AppSettings(), hardware, null,
            Path.Combine(Path.GetTempPath(), "nitrolite-profiles-" + Guid.NewGuid().ToString("N") + ".json"),
            powerState: () => (false, 50));
}
