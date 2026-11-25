using System.IO;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Two update defects. The previous executable was swept at the new build's first start, before that build had shown it
/// could start at all, so a release that crashed in its start-up left nothing to go back to. And every failed release
/// check — offline, rate-limited, a release without this platform's package — was reported as "up to date".
/// </summary>
public class CleanupCUpdateTests : IDisposable
{
    private readonly string _root;

    public CleanupCUpdateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NitroLite-cleanupc-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Install => Path.Combine(_root, "install");

    private string Exe => Path.Combine(Install, "NitroLite.exe");

    private string Marker => Path.Combine(Install, UpdateService.PendingUpdateMarkerName);

    /// <summary>An install folder running "old exe", updated in place to "new exe" through the real two-phase install.</summary>
    private void InstallAnUpdate()
    {
        Directory.CreateDirectory(Install);
        File.WriteAllText(Exe, "old exe");

        var staging = Path.Combine(Path.GetTempPath(), "NitroLite-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllText(Path.Combine(staging, "NitroLite.exe"), "new exe");
            File.WriteAllText(Path.Combine(staging, "LICENSE"), "MIT");
            UpdateService.InstallFromStaging(staging, Install);
        }
        finally
        {
            UpdateService.CleanUp(staging);
        }
    }

    // ---------------------------------------------------------------- rollback

    [Fact]
    public void AnInstalledUpdate_IsMarkedPending_AndItsPreviousExecutableSurvivesTheStartupSweep()
    {
        InstallAnUpdate();

        Assert.True(File.Exists(Marker));
        Assert.True(UpdateService.IsUpdatePending(Install));

        var report = UpdateService.RecoverInterruptedUpdate(Install);

        Assert.Empty(report.Failed);
        Assert.Equal("new exe", File.ReadAllText(Exe));
        Assert.Equal("old exe", File.ReadAllText(Exe + ".old"));
        Assert.DoesNotContain(Exe + ".old", report.Removed);
    }

    [Fact]
    public void AHealthyStart_SweepsThePreviousExecutable_AndClearsTheMarker()
    {
        InstallAnUpdate();
        Assert.Equal(PendingUpdateState.Pending, UpdateService.RecordStartupAttempt(Install));

        UpdateService.MarkStartupHealthy(Install);

        Assert.False(File.Exists(Marker));
        Assert.False(File.Exists(Exe + ".old"));
        Assert.Equal("new exe", File.ReadAllText(Exe));

        // Nothing is pending any more, so later starts neither count nor roll back.
        Assert.Equal(PendingUpdateState.None, UpdateService.RecordStartupAttempt(Install));
    }

    [Fact]
    public void ABuildThatNeverReachesAHealthyStart_IsRolledBackAfterTheAllowedAttempts()
    {
        InstallAnUpdate();

        for (var i = 0; i < UpdateService.MaxUnconfirmedStarts; i++)
        {
            Assert.Equal(PendingUpdateState.Pending, UpdateService.RecordStartupAttempt(Install));
            Assert.Equal("new exe", File.ReadAllText(Exe));
        }

        Assert.Equal(PendingUpdateState.RolledBack, UpdateService.RecordStartupAttempt(Install));

        // The build that worked is back; the failed one is renamed aside, never deleted here.
        Assert.Equal("old exe", File.ReadAllText(Exe));
        Assert.False(File.Exists(Exe + ".old"));
        Assert.False(File.Exists(Marker));
        var aside = Assert.Single(Directory.GetFiles(Install, "NitroLite.exe.old-stale-*"));
        Assert.Equal("new exe", File.ReadAllText(aside));

        // The next start's ordinary sweep takes the failed build away.
        var report = UpdateService.RecoverInterruptedUpdate(Install);
        Assert.Contains(aside, report.Removed);
        Assert.Equal("old exe", File.ReadAllText(Exe));
    }

    [Fact]
    public void OneUnconfirmedStart_DoesNotRollBack()
    {
        // A user who closes the window a moment after it opens must not lose an update that works.
        InstallAnUpdate();

        Assert.Equal(PendingUpdateState.Pending, UpdateService.RecordStartupAttempt(Install));
        Assert.Equal(PendingUpdateState.Pending, UpdateService.RecordStartupAttempt(Install));

        Assert.Equal("new exe", File.ReadAllText(Exe));
    }

    [Fact]
    public void AGarbledMarker_CountsAsZero_AndNeverTriggersARollback()
    {
        InstallAnUpdate();
        File.WriteAllText(Marker, "not a number");

        Assert.Equal(PendingUpdateState.Pending, UpdateService.RecordStartupAttempt(Install));
        Assert.Equal("new exe", File.ReadAllText(Exe));
    }

    [Fact]
    public void AMarkerWithNoPreviousExecutable_IsDropped()
    {
        InstallAnUpdate();
        File.Delete(Exe + ".old");

        Assert.Equal(PendingUpdateState.None, UpdateService.RecordStartupAttempt(Install));
        Assert.False(File.Exists(Marker));
        Assert.Equal("new exe", File.ReadAllText(Exe));
    }

    [Fact]
    public void AFirstInstallIntoAnEmptyFolder_IsNotMarkedPending()
    {
        Directory.CreateDirectory(Install);
        var staging = Path.Combine(Path.GetTempPath(), "NitroLite-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllText(Path.Combine(staging, "NitroLite.exe"), "new exe");
            UpdateService.InstallFromStaging(staging, Install);
        }
        finally
        {
            UpdateService.CleanUp(staging);
        }

        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void AnExecutableRestoredFromItsBackup_ClearsThePendingMarker()
    {
        // A power cut between the two moves of the commit: the executable is gone, the backup is the build that worked.
        InstallAnUpdate();
        File.Delete(Exe);

        var report = UpdateService.RecoverInterruptedUpdate(Install);

        Assert.Contains(Exe, report.Restored);
        Assert.Equal("old exe", File.ReadAllText(Exe));
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void MarkStartupHealthy_WithNothingPending_TouchesNothing()
    {
        Directory.CreateDirectory(Install);
        File.WriteAllText(Exe, "exe");
        File.WriteAllText(Exe + ".old", "user kept this?");

        UpdateService.MarkStartupHealthy(Install);

        // Without a marker the method does nothing; the ordinary startup sweep owns this file.
        Assert.True(File.Exists(Exe + ".old"));
    }

    // ---------------------------------------------------------------- three-way check result

    private static JsonElement Release(string tag, params string[] assetNames)
    {
        var assets = assetNames.Select(name => new Dictionary<string, object>
        {
            ["name"] = name,
            ["browser_download_url"] = $"https://github.com/canerztk8/NitroLite/releases/download/{tag}/{name}",
            ["size"] = 123,
            ["digest"] = "sha256:" + new string('a', 64)
        });
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["tag_name"] = tag,
            ["html_url"] = $"https://github.com/canerztk8/NitroLite/releases/tag/{tag}",
            ["assets"] = assets.ToArray()
        });
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ClassifyRelease_AnOlderOrEqualTag_IsCurrent()
    {
        var outcome = UpdateService.ClassifyRelease(Release("v1.2.0", "NitroLite-v1.2.0-win-x64.zip"),
            new Version(1, 2, 0), UpdateService.WindowsAssetSuffix);

        Assert.Equal(UpdateCheckStatus.Current, outcome.Status);
        Assert.Null(outcome.Update);
    }

    [Fact]
    public void ClassifyRelease_ANewerTagWithThePackage_IsFound()
    {
        var outcome = UpdateService.ClassifyRelease(Release("v9.0.0", "NitroLite-v9.0.0-win-x64.zip"),
            new Version(1, 2, 0), UpdateService.WindowsAssetSuffix);

        Assert.Equal(UpdateCheckStatus.Found, outcome.Status);
        Assert.NotNull(outcome.Update);
        Assert.Equal("v9.0.0", outcome.Update!.DisplayVersion);
        Assert.Equal("NitroLite-v9.0.0-win-x64.zip", outcome.Update.AssetName);
        Assert.Equal(new string('a', 64), outcome.Update.Sha256);
    }

    [Fact]
    public void ClassifyRelease_ANewerTagWithoutThisPlatformsPackage_IsAFailure_NotUpToDate()
    {
        var outcome = UpdateService.ClassifyRelease(Release("v9.0.0", "NitroLite-v9.0.0-linux-x64.deb"),
            new Version(1, 2, 0), UpdateService.WindowsAssetSuffix);

        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.Equal(UpdateCheckFailure.NoPackage, outcome.Failure);
    }

    [Fact]
    public void ClassifyRelease_AnUnreadableAnswer_IsAFailure()
    {
        using var document = JsonDocument.Parse("""{"message":"API rate limit exceeded"}""");

        var outcome = UpdateService.ClassifyRelease(document.RootElement, new Version(1, 2, 0),
            UpdateService.WindowsAssetSuffix);

        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.Equal(UpdateCheckFailure.BadResponse, outcome.Failure);
    }

    [Fact]
    public void ClassifyRelease_AgreesWithParseRelease_OnEveryFoundRelease()
    {
        var release = Release("v9.1.2", "NitroLite-v9.1.2-win-x64.zip");
        var info = UpdateService.ParseRelease(release, new Version(1, 0, 0));
        var outcome = UpdateService.ClassifyRelease(release, new Version(1, 0, 0), UpdateService.WindowsAssetSuffix);

        Assert.NotNull(info);
        Assert.Equal(UpdateService.ToCheckResult(info!), outcome.Update);
    }

    private sealed class OldStyleUpdates(UpdateCheckResult? answer) : IUpdateService
    {
        public string CurrentVersionText => "v1.0.0";
        public bool IsPackaged => false;
        public string ReleasePageUrl => "https://example.invalid/releases";
        public Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(answer);
        public Task<UpdateInstallResult> InstallAsync(UpdateCheckResult result, IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(UpdateInstallResult.Fail("no"));
        public string ReleaseNotesUrl(UpdateCheckResult? result) => ReleasePageUrl;
        public string BuildIssueBody(string version, string osDescription, string machineModel, string language) => "";
        public string GetMachineModel() => "";
        public string BuildIssueUrl(string title, string body) => "";
    }

    [Fact]
    public async Task TheDefaultThreeWayCheck_MapsAnOldImplementationsAnswer()
    {
        IUpdateService none = new OldStyleUpdates(null);
        IUpdateService found = new OldStyleUpdates(new UpdateCheckResult("v2.0.0", null, null));

        Assert.Equal(UpdateCheckStatus.Current, (await none.CheckForUpdateAsync()).Status);
        var outcome = await found.CheckForUpdateAsync();
        Assert.Equal(UpdateCheckStatus.Found, outcome.Status);
        Assert.Equal("v2.0.0", outcome.Update!.DisplayVersion);
    }
}
