using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The capability probe runs on the thread pool, so the window is up before the answer is in.
/// </summary>
/// <remarks>
/// These tests pin the three things that make that safe: the controls stay busy until the probe lands, the first
/// hardware read waits for it instead of blocking the UI thread, and a probe that fails still releases the window. The
/// fan curve is the one that matters most — it writes to the firmware, so it must not start against a capability list
/// nobody has read yet.
/// </remarks>
public class ProbeReadinessTests
{
    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-probe-" + Guid.NewGuid().ToString("N") + ".json");

    private static MainViewModel CreateViewModel(IHardwareController hardware, Task? probeTask) =>
        new(
            new AppSettings(),
            hardware,
            platform: null,
            TempSettingsPath(),
            powerState: () => (true, 80),
            dialogs: new Phase3Fakes.FakeDialogs(),
            batteryHealth: new Phase3Fakes.FakeBattery(),
            storageHealth: new Phase3Fakes.FakeStorage(),
            debloat: new Phase3Fakes.FakeDebloat(),
            systemTools: new Phase3Fakes.FakeSystemTools(),
            updates: new Phase3Fakes.FakeUpdates(),
            diagnostics: new Phase3Fakes.FakeDiagnostics(),
            runAtStartup: new Phase3Fakes.FakeRunAtStartup(),
            probeTask: probeTask);

    [Fact]
    public async Task InitializeAsync_WaitsForTheProbeBeforeTheFirstHardwareRead()
    {
        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hardware = new Phase3Fakes.StubHardware();
        using var vm = CreateViewModel(hardware, probe.Task);

        var init = vm.InitializeAsync();

        // The probe has not answered, so the window is still busy and the first read has not run. The page binds its
        // IsEnabled to IsHardwareReady, so both halves of the pair are asserted.
        Assert.True(vm.IsHardwareBusy);
        Assert.False(vm.IsHardwareReady);
        Assert.False(init.IsCompleted);

        probe.SetResult();
        await init;

        Assert.False(vm.IsHardwareBusy);
        Assert.True(vm.IsHardwareReady);
    }

    [Fact]
    public async Task InitializeAsync_ReleasesTheControlsWhenTheProbeFails()
    {
        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hardware = new Phase3Fakes.StubHardware();
        using var vm = CreateViewModel(hardware, probe.Task);

        var init = vm.InitializeAsync();
        probe.SetException(new InvalidOperationException("the WMI repository is damaged"));

        await init;

        // A failed probe is not a reason to leave the window read-only for the rest of the session.
        Assert.False(vm.IsHardwareBusy);
    }

    [Fact]
    public async Task InitializeAsync_WithoutAProbeTask_IsReadyImmediately()
    {
        using var hardware = new Phase3Fakes.StubHardware();
        using var vm = CreateViewModel(hardware, probeTask: null);

        await vm.InitializeAsync();

        Assert.False(vm.IsHardwareBusy);
    }

    [Fact]
    public async Task FanCurve_DoesNotRunUntilTheProbeHasAnswered()
    {
        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hardware = new Phase3Fakes.StubHardware { ReportedFanMode = FanMode.Custom };
        using var vm = CreateViewModel(hardware, probe.Task);

        // Choosing custom mode is what starts the loop, and the probe has not answered yet. A write before the
        // capability list is in is a write against a guess, so the loop has to stay stopped.
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        Assert.False(vm.FanCurveRunning);

        var init = vm.InitializeAsync();
        Assert.False(vm.FanCurveRunning);

        probe.SetResult();
        await init;

        Assert.True(vm.FanCurveRunning);
    }
}
