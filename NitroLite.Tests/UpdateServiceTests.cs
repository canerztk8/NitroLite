using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The updater is the one place where NitroLite replaces its own executable, so every branch that can leave the
/// user without a working app is pinned here: the package layout, the digest check, the swap and its rollback.
/// </summary>
public class UpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nl_upd_" + Guid.NewGuid().ToString("N"));

    public UpdateServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Path_(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    // ---- release parsing -----------------------------------------------------------------------------------

    private static JsonElement Release(string tag, params (string Name, string? Digest)[] assets)
    {
        var assetJson = string.Join(",", assets.Select(a =>
        {
            var digest = a.Digest == null ? "" : $",\"digest\":\"{a.Digest}\"";
            return $"{{\"name\":\"{a.Name}\",\"size\":1234,\"browser_download_url\":\"https://example.test/{a.Name}\"{digest}}}";
        }));
        var json = $"{{\"tag_name\":\"{tag}\",\"assets\":[{assetJson}]}}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void ParseRelease_NewerVersionWithDigest_IsAccepted()
    {
        var info = UpdateService.ParseRelease(
            Release("v1.2.0", ("NitroLite-v1.2.0-win-x64.zip", "sha256:ABCDEF")), new Version(1, 1, 0));

        Assert.NotNull(info);
        Assert.Equal("v1.2.0", info!.DisplayVersion);
        Assert.Equal("NitroLite-v1.2.0-win-x64.zip", info.AssetName);
        Assert.Equal("abcdef", info.Sha256);
        Assert.Equal(1234, info.SizeBytes);
    }

    [Fact]
    public void ParseRelease_SameOrOlderVersion_IsIgnored()
    {
        var release = Release("v1.1.0", ("NitroLite-v1.1.0-win-x64.zip", "sha256:aa"));
        Assert.Null(UpdateService.ParseRelease(release, new Version(1, 1, 0)));
        Assert.Null(UpdateService.ParseRelease(release, new Version(1, 2, 0)));
    }

    [Fact]
    public void ParseRelease_WithoutAPublishedDigest_StillReportsTheUpdateButWithNoHash()
    {
        // The caller refuses to install without a hash; parsing must not hide the release, or the user would never
        // learn that a new version exists.
        var info = UpdateService.ParseRelease(
            Release("v1.2.0", ("NitroLite-v1.2.0-win-x64.zip", null)), new Version(1, 1, 0));

        Assert.NotNull(info);
        Assert.Null(info!.Sha256);
    }

    [Fact]
    public void ParseRelease_IgnoresAssetsForOtherPlatforms()
    {
        var info = UpdateService.ParseRelease(
            Release("v1.2.0", ("NitroLite-v1.2.0-win-arm64.zip", "sha256:aa"), ("NitroLite-v1.2.0-win-x64.zip", "sha256:bb")),
            new Version(1, 1, 0));

        Assert.Equal("NitroLite-v1.2.0-win-x64.zip", info!.AssetName);
        Assert.Equal("bb", info.Sha256);
    }

    [Fact]
    public void ParseRelease_WithoutAPackageForThisPlatform_IsIgnored()
    {
        Assert.Null(UpdateService.ParseRelease(
            Release("v1.2.0", ("NitroLite-v1.2.0-win-arm64.zip", "sha256:aa")), new Version(1, 1, 0)));
    }

    [Fact]
    public void ParseRelease_MalformedPayload_IsIgnoredRatherThanThrowing()
    {
        Assert.Null(UpdateService.ParseRelease(JsonDocument.Parse("{}").RootElement, new Version(1, 0, 0)));
        Assert.Null(UpdateService.ParseRelease(JsonDocument.Parse("[]").RootElement, new Version(1, 0, 0)));
        Assert.Null(UpdateService.ParseRelease(
            JsonDocument.Parse("{\"tag_name\":\"not-a-version\",\"assets\":[]}").RootElement, new Version(1, 0, 0)));
    }

    [Theory]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("1.2.0", 1, 2, 0)]
    [InlineData("V1.2", 1, 2, 0)]
    [InlineData("v1.2.0.0", 1, 2, 0)]
    [InlineData(" v1.3.1 ", 1, 3, 1)]
    public void TryParseTag_AcceptsTheShapesGitHubUses(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateService.TryParseTag(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("v1.2.0-beta")]
    public void TryParseTag_RejectsAnythingElse(string tag) =>
        Assert.False(UpdateService.TryParseTag(tag, out _));

    // ---- digest --------------------------------------------------------------------------------------------

    [Fact]
    public void VerifyDigest_MatchesThePublishedHashCaseInsensitively()
    {
        var file = Path_("pkg.zip");
        File.WriteAllText(file, "package");
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

        Assert.True(UpdateService.VerifyDigest(file, sha));
        Assert.True(UpdateService.VerifyDigest(file, sha.ToUpperInvariant()));
        Assert.True(UpdateService.VerifyDigest(file, "  " + sha + "  "));
    }

    [Fact]
    public void VerifyDigest_RejectsAWrongOrMissingHash()
    {
        var file = Path_("pkg.zip");
        File.WriteAllText(file, "package");

        Assert.False(UpdateService.VerifyDigest(file, new string('0', 64)));
        Assert.False(UpdateService.VerifyDigest(file, null));
        Assert.False(UpdateService.VerifyDigest(file, ""));
        Assert.False(UpdateService.VerifyDigest(Path_("missing.zip"), new string('0', 64)));
    }

    // ---- extraction ----------------------------------------------------------------------------------------

    /// <summary>Builds a zip shaped exactly like scripts/publish-release.ps1 produces.</summary>
    private string BuildPackage(string? extraEntry = null)
    {
        var zip = Path_("NitroLite-v1.2.0-win-x64.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);

        void Add(string name, string content)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        Add("NitroLite/NitroLite.exe", "new exe");
        Add("NitroLite/LICENSE", "MIT");
        if (extraEntry != null) Add(extraEntry, "surprise");
        return zip;
    }

    [Fact]
    public void ExtractToStaging_KeepsTheReleaseLayout()
    {
        var staging = UpdateService.ExtractToStaging(BuildPackage());
        try
        {
            Assert.True(File.Exists(Path.Combine(staging, "NitroLite.exe")));
            Assert.True(File.Exists(Path.Combine(staging, "LICENSE")));
            Assert.Equal("new exe", File.ReadAllText(Path.Combine(staging, "NitroLite.exe")));
        }
        finally { UpdateService.CleanUp(staging); }
    }

    [Fact]
    public void ExtractToStaging_AcceptsBackslashEntryNames()
    {
        // Compress-Archive under Windows PowerShell writes "NitroLite\NitroLite.exe", while PowerShell 7 writes
        // forward slashes. The release script runs under either, so both shapes have to extract.
        var zip = Path_("backslash.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[]
                     {
                         @"NitroLite\NitroLite.exe",
                         @"NitroLite\LICENSE"
                     })
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(name);
            }
        }

        var staging = UpdateService.ExtractToStaging(zip);
        try
        {
            Assert.True(File.Exists(Path.Combine(staging, "NitroLite.exe")));
            Assert.True(File.Exists(Path.Combine(staging, "LICENSE")));
        }
        finally { UpdateService.CleanUp(staging); }
    }

    [Fact]
    public void ExtractToStaging_RefusesAFileTheReleaseNeverContains()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            UpdateService.ExtractToStaging(BuildPackage("NitroLite/evil.dll")));
        Assert.Contains("evil.dll", ex.Message);
    }

    [Fact]
    public void ExtractToStaging_RefusesAPackageWithoutTheExecutable()
    {
        var zip = Path_("empty.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("NitroLite/LICENSE");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("MIT");
        }

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractToStaging(zip));
    }

    [Fact]
    public void ExtractToStaging_IgnoresEntriesOutsideThePackageFolder()
    {
        // A zip that carries a second top-level folder must not leak it into the staging area.
        var staging = UpdateService.ExtractToStaging(BuildPackage("Other/readme.txt"));
        try
        {
            Assert.False(Directory.Exists(Path.Combine(staging, "Other")));
        }
        finally { UpdateService.CleanUp(staging); }
    }

    [ReleasePackageFact]
    public void ExtractToStaging_AcceptsTheRealReleasePackage()
    {
        // The archive the release script actually produced, not a hand-built one.
        var package = ReleasePackage.Find();
        Assert.NotNull(package);

        var staging = UpdateService.ExtractToStaging(package!);
        try
        {
            foreach (var required in UpdateService.PackageFiles)
                Assert.True(File.Exists(Path.Combine(staging, required)), $"{required} was not extracted");

            // Nothing else may appear: the allowlist is the whole point.
            var extracted = Directory.GetFiles(staging, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(staging, file).Replace('\\', '/'))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(UpdateService.PackageFiles.OrderBy(name => name, StringComparer.Ordinal).ToArray(), extracted);
        }
        finally { UpdateService.CleanUp(staging); }
    }

    // ---- install -------------------------------------------------------------------------------------------

    private string InstallFolder()
    {
        var install = Path_("install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "NitroLite.exe"), "old exe");
        return install;
    }

    private string StagingFolder(string exeContent = "new exe")
    {
        var staging = Path_("stage");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "NitroLite.exe"), exeContent);
        File.WriteAllText(Path.Combine(staging, "LICENSE"), "MIT");
        return staging;
    }

    [Fact]
    public void InstallFromStaging_ReplacesTheExecutableAndKeepsTheOldOne()
    {
        var install = InstallFolder();
        var target = UpdateService.InstallFromStaging(StagingFolder(), install);

        Assert.Equal(Path.Combine(install, "NitroLite.exe"), target);
        Assert.Equal("new exe", File.ReadAllText(target));
        Assert.Equal("old exe", File.ReadAllText(target + ".old"));
    }

    [Fact]
    public void InstallFromStaging_RecreatesTheLicenceFile()
    {
        var install = InstallFolder();
        UpdateService.InstallFromStaging(StagingFolder(), install);

        Assert.True(File.Exists(Path.Combine(install, "LICENSE")));
    }

    [Fact]
    public void InstallFromStaging_PutsTheOldExecutableBackWhenTheCopyFails()
    {
        var install = InstallFolder();
        var staging = StagingFolder();

        // A directory where the staged executable should be makes the copy fail after the running exe was renamed.
        File.Delete(Path.Combine(staging, "NitroLite.exe"));
        Directory.CreateDirectory(Path.Combine(staging, "NitroLite.exe"));

        Assert.ThrowsAny<Exception>(() => UpdateService.InstallFromStaging(staging, install));

        Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, "NitroLite.exe")));
        Assert.False(File.Exists(Path.Combine(install, "NitroLite.exe.old")));
    }

    [Fact]
    public void InstallFromStaging_ReplacesAStaleBackupFromAnEarlierUpdate()
    {
        var install = InstallFolder();
        File.WriteAllText(Path.Combine(install, "NitroLite.exe.old"), "ancient");

        UpdateService.InstallFromStaging(StagingFolder(), install);

        Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, "NitroLite.exe.old")));
    }

    // ---- download ------------------------------------------------------------------------------------------

    /// <summary>
    /// A one-shot HTTP server on loopback. A raw socket is used instead of HttpListener because HttpListener needs a
    /// URL reservation on Windows, which a test run does not have.
    /// </summary>
    private sealed class TinyHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serve;
        private readonly byte[] _body;
        private readonly bool _truncate;

        public TinyHttpServer(byte[] body, bool truncate = false)
        {
            _body = body;
            _truncate = truncate;
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/NitroLite-v1.2.0-win-x64.zip";
            _serve = Task.Run(ServeAsync);
        }

        public string Url { get; }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                using var stream = client.GetStream();

                var buffer = new byte[4096];
                var head = new StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read == 0) break;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var response =
                    $"HTTP/1.1 200 OK\r\nContent-Length: {_body.Length}\r\nContent-Type: application/zip\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);

                var payload = _truncate ? _body.AsSpan(0, _body.Length / 2).ToArray() : _body;
                await stream.WriteAsync(payload).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                // The client hanging up is the normal end of a truncated response.
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _serve.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    private sealed class CollectingProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();
        public void Report(double value) => Values.Add(value);
    }

    private static UpdateInfo InfoFor(string url, long size) =>
        new(new Version(1, 2, 0), "v1.2.0", "NitroLite-v1.2.0-win-x64.zip", url, null, size);

    [Fact]
    public async Task DownloadAsync_WritesThePackageAndReportsProgress()
    {
        var payload = File.ReadAllBytes(BuildPackage());
        using var server = new TinyHttpServer(payload);
        var progress = new CollectingProgress();

        var file = await UpdateService.DownloadAsync(InfoFor(server.Url, payload.Length), progress);
        try
        {
            Assert.True(File.Exists(file));
            Assert.Equal(payload, File.ReadAllBytes(file));
            Assert.NotEmpty(progress.Values);
            Assert.All(progress.Values, value => Assert.InRange(value, 0, 1));
            Assert.Equal(1.0, progress.Values[^1], 3);
        }
        finally { UpdateService.CleanUp(file); }
    }

    [Fact]
    public async Task DownloadAsync_LeavesNoTemporaryFileWhenTheResponseIsCutShort()
    {
        var payload = File.ReadAllBytes(BuildPackage());
        using var server = new TinyHttpServer(payload, truncate: true);

        var before = Directory.GetFiles(Path.GetTempPath(), "NitroLite-update-*.zip").Length;
        await Assert.ThrowsAnyAsync<Exception>(() => UpdateService.DownloadAsync(InfoFor(server.Url, payload.Length)));
        var after = Directory.GetFiles(Path.GetTempPath(), "NitroLite-update-*.zip").Length;

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task DownloadAsync_ThrowsWhenTheServerRefuses()
    {
        // Nothing is listening on this port, so the request fails before a file is created.
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            UpdateService.DownloadAsync(InfoFor($"http://127.0.0.1:{port}/pkg.zip", 10)));
    }

    [Fact]
    public void CleanUpPreviousVersion_RemovesTheLeftoverExecutableAndNeverThrows()
    {
        var leftover = Path.Combine(AppContext.BaseDirectory, "NitroLite.exe.old");
        File.WriteAllText(leftover, "previous build");

        UpdateService.CleanUpPreviousVersion();
        Assert.False(File.Exists(leftover));

        // Nothing to delete is not an error.
        UpdateService.CleanUpPreviousVersion();
    }

    // ---- issue report --------------------------------------------------------------------------------------

    [Fact]
    public void BuildIssueBody_CarriesTheFourFactsAndNothingThatIdentifiesTheUser()
    {
        string original = Loc.Language;
        try
        {
            Loc.SetLanguage("en");
            var body = UpdateService.BuildIssueBody("v1.2.0", "Microsoft Windows 11 Pro", "Acer Nitro AN515-58", "Deutsch");

            Assert.Contains("NitroLite: v1.2.0", body);
            Assert.Contains("Windows: Microsoft Windows 11 Pro", body);
            Assert.Contains("Laptop: Acer Nitro AN515-58", body);
            Assert.Contains("Language: Deutsch", body);
            Assert.Contains("**What happened**", body);
            Assert.Contains("**Steps to reproduce**", body);
            Assert.DoesNotContain(Environment.UserName, body);
        }
        finally { Loc.SetLanguage(original); }
    }

    [Theory]
    [InlineData("tr", "Ne oldu", "Dizüstü")]
    [InlineData("en", "What happened", "Laptop")]
    [InlineData("de", "Was ist passiert", "Notebook")]
    [InlineData("es", "Qué ocurrió", "Portátil")]
    [InlineData("ru", "Что произошло", "Ноутбук")]
    [InlineData("pt", "O que aconteceu", "Notebook")]
    [InlineData("hi", "क्या हुआ", "लैपटॉप")]
    [InlineData("id", "Apa yang terjadi", "Laptop")]
    public void BuildIssueBody_FollowsTheSelectedLanguage(string code, string happened, string laptop)
    {
        // The person filing the report is the one reading the prefilled page, so its labels follow their language.
        string original = Loc.Language;
        try
        {
            Loc.SetLanguage(code);
            var body = UpdateService.BuildIssueBody("v1.2.0", "Windows 11", "Acer Nitro AN515-58", "Deutsch");

            Assert.Contains($"**{happened}**", body);
            Assert.Contains($"{laptop}: Acer Nitro AN515-58", body);
            Assert.Contains("NitroLite: v1.2.0", body);
            Assert.Contains("Windows: Windows 11", body);
            if (code != "en") Assert.DoesNotContain("**What happened**", body);
        }
        finally { Loc.SetLanguage(original); }
    }

    [Fact]
    public void BuildIssueUrl_EscapesTheTitleAndBody()
    {
        var url = UpdateService.BuildIssueUrl("NitroLite hata bildirimi", "line one\nline two & more");

        Assert.StartsWith(UpdateService.NewIssueUrl + "?title=", url);
        Assert.Contains("%0A", url);
        Assert.Contains("%26", url);
        Assert.DoesNotContain("\n", url);
    }

    [Fact]
    public void GetMachineModel_NeverReturnsAnEmptyString()
    {
        Assert.False(string.IsNullOrWhiteSpace(UpdateService.GetMachineModel()));
    }

    // ---- version -------------------------------------------------------------------------------------------

    [Fact]
    public void CurrentVersionText_IsTheVersionTheBuildReports()
    {
        var version = UpdateService.CurrentVersion;
        var expected = version.Build > 0
            ? $"v{version.Major}.{version.Minor}.{version.Build}"
            : $"v{version.Major}.{version.Minor}";
        Assert.Equal(expected, UpdateService.CurrentVersionText);
    }

    [Fact]
    public void PackageFiles_MatchWhatTheReleaseScriptProduces()
    {
        // scripts/publish-release.ps1 throws on any other file, so this list and that script must agree.
        Assert.Equal(
            new[]
            {
                "NitroLite.exe",
                "LICENSE"
            },
            UpdateService.PackageFiles);
    }
}
