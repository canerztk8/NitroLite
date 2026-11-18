using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The capability probe runs on the thread pool while the window is already open, so the interesting cases are the
/// ones where the two do not line up: a probe that is slower than the user, a window that closes before the answer
/// arrives, and two initialisations racing each other.
/// </summary>
/// <remarks>
/// <para>
/// These are stress tests rather than unit tests in the narrow sense: several of them run the same scenario hundreds
/// of times with the probe landing at a different point each time, because a race that only shows up one run in fifty
/// is exactly the race that ships. Nothing here sleeps to make a race happen — the probe is a
/// <see cref="TaskCompletionSource"/> the test completes by hand, so every interleaving is chosen rather than hoped
/// for.
/// </para>
/// <para>
/// The invariant under test is the one the fan curve depends on: no hardware write, and no read of a service the
/// composition root has already disposed, before the probe has answered.
/// </para>
/// </remarks>
public class ProbeStressTests
{
    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-probe-stress-" + Guid.NewGuid().ToString("N") + ".json");

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
    public async Task InitializeAsync_ClosedWhileTheProbeIsRunning_DoesNotTouchTheDisposedServices()
    {
        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hardware = new Phase3Fakes.StubHardware();
        var vm = CreateViewModel(hardware, probe.Task);

        var init = vm.InitializeAsync();

        // The window closes before the probe answers. This is the fast open-and-close on a machine whose WMI
        // repository is damaged: the probe is the one thing that can still be running minutes later.
        vm.Dispose();
        probe.SetResult();

        // The continuation must not throw, and it must not have gone on to read the backend the composition root
        // disposes with the window.
        await init;
        Assert.Equal(0, hardware.ConnectCount);
        Assert.False(vm.IsHardwareReady);
    }

    [Fact]
    public async Task InitializeAsync_ClosedWhileTheProbeIsRunning_DoesNotStartTheFanCurve()
    {
        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hardware = new Phase3Fakes.StubHardware { ReportedFanMode = FanMode.Custom };
        var vm = CreateViewModel(hardware, probe.Task);

        // Custom mode is what starts the loop, and the machine is already in it when the window opens.
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);

        var init = vm.InitializeAsync();
        vm.Dispose();
        probe.SetResult();
        await init;

        // The loop was disposed with the window. Starting it again from a continuation that outlived the window would
        // be a write to the firmware from a view model nobody is watching.
        Assert.False(vm.FanCurveRunning);
        Assert.Empty(hardware.Writes);
    }

    [Fact]
    public async Task InitializeAsync_CalledTwiceConcurrently_RunsTheFirstReadOnce()
    {
        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hardware = new Phase3Fakes.StubHardware();
        using var vm = CreateViewModel(hardware, probe.Task);

        // The shell calls InitializeAsync when the window opens; a second caller is the tray restoring the window, or
        // a test that forgot it had already started. Both must end up with the same single first read.
        var first = vm.InitializeAsync();
        var second = vm.InitializeAsync();

        probe.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, hardware.ConnectCount);
        Assert.True(vm.IsHardwareReady);
    }

    [Fact]
    public async Task InitializeAsync_WithASlowProbe_ReturnsWithoutBlockingTheCaller()
    {
        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hardware = new Phase3Fakes.StubHardware();
        using var vm = CreateViewModel(hardware, probe.Task);

        // The probe is still running. Calling InitializeAsync must hand back a task immediately rather than block the
        // thread that called it — that thread is the UI thread, and blocking it is the bug E exists to prevent.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var init = vm.InitializeAsync();
        started.Stop();

        Assert.False(init.IsCompleted);
        Assert.True(started.ElapsedMilliseconds < 500, $"InitializeAsync blocked for {started.ElapsedMilliseconds} ms");

        // The window is still usable while it waits: the banner binds to IsHardwareBusy, and reading the rest of the
        // view model must not deadlock on the probe.
        Assert.True(vm.IsHardwareBusy);
        Assert.NotNull(vm.FanModes);
        Assert.NotNull(vm.Modes);

        probe.SetResult();
        await init;
        Assert.True(vm.IsHardwareReady);
    }

    [Fact]
    public async Task InitializeAsync_ProbeThatNeverAnswers_KeepsTheControlsBusyWithoutThrowing()
    {
        // A probe that never completes models the worst WMI repository: the window has to stay open and explain
        // itself rather than crash or silently enable controls against a capability list nobody has read.
        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hardware = new Phase3Fakes.StubHardware();
        using var vm = CreateViewModel(hardware, probe.Task);

        var init = vm.InitializeAsync();

        Assert.True(vm.IsHardwareBusy);
        Assert.False(vm.IsHardwareReady);
        Assert.False(init.IsCompleted);

        // Nothing was written and nothing was read from the backend while the probe is outstanding.
        Assert.Equal(0, hardware.ConnectCount);
        Assert.Empty(hardware.Writes);

        probe.SetResult();
        await init;
    }

    [Fact]
    public async Task InitializeAsync_StressOpenAndCloseAtEveryProbeStage_LeavesNoUnobservedFailure()
    {
        // The probe lands at a different point in every iteration: before the window opens, while it is opening, and
        // after it has been closed. A race that only shows up one run in fifty is the race that ships, so the loop is
        // long enough to hit each of those windows many times.
        for (var i = 0; i < 200; i++)
        {
            var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var hardware = new Phase3Fakes.StubHardware();
            var vm = CreateViewModel(hardware, probe.Task);

            var init = vm.InitializeAsync();

            switch (i % 4)
            {
                case 0:
                    // Closed before the probe answers.
                    vm.Dispose();
                    probe.SetResult();
                    break;

                case 1:
                    // Probe answers, then the window closes immediately.
                    probe.SetResult();
                    vm.Dispose();
                    break;

                case 2:
                    // Probe answers and the window stays open.
                    probe.SetResult();
                    break;

                default:
                    // Probe fails, then the window closes.
                    probe.SetException(new TimeoutException("the WMI repository did not answer"));
                    vm.Dispose();
                    break;
            }

            // Awaiting is the assertion: an exception escaping the continuation would surface here, and an
            // ObjectDisposedException from a disposed service is exactly what this loop is looking for.
            await init;
            vm.Dispose();
        }
    }

    [Fact]
    public async Task InitializeAsync_ProbeThatFails_StillLetsTheWindowReadTheBackend()
    {
        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hardware = new Phase3Fakes.StubHardware();
        using var vm = CreateViewModel(hardware, probe.Task);

        var init = vm.InitializeAsync();
        probe.SetException(new TimeoutException("the WMI repository did not answer"));
        await init;

        // A failed probe is a machine whose capabilities could not be read, not a machine with no hardware. The
        // backend still has to be read, or the window would show nothing at all.
        Assert.True(vm.IsHardwareReady);
        Assert.Equal(1, hardware.ConnectCount);
    }

    [Fact]
    public async Task InitializeAsync_BackendWithNoSensors_FallsBackInsteadOfThrowing()
    {
        // A machine whose firmware answers no sensors at all: every reading is zero, which the page renders as "--".
        // The point of the test is that the first read completes rather than throwing on a null sample.
        using var hardware = new Phase3Fakes.StubHardware
        {
            Sample = new MonitorData
            {
                CpuTemperature = 0,
                GpuTemperature = 0,
                SysTemperature = 0,
                CpuFanSpeed = 0,
                GpuFanSpeed = 0,
                IsFanRpmMeasured = false
            }
        };
        using var vm = CreateViewModel(hardware, probeTask: null);

        await vm.InitializeAsync();

        Assert.True(vm.IsHardwareReady);
        Assert.Equal(1, hardware.ConnectCount);
    }
}
