using System.IO;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary><c>--linux-selftest</c>: what it reports, and that it changes nothing.</summary>
public class LinuxSelfTestTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void RealCapture_ReportsTheMachineAndTheMissingHelper()
    {
        var capture = LinuxCapture.LoadFile(Path.Combine(RepoRoot(), "tools", "linux-capture", "samples", "an16-41-ubuntu-26.04.txt"));
        var test = new LinuxSelfTest(capture.FileSystem, capture.Runner, helper: null, power: capture.PowerProfiles);

        var report = test.Run(new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc));

        Assert.Contains("Nitro AN16-41", report);
        Assert.Contains("Ubuntu 26.04.1 LTS", report);
        Assert.Contains("[FAIL] helper installed", report);
        Assert.Contains("[ok] operating mode: ok (balanced)", report);
        Assert.Contains("NVIDIA GeForce RTX 4070 Laptop GPU", report);
        Assert.Contains("Linux 7.4", report);

        // The capture was taken before the helper existed and with the old rule, so the boost switch really was
        // root-only on that machine; the self-test has to say so.
        Assert.Contains("[FAIL] CPU boost: permission denied", report);
        Assert.Equal(2, test.Failures);
    }

    [Fact]
    public void WriteBack_WritesTheCurrentValue_AndNeverAFanSpeed()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile("/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        fs.WithFile(LinuxPaths.PlatformProfile, "quiet\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced performance\n");
        fs.WithDirectory("/sys/class/hwmon/hwmon4");
        fs.WithFile("/sys/class/hwmon/hwmon4/name", "acer\n");
        fs.WithFile("/sys/class/hwmon/hwmon4/fan1_input", "2400\n");
        fs.WithFile("/sys/class/hwmon/hwmon4/pwm1", "128\n");
        fs.WithFile("/sys/class/hwmon/hwmon4/pwm1_enable", "2\n");

        var test = new LinuxSelfTest(fs, new NoCommands());
        var report = test.Run();

        Assert.Equal("quiet", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
        Assert.Equal("2", fs.Peek("/sys/class/hwmon/hwmon4/pwm1_enable")?.Trim());
        Assert.DoesNotContain(fs.Writes, w => w.Path.EndsWith("/pwm1", StringComparison.Ordinal));
        Assert.Contains(fs.Writes, w => w.Path == LinuxPaths.PlatformProfile && w.Value.Trim() == "quiet");
        Assert.Contains("[ok] CPU fan speed: writable (not written", report);
    }

    private sealed class NoCommands : ICommandRunner
    {
        public string? Find(string name) => null;
        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout) => CommandResult.NotRun("no");
    }
}
