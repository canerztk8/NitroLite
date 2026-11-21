using System.Text.Json;
using NitroLite.Core;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>The Linux package update: which asset it picks, how it detects its install, and what it refuses.</summary>
public class LinuxUpdateTests
{
    private const string Release = """
        {
          "tag_name": "v9.9.9",
          "html_url": "https://github.com/canerztk8/NitroLite/releases/tag/v9.9.9",
          "assets": [
            { "name": "NitroLite-v9.9.9-win-x64.zip", "size": 100,
              "browser_download_url": "https://github.com/canerztk8/NitroLite/releases/download/v9.9.9/NitroLite-v9.9.9-win-x64.zip",
              "digest": "sha256:aaaa" },
            { "name": "NitroLite-v9.9.9-amd64.deb", "size": 200,
              "browser_download_url": "https://github.com/canerztk8/NitroLite/releases/download/v9.9.9/NitroLite-v9.9.9-amd64.deb",
              "digest": "sha256:bbbb" },
            { "name": "NitroLite-v9.9.9-x86_64.rpm", "size": 300,
              "browser_download_url": "https://github.com/canerztk8/NitroLite/releases/download/v9.9.9/NitroLite-v9.9.9-x86_64.rpm",
              "digest": "sha256:cccc" }
          ]
        }
        """;

    [Theory]
    [InlineData(UpdateService.WindowsAssetSuffix, "NitroLite-v9.9.9-win-x64.zip", "aaaa")]
    [InlineData(LinuxUpdateService.DebSuffix, "NitroLite-v9.9.9-amd64.deb", "bbbb")]
    [InlineData(LinuxUpdateService.RpmSuffix, "NitroLite-v9.9.9-x86_64.rpm", "cccc")]
    public void Release_PicksTheAssetForEachInstall(string suffix, string asset, string sha)
    {
        using var doc = JsonDocument.Parse(Release);
        var info = UpdateService.ParseRelease(doc.RootElement, new Version(1, 0, 0), suffix);

        Assert.NotNull(info);
        Assert.Equal(asset, info!.AssetName);
        Assert.Equal(sha, info.Sha256);
    }

    [Fact]
    public void Release_TheOldOverloadStillMeansTheWindowsZip()
    {
        using var doc = JsonDocument.Parse(Release);
        Assert.Equal("NitroLite-v9.9.9-win-x64.zip", UpdateService.ParseRelease(doc.RootElement, new Version(1, 0, 0))!.AssetName);
    }

    private sealed class Runner : ICommandRunner
    {
        public bool RpmOwns { get; init; }
        public bool HasRpm { get; init; }
        public string? Find(string name) => name switch
        {
            "rpm" when HasRpm => "/usr/bin/rpm",
            "pkexec" => "/usr/bin/pkexec",
            _ => null
        };

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout) =>
            new(RpmOwns ? 0 : 1, "", "");
    }

    [Fact]
    public void Detect_DebNeedsTheProcessPathAndTheDpkgRecord()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/var/lib/dpkg/info/nitrolite.list", "/opt/nitrolite/NitroLite.Desktop\n");

        Assert.Equal(LinuxInstallKind.Deb, LinuxUpdateService.DetectKind(fs, new Runner(), LinuxUpdateService.PackagedExecutable));
        // Unpacked into /opt by hand is still an archive install.
        Assert.Equal(LinuxInstallKind.Archive, LinuxUpdateService.DetectKind(new InMemorySysfsFileSystem(), new Runner(), LinuxUpdateService.PackagedExecutable));
        // A copy run from elsewhere is not the package.
        Assert.Equal(LinuxInstallKind.Archive, LinuxUpdateService.DetectKind(fs, new Runner(), "/home/u/NitroLite-linux-x64/NitroLite.Desktop"));
    }

    [Fact]
    public void Detect_RpmAsksTheRpmDatabase()
    {
        var fs = new InMemorySysfsFileSystem();
        Assert.Equal(LinuxInstallKind.Rpm,
            LinuxUpdateService.DetectKind(fs, new Runner { HasRpm = true, RpmOwns = true }, LinuxUpdateService.PackagedExecutable));
        Assert.Equal(LinuxInstallKind.Archive,
            LinuxUpdateService.DetectKind(fs, new Runner { HasRpm = true, RpmOwns = false }, LinuxUpdateService.PackagedExecutable));
    }

    [Fact]
    public void InstallCommand_UsesThePackageManagerWithTheFilePath()
    {
        Assert.Equal(new[] { "apt-get", "install", "-y", "/tmp/x/NitroLite-v9.9.9-amd64.deb" },
            LinuxUpdateService.InstallCommand(LinuxInstallKind.Deb, "/tmp/x/NitroLite-v9.9.9-amd64.deb"));
        Assert.Equal(new[] { "dnf", "install", "-y", "/tmp/x/NitroLite-v9.9.9-x86_64.rpm" },
            LinuxUpdateService.InstallCommand(LinuxInstallKind.Rpm, "/tmp/x/NitroLite-v9.9.9-x86_64.rpm"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LinuxUpdateService.InstallCommand(LinuxInstallKind.Archive, "/x"));
    }

    [Fact]
    public async Task Install_ArchiveCopiesOpenTheReleasePageInstead()
    {
        var service = new LinuxUpdateService(new InMemorySysfsFileSystem(), new Runner(), LinuxInstallKind.Archive);
        Assert.False(service.IsPackaged);

        var result = await service.InstallAsync(new UpdateCheckResult("v9.9.9", null, "bbbb",
            "https://github.com/canerztk8/NitroLite/releases/download/v9.9.9/NitroLite-v9.9.9-amd64.deb", "NitroLite-v9.9.9-amd64.deb", 200));
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("http://github.com/x/NitroLite-v9.9.9-amd64.deb", "NitroLite-v9.9.9-amd64.deb", "bbbb")]   // not https
    [InlineData("https://evil.example/NitroLite-v9.9.9-amd64.deb", "NitroLite-v9.9.9-amd64.deb", "bbbb")] // not GitHub
    [InlineData("https://github.com/canerztk8/NitroLite/releases/download/v9.9.9/NitroLite-v9.9.9-win-x64.zip",
        "NitroLite-v9.9.9-win-x64.zip", "aaaa")]                                                              // wrong asset
    [InlineData("https://github.com/canerztk8/NitroLite/releases/download/v9.9.9/NitroLite-v9.9.9-amd64.deb",
        "NitroLite-v9.9.9-amd64.deb", null)]                                                                  // no digest
    public async Task Install_RefusesAnythingItCannotVerify(string url, string asset, string? sha)
    {
        var service = new LinuxUpdateService(new InMemorySysfsFileSystem(), new Runner(), LinuxInstallKind.Deb);
        var result = await service.InstallAsync(new UpdateCheckResult("v9.9.9", null, sha, url, asset, 200));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}
