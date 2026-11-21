using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Linux;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The capture format written by <c>tools/linux-capture/nitrolite-capture.sh</c>, and the replay that lets the Linux
/// backend run on a recorded machine.
/// </summary>
public class LinuxCaptureTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static LinuxCapture Sample() => LinuxCapture.LoadFile(
        Path.Combine(RepoRoot(), "tools", "linux-capture", "samples", "an16-41-ubuntu-reconstructed.txt"));

    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    [Fact]
    public void Parse_RebuildsFilesDirectoriesWritabilityAndCommands()
    {
        const string text =
            "NITROLITE-CAPTURE 1\n" +
            "@@ meta captured 2026-11-21T00:00:00Z\n" +
            "@@ dir /sys/class/hwmon/hwmon0\n" +
            "@@ file /sys/class/hwmon/hwmon0/name\n" +
            "| k10temp\n" +
            "@@ file /sys/firmware/acpi/platform_profile\n" +
            "| balanced\n" +
            "@@ ro /sys/firmware/acpi/platform_profile\n" +
            "@@ file /proc/cpuinfo\n" +
            "| model name\t: X\n" +
            "| @@ not a marker\n" +
            "@@ hex /sys/class/hidraw/hidraw0/device/report_descriptor\n" +
            "| 0600ff85a4\n" +
            "@@ which busctl present\n" +
            "@@ cmd busctl --system get-property a b c ActiveProfile\n" +
            "@@ exit 0\n" +
            "| s \"balanced\"\n" +
            "@@ end\n";

        var capture = LinuxCapture.Parse(text);
        var fs = capture.FileSystem;

        Assert.Equal("2026-11-21T00:00:00Z", capture.Captured);
        Assert.True(fs.DirectoryExists("/sys/class/hwmon/hwmon0"));
        Assert.Equal("k10temp", fs.ReadAllText("/sys/class/hwmon/hwmon0/name"));
        Assert.True(fs.FileExists("/sys/firmware/acpi/platform_profile"));
        Assert.False(fs.CanWrite("/sys/firmware/acpi/platform_profile"));
        Assert.Contains("@@ not a marker", fs.ReadAllText("/proc/cpuinfo"));
        Assert.Equal(new byte[] { 0x06, 0x00, 0xFF, 0x85, 0xA4 },
            capture.ReadBytes("/sys/class/hidraw/hidraw0/device/report_descriptor"));

        var runner = capture.Runner;
        Assert.Equal("/usr/bin/busctl", runner.Find("busctl"));
        Assert.Null(runner.Find("nvidia-smi"));
        var result = runner.Run("/usr/bin/busctl", new[] { "--system", "get-property", "a", "b", "c", "ActiveProfile" },
            TimeSpan.FromSeconds(1));
        Assert.True(result.Ok);
        Assert.Equal("s \"balanced\"", result.Output);
        Assert.False(runner.Run("/usr/bin/busctl", new[] { "unrecorded" }, TimeSpan.FromSeconds(1)).Ok);
    }

    [Fact]
    public void Parse_RejectsAFileThatIsNotACapture() =>
        Assert.Throws<FormatException>(() => LinuxCapture.Parse("hello\n"));

    [Fact]
    public void Sample_ProbesLikeTheReportedMachine()
    {
        var capture = Sample();
        var caps = new LinuxCapabilityProbe(capture.FileSystem, Catalog(), capture.PowerProfiles, capture.ReadBytes).Probe();

        Assert.Equal("AN16-41", caps.Identity.ModelCode);
        Assert.True(caps.AcerWmiLoaded);
        Assert.Equal("N", caps.PredatorV4);
        Assert.Equal(KernelSetupHint.EnablePredatorV4, caps.KernelHint);
        Assert.True(caps.ModeViaPowerProfiles);
        Assert.False(caps.NeedsUdevRule);
        Assert.False(caps.Hardware.FanControl);
        Assert.False(caps.Hardware.KeyboardLighting);
    }

    [Fact]
    public async Task Sample_ModeSwitchGoesThroughTheReplayedDaemon_AndSticks()
    {
        var capture = Sample();
        using var controller = LinuxSysfsHardwareController.Create(new AppSettings(), capture.FileSystem, Catalog(),
            powerProfiles: capture.PowerProfiles, readBytes: capture.ReadBytes);

        Assert.True(await controller.SetOperatingModeAsync(OperatingMode.Turbo));
        Assert.True(await controller.SetOperatingModeAsync(OperatingMode.Quiet));
        Assert.False(controller.PermissionHintRaised);
    }

    [Fact]
    public void Sample_NvidiaIsReadFromTheRecording()
    {
        var capture = Sample();
        var gpu = new LinuxNvidiaGpu(capture.FileSystem, capture.Runner);

        Assert.True(gpu.IsAvailable);
        var sample = gpu.QueryNow();
        Assert.NotNull(sample);
        Assert.Equal("NVIDIA GeForce RTX 4070 Laptop GPU", sample!.Name);
        Assert.Equal(82, sample.TemperatureC);
    }

    [Fact]
    public void CaptureScript_RecordsTheCommandsExactlyAsTheAppRunsThem()
    {
        // The replay matches on the command line, so the script and the app must agree on every argument. A change to
        // either side that the other does not follow would make the replay answer "not in the capture".
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "linux-capture", "nitrolite-capture.sh"));

        Assert.Contains("cmd busctl --system --json=short get-property $PP $PPP $PP ActiveProfile", script);
        Assert.Contains("cmd busctl --system --json=short get-property $PP $PPP $PP Profiles", script);
        Assert.Contains("cmd powerprofilesctl get", script);
        Assert.Contains("--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total,clocks.gr,power.draw " +
                        "--format=csv,noheader,nounits", script);

        // And it never reads the files that name the unit rather than the model.
        Assert.DoesNotContain("product_serial", script);
        Assert.DoesNotContain("board_serial", script);
        Assert.DoesNotContain("product_uuid", script);
        Assert.DoesNotContain("serial_number", script.Replace("(serial_number is never read)", ""));
    }
}
