using System.IO;
using NitroLite.Linux;
using Xunit;

namespace NitroLite.Tests;

/// <summary>The XDG autostart entry behind "start with the session" on Linux.</summary>
public class LinuxRunAtStartupTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "nl-autostart-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnableWritesTheEntry_DisableRemovesIt()
    {
        var dir = TempDir();
        try
        {
            var service = new LinuxRunAtStartupService(dir, "/opt/nitrolite/NitroLite.Desktop");
            Assert.True(service.IsSupported);
            Assert.False(await service.IsEnabledAsync());

            Assert.True(await service.SetEnabledAsync(true, startMinimized: true));
            Assert.True(await service.IsEnabledAsync());
            var entry = await File.ReadAllTextAsync(service.EntryPath);
            Assert.Contains("Exec=\"/opt/nitrolite/NitroLite.Desktop\" --autostart --minimized", entry);
            Assert.StartsWith("[Desktop Entry]", entry);

            Assert.True(await service.SetEnabledAsync(true, startMinimized: false));
            Assert.DoesNotContain("--minimized", await File.ReadAllTextAsync(service.EntryPath));

            Assert.True(await service.SetEnabledAsync(false, startMinimized: false));
            Assert.False(File.Exists(service.EntryPath));
            Assert.False(await service.IsEnabledAsync());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AnEntryTheDesktopDisabled_ReadsAsDisabled()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var service = new LinuxRunAtStartupService(dir, "/usr/bin/nitrolite");
            await File.WriteAllTextAsync(service.EntryPath,
                LinuxRunAtStartupService.BuildEntry("/usr/bin/nitrolite", false).Replace(
                    "X-GNOME-Autostart-enabled=true", "X-GNOME-Autostart-enabled=false", StringComparison.Ordinal));

            Assert.False(await service.IsEnabledAsync());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("/opt/nitrolite/NitroLite.Desktop", "\"/opt/nitrolite/NitroLite.Desktop\"")]
    [InlineData("/home/u/My Apps/NitroLite.Desktop", "\"/home/u/My Apps/NitroLite.Desktop\"")]
    [InlineData(@"/odd/$path`with""quotes\", @"""/odd/\$path\`with\""quotes\\""")]
    public void Exec_IsQuotedPerTheDesktopEntrySpec(string path, string expected) =>
        Assert.Equal(expected, LinuxRunAtStartupService.Quote(path));

    [Fact]
    public void WithoutAKnownExecutable_ItIsNotOffered() =>
        Assert.False(new LinuxRunAtStartupService(TempDir(), "").IsSupported);
}
