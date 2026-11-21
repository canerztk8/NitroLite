using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Lighting;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The Linux paths that work with nothing installed: the operating mode through power-profiles-daemon, the KB5130
/// keyboard through hidraw, the NVIDIA GPU through nvidia-smi, and the one-prompt setup. Also the permission rule that
/// used to switch the operating mode off for good on a machine without the udev rule.
/// </summary>
/// <remarks>
/// The fixture is the AN16-41 as a stock Ubuntu install reported it on 2026-11-21: acer-wmi loaded without the
/// PredatorSense v4 quirk, a read-only platform_profile, no Linuwu-Sense, no Acer hwmon device.
/// </remarks>
public class LinuxZeroInstallTests
{
    // ---------------------------------------------------------------- fixtures

    private static InMemorySysfsFileSystem StockUbuntuAn1641()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        fs.WithFile("/sys/class/dmi/id/board_name", "Sierra_PEH\n");
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile("/sys/class/dmi/id/bios_version", "V1.27\n");
        fs.WithFile("/sys/class/dmi/id/bios_vendor", "INSYDE Corp.\n");

        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "low-power balanced performance\n");
        fs.DenyWrite(LinuxPaths.PlatformProfile);

        fs.WithDirectory(LinuxPaths.AcerWmiModule);
        fs.WithDirectory(LinuxPaths.AcpiPlatformRoot);
        fs.WithFile(LinuxPaths.AcerWmiPredatorV4, "N\n");

        fs.WithFile("/sys/class/hwmon/hwmon0/name", "k10temp\n");
        fs.WithFile("/sys/class/hwmon/hwmon0/temp1_input", "55000\n");
        fs.WithFile("/sys/class/hwmon/hwmon1/name", "amdgpu\n");
        fs.WithFile("/sys/class/hwmon/hwmon1/temp1_input", "50000\n");
        return fs;
    }

    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    private static LinuxCapabilities Probe(InMemorySysfsFileSystem fs, IPowerProfiles? power = null,
        Func<string, byte[]?>? readBytes = null) =>
        new LinuxCapabilityProbe(fs, Catalog(), power, readBytes).Probe();

    private static LinuxSysfsHardwareController Controller(
        InMemorySysfsFileSystem fs, AppSettings settings, IPowerProfiles? power = null) =>
        new(fs, Probe(fs, power), new ReadBackPolicy(settings, _ => Task.CompletedTask));

    /// <summary>A daemon that applies what it is asked, optionally refusing <c>performance</c> like a hot machine.</summary>
    private sealed class FakePowerProfiles : IPowerProfiles
    {
        public bool RefusePerformance { get; init; }
        public string Active { get; private set; } = "balanced";
        public List<string> Requests { get; } = new();

        public bool IsAvailable => true;
        public IReadOnlyList<string> Profiles { get; init; } = new[] { "power-saver", "balanced", "performance" };
        public string? GetActive() => Active;

        public bool SetActive(string profile)
        {
            Requests.Add(profile);
            if (RefusePerformance && profile == "performance") return true; // accepted, then ignored
            Active = profile;
            return true;
        }
    }

    /// <summary>Records every command; answers from a script keyed by the program name.</summary>
    private sealed class FakeRunner : ICommandRunner
    {
        private readonly Dictionary<string, string?> _paths;
        private readonly Func<string, IReadOnlyList<string>, CommandResult> _answer;

        public FakeRunner(Dictionary<string, string?> paths, Func<string, IReadOnlyList<string>, CommandResult> answer)
        {
            _paths = paths;
            _answer = answer;
        }

        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = new();

        public string? Find(string name) => _paths.TryGetValue(name, out var path) ? path : null;

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return _answer(fileName, arguments);
        }
    }

    // ---------------------------------------------------------------- the permission rule

    [Fact]
    public async Task PermissionDenied_IsNeverCountedAsAFirmwareFailure_SoTheModeIsNotDisabledForGood()
    {
        var settings = new AppSettings();

        // Five launches without the udev rule and without a daemon: each tries to change the mode once.
        for (int session = 0; session < 5; session++)
        {
            var fs = StockUbuntuAn1641();
            using var controller = Controller(fs, settings);
            Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Turbo));
            Assert.True(controller.PermissionHintRaised);
        }

        Assert.DoesNotContain(ReadBackPolicy.OperatingMode, settings.DisabledCapabilities);
        Assert.False(settings.CapabilitySessionFailures.ContainsKey(ReadBackPolicy.OperatingMode));

        // Once the rule is installed the very next session writes, which is what the old behaviour broke.
        var allowed = new InMemorySysfsFileSystem();
        allowed.WithFile("/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        allowed.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        allowed.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        allowed.WithFile(LinuxPaths.PlatformProfileChoices, "low-power balanced performance\n");

        using var after = Controller(allowed, settings);
        Assert.True(await after.SetOperatingModeAsync(OperatingMode.Turbo));
        Assert.Equal("performance", allowed.Peek(LinuxPaths.PlatformProfile)?.Trim());
    }

    /// <summary>Counts every write attempt, including the refused ones the in-memory tree does not record.</summary>
    private sealed class CountingFs : ISysfsFileSystem
    {
        private readonly ISysfsFileSystem _inner;
        public CountingFs(ISysfsFileSystem inner) => _inner = inner;
        public int WriteAttempts { get; private set; }
        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public string? ReadAllText(string path) => _inner.ReadAllText(path);
        public SysfsWriteResult WriteAllText(string path, string value) { WriteAttempts++; return _inner.WriteAllText(path, value); }
        public bool CanWrite(string path) => _inner.CanWrite(path);
        public IReadOnlyList<string> EnumerateDirectories(string path) => _inner.EnumerateDirectories(path);
        public IReadOnlyList<string> EnumerateFiles(string path) => _inner.EnumerateFiles(path);
    }

    [Fact]
    public async Task PermissionDenied_IsNotRetried()
    {
        var fs = new CountingFs(StockUbuntuAn1641());
        var caps = new LinuxCapabilityProbe(fs, Catalog()).Probe();
        using var controller = new LinuxSysfsHardwareController(fs, caps,
            new ReadBackPolicy(new AppSettings(), _ => Task.CompletedTask));

        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Quiet));
        Assert.Equal(1, fs.WriteAttempts);
    }

    // ---------------------------------------------------------------- power-profiles-daemon

    [Fact]
    public void ReadOnlyPlatformProfile_WithADaemon_RoutesTheModeThroughIt_AndNeedsNoUdevRuleForIt()
    {
        var caps = Probe(StockUbuntuAn1641(), new FakePowerProfiles());

        Assert.True(caps.ModeViaPowerProfiles);
        Assert.True(caps.Hardware.OperatingModeControl);
        Assert.False(caps.NeedsUdevRule);
        Assert.Equal(new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Turbo },
            caps.Hardware.SupportedOperatingModes);
    }

    [Fact]
    public void WritablePlatformProfile_IsStillPreferredOverTheDaemon()
    {
        var fs = StockUbuntuAn1641();
        var open = new InMemorySysfsFileSystem();
        open.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        open.WithFile(LinuxPaths.PlatformProfileChoices, fs.Peek(LinuxPaths.PlatformProfileChoices)!);

        var caps = Probe(open, new FakePowerProfiles());

        Assert.False(caps.ModeViaPowerProfiles);
        Assert.Contains(OperatingMode.Eco, caps.Hardware.SupportedOperatingModes);
    }

    [Fact]
    public async Task ModeThroughTheDaemon_IsAppliedAndReadBack()
    {
        var power = new FakePowerProfiles();
        using var controller = Controller(StockUbuntuAn1641(), new AppSettings(), power);

        Assert.True(await controller.SetOperatingModeAsync(OperatingMode.Quiet));
        Assert.Equal("power-saver", power.Active);
        Assert.False(controller.PermissionHintRaised);
    }

    [Fact]
    public async Task ModeThroughTheDaemon_ARefusalIsAFailedWrite_NotAMoveThatHappened()
    {
        var power = new FakePowerProfiles { RefusePerformance = true };
        using var controller = Controller(StockUbuntuAn1641(), new AppSettings(), power);

        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Turbo));
        Assert.Equal("balanced", power.Active);
        Assert.NotNull(controller.LastError);
    }

    [Theory]
    [InlineData("{\"type\":\"s\",\"data\":\"balanced\"}", "balanced")]
    [InlineData("{\"type\":\"s\",\"data\":\"power-saver\"}", "power-saver")]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public void DaemonJsonString_Parses(string output, string? expected) =>
        Assert.Equal(expected, PowerProfilesDaemon.ParseJsonString(output));

    [Theory]
    [InlineData("s \"performance\"", "performance")]
    [InlineData("b true", null)]
    public void DaemonPlainString_Parses(string output, string? expected) =>
        Assert.Equal(expected, PowerProfilesDaemon.ParsePlainString(output));

    [Fact]
    public void DaemonProfiles_AreReadOutOfTheDictionaryArray()
    {
        const string json =
            "{\"type\":\"aa{sv}\",\"data\":[" +
            "{\"Profile\":{\"type\":\"s\",\"data\":\"power-saver\"},\"Driver\":{\"type\":\"s\",\"data\":\"amd_pmf\"}}," +
            "{\"Profile\":{\"type\":\"s\",\"data\":\"balanced\"},\"Driver\":{\"type\":\"s\",\"data\":\"amd_pmf\"}}]}";

        Assert.Equal(new[] { "power-saver", "balanced" }, PowerProfilesDaemon.ParseProfiles(json));
        Assert.Equal(new[] { OperatingMode.Quiet, OperatingMode.Default },
            PowerProfilesDaemon.ToModes(PowerProfilesDaemon.ParseProfiles(json)));
    }

    [Fact]
    public void Daemon_IsReachedThroughBusctl_WithTheValuePassedAsItsOwnArgument()
    {
        var runner = new FakeRunner(
            new() { ["busctl"] = "/usr/bin/busctl" },
            (_, args) => args.Contains("ActiveProfile") && args.Contains("--json=short")
                ? new CommandResult(0, "{\"type\":\"s\",\"data\":\"balanced\"}", "")
                : args.Contains("Profiles")
                    ? new CommandResult(0, "{\"type\":\"aa{sv}\",\"data\":[]}", "")
                    : new CommandResult(0, "", ""));

        var daemon = new PowerProfilesDaemon(runner);
        Assert.True(daemon.IsAvailable);
        Assert.True(daemon.SetActive("performance"));

        var set = runner.Calls.Last();
        Assert.Equal("/usr/bin/busctl", set.File);
        Assert.Equal(new[]
        {
            "--system", "set-property", "org.freedesktop.UPower.PowerProfiles", "/org/freedesktop/UPower/PowerProfiles",
            "org.freedesktop.UPower.PowerProfiles", "ActiveProfile", "s", "performance"
        }, set.Args);
    }

    [Fact]
    public void NoBusctlAndNoCtl_MeansNoDaemon()
    {
        var daemon = new PowerProfilesDaemon(new FakeRunner(new(), (_, _) => CommandResult.NotRun("missing")));
        Assert.False(daemon.IsAvailable);
        Assert.False(daemon.SetActive("balanced"));
    }

    // ---------------------------------------------------------------- the kernel hint

    [Fact]
    public void PredatorV4Off_AndNoTachometer_SuggestsTurningItOn()
    {
        Assert.Equal(KernelSetupHint.EnablePredatorV4, Probe(StockUbuntuAn1641()).KernelHint);
    }

    [Fact]
    public void KernelWithoutTheSwitch_SuggestsLinuwuSenseInstead()
    {
        var fs = StockUbuntuAn1641();
        fs.WithoutFile(LinuxPaths.AcerWmiPredatorV4);
        Assert.Equal(KernelSetupHint.InstallLinuwuSense, Probe(fs).KernelHint);
    }

    [Fact]
    public void NonAcerMachine_GetsNoKernelHint()
    {
        var fs = StockUbuntuAn1641();
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "LENOVO\n");
        fs.WithFile("/sys/class/dmi/id/product_name", "Legion 5\n");
        Assert.Equal(KernelSetupHint.None, Probe(fs).KernelHint);
    }

    // ---------------------------------------------------------------- the report

    [Fact]
    public void Report_PrintsTheChoicesThemselves_NotThePathToThem()
    {
        var report = LinuxHardwareReport.Build(Probe(StockUbuntuAn1641(), new FakePowerProfiles()), "1.2.9",
            new DateTime(2026, 11, 21, 0, 50, 0, DateTimeKind.Utc));

        Assert.Contains("platform_profile_choices: low-power balanced performance", report);
        Assert.DoesNotContain("platform_profile_choices: /sys/", report);
        Assert.Contains("operating mode written through: power-profiles-daemon", report);
        Assert.Contains("acer-wmi predator_v4: N", report);
    }

    // ---------------------------------------------------------------- the KB5130 keyboard through hidraw

    private static readonly byte[] VendorDescriptor =
    {
        0x06, 0x00, 0xFF, // Usage Page (Vendor 0xFF00)
        0x09, 0x01,       // Usage
        0xA1, 0x01,       // Collection (Application)
        0x85, 0xA4,       // Report ID 0xA4
        0x95, 0x0A,       // Report Count 10
        0x75, 0x08,       // Report Size 8
        0xB1, 0x02,       // Feature
        0xC0
    };

    private static readonly byte[] KeyboardDescriptor =
    {
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, 0x01, 0x05, 0x07, 0xC0
    };

    [Fact]
    public void Hidraw_FindsOnlyTheVendorInterfaceOfTheKb5130()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithDirectory("/sys/class/hidraw/hidraw0");
        fs.WithFile("/sys/class/hidraw/hidraw0/device/uevent", "DRIVER=hid-generic\nHID_ID=0003:00000CF2:00005130\n");
        fs.WithDirectory("/sys/class/hidraw/hidraw1");
        fs.WithFile("/sys/class/hidraw/hidraw1/device/uevent", "DRIVER=hid-generic\nHID_ID=0003:00000CF2:00005130\n");
        fs.WithDirectory("/sys/class/hidraw/hidraw2");
        fs.WithFile("/sys/class/hidraw/hidraw2/device/uevent", "HID_ID=0003:0000046D:0000C52B\n");

        byte[]? Read(string path) => path.Contains("hidraw0", StringComparison.Ordinal)
            ? KeyboardDescriptor
            : VendorDescriptor;

        Assert.Equal("/dev/hidraw1", LinuxHidrawLocator.FindEneKb5130(fs, Read));
    }

    [Fact]
    public void Hidraw_KeyboardOwnInputInterface_IsNeverALightingInterface()
    {
        Assert.False(LinuxHidrawLocator.IsVendorLightingInterface(KeyboardDescriptor));
        Assert.True(LinuxHidrawLocator.IsVendorLightingInterface(VendorDescriptor));
    }

    [Fact]
    public void Hidraw_ADataByteThatLooksLikeATag_DoesNotMatch()
    {
        // 0x85 0xA4 appears only as the two data bytes of a Usage item, never as a Report ID item.
        var tricky = new byte[] { 0x06, 0x00, 0xFF, 0x0A, 0x85, 0xA4, 0xC0 };
        Assert.False(LinuxHidrawLocator.IsVendorLightingInterface(tricky));
    }

    [Theory]
    [InlineData("HID_ID=0003:00000CF2:00005130", 0x0CF2, 0x5130)]
    [InlineData("DRIVER=x\nHID_ID=0005:0000048D:0000C966\nHID_NAME=y", 0x048D, 0xC966)]
    public void Hidraw_UeventIsParsed(string uevent, int vendor, int product)
    {
        Assert.True(LinuxHidrawLocator.TryParseHidId(uevent, out var v, out var p));
        Assert.Equal(vendor, v);
        Assert.Equal(product, p);
    }

    [Fact]
    public void Hidraw_FeatureIoctlIsTheKernelsHidiocsfeature()
    {
        // _IOC(_IOC_READ|_IOC_WRITE, 'H', 0x06, 11) from <linux/hidraw.h>.
        Assert.Equal(0xC00B4806UL, LinuxHidrawKeyboardWriter.FeatureRequest(EneKb5130Protocol.ReportLength));
    }

    [Fact]
    public void Hidraw_Kb5130_GivesTheMachineKeyboardLighting_AndAsksForTheUdevRuleWhenNotWritable()
    {
        var fs = StockUbuntuAn1641();
        fs.WithDirectory("/sys/class/hidraw/hidraw3");
        fs.WithFile("/sys/class/hidraw/hidraw3/device/uevent", "HID_ID=0003:00000CF2:00005130\n");
        fs.WithFile("/dev/hidraw3", "");
        fs.DenyWrite("/dev/hidraw3");

        var caps = Probe(fs, new FakePowerProfiles(), _ => VendorDescriptor);

        Assert.Equal("/dev/hidraw3", caps.Nodes.HidrawKeyboard);
        Assert.True(caps.Hardware.KeyboardLighting);
        Assert.True(caps.NeedsUdevRule);
    }

    // ---------------------------------------------------------------- NVIDIA

    [Fact]
    public void Nvidia_CsvLineIsParsed_AndNotAvailableFieldsReadAsZero()
    {
        var sample = LinuxNvidiaGpu.Parse("NVIDIA GeForce RTX 4060 Laptop GPU, 61, 34, 1203, 8188, 2100, [N/A]\n");

        Assert.NotNull(sample);
        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", sample!.Name);
        Assert.Equal(61, sample.TemperatureC);
        Assert.Equal(34, sample.UtilizationPercent);
        Assert.Equal(8188, sample.MemoryTotalMb);
        Assert.Equal(0, sample.PowerWatts);
    }

    [Fact]
    public void Nvidia_ASleepingGpu_IsNotWokenToBeAsked()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithDirectory("/sys/bus/pci/devices/0000:01:00.0");
        fs.WithFile("/sys/bus/pci/devices/0000:01:00.0/vendor", "0x10de\n");
        fs.WithFile("/sys/bus/pci/devices/0000:01:00.0/class", "0x030000\n");
        fs.WithFile("/sys/bus/pci/devices/0000:01:00.0/power/runtime_status", "suspended\n");

        var runner = new FakeRunner(new() { ["nvidia-smi"] = "/usr/bin/nvidia-smi" },
            (_, _) => new CommandResult(0, "X, 50, 1, 1, 1, 1, 1", ""));
        var gpu = new LinuxNvidiaGpu(fs, runner);

        Assert.True(gpu.IsAvailable);
        Assert.True(gpu.IsSuspended);
        Assert.Null(gpu.Latest());
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Nvidia_NoNvidiaDevice_IsNotAvailable()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithDirectory("/sys/bus/pci/devices/0000:05:00.0");
        fs.WithFile("/sys/bus/pci/devices/0000:05:00.0/vendor", "0x1002\n");
        fs.WithFile("/sys/bus/pci/devices/0000:05:00.0/class", "0x030000\n");

        Assert.False(new LinuxNvidiaGpu(fs, new FakeRunner(new(), (_, _) => default)).IsAvailable);
    }

    // ---------------------------------------------------------------- the one-prompt setup

    [Fact]
    public async Task Setup_RunsOneFixedScriptThroughPkexec_WithTheUserAndRuleAsArguments()
    {
        var runner = new FakeRunner(
            new() { ["pkexec"] = "/usr/bin/pkexec", ["sh"] = "/bin/sh" },
            (_, _) => new CommandResult(0, "", ""));
        var setup = new LinuxPlatformSetup(Probe(StockUbuntuAn1641()), () => "/home/u/.local/share/nitrolite/99-nitrolite.rules",
            runner, new InMemorySysfsFileSystem(), user: "cztrk");

        var result = await setup.GrantAccessAsync();

        Assert.True(result.Ok);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("/usr/bin/pkexec", call.File);
        Assert.Equal("/bin/sh", call.Args[0]);
        Assert.Equal("-c", call.Args[1]);
        Assert.Equal(LinuxPlatformSetup.GrantScript, call.Args[2]);
        Assert.Equal("cztrk", call.Args[4]);
        Assert.EndsWith("99-nitrolite.rules", call.Args[5]);

        // Nothing from outside is spliced into the script text.
        Assert.DoesNotContain("cztrk", call.Args[2]);
    }

    [Fact]
    public async Task Setup_ADismissedPasswordPrompt_ChangesNothingAndSaysSo()
    {
        var runner = new FakeRunner(
            new() { ["pkexec"] = "/usr/bin/pkexec", ["sh"] = "/bin/sh" },
            (_, _) => new CommandResult(126, "", "Request dismissed"));
        var setup = new LinuxPlatformSetup(Probe(StockUbuntuAn1641()), () => "/tmp/r", runner,
            new InMemorySysfsFileSystem(), user: "u");

        var result = await setup.GrantAccessAsync();

        Assert.False(result.Ok);
        Assert.Contains("Şifre penceresi kapatıldı", result.Message);
    }

    [Fact]
    public async Task Setup_WithoutPkexec_FallsBackToTheCopyableCommand()
    {
        var setup = new LinuxPlatformSetup(Probe(StockUbuntuAn1641()), () => "/tmp/r",
            new FakeRunner(new(), (_, _) => default), new InMemorySysfsFileSystem(), user: "u");

        Assert.False(setup.CanElevate);
        Assert.False((await setup.EnablePredatorV4Async()).Ok);
    }

    [Fact]
    public void Setup_InputGroupMembership_IsReadFromTheProcessGroups()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/etc/group", "root:x:0:\ninput:x:104:\nnitrolite:x:998:cztrk\n");
        fs.WithFile("/proc/self/status", "Name:\tNitroLite\nGroups:\t4 24 27 1000\n");
        var setup = new LinuxPlatformSetup(Probe(StockUbuntuAn1641()), () => null,
            new FakeRunner(new(), (_, _) => default), fs, user: "u");

        Assert.True(setup.NeedsInputGroup);

        fs.WithFile("/proc/self/status", "Groups:\t4 104 1000\n");
        Assert.False(setup.NeedsInputGroup);
    }

    // ---------------------------------------------------------------- diagnostics

    [Theory]
    [InlineData("NAME=\"Ubuntu\"\nVERSION_ID=\"26.04\"\nPRETTY_NAME=\"Ubuntu 26.04 LTS\"\n", "Ubuntu 26.04 LTS")]
    [InlineData("NAME=Fedora\nVERSION_ID=43\n", "Fedora 43")]
    public void Diagnostics_OsNameComesFromOsRelease(string osRelease, string expected) =>
        Assert.Equal(expected, LinuxDiagnosticsService.OsName(osRelease));

    [Fact]
    public void Diagnostics_SystemTextNeverNamesTheMachine()
    {
        var text = LinuxDiagnosticsService.BuildSystemText();
        Assert.Contains($"Machine          : {Redaction.MachinePlaceholder}", text);
        Assert.DoesNotContain(Environment.MachineName, text.Replace(Redaction.MachinePlaceholder, ""),
            StringComparison.OrdinalIgnoreCase);
    }
}
