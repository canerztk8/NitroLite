using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The release-notes button is the second half of the title bar's update notice, and the only thing it does is hand
/// a URL from a JSON payload to the shell. Both halves of that are pinned here: the button must never outlive the
/// badge it sits next to, and the address must never be anything but https.
/// </summary>
public class ReleaseNotesButtonTests
{
    private static UpdateInfo Pending(string? htmlUrl = "https://github.com/canerztk8/NitroLite/releases/tag/v1.2.5") =>
        new(new Version(1, 2, 5), "v1.2.5", "NitroLite-v1.2.5-win-x64.zip",
            "https://github.com/canerztk8/NitroLite/releases/download/v1.2.5/NitroLite-v1.2.5-win-x64.zip",
            "abcdef", 1234, htmlUrl);

    // ---- visibility lifecycle ------------------------------------------------------------------------------

    [Fact]
    public void VisibilityFor_NoPendingRelease_IsCollapsed()
    {
        Assert.Equal(Visibility.Collapsed, UpdateNotice.VisibilityFor(null));
    }

    [Fact]
    public void VisibilityFor_APendingRelease_IsVisible()
    {
        Assert.Equal(Visibility.Visible, UpdateNotice.VisibilityFor(Pending()));
    }

    [Fact]
    public async Task Apply_MovesTheBadgeAndTheNotesButtonTogether()
    {
        await StaTestRunner.RunInStaAsync(() =>
        {
            var badge = new Button { Visibility = Visibility.Collapsed };
            var notes = new Button { Visibility = Visibility.Collapsed };

            UpdateNotice.Apply(badge, notes, Pending());
            Assert.Equal(Visibility.Visible, badge.Visibility);
            Assert.Equal(Visibility.Visible, notes.Visibility);

            UpdateNotice.Apply(badge, notes, null);
            Assert.Equal(Visibility.Collapsed, badge.Visibility);
            Assert.Equal(Visibility.Collapsed, notes.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Apply_StartsCollapsedAndReturnsToCollapsedAfterTheUpdateIsCleared()
    {
        // The lifecycle the title bar actually runs: nothing at start, both halves on when GitHub reports a release,
        // both off again once it is installed or the notice is cleared.
        await StaTestRunner.RunInStaAsync(() =>
        {
            var badge = new Button { Visibility = Visibility.Collapsed };
            var notes = new Button { Visibility = Visibility.Collapsed };

            Assert.Equal(Visibility.Collapsed, notes.Visibility);

            UpdateNotice.Apply(badge, notes, Pending());
            Assert.Equal(Visibility.Visible, notes.Visibility);

            UpdateNotice.Apply(badge, notes, null);
            Assert.Equal(Visibility.Collapsed, notes.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Apply_ToleratesAMissingElement()
    {
        // The test harness builds a window without its XAML, so the named fields are null there. A null must not
        // throw, and it must not stop the other half from moving.
        await StaTestRunner.RunInStaAsync(() =>
        {
            var notes = new Button { Visibility = Visibility.Collapsed };

            UpdateNotice.Apply(null, notes, Pending());
            Assert.Equal(Visibility.Visible, notes.Visibility);

            UpdateNotice.Apply(null, null, Pending());
            UpdateNotice.Apply(null, notes, null);
            Assert.Equal(Visibility.Collapsed, notes.Visibility);
            return Task.CompletedTask;
        });
    }

    // ---- safe URL validation --------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/canerztk8/NitroLite/releases/tag/v1.2.5")]
    [InlineData("HTTPS://github.com/canerztk8/NitroLite")]
    [InlineData("https://example.test/a?b=c#d")]
    public void IsSafeHttpsUrl_AcceptsHttps(string url)
    {
        Assert.True(UpdateService.IsSafeHttpsUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://github.com/canerztk8/NitroLite")]          // downgraded scheme
    [InlineData("file:///C:/Windows/System32/calc.exe")]           // local executable
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.test/x")]
    [InlineData("//github.com/canerztk8/NitroLite")]               // protocol-relative
    [InlineData("github.com/canerztk8/NitroLite")]                 // no scheme at all
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("https://")]                                       // scheme but no host
    public void IsSafeHttpsUrl_RejectsEverythingElse(string? url)
    {
        Assert.False(UpdateService.IsSafeHttpsUrl(url));
    }

    [Fact]
    public void ReleaseNotesUrl_UsesTheReleasePageGitHubReported()
    {
        Assert.Equal("https://github.com/canerztk8/NitroLite/releases/tag/v1.2.5",
            UpdateService.ReleaseNotesUrl(Pending()));
    }

    [Fact]
    public void ReleaseNotesUrl_WithoutAReleasePage_FallsBackToTheReleasesList()
    {
        Assert.Equal(UpdateService.ReleasePageUrl, UpdateService.ReleaseNotesUrl(Pending(htmlUrl: null)));
        Assert.Equal(UpdateService.ReleasePageUrl, UpdateService.ReleaseNotesUrl(null));
    }

    [Theory]
    [InlineData("http://github.com/canerztk8/NitroLite/releases/tag/v1.2.5")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void ReleaseNotesUrl_RefusesAnUnsafeAddressFromThePayload(string htmlUrl)
    {
        // The payload is remote input. A page that is not https is dropped rather than opened, and the button still
        // has somewhere to go.
        Assert.Equal(UpdateService.ReleasePageUrl, UpdateService.ReleaseNotesUrl(Pending(htmlUrl)));
    }

    [Fact]
    public void ParseRelease_ReadsTheReleasePageUrl()
    {
        var json = """
            {
              "tag_name": "v1.2.5",
              "html_url": "https://github.com/canerztk8/NitroLite/releases/tag/v1.2.5",
              "assets": [
                {
                  "name": "NitroLite-v1.2.5-win-x64.zip",
                  "size": 1234,
                  "browser_download_url": "https://example.test/NitroLite-v1.2.5-win-x64.zip",
                  "digest": "sha256:abcdef"
                }
              ]
            }
            """;

        var info = UpdateService.ParseRelease(JsonDocument.Parse(json).RootElement.Clone(), new Version(1, 2, 0));

        Assert.NotNull(info);
        Assert.Equal("https://github.com/canerztk8/NitroLite/releases/tag/v1.2.5", info!.HtmlUrl);
        Assert.Equal("https://github.com/canerztk8/NitroLite/releases/tag/v1.2.5", UpdateService.ReleaseNotesUrl(info));
    }

    [Fact]
    public void ParseRelease_WithoutAReleasePageUrl_StillReportsTheUpdate()
    {
        // A payload that carries no html_url must not hide the release: the update is still installable, the notes
        // button just falls back to the releases list.
        var json = """
            {
              "tag_name": "v1.2.5",
              "assets": [
                {
                  "name": "NitroLite-v1.2.5-win-x64.zip",
                  "size": 1234,
                  "browser_download_url": "https://example.test/NitroLite-v1.2.5-win-x64.zip",
                  "digest": "sha256:abcdef"
                }
              ]
            }
            """;

        var info = UpdateService.ParseRelease(JsonDocument.Parse(json).RootElement.Clone(), new Version(1, 2, 0));

        Assert.NotNull(info);
        Assert.Null(info!.HtmlUrl);
        Assert.Equal(UpdateService.ReleasePageUrl, UpdateService.ReleaseNotesUrl(info));
    }
}
