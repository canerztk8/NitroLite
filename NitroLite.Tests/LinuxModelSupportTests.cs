using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The kernel's own fan control, the table of models the mainline kernel supports, the in-app capture and the udev rule
/// that makes the sysfs attributes writable.
/// </summary>
public class LinuxModelSupportTests
{
    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>An AN16-41 on Linux 7.4: the kernel lists it, so the acer hwmon device has fans and pwm control.</summary>
    private static InMemorySysfsFileSystem An1641OnLinux74()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile("/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        fs.WithFile("/sys/class/dmi/id/board_name", "Sierra_PEH\n");
        fs.WithFile("/proc/sys/kernel/osrelease", "7.4.0-1-generic\n");
        fs.WithDirectory(LinuxPaths.AcerWmiModule);
        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "low-power quiet balanced balanced-performance performance\n");

        const string acer = "/sys/class/hwmon/hwmon4";
        fs.WithDirectory(acer);
        fs.WithFile(acer + "/name", "acer\n");
        fs.WithFile(acer + "/temp1_input", "61000\n");
        fs.WithFile(acer + "/fan1_input", "2436\n");
        fs.WithFile(acer + "/fan2_input", "2409\n");
        fs.WithFile(acer + "/pwm1", "0\n");
        fs.WithFile(acer + "/pwm2", "0\n");
        fs.WithFile(acer + "/pwm1_enable", "2\n");
        fs.WithFile(acer + "/pwm2_enable", "2\n");
        return fs;
    }

    private static LinuxSysfsHardwareController Controller(InMemorySysfsFileSystem fs) =>
        new(fs, new LinuxCapabilityProbe(fs, Catalog()).Probe(), new ReadBackPolicy(new AppSettings(), _ => Task.CompletedTask));

    // ---------------------------------------------------------------- kernel fan control

    [Fact]
    public void KernelPwm_IsFanControl_WithoutLinuwuSense()
    {
        var caps = new LinuxCapabilityProbe(An1641OnLinux74(), Catalog()).Probe();

        Assert.True(caps.Nodes.HasKernelFanControl);
        Assert.True(caps.Hardware.FanControl);
        Assert.Null(caps.Nodes.SenseRoot);
        Assert.Null(caps.ModelNote); // 7.4 already does everything the kernel can for this model
    }

    [Fact]
    public async Task KernelPwm_CustomSpeedWritesTheModeThenTheSpeeds()
    {
        var fs = An1641OnLinux74();
        using var controller = Controller(fs);

        Assert.True(await controller.SetFanModeAsync(FanMode.Custom, 60, 40));

        Assert.Equal("1", fs.Peek("/sys/class/hwmon/hwmon4/pwm1_enable")?.Trim());
        Assert.Equal("1", fs.Peek("/sys/class/hwmon/hwmon4/pwm2_enable")?.Trim());
        Assert.Equal("153", fs.Peek("/sys/class/hwmon/hwmon4/pwm1")?.Trim());
        Assert.Equal("102", fs.Peek("/sys/class/hwmon/hwmon4/pwm2")?.Trim());
        Assert.Equal(FanMode.Custom, await controller.GetFanModeAsync());

        // The mode is written before the speed, which is what the firmware needs.
        var order = fs.Writes.Select(w => Path.GetFileName(w.Path)).ToList();
        Assert.True(order.IndexOf("pwm1_enable") < order.IndexOf("pwm1"));
    }

    [Theory]
    [InlineData(FanMode.Auto, "2")]
    [InlineData(FanMode.Max, "0")]
    public async Task KernelPwm_AutoAndMaxAreTheEnableValues(FanMode mode, string expected)
    {
        var fs = An1641OnLinux74();
        using var controller = Controller(fs);

        Assert.True(await controller.SetFanModeAsync(mode));
        Assert.Equal(expected, fs.Peek("/sys/class/hwmon/hwmon4/pwm1_enable")?.Trim());
        Assert.Equal(expected, fs.Peek("/sys/class/hwmon/hwmon4/pwm2_enable")?.Trim());
        Assert.Equal(mode, await controller.GetFanModeAsync());
    }

    [Fact]
    public async Task KernelPwm_WithoutPermission_AsksForTheRuleAndIsNotCountedAsBroken()
    {
        var fs = An1641OnLinux74();
        fs.DenyWrite("/sys/class/hwmon/hwmon4/pwm1_enable");
        var settings = new AppSettings();
        var caps = new LinuxCapabilityProbe(fs, Catalog()).Probe();
        using var controller = new LinuxSysfsHardwareController(fs, caps, new ReadBackPolicy(settings, _ => Task.CompletedTask));

        Assert.True(caps.NeedsUdevRule);
        var report = LinuxHardwareReport.Build(caps);
        Assert.Contains("Direct write permission denied: /sys/class/hwmon/hwmon4/pwm1_enable", report);
        Assert.Contains("| Fan: manual | Interface detected | Not recorded |", report);
        Assert.False(await controller.SetFanModeAsync(FanMode.Max));
        Assert.True(controller.PermissionHintRaised);
        Assert.Empty(settings.CapabilitySessionFailures);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 77)]
    [InlineData(50, 128)]
    [InlineData(100, 255)]
    public void PwmScale_RoundTrips(int percent, int pwm)
    {
        Assert.Equal(pwm, LinuxSysfsHardwareController.PercentToPwm(percent));
        Assert.Equal(percent, LinuxSysfsHardwareController.PwmToPercent(pwm));
    }

    // ---------------------------------------------------------------- the mainline model table

    [Theory]
    [InlineData("7.0.0-44-generic", 7, 0)]
    [InlineData("6.19.3-arch1-1", 6, 19)]
    [InlineData("7.4.0-rc3+", 7, 4)]
    public void KernelRelease_IsParsed(string release, int major, int minor) =>
        Assert.Equal(new Version(major, minor), LinuxModelSupport.ParseKernel(release));

    [Fact]
    public void An1641_IsInTheTable_WithFanControlFrom74()
    {
        var model = LinuxModelSupport.Find(new DeviceIdentity { Model = "Nitro AN16-41", ModelCode = "AN16-41" });

        Assert.NotNull(model);
        Assert.Equal(new Version(7, 4), model!.Since);
        Assert.Equal(new Version(7, 4), model.FanControlSince);
    }

    [Fact]
    public void Describe_OnAnOlderKernel_NamesTheReleaseAndTheWorkaround()
    {
        var model = LinuxModelSupport.Find(new DeviceIdentity { Model = "Nitro AN16-41" });
        var note = LinuxModelSupport.Describe(model, new Version(7, 0));

        Assert.NotNull(note);
        Assert.Contains("7.4", note);
        Assert.Contains("7.0", note);
        Assert.Contains("predator_v4", note);
    }

    [Fact]
    public void Describe_WhenModesAreInButFanControlIsNot_SaysWhenFansArrive()
    {
        var model = LinuxModelSupport.Find(new DeviceIdentity { Model = "Predator PH16-72" });
        var note = LinuxModelSupport.Describe(model, new Version(6, 16));

        Assert.Contains("6.19", note);
    }

    [Fact]
    public void OlderKernelDistinguishesModesFromManualFans()
    {
        var model = LinuxModelSupport.Find(new DeviceIdentity { ModelCode = "AN515-58-79QL" });
        var note = LinuxModelSupport.Describe(model, new Version(6, 10));
        Assert.Contains("6.14", note);
        Assert.Contains("6.19", note);
    }

    [Fact]
    public void Describe_UnknownModel_SaysNothing()
    {
        Assert.Null(LinuxModelSupport.Find(new DeviceIdentity { Model = "Aspire A515-57" }));
        Assert.Null(LinuxModelSupport.Describe(null, new Version(7, 0)));
    }

    [Fact]
    public void RealCapture_GetsTheModelNoteFor74()
    {
        var capture = LinuxCapture.LoadFile(Path.Combine(RepoRoot(), "tools", "linux-capture", "samples", "an16-41-ubuntu-26.04.txt"));
        var caps = new LinuxCapabilityProbe(capture.FileSystem, Catalog(), capture.PowerProfiles, capture.ReadBytes).Probe();

        Assert.Equal(new Version(7, 0), caps.KernelVersion);
        Assert.Equal("Nitro AN16-41", caps.KernelModel?.ProductName);
        Assert.Contains("7.4", caps.ModelNote);
    }

    // ---------------------------------------------------------------- the in-app capture

    private sealed class Runner : ICommandRunner
    {
        public string? Find(string name) => name is "busctl" or "nvidia-smi" or "sh" ? "/usr/bin/" + name : null;

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout) =>
            Path.GetFileName(fileName) switch
            {
                "busctl" when arguments.Contains("ActiveProfile") && arguments.Contains("--json=short") =>
                    new CommandResult(0, "{\"type\":\"s\",\"data\":\"balanced\"}", ""),
                "busctl" when arguments.Contains("Profiles") =>
                    new CommandResult(0, "{\"type\":\"aa{sv}\",\"data\":[{\"Profile\":{\"type\":\"s\",\"data\":\"balanced\"}}]}", ""),
                "nvidia-smi" => new CommandResult(0, "NVIDIA GeForce RTX 4070 Laptop GPU, 48, 10, 804, 8188, 285, 8.80", ""),
                _ => new CommandResult(1, "", "")
            };
    }

    [Fact]
    public void InAppCapture_ReplaysLikeTheMachineItRecorded()
    {
        var fs = An1641OnLinux74();
        fs.DenyWrite("/sys/class/hwmon/hwmon4/pwm1");
        fs.WithDirectory("/sys/class/hidraw/hidraw0");
        fs.WithFile("/sys/class/hidraw/hidraw0/device/uevent",
            "DRIVER=hid-generic\nHID_ID=0003:0000373B:000012DA\nHID_NAME=Dongle\nHID_PHYS=usb-1/input0\nHID_UNIQ=541505796617\n");
        fs.WithDirectory("/sys/bus/pci/devices/0000:01:00.0");
        fs.WithFile("/sys/bus/pci/devices/0000:01:00.0/vendor", "0x10de\n");
        fs.WithFile("/sys/bus/pci/devices/0000:01:00.0/class", "0x030000\n");
        fs.WithFile("/sys/bus/pci/devices/0000:01:00.0/power/runtime_status", "active\n");

        var text = new LinuxCaptureWriter(fs, new Runner(), _ => new byte[] { 0x06, 0x00, 0xFF })
            .Capture(new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc));

        // The device serial and the bus path never leave the machine.
        Assert.DoesNotContain("541505796617", text);
        Assert.DoesNotContain("HID_PHYS", text);
        Assert.Contains("HID_ID=0003:0000373B:000012DA", text);

        var replay = LinuxCapture.Parse(text);
        Assert.Equal("2026-11-21T12:00:00Z", replay.Captured);
        Assert.False(replay.FileSystem.CanWrite("/sys/class/hwmon/hwmon4/pwm1"));
        Assert.True(replay.FileSystem.CanWrite("/sys/class/hwmon/hwmon4/pwm1_enable"));

        var original = new LinuxCapabilityProbe(fs, Catalog()).Probe();
        var replayed = new LinuxCapabilityProbe(replay.FileSystem, Catalog()).Probe();
        Assert.Equal(original.Hardware.FanControl, replayed.Hardware.FanControl);
        Assert.Equal(original.KernelVersion, replayed.KernelVersion);
        Assert.Equal(original.Identity.ModelCode, replayed.Identity.ModelCode);

        Assert.Equal("NVIDIA GeForce RTX 4070 Laptop GPU", new LinuxNvidiaGpu(replay.FileSystem, replay.Runner).QueryNow()?.Name);
    }

    [Fact]
    public void LinuxCaptureIssue_SaysWhichFileToAttach()
    {
        var url = CompatibilityReportUrl.BuildLinuxCapture("AN16-41", "Sierra_PEH", "nitrolite-capture-20261121-120000.txt",
            "- Distribution: Ubuntu 26.04.1 LTS\n- Kernel: 7.0.0-44-generic");
        var decoded = Uri.UnescapeDataString(url);

        Assert.StartsWith(CompatibilityReportUrl.NewIssueUrl, url, StringComparison.Ordinal);
        Assert.Contains("[Linux capture] AN16-41 (Sierra_PEH)", decoded);
        Assert.Contains("labels=linux", url);
        Assert.Contains("nitrolite-capture-20261121-120000.txt", decoded);
        Assert.Contains("Kernel: 7.0.0-44-generic", decoded);
    }

    // ---------------------------------------------------------------- the udev rule

    [Fact]
    public void UdevRule_HandsSysfsAttributesToTheGroup_AndKeepsUaccessForDeviceNodesOnly()
    {
        var rule = File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "linux", "99-nitrolite.rules"));
        var active = rule.Split('\n').Where(l => !l.TrimStart().StartsWith('#') && l.Contains("==", StringComparison.Ordinal)).ToList();

        // uaccess only reaches /dev nodes, so the only rule that may use it is the hidraw one.
        Assert.All(active.Where(l => l.Contains("uaccess", StringComparison.Ordinal)),
            l => Assert.Contains("SUBSYSTEM==\"hidraw\"", l));

        Assert.Contains(active, l => l.Contains("ATTR{name}==\"acer\"") && l.Contains("pwm1_enable") && l.Contains("chgrp nitrolite"));
        Assert.Contains(active, l => l.Contains("charge_control_end_threshold") && l.Contains("chgrp nitrolite"));
        Assert.Contains(active, l => l.Contains("nitro_sense") && l.Contains("chgrp nitrolite"));
        Assert.DoesNotContain("0666", string.Join("\n", active));
        Assert.DoesNotContain("a+w", string.Join("\n", active));
    }
}
