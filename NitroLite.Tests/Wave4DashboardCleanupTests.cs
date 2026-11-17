using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using NitroLite.Windows;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The dashboard cleanup pass: the rail row that had nothing to say, the mouse controls that stayed on screen after
/// the integration was switched off, the third GPU mode, and the two controls that were replaced by the picker they
/// sat next to.
/// </summary>
public class Wave4DashboardCleanupTests
{
    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-wave4-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* a leftover temp file is not a failure */ }
    }

    /// <summary>A mouse that answers from memory, so the write can be counted without a device.</summary>
    private sealed class FakeMouse : IMouseProfileService
    {
        public bool IsSupported { get; set; } = true;

        public int CurrentProfile { get; set; } = 2;

        public List<int> Applied { get; } = new();

        public Task<bool> ApplyProfileAsync(int profile, CancellationToken cancellationToken = default)
        {
            Applied.Add(profile);
            CurrentProfile = profile;
            return Task.FromResult(true);
        }

        public Task<int?> QueryCurrentProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(CurrentProfile);
    }

    private static MainViewModel Create(AppSettings settings, string path, IMouseProfileService? mouse = null) =>
        new(settings, null, null, path, mouseProfiles: mouse);

    // ---------------------------------------------------------------- the rail's OYUN row

    [Fact]
    public async Task SystemGameRow_IsHiddenWhileNoGameRuns()
    {
        // The row used to be drawn with an em dash in it, which is a line the user has to read to learn nothing.
        var path = TempSettingsPath();
        try
        {
            using var vm = Create(new AppSettings(), path);
            await vm.InitializeAsync();

            Assert.False(vm.ShowSystemGameRow);
            Assert.Equal("—", vm.SystemGameText);
        }
        finally
        {
            Delete(path);
        }
    }

    // ---------------------------------------------------------------- the ATK integration switch

    [Fact]
    public async Task MouseProfileUi_IsHiddenWhenTheAtkIntegrationIsOff()
    {
        // The hardware answered, so the capability is there. The switch is what decides whether the controls are drawn:
        // a user who turned the integration off asked for the mouse controls to go away, not just for the writes to
        // stop. This is the bug that was reported — the per-game picker stayed on screen with the switch off.
        var path = TempSettingsPath();
        try
        {
            var mouse = new FakeMouse { IsSupported = true };

            using (var vm = Create(new AppSettings { AtkMouseIntegrationEnabled = false }, path, mouse))
            {
                await vm.InitializeAsync();

                Assert.True(vm.MouseProfileAvailable);
                Assert.False(vm.ShowMouseProfileUi);
                Assert.False(vm.ShowSystemMouseRow);
            }

            using (var vm = Create(new AppSettings { AtkMouseIntegrationEnabled = true }, path, mouse))
            {
                await vm.InitializeAsync();

                Assert.True(vm.ShowMouseProfileUi);
                Assert.True(vm.ShowSystemMouseRow);
            }
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task MouseProfileUi_DisappearsTheMomentTheSwitchIsTurnedOff()
    {
        var path = TempSettingsPath();
        try
        {
            var mouse = new FakeMouse { IsSupported = true };
            using var vm = Create(new AppSettings { AtkMouseIntegrationEnabled = true }, path, mouse);
            await vm.InitializeAsync();

            Assert.True(vm.ShowMouseProfileUi);

            vm.AtkMouseIntegrationOn = false;

            Assert.False(vm.ShowMouseProfileUi);
            Assert.False(vm.ShowSystemMouseRow);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task DefaultMouseProfile_AppliesOnSelectionWithoutAnApplyButton()
    {
        // The picker is the action. A separate Uygula button was a second click for the same write.
        var path = TempSettingsPath();
        try
        {
            var mouse = new FakeMouse { IsSupported = true };
            var settings = new AppSettings { AtkMouseIntegrationEnabled = true };
            using var vm = Create(settings, path, mouse);
            await vm.InitializeAsync();

            mouse.Applied.Clear();
            vm.SelectedMouseProfile = vm.MouseProfileOptions.Single(o => o.Index == 3);

            Assert.Contains(3, mouse.Applied);
            Assert.Equal(3, settings.DefaultAtkMouseProfile);
        }
        finally
        {
            Delete(path);
        }
    }

    // ---------------------------------------------------------------- the MUX picker

    [Fact]
    public async Task GpuModes_OffersTheThreeTheFirmwareKnows()
    {
        // NitroSense calls this "GPU Operating" and offers three. The WMI protocol encodes all three
        // (Discrete=1, Optimus=2, Automatic=3), so a two-entry list hid a mode the machine can switch to.
        var path = TempSettingsPath();
        try
        {
            using var vm = Create(new AppSettings(), path);
            await vm.InitializeAsync();

            Assert.Equal(3, vm.GpuModes.Count);
            Assert.Equal(GpuOperatingMode.Optimus, vm.GpuModes[0].Mode);
            Assert.Equal(GpuOperatingMode.DiscreteGpu, vm.GpuModes[1].Mode);
            Assert.Equal(GpuOperatingMode.Automatic, vm.GpuModes[2].Mode);
            Assert.Equal(vm.Labels.GpuAutomatic, vm.GpuModes[2].Label);
        }
        finally
        {
            Delete(path);
        }
    }

    // ---------------------------------------------------------------- the game fan curve

    [Fact]
    public async Task GameFanCurveEditor_FollowsTheFanPicker()
    {
        // The switch that used to sit above the anchors is gone. Choosing Özel Eğri is what opens the editor, so the
        // picker and the editor cannot disagree.
        var path = TempSettingsPath();
        var settings = new AppSettings
        {
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "cs2", DisplayName = "Counter-Strike 2", TargetFanMode = FanMode.Auto }
            }
        };

        try
        {
            using var vm = Create(settings, path);
            await vm.InitializeAsync();

            Assert.NotNull(vm.SelectedGameProfile);
            Assert.False(vm.ShowGameFanCurveEditor);

            vm.SelectedGameFanMode = vm.GameFanChoices.Single(f => f.Mode == FanMode.Custom);

            Assert.True(vm.ShowGameFanCurveEditor);
            Assert.True(settings.GameProfiles[0].TargetFanCurveEnabled);

            vm.SelectedGameFanMode = vm.GameFanChoices.Single(f => f.Mode == FanMode.Max);

            Assert.False(vm.ShowGameFanCurveEditor);
            Assert.False(settings.GameProfiles[0].TargetFanCurveEnabled);
        }
        finally
        {
            Delete(path);
        }
    }

    // ---------------------------------------------------------------- the Nitro key

    [Fact]
    public void NitroSenseWatcher_RetriesAPressThatLandedInsideTheCooldown()
    {
        // A press inside the cooldown used to be dropped for good: the window was remembered before the cooldown was
        // checked, so the same window matched the dedupe on every later poll and NitroSense was never closed. That is
        // the "spamming the key opens it" report. The window is still in front, so the next poll after the cooldown
        // has to act on it.
        using var watcher = new NitroSenseWatcher(
            foregroundProcessId: () => 4242,
            isNitroSense: id => id == 4242);

        int activations = 0;
        watcher.NitroSenseActivated += (_, _) => activations++;

        watcher.SuppressFor(TimeSpan.FromMilliseconds(150));
        watcher.Poll();
        Assert.Equal(0, activations);

        Thread.Sleep(300);
        watcher.Poll();

        Assert.Equal(1, activations);
    }
}
