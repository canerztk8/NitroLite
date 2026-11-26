using System.IO;
using System.Diagnostics;
using NitroLite.Core;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// predator_v4 applied without a restart: the script text (constant, pinned PATH, no user input, valid POSIX sh), what
/// the app says for each outcome the script can end in, and the acpitz fallback for the system temperature. Nothing
/// here runs modprobe: the runner is a fake.
/// </summary>
public class LinuxPredatorV4LiveTests
{
    private const string ParamPath = "/sys/module/acer_wmi/parameters/predator_v4";

    private sealed class Runner : ICommandRunner
    {
        private readonly CommandResult _result;

        public Runner(CommandResult result) => _result = result;

        public List<IReadOnlyList<string>> Calls { get; } = new();

        public string? Find(string name) => name switch { "pkexec" => "/usr/bin/pkexec", "sh" => "/bin/sh", _ => null };

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Calls.Add(arguments);
            return _result;
        }
    }

    private static LinuxPlatformSetup Setup(CommandResult result, string? predatorParam)
    {
        var fs = new InMemorySysfsFileSystem();
        if (predatorParam != null) fs.WithFile(ParamPath, predatorParam);
        return new LinuxPlatformSetup(new LinuxCapabilities(), () => null, new Runner(result), fs, user: "u");
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    [Fact]
    public async Task Enable_Live_WhenTheModuleReloadedAndTheParameterIsOn_SaysNoRestartNeeded()
    {
        var result = await Setup(new CommandResult(0, LinuxPlatformSetup.LiveMarker, ""), "Y\n").EnablePredatorV4Async();

        Assert.True(result.Ok);
        Assert.Contains("yeniden başlatma gerekmez", result.Message);
    }

    [Fact]
    public async Task Enable_Live_AcceptsOneAsOn()
    {
        var result = await Setup(new CommandResult(0, LinuxPlatformSetup.LiveMarker, ""), "1\n").EnablePredatorV4Async();

        Assert.Contains("yeniden başlatma gerekmez", result.Message);
    }

    [Fact]
    public async Task Enable_MarkerButParameterStillOff_AsksForARestart()
    {
        var result = await Setup(new CommandResult(0, LinuxPlatformSetup.LiveMarker, ""), "N\n").EnablePredatorV4Async();

        Assert.True(result.Ok);
        Assert.Contains("yeniden başlatın", result.Message);
        Assert.True(result.Live); // the module did reload, so the old input device is gone either way
    }

    [Fact]
    public async Task Enable_ModuleInUse_NoMarker_AsksForARestart()
    {
        var result = await Setup(new CommandResult(0, "", ""), "N\n").EnablePredatorV4Async();

        Assert.True(result.Ok);
        Assert.Contains("yeniden başlatın", result.Message);
    }

    [Fact]
    public async Task Enable_ParameterOnWithoutMarker_IsNotClaimedAsLive()
    {
        // The marker is the script's word that it reloaded the module; the parameter alone could be an old reading.
        var result = await Setup(new CommandResult(0, "", ""), "Y\n").EnablePredatorV4Async();

        Assert.Contains("yeniden başlatın", result.Message);
    }

    [Fact]
    public async Task Enable_ReloadFailed_IsAFailureThatSaysEverythingWasRolledBack()
    {
        var result = await Setup(new CommandResult(LinuxPlatformSetup.ReloadFailedExitCode, "", "modprobe: boom"), "N\n")
            .EnablePredatorV4Async();

        Assert.False(result.Ok);
        Assert.Contains("eski haline", result.Message);
    }

    [Fact]
    public async Task Disable_Live_SaysNoRestartNeeded()
    {
        var result = await Setup(new CommandResult(0, LinuxPlatformSetup.LiveMarker, ""), "N\n").DisablePredatorV4Async();

        Assert.True(result.Ok);
        Assert.Contains("yeniden başlatma gerekmez", result.Message);
    }

    [Fact]
    public async Task Disable_MarkerButParameterStillOn_AsksForARestart()
    {
        var result = await Setup(new CommandResult(0, LinuxPlatformSetup.LiveMarker, ""), "Y\n").DisablePredatorV4Async();
        Assert.Contains("yeniden başlatın", result.Message);
        Assert.True(result.Live);
        Assert.Contains("yeniden başlatın", result.Message);
    }

    [Fact]
    public async Task Disable_NoMarker_AsksForARestart()
    {
        var result = await Setup(new CommandResult(0, "", ""), "Y\n").DisablePredatorV4Async();

        Assert.True(result.Ok);
        Assert.Contains("yeniden başlatın", result.Message);
    }

    [Fact]
    public void Scripts_PinPath_TakeNoArguments_AndRestoreTheModuleWhenTheReloadFails()
    {
        foreach (var script in new[] { LinuxPlatformSetup.PredatorV4Script, LinuxPlatformSetup.DisablePredatorV4Script })
        {
            Assert.Contains("PATH=/usr/sbin:/usr/bin:/sbin:/bin", script);
            Assert.DoesNotContain("$1", script);
            Assert.DoesNotContain("$@", script);
            Assert.DoesNotContain("$*", script);
            Assert.Contains("modprobe -r acer_wmi", script);
            Assert.Contains(LinuxPlatformSetup.LiveMarker, script);
        }

        var enable = LinuxPlatformSetup.PredatorV4Script;
        Assert.Contains("exit " + LinuxPlatformSetup.ReloadFailedExitCode, enable);
        // On a failed reload the option file goes and the module comes back without it, before the script ends.
        var rm = enable.IndexOf("rm -f -- " + LinuxPlatformSetup.PredatorV4ConfPath, StringComparison.Ordinal);
        var exit = enable.IndexOf("exit " + LinuxPlatformSetup.ReloadFailedExitCode, StringComparison.Ordinal);
        var restore = enable.IndexOf("modprobe acer_wmi", rm, StringComparison.Ordinal);
        Assert.True(rm > 0 && restore > rm && exit > restore);
        // The option file is written before any reload is tried.
        Assert.True(enable.IndexOf("options acer_wmi predator_v4=1", StringComparison.Ordinal) < enable.IndexOf("modprobe -r", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    public void Scripts_ArePosixShSyntax(string which)
    {
        var script = which == "enable" ? LinuxPlatformSetup.PredatorV4Script : LinuxPlatformSetup.DisablePredatorV4Script;
        var path = Path.Combine(Path.GetTempPath(), $"nl-pv4-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, script);
        try
        {
            var info = new ProcessStartInfo("sh", new[] { "-n", path }) { RedirectStandardError = true, UseShellExecute = false };
            Process? process;
            try { process = Process.Start(info); }
            catch (System.ComponentModel.Win32Exception) { return; } // no sh on this machine: CI's Linux job checks it
            Assert.NotNull(process);
            using (process)
            {
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, error);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PostInstall_TriggersTheInputSubsystemToo_SoTheNitroKeyRuleAppliesWithoutAReboot()
    {
        var script = File.ReadAllText(RepoFile("packaging", "linux", "package-postinstall.sh"));
        Assert.Contains("--subsystem-match=input", script);
        Assert.Contains("--subsystem-match=hidraw", script);
    }

    [Fact]
    public void InstallSh_UserMode_WarnsWhenThePackageIsAlreadyInstalled()
    {
        var script = File.ReadAllText(RepoFile("packaging", "linux", "install.sh"));
        Assert.Contains("[ -e \"/opt/nitrolite/$BIN_NAME\" ]", script);
        Assert.Contains("shadow", script);
    }

    [Fact]
    public void ReleaseNotes_PutThePackagesFirst_AndNameTheRealFiles()
    {
        var workflow = File.ReadAllText(RepoFile(".github", "workflows", "release.yml"));
        var deb = workflow.IndexOf("amd64.deb", StringComparison.Ordinal);
        var tar = workflow.IndexOf("-linux-x64.tar.gz${tick}", StringComparison.Ordinal);
        Assert.True(deb > 0 && tar > deb);
        Assert.Contains("x86_64.rpm", workflow);
        Assert.Contains("sudo apt install ./NitroLite-", workflow);
    }

    // ---------------------------------------------------------------- hwmon: acpitz as the system temperature

    private static LinuxHwmonDevice Device(string name, params int[] temps) =>
        new("/sys/class/hwmon/" + name, name, temps, Array.Empty<int>());

    [Fact]
    public void Sample_WithoutAcer_TakesTheSystemTemperatureFromAcpitz()
    {
        var sample = LinuxHwmon.Sample(new[] { Device("acpitz", 49), Device("k10temp", 58), Device("amdgpu", 51) });

        Assert.Equal(58, sample.CpuTemperatureC);
        Assert.Equal(49, sample.SysTemperatureC);
    }

    [Fact]
    public void Sample_WithAcer_StillUsesTheAcerBoardSensor()
    {
        var sample = LinuxHwmon.Sample(new[] { Device("acer", 44), Device("acpitz", 49), Device("k10temp", 58) });

        Assert.Equal(44, sample.SysTemperatureC);
    }

    [Fact]
    public void Sample_AcpitzWithoutADieSensor_IsNotInventedAsASystemReading()
    {
        var sample = LinuxHwmon.Sample(new[] { Device("acpitz", 49) });

        Assert.Null(sample.SysTemperatureC);
    }

    // ---------------------------------------------------------------- round 2: driver missing, Live flag, Nitro key, hwmon

    [Fact]
    public async Task Enable_AndDisable_DriverCouldNotBeLoadedBack_TellsTheUserToRestart_NotThatEverythingWasRestored()
    {
        var enable = await Setup(new CommandResult(LinuxPlatformSetup.DriverMissingExitCode, "", ""), "N\n").EnablePredatorV4Async();
        var disable = await Setup(new CommandResult(LinuxPlatformSetup.DriverMissingExitCode, "", ""), "N\n").DisablePredatorV4Async();

        foreach (var result in new[] { enable, disable })
        {
            Assert.False(result.Ok);
            Assert.True(result.Live); // the module was unloaded, so the old input device is gone: the app must reprobe
            Assert.Contains("yeniden başlatın", result.Message);
            Assert.DoesNotContain("eski haline", result.Message);
        }
    }

    [Fact]
    public void Scripts_PinTheExactReloadAndRestoreLines()
    {
        var enable = LinuxPlatformSetup.PredatorV4Script;
        var disable = LinuxPlatformSetup.DisablePredatorV4Script;

        Assert.Contains("modprobe -r acer_wmi || exit 0\n", enable);
        Assert.Contains("modprobe -r acer_wmi || exit 0\n", disable);
        // The restore after a failed reload: if it fails too, the driver is missing (11), not "restored" (10).
        Assert.Contains("rm -f -- " + LinuxPlatformSetup.PredatorV4ConfPath + "\nmodprobe acer_wmi || exit 11\n"
            + "udevadm settle --timeout=5 2>/dev/null || true; echo " + LinuxPlatformSetup.LiveMarker + "\nexit 10\n", enable);
        Assert.EndsWith("modprobe acer_wmi || exit 11\nudevadm settle --timeout=5 2>/dev/null || true; echo "
            + LinuxPlatformSetup.LiveMarker + "\n", disable);
        // udev gets to finish its chgrp rules before the script says the module is live.
        foreach (var script in new[] { enable, disable })
        {
            Assert.Contains("if modprobe acer_wmi; then udevadm settle --timeout=5 2>/dev/null || true; echo "
                + LinuxPlatformSetup.LiveMarker + "; exit 0; fi\n", script);
        }
    }

    [Fact]
    public async Task Enable_ReloadFailedButRestored_IsAFailureThatIsStillLive()
    {
        // Exit 10: the option was taken back out and the module loaded again without it. The marker is printed before
        // the exit, the step is a failure, and the input device was still replaced.
        var result = await Setup(new CommandResult(LinuxPlatformSetup.ReloadFailedExitCode, LinuxPlatformSetup.LiveMarker, ""), "N\n")
            .EnablePredatorV4Async();

        Assert.False(result.Ok);
        Assert.True(result.Live);
        Assert.Contains("eski haline", result.Message);
    }

    [Fact]
    public async Task Live_IsSetOnlyWhenTheScriptReloadedTheModule()
    {
        var live = await Setup(new CommandResult(0, LinuxPlatformSetup.LiveMarker, ""), "Y\n").EnablePredatorV4Async();
        var notLive = await Setup(new CommandResult(0, "", ""), "N\n").EnablePredatorV4Async();
        var disableLive = await Setup(new CommandResult(0, LinuxPlatformSetup.LiveMarker, ""), "N\n").DisablePredatorV4Async();

        Assert.True(live.Live);
        Assert.False(notLive.Live);
        Assert.True(disableLive.Live);
    }

    private sealed class FakeKey : INitroKeyListener
    {
        public List<string> Calls { get; } = new();

        public bool IsSupported => true;

        public event EventHandler? NitroKeyPressed { add { } remove { } }

        public void Start() { lock (Calls) Calls.Add("start"); }

        public void Stop() { lock (Calls) Calls.Add("stop"); }

        public void Dispose() { }
    }

    private sealed class LiveSetup : IPlatformSetup
    {
        public bool CanElevate => true;
        public bool NeedsInputGroup => false;
        public KernelSetupHint KernelHint => default;
        public string? ModelNote => null;
        public string CaptureSummary => "";
        public bool Live { get; init; } = true;
        public Task<PlatformSetupResult> GrantAccessAsync() => Task.FromResult(new PlatformSetupResult(true, "ok"));
        public Task<PlatformSetupResult> EnablePredatorV4Async() => Task.FromResult(new PlatformSetupResult(true, "ok", Live));
        public Task<string?> WriteCaptureAsync(string directory) => Task.FromResult<string?>(null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    private static string TempSettings() => Path.Combine(Path.GetTempPath(), $"nl_pv4_{Guid.NewGuid():N}.json");

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ALiveReload_RestartsTheNitroKeyListener_OnlyWhenEnabled(bool enabled, bool live)
    {
        var key = new FakeKey();
        using var vm = new NitroLite.Desktop.ViewModels.MainViewModel(
            new AppSettings { NitroKeyEnabled = enabled }, null, null, TempSettings(),
            nitroKey: key, platformSetup: new LiveSetup { Live = live });

        vm.EnablePredatorV4Command.Execute(null);

        if (!live)
        {
            await Task.Delay(300);
            Assert.Empty(key.Calls);
            return;
        }

        await WaitUntilAsync(() => { lock (key.Calls) return key.Calls.Count >= (enabled ? 2 : 1); });
        await Task.Delay(100);
        lock (key.Calls) Assert.Equal(enabled ? new[] { "stop", "start" } : new[] { "stop" }, key.Calls.ToArray());
    }

    [Fact]
    public async Task ADisableThatReloadedTheModule_RestartsTheNitroKeyListenerToo()
    {
        var key = new FakeKey();
        using var vm = new NitroLite.Desktop.ViewModels.MainViewModel(
            new AppSettings { NitroKeyEnabled = true }, null, null, TempSettings(), nitroKey: key)
        {
            PlatformExtras = new NitroLite.Desktop.ViewModels.PlatformExtras
            {
                DisablePredatorV4 = () => Task.FromResult(new PlatformSetupResult(true, "off", Live: true))
            }
        };

        vm.DisablePredatorV4Command.Execute(null);

        await WaitUntilAsync(() => { lock (key.Calls) return key.Calls.Count >= 2; });
        lock (key.Calls) Assert.Equal(new[] { "stop", "start" }, key.Calls.ToArray());
    }

    private sealed class DyingThenGoodDevice : IEvdevDevice
    {
        private readonly bool _dies;
        private int _sent;

        public DyingThenGoodDevice(bool dies) => _dies = dies;

        public string Name => "Acer WMI hotkeys";
        public int EventSize => 24;
        public bool SupportsKey(int code) => true;

        public int Read(Span<byte> buffer)
        {
            if (_dies) throw new IOException("ENODEV");
            if (_sent++ > 0) return 0;

            // EV_KEY, KEY_PROG1 (148), value 1, at the offset a 64-bit input_event uses.
            buffer.Slice(0, 24).Clear();
            BitConverter.GetBytes((ushort)1).CopyTo(buffer.Slice(16));
            BitConverter.GetBytes((ushort)148).CopyTo(buffer.Slice(18));
            BitConverter.GetBytes(1).CopyTo(buffer.Slice(20));
            return 24;
        }

        public void Dispose() { }
    }

    private sealed class SequenceSource : IEvdevSource
    {
        private int _calls;

        public IReadOnlyList<IEvdevDevice> OpenAll() => new IEvdevDevice[] { new DyingThenGoodDevice(dies: _calls++ == 0) };
    }

    [Fact]
    public async Task NitroKeyListener_AfterTheDeviceDiesAndIsRestarted_ReadsTheNewDevice()
    {
        using var listener = new LinuxNitroKeyListener(new SequenceSource());
        var pressed = new TaskCompletionSource();
        listener.NitroKeyPressed += (_, _) => pressed.TrySetResult();

        Assert.True(listener.IsSupported);
        listener.Start();
        await Task.Delay(200);
        Assert.False(pressed.Task.IsCompleted);

        listener.Stop();
        listener.Start();

        Assert.Same(pressed.Task, await Task.WhenAny(pressed.Task, Task.Delay(3000)));
    }

    [Fact]
    public void Sample_FanOnlyAcer_StillGetsTheSystemTemperatureFromAcpitz()
    {
        var fanOnly = new LinuxHwmonDevice("/sys/class/hwmon/acer", "acer", Array.Empty<int>(), new[] { 2000 });
        var sample = LinuxHwmon.Sample(new[] { fanOnly, Device("acpitz", 49), Device("k10temp", 58) });

        Assert.Equal(49, sample.SysTemperatureC);
        Assert.Equal(2000, sample.CpuFanRpm);
    }

    // ---------------------------------------------------------------- round 3: re-probing after a reload

    private sealed class TrackedDevice : IEvdevDevice
    {
        private int _sent;

        public bool Disposed { get; private set; }

        public string Name => "Acer WMI hotkeys";
        public int EventSize => 24;
        public bool SupportsKey(int code) => true;

        public int Read(Span<byte> buffer)
        {
            if (Disposed) return 0;
            if (_sent++ > 0) return 0;

            buffer.Slice(0, 24).Clear();
            BitConverter.GetBytes((ushort)1).CopyTo(buffer.Slice(16));
            BitConverter.GetBytes((ushort)148).CopyTo(buffer.Slice(18));
            BitConverter.GetBytes(1).CopyTo(buffer.Slice(20));
            return 24;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class CountingSource : IEvdevSource
    {
        private readonly bool _emptyFirst;

        public CountingSource(bool emptyFirst) => _emptyFirst = emptyFirst;

        public int Opens { get; private set; }

        public List<TrackedDevice> Devices { get; } = new();

        public IReadOnlyList<IEvdevDevice> OpenAll()
        {
            Opens++;
            if (_emptyFirst && Opens == 1) return Array.Empty<IEvdevDevice>();

            var device = new TrackedDevice();
            Devices.Add(device);
            return new IEvdevDevice[] { device };
        }
    }

    [Fact]
    public async Task NitroKeyListener_NoDeviceAtFirst_StopThenStart_ProbesAgainAndHearsTheKey()
    {
        var source = new CountingSource(emptyFirst: true);
        using var listener = new LinuxNitroKeyListener(source);
        var pressed = new TaskCompletionSource();
        listener.NitroKeyPressed += (_, _) => pressed.TrySetResult();

        Assert.False(listener.IsSupported);
        listener.Start();
        listener.Stop();
        listener.Start();

        Assert.True(listener.IsSupported);
        Assert.Same(pressed.Task, await Task.WhenAny(pressed.Task, Task.Delay(3000)));
    }

    [Fact]
    public void NitroKeyListener_StopWithoutStart_ClosesTheProbedDevice_AndTheNextProbeOpensAgain()
    {
        var source = new CountingSource(emptyFirst: false);
        using var listener = new LinuxNitroKeyListener(source);

        Assert.True(listener.IsSupported);
        Assert.Equal(1, source.Opens);

        listener.Stop();

        Assert.True(source.Devices[0].Disposed);
        Assert.True(listener.IsSupported);
        Assert.Equal(2, source.Opens);
    }

    [Fact]
    public async Task ALiveReload_RaisesNitroKeyAvailableAgain()
    {
        var key = new FakeKey();
        using var vm = new NitroLite.Desktop.ViewModels.MainViewModel(
            new AppSettings { NitroKeyEnabled = true }, null, null, TempSettings(), nitroKey: key,
            platformSetup: new LiveSetup());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => { lock (raised) raised.Add(e.PropertyName); };

        vm.EnablePredatorV4Command.Execute(null);

        await WaitUntilAsync(() => { lock (raised) return raised.Contains(nameof(vm.NitroKeyAvailable)); });
    }

    [Fact]
    public async Task NitroKeyListener_StartAndStopFromTwoThreads_NeverLeaveAReaderOnAClosedDevice()
    {
        var source = new CountingSource(emptyFirst: false);
        using var listener = new LinuxNitroKeyListener(source);

        var starter = Task.Run(() => { for (var i = 0; i < 100; i++) listener.Start(); });
        var stopper = Task.Run(() => { for (var i = 0; i < 100; i++) listener.Stop(); });
        await Task.WhenAll(starter, stopper);

        // Whatever order the calls took, the listener is in one coherent state: a final Stop then Start hears the key.
        listener.Stop();
        var pressed = new TaskCompletionSource();
        listener.NitroKeyPressed += (_, _) => pressed.TrySetResult();
        listener.Start();

        Assert.Same(pressed.Task, await Task.WhenAny(pressed.Task, Task.Delay(3000)));
    }
}
