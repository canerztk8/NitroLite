using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The mode the machine is running keeps a card even when the firmware's list omits it (a model whose service answers
/// [0,1,4,6] but sits in Turbo). Without the card the read-back finds nothing to select and the page names a mode the
/// machine is not in.
/// </summary>
public class RunningModeCardTests
{
    private static readonly OperatingMode[] WithoutTurbo =
    {
        OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Eco
    };

    [Fact]
    public async Task OnTheCharger_ARunningModeTheListOmits_GetsACard_AndNothingIsWritten()
    {
        var settings = new AppSettings();
        var hardware = Hardware(WithoutTurbo, reported: OperatingMode.Turbo);
        bool onAc = true;
        using var vm = Create(hardware, settings, () => (onAc, 80));

        await vm.InitializeAsync();

        Assert.Equal(OperatingMode.Turbo, vm.SelectedMode?.Mode);
        // Where the full set would put it. Eco is a battery card and is hidden on the charger.
        Assert.Equal(
            new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo },
            vm.Modes.Select(m => m.Mode).ToArray());
        // An observation is not a choice: nothing reached the hardware and the saved choice is untouched.
        Assert.DoesNotContain(hardware.Writes, w => w.StartsWith("mode:", StringComparison.Ordinal));
        Assert.Null(settings.LastOperatingMode);
    }

    [Fact]
    public async Task OnBattery_ARunningModeTheListOmits_GetsACard_AndLaterReadsDoNotRebuild()
    {
        // On battery the high-power group is hidden. The running mode is exempt from that, or the card is dropped
        // again by the rebuild that added it, the read-back finds none, and every later read rebuilds the list.
        var hardware = Hardware(WithoutTurbo, reported: OperatingMode.Turbo);
        using var vm = Create(hardware, new AppSettings(), () => (false, 60));

        await vm.InitializeAsync();

        Assert.Contains(vm.Modes, m => m.Mode == OperatingMode.Turbo);
        Assert.Equal(OperatingMode.Turbo, vm.SelectedMode?.Mode);

        int changes = 0;
        vm.Modes.CollectionChanged += (_, _) => changes++;

        for (int i = 0; i < 25; i++) await vm.RefreshTelemetryAsync();
        await Task.Delay(300);

        Assert.Equal(0, changes);
        Assert.Equal(OperatingMode.Turbo, vm.SelectedMode?.Mode);
    }

    [Fact]
    public async Task TheCardHeals_WhenTheUserLeavesTheModeAndTheGroupIsHidden()
    {
        // The exemption is for the mode the machine is in. Once the user picks Quiet and the source changes, an
        // unsupported high-power card must not linger on battery.
        var hardware = Hardware(WithoutTurbo, reported: OperatingMode.Turbo);
        bool onAc = true;
        using var vm = Create(hardware, new AppSettings(), () => (onAc, 80));
        await vm.InitializeAsync();
        Assert.Contains(vm.Modes, m => m.Mode == OperatingMode.Turbo);

        hardware.ReportedMode = OperatingMode.Quiet;
        vm.Modes.Single(m => m.Mode == OperatingMode.Quiet).SelectCommand.Execute(null);
        await WaitUntilAsync(() => hardware.Writes.Contains("mode:Quiet"));
        Assert.Equal(OperatingMode.Quiet, vm.SelectedMode?.Mode);

        onAc = false;
        await vm.RefreshTelemetryAsync();

        Assert.DoesNotContain(vm.Modes, m => m.Mode == OperatingMode.Turbo);
        Assert.Equal(OperatingMode.Quiet, vm.SelectedMode?.Mode);
    }

    [Fact]
    public async Task TheSelectedMode_StaysACard_WhenALaterCapabilityReadOmitsIt()
    {
        var hardware = Hardware(new[]
        {
            OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo
        }, reported: OperatingMode.Turbo);
        using var vm = Create(hardware, new AppSettings(), () => (true, 80));
        await vm.InitializeAsync();
        Assert.Equal(OperatingMode.Turbo, vm.SelectedMode?.Mode);

        // The service now lists no Turbo, and the read-back has nothing to say.
        hardware.Capabilities = hardware.Capabilities with { SupportedOperatingModes = WithoutTurbo };
        hardware.ReportedMode = null;
        await vm.OnBackendCapabilitiesChangedAsync();

        Assert.Contains(vm.Modes, m => m.Mode == OperatingMode.Turbo);
        Assert.Equal(OperatingMode.Turbo, vm.SelectedMode?.Mode);
    }

    [Fact]
    public async Task AGameTarget_StaysSelectable_WhenTheRebuiltListOmitsIt()
    {
        var hardware = Hardware(new[]
        {
            OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo
        }, reported: OperatingMode.Quiet);
        using var vm = Create(hardware, new AppSettings(), () => (true, 80));
        await vm.InitializeAsync();

        vm.NewGameMode = vm.GameModeChoices.Single(m => m.Mode == OperatingMode.Turbo);

        hardware.Capabilities = hardware.Capabilities with { SupportedOperatingModes = WithoutTurbo };
        await vm.OnBackendCapabilitiesChangedAsync();

        Assert.Equal(OperatingMode.Turbo, vm.NewGameMode?.Mode);
        Assert.Contains(vm.GameModeChoices, m => m.Mode == OperatingMode.Turbo);
    }

    // ---------------------------------------------------------------- helpers

    private static Phase3Fakes.StubHardware Hardware(OperatingMode[] supported, OperatingMode? reported)
    {
        var hardware = new Phase3Fakes.StubHardware { ReportedMode = reported };
        hardware.Capabilities = hardware.Capabilities with { SupportedOperatingModes = supported };
        return hardware;
    }

    private static MainViewModel Create(
        Phase3Fakes.StubHardware hardware, AppSettings settings, Func<(bool OnAc, int? BatteryPercent)> powerState) =>
        new(settings, hardware, null,
            Path.Combine(Path.GetTempPath(), "nitrolite-runmode-" + Guid.NewGuid().ToString("N"), "settings.json"),
            powerState: powerState);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
    }
}
