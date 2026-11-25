using System.IO;
using System.Net.Sockets;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The group D clean-up of the Linux backend and its packaging: a rescan that really probes again, a helper first
/// answer that cannot hold the start-up, the helper's fan ownership, the narrow Nitro key rule, the backlight choice,
/// the OpenRGB brightness, and the install/uninstall scripts. No test reaches real hardware: every tree is in memory
/// and every helper runs on a temporary socket.
/// </summary>
public class CleanupDLinuxTests
{
    private const string Hwmon = "/sys/class/hwmon/hwmon4";
    private const string Pwm1 = Hwmon + "/pwm1";
    private const string Pwm1Enable = Hwmon + "/pwm1_enable";
    private const string Pwm2 = Hwmon + "/pwm2";
    private const string Pwm2Enable = Hwmon + "/pwm2_enable";

    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Packaging(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "linux", name));

    /// <summary>An Acer with the platform profile and the kernel's two-fan control, as root sees it (all writable).</summary>
    private static InMemorySysfsFileSystem TwoFanMachine()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile("/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced performance\n");
        fs.WithDirectory(Hwmon);
        fs.WithFile(Hwmon + "/name", "acer\n");
        fs.WithFile(Hwmon + "/fan1_input", "2400\n");
        fs.WithFile(Pwm1, "128\n");
        fs.WithFile(Pwm1Enable, "2\n");
        fs.WithFile(Pwm2, "128\n");
        fs.WithFile(Pwm2Enable, "2\n");
        return fs;
    }

    private static LinuxHelperPolicy Policy(ISysfsFileSystem fs) =>
        LinuxHelperPolicy.For(new LinuxCapabilityProbe(fs, Catalog()).Probe(), fs);

    private static (Socket Listener, string Path) Listen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nl-d-{Guid.NewGuid():N}.sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(16);
        return (listener, path);
    }

    private sealed class Gate : ICallerAuthorizer
    {
        private int _allow = 1;
        private int _calls;

        public bool Allow
        {
            get => Volatile.Read(ref _allow) == 1;
            set => Volatile.Write(ref _allow, value ? 1 : 0);
        }

        public int DelayFirstMs { get; init; }

        public bool IsAuthorized(Socket client)
        {
            if (Interlocked.Increment(ref _calls) == 1 && DelayFirstMs > 0) Thread.Sleep(DelayFirstMs);
            return Allow;
        }
    }

    // ---------------------------------------------------------------- 1. rescan really probes again

    [Fact]
    public void Rescan_ProbesAgain_AndFindsAModuleInstalledAfterStartUp()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile("/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced performance\n");

        using var controller = LinuxSysfsHardwareController.Create(new AppSettings(), fs, Catalog(), new OpenRgbClient(port: 1));
        Assert.False(controller.Capabilities.Hardware.FanControl);

        // Linuwu-Sense is installed while the app runs.
        var sense = LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, LinuxPaths.NitroSense);
        fs.WithDirectory(sense);
        fs.WithFile(LinuxPaths.Combine(sense, LinuxPaths.FanSpeed), "0,0\n");

        var changed = 0;
        controller.CapabilitiesChanged += (_, _) => changed++;
        ((IRescannableBackend)controller).ResetReadBackCounters();

        Assert.True(controller.Capabilities.Hardware.FanControl);
        Assert.True(((IRescannableBackend)controller).Snapshot is LinuxCapabilities { Hardware.FanControl: true });
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Rescan_ClearsAPermissionRefusalFromBeforeIt()
    {
        var fs = TwoFanMachine();
        fs.DenyWrite(LinuxPaths.PlatformProfile);
        using var controller = LinuxSysfsHardwareController.Create(new AppSettings(), fs, Catalog(), new OpenRgbClient(port: 1));

        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Turbo));
        Assert.True(controller.PermissionHintRaised);
        Assert.Equal(LinuxWriteDenial.UdevRuleMissing, controller.LastDenial);

        ((IRescannableBackend)controller).ResetReadBackCounters();
        Assert.False(controller.PermissionHintRaised);
        Assert.Equal(LinuxWriteDenial.None, controller.LastDenial);
    }

    [Fact]
    public async Task Rescan_ResetsTheHelperClient_AndTheHelperProbesAgainOnList()
    {
        var root = TwoFanMachine();
        var (listener, socketPath) = Listen();
        using var _ = listener;
        using var server = new LinuxHelperServer(listener, Policy(root), root, new Gate(), TimeSpan.FromSeconds(30),
            refreshPolicy: () => Policy(root)) { PolicyRefreshInterval = TimeSpan.Zero };
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            using var client = new LinuxHelperClient(socketPath);
            var limiter = LinuxPaths.Combine(LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, LinuxPaths.NitroSense), "battery_limiter");
            Assert.DoesNotContain(limiter, client.Allowed);

            root.WithFile(limiter, "0\n");
            Assert.DoesNotContain(limiter, client.Allowed); // cached until the rescan

            client.Reset();
            Assert.Contains(limiter, client.Allowed);
            Assert.Equal(HelperState.Ready, client.State);
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task Denial_SaysWhichOfTheHelperCasesItIs()
    {
        var user = TwoFanMachine();
        user.DenyWrite(LinuxPaths.PlatformProfile);
        user.DenyWrite(Pwm1Enable);

        // No helper socket at all: unreachable, not "install the udev rule".
        using (var none = new LinuxHelperClient(Path.Combine(Path.GetTempPath(), $"nl-none-{Guid.NewGuid():N}.sock")))
        {
            var fs = new HelperBackedFileSystem(user, none);
            Assert.True(fs.WriteAllText(Pwm1Enable, "1\n").NeedsUdevRule);
            Assert.Equal(LinuxWriteDenial.HelperUnreachable, fs.LastDenial);
        }

        var root = TwoFanMachine();
        var gate = new Gate { Allow = false };
        var (listener, socketPath) = Listen();
        using var _ = listener;
        // The helper's own probe found no platform profile: it may write the fans, not the mode.
        var helperTree = TwoFanMachine();
        helperTree.WithoutFile(LinuxPaths.PlatformProfile);
        using var server = new LinuxHelperServer(listener, Policy(helperTree), root, gate, TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            using var client = new LinuxHelperClient(socketPath);
            var fs = new HelperBackedFileSystem(user, client);

            Assert.True(fs.WriteAllText(Pwm1Enable, "1\n").NeedsUdevRule);
            Assert.Equal(LinuxWriteDenial.HelperNotAuthorized, fs.LastDenial);

            gate.Allow = true;
            client.Reset();
            Assert.True(fs.WriteAllText(Pwm1Enable, "2\n").Ok);
            Assert.Equal(LinuxWriteDenial.None, fs.LastDenial);

            Assert.True(fs.WriteAllText(LinuxPaths.PlatformProfile, "quiet\n").NeedsUdevRule);
            Assert.Equal(LinuxWriteDenial.NotInHelperList, fs.LastDenial);
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    // ---------------------------------------------------------------- 2. the first helper answer is short

    [Fact]
    public async Task HelperClient_ASlowFirstAnswer_DoesNotHoldTheProbe_AndTheRetryReportsTheChange()
    {
        var root = TwoFanMachine();
        var (listener, socketPath) = Listen();
        using var _ = listener;
        using var server = new LinuxHelperServer(listener, Policy(root), root, new Gate { DelayFirstMs = 6000 },
            TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            using var client = new LinuxHelperClient(socketPath);
            using var changed = new ManualResetEventSlim(false);
            client.StateChanged += (_, _) => changed.Set();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Empty(client.Allowed);
            watch.Stop();

            Assert.Equal(HelperState.Unreachable, client.State);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the first ask took {watch.Elapsed}");

            // The background retry asks again with the full timeout and reports the answer that arrived.
            Assert.True(await Task.Run(() => changed.Wait(TimeSpan.FromSeconds(20))), "no state change was reported");
            Assert.Equal(HelperState.Ready, client.State);
            Assert.Contains(Pwm1Enable, client.Allowed);
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    // ---------------------------------------------------------------- 3/4. fan ownership and the hand-back

    [Fact]
    public async Task Watchdog_HandsBackFullSpeed_WhenTheAppGoesAway()
    {
        var root = TwoFanMachine();
        var (listener, socketPath) = Listen();
        using var _ = listener;
        using var server = new LinuxHelperServer(listener, Policy(root), root, new Gate(), TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            var client = new LinuxHelperClient(socketPath);
            Assert.True(client.Write(Pwm1Enable, "0").Ok); // Max
            Assert.True(client.Write(Pwm2Enable, "0").Ok);

            client.Dispose(); // the app crashed in Max

            await Phase3Fakes.WaitUntilAsync(() => root.Peek(Pwm1Enable)?.Trim() == "2" && root.Peek(Pwm2Enable)?.Trim() == "2", 5000);
            Assert.Equal("2", root.Peek(Pwm1Enable)?.Trim());
            Assert.Equal("2", root.Peek(Pwm2Enable)?.Trim());
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task Watchdog_OneFanBackToAutomatic_DoesNotEndTheOwnershipOfTheOther()
    {
        var root = TwoFanMachine();
        var (listener, socketPath) = Listen();
        using var _ = listener;
        using var server = new LinuxHelperServer(listener, Policy(root), root, new Gate(), TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            var client = new LinuxHelperClient(socketPath);
            Assert.True(client.Write(Pwm1Enable, "1").Ok);
            Assert.True(client.Write(Pwm2Enable, "1").Ok);
            Assert.True(client.Write(Pwm1Enable, "2").Ok); // only the CPU fan goes back

            client.Dispose();

            await Phase3Fakes.WaitUntilAsync(() => root.Peek(Pwm2Enable)?.Trim() == "2", 5000);
            Assert.Equal("2", root.Peek(Pwm2Enable)?.Trim());
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task Server_Stopped_ByTheHost_HandsTheFansBackBeforeItReturns()
    {
        // SIGTERM/SIGINT cancel this token in LinuxHelperHost; the hand-back is the server's finally.
        var root = TwoFanMachine();
        var (listener, socketPath) = Listen();
        using var _ = listener;
        using var server = new LinuxHelperServer(listener, Policy(root), root, new Gate(), TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        using var client = new LinuxHelperClient(socketPath);
        try
        {
            Assert.True(client.Write(Pwm1Enable, "1").Ok);
            Assert.True(client.Write(Pwm1, "200").Ok);
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }

        Assert.Equal("2", root.Peek(Pwm1Enable)?.Trim());
    }

    [Fact]
    public void Policy_CountsFullSpeedAsOutOfAutomatic()
    {
        var policy = Policy(TwoFanMachine());
        Assert.Contains((Pwm1Enable, "*", "2"), policy.FanResets);
        Assert.Contains((Pwm2Enable, "*", "2"), policy.FanResets);
    }

    [Fact]
    public void HelperHost_SocketPath_IsOnlyEverRemovedWhenItIsASocket()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"nl-d-missing-{Guid.NewGuid():N}");
        Assert.Equal(LinuxHelperHost.PathKind.Missing, LinuxHelperHost.SocketPathState(missing));

        var file = Path.GetTempFileName();
        try
        {
            Assert.Equal(LinuxHelperHost.PathKind.Other, LinuxHelperHost.SocketPathState(file));
        }
        finally
        {
            File.Delete(file);
        }

        Assert.Equal(LinuxHelperHost.PathKind.Other, LinuxHelperHost.SocketPathState(Path.GetTempPath()));
    }

    [Fact]
    public void ServiceUnit_StillRunsTheBinaryFromTheTemplatedFolder()
    {
        // install.sh only substitutes a path it has checked against [A-Za-z0-9._/+-], so the line needs no quoting.
        Assert.Contains("ExecStart=@APP_DIR@/NitroLite.Desktop --linux-helper", Packaging("nitrolite-helper.service.in"));
    }

    // ---------------------------------------------------------------- 5. the Nitro key without the input group

    [Fact]
    public void GrantScript_NoLongerAddsTheInputGroup_AndTheRuleHandsOutOnlyTheHotkeyDevice()
    {
        Assert.DoesNotContain("input", LinuxPlatformSetup.GrantScript);
        Assert.DoesNotContain("input", NitroLite.Desktop.UdevGuide.InstallCommand("/tmp/r"));

        var rule = Packaging("99-nitrolite.rules");
        var input = rule.Split('\n').Where(l => !l.TrimStart().StartsWith('#') && l.Contains("SUBSYSTEM==\"input\"")).ToList();
        var line = Assert.Single(input);
        Assert.Contains($"ATTRS{{name}}==\"{LinuxNitroKeyListener.AcerDeviceName}\"", line);
        Assert.Contains("KERNEL==\"event*\"", line);
        Assert.Contains("TAG+=\"uaccess\"", line);
        Assert.Contains("RUN{builtin}+=\"uaccess\"", line);
        Assert.StartsWith("ACTION!=\"remove\"", line);
        Assert.DoesNotContain("MODE=", line);
        Assert.DoesNotContain("GROUP=", line);
        Assert.DoesNotContain("usermod -aG nitrolite,input", rule);
    }

    // ---------------------------------------------------------------- 7. predator_v4 undo and the uninstaller

    [Fact]
    public async Task PredatorV4_CanBeTurnedBackOff_ThroughOnePrompt()
    {
        var runner = new Runner();
        var fs = new InMemorySysfsFileSystem();
        var setup = new LinuxPlatformSetup(new LinuxCapabilities(), () => null, runner, fs, user: "u");

        Assert.False(setup.PredatorV4OptionInstalled);
        fs.WithFile(LinuxPlatformSetup.PredatorV4ConfPath, "options acer_wmi predator_v4=1\n");
        Assert.True(setup.PredatorV4OptionInstalled);

        var result = await setup.DisablePredatorV4Async();

        Assert.True(result.Ok);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(LinuxPlatformSetup.DisablePredatorV4Script, call[2]);
        Assert.Contains("rm -f -- " + LinuxPlatformSetup.PredatorV4ConfPath, LinuxPlatformSetup.DisablePredatorV4Script);
    }

    [Fact]
    public void Uninstall_CleansTheSystemUpWithoutTheAppFolder_AndRemovesTheModprobeOption()
    {
        var script = Packaging("uninstall.sh");
        Assert.Contains(LinuxPlatformSetup.PredatorV4ConfPath, script);
        // The missing-folder branch warns and carries on; it no longer exits before the helper and the rule go.
        Assert.Contains("it was left alone", script);
        var check = script.IndexOf("does not look like a NitroLite install", StringComparison.Ordinal);
        var helper = script.IndexOf("Removing the NitroLite helper service", StringComparison.Ordinal);
        Assert.True(check > 0 && helper > check);
        Assert.DoesNotContain("Nothing was removed", script);

        var postremove = Packaging("package-postremove.sh");
        Assert.Contains(LinuxPlatformSetup.PredatorV4ConfPath, postremove);
        Assert.Contains("purge|0)", postremove);
    }

    [Fact]
    public void Install_System_RefusesAPrefixAUserCouldReplace()
    {
        var script = Packaging("install.sh");
        Assert.Contains("realpath -m", script);
        Assert.Contains("stat -L -c '%u %a'", script);
        Assert.Contains("*[!A-Za-z0-9._/+-]*", script);
        // The check sits before anything is copied.
        Assert.True(script.IndexOf("stat -L -c", StringComparison.Ordinal) < script.IndexOf("$SUDO mkdir -p \"$APP_DIR\"", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 8. the backlight

    [Fact]
    public void Backlight_PrefersTheRawDevice_OverADeadAcpiVideoOne()
    {
        var fs = new InMemorySysfsFileSystem();
        foreach (var (name, type) in new[] { ("acpi_video0", "firmware"), ("nvidia_0", "raw") })
        {
            fs.WithDirectory("/sys/class/backlight/" + name);
            fs.WithFile($"/sys/class/backlight/{name}/brightness", "50\n");
            fs.WithFile($"/sys/class/backlight/{name}/max_brightness", "100\n");
            fs.WithFile($"/sys/class/backlight/{name}/type", type + "\n");
        }

        var service = new LinuxScreenBrightnessService(fs);

        Assert.Equal("/sys/class/backlight/nvidia_0/brightness", service.Node);
        var description = LinuxScreenBrightnessService.Describe(fs);
        Assert.Equal("nvidia_0 (raw); not used: acpi_video0 (firmware)", description);
    }

    // ---------------------------------------------------------------- 10. OpenRGB brightness and effects

    [Theory]
    [InlineData(9, 255, 128, 0)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(2, 56, 28, 0)] // level 2 is 22 %
    public void OpenRgb_ColoursAreScaledByTheBrightnessLevel(int level, byte r, byte g, byte b) =>
        Assert.Equal(new RgbColor(r, g, b), LinuxSysfsHardwareController.ScaleColor(new RgbColor(255, 128, 0), level));

    [Fact]
    public async Task OpenRgb_WritesTheScaledColours_AndFlagsThatEffectsAreNotAvailable()
    {
        using var server = new OpenRgbFakeServer();
        await server.StartAsync();
        using var client = new OpenRgbClient(OpenRgbClient.DefaultHost, server.Port, delay: (_, _) => Task.CompletedTask);
        using var controller = new LinuxSysfsHardwareController(
            new InMemorySysfsFileSystem(), new LinuxCapabilities(), new ReadBackPolicy(new AppSettings()), client);

        Assert.False(controller.SupportsLightingEffects);

        Assert.True(await controller.SetLightingAsync("STATIC", 2, 1, 1, "#FF8000", null));
        Assert.Equal(new RgbColor(56, 28, 0), server.Colors[0]);

        // An effect is written as its colours and reported as applied, so the page still saves the lighting; the flag
        // above is what tells the page not to offer effects here.
        Assert.True(await controller.SetLightingAsync("BREATH", 9, 1, 1, "#FF8000", null));
        Assert.Equal(new RgbColor(255, 128, 0), server.Colors[0]);
        Assert.False(controller.SupportsLightingEffects);
    }

    // ---------------------------------------------------------------- review round: D1/D2/D3/D6/D7

    [Fact]
    public async Task Reset_KeepsAHealthyConnection_SoAFullSpeedFanIsNotHandedBack()
    {
        var root = TwoFanMachine();
        var (listener, socketPath) = Listen();
        using var _ = listener;
        using var server = new LinuxHelperServer(listener, Policy(root), root, new Gate(), TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            using var client = new LinuxHelperClient(socketPath);
            Assert.NotEmpty(client.Allowed);
            Assert.True(client.Write(Pwm1Enable, "0").Ok); // Max through the helper

            client.Reset(); // a rescan, "Grant access", or the automatic one
            Assert.Contains(Pwm1Enable, client.Allowed);

            await Task.Delay(300);
            Assert.Equal("0", root.Peek(Pwm1Enable)?.Trim());
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task Reset_AfterTheHelperClosedTheIdleConnection_ReconnectsWithinTheFirstQuery()
    {
        var root = TwoFanMachine();
        var (listener, socketPath) = Listen();
        using var _ = listener;
        using var server = new LinuxHelperServer(listener, Policy(root), root, new Gate(), TimeSpan.FromSeconds(30),
            connectionIdleTimeout: TimeSpan.FromMilliseconds(200));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            using var client = new LinuxHelperClient(socketPath);
            Assert.NotEmpty(client.Allowed);
            await Task.Delay(800); // the helper closes the silent, non-owner connection; the client still says Ready

            var states = new List<HelperState>();
            client.StateChanged += (_, _) => states.Add(client.State);
            client.Reset(); // the kept connection is the dead one

            Assert.Contains(Pwm1Enable, client.Allowed);
            Assert.Equal(HelperState.Ready, client.State);
            Assert.Empty(states); // no Unreachable answer, so no background retry and no change to report
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    private sealed class Hang : ICallerAuthorizer
    {
        public ManualResetEventSlim Release { get; } = new(false);

        public bool IsAuthorized(Socket client)
        {
            Release.Wait(TimeSpan.FromSeconds(20));
            return true;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetAndDispose_DoNotWaitForARequestStuckOnPolkit(bool dispose)
    {
        var root = TwoFanMachine();
        var (listener, socketPath) = Listen();
        using var _ = listener;
        var hang = new Hang();
        using var server = new LinuxHelperServer(listener, Policy(root), root, hang, TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        var client = new LinuxHelperClient(socketPath);
        try
        {
            // A write whose answer waits for an authorization that does not come (the client timeout is 15 s).
            var write = Task.Run(() => client.Write(Pwm1Enable, "1"));
            await Task.Delay(500);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (dispose) client.Dispose();
            else client.Reset();
            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"{(dispose ? "Dispose" : "Reset")} took {watch.Elapsed}");

            // Closing the socket ends the stuck request too, long before its own timeout.
            Assert.Same(write, await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.False((await write).Ok);
        }
        finally
        {
            hang.Release.Set();
            client.Dispose();
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task HelperExit_LeavesAFanItNeverWroteAlone()
    {
        // The firmware (or the turbo key) put the fans at full speed; the app only listed.
        var root = TwoFanMachine();
        root.WithFile(Pwm1Enable, "0\n");
        var (listener, socketPath) = Listen();
        using var _ = listener;
        using var server = new LinuxHelperServer(listener, Policy(root), root, new Gate(), TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            using var client = new LinuxHelperClient(socketPath);
            Assert.NotEmpty(client.Allowed);
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }

        Assert.Equal("0", root.Peek(Pwm1Enable)?.Trim());
        Assert.DoesNotContain(root.Writes, w => w.Path.EndsWith("_enable", StringComparison.Ordinal));
    }

    [Fact]
    public void Backlight_TakesTheNvidiaEcBacklight_AheadOfRawDevices()
    {
        var fs = new InMemorySysfsFileSystem();
        foreach (var (name, type) in new[] { ("amdgpu_bl1", "raw"), ("nvidia_0", "raw"), (LinuxScreenBrightnessService.NvidiaWmiEcBacklight, "firmware") })
        {
            fs.WithDirectory("/sys/class/backlight/" + name);
            fs.WithFile($"/sys/class/backlight/{name}/brightness", "50\n");
            fs.WithFile($"/sys/class/backlight/{name}/max_brightness", "100\n");
            fs.WithFile($"/sys/class/backlight/{name}/type", type + "\n");
        }

        Assert.Equal("/sys/class/backlight/nvidia_wmi_ec_backlight/brightness", new LinuxScreenBrightnessService(fs).Node);
    }

    [Fact]
    public void HelperHost_SocketPath_RecognisesARealSocket_OnLinux()
    {
        if (!OperatingSystem.IsLinux()) return; // lstat is the Linux path; Windows CI runs the non-Linux branch above

        var (listener, path) = Listen();
        try
        {
            Assert.Equal(LinuxHelperHost.PathKind.Socket, LinuxHelperHost.SocketPathState(path));
        }
        finally
        {
            listener.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void HelperHost_HandlesSigtermAndSigint()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Linux", "LinuxHelperHost.cs"));
        Assert.Contains("PosixSignalRegistration.Create(PosixSignal.SIGTERM", source);
        Assert.Contains("PosixSignalRegistration.Create(PosixSignal.SIGINT", source);
        Assert.Contains("server.RunAsync(stop.Token)", source);
    }

    [Fact]
    public void Uninstall_TouchesSystemPiecesOnlyWithSystem()
    {
        var script = Packaging("uninstall.sh");
        Assert.Contains("if [ \"$MODE\" = \"system\" ] && [ -f \"$MODPROBE_CONF\" ]; then", script);
        Assert.Contains("if [ \"$MODE\" = \"system\" ] && { [ -f /etc/systemd/system/nitrolite-helper.socket ]", script);
    }

    // ---------------------------------------------------------------- helpers

    private sealed class Runner : ICommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public string? Find(string name) => name switch
        {
            "pkexec" => "/usr/bin/pkexec",
            "sh" => "/bin/sh",
            _ => null
        };

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Calls.Add(arguments);
            return new CommandResult(0, "", "");
        }
    }
}
