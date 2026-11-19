using System;
using System.Collections.Generic;
using System.Linq;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The "report this laptop on GitHub" link: a title from public model strings, a body from the sanitised report, and a
/// hard length cap because a URL is not a file.
/// </summary>
/// <remarks>
/// The link exists so that a compatibility report arrives without the owner having to know what to paste where. That
/// makes two things load-bearing. The encoding has to survive the trip — the report is full of <c>°</c>, <c>|</c>,
/// newlines and <c>&amp;</c>, and one unescaped ampersand would cut the body at the first table row. And the cap has to
/// be enforced here rather than by the browser, because every browser cuts at a different point and some cut silently,
/// which would leave half a report in a public issue and no sign that anything was lost.
/// </remarks>
public class CompatibilityReportUrlTests
{
    private const string SmallReport =
        "### NitroLite hardware report\n\n| Device | |\n| --- | --- |\n| Model | Nitro AN16-61 |\n"
        + "| BIOS | INSYDE Corp. 1.53 |\n\n| Sensors | |\n| --- | --- |\n| Runtime verification | runtime-verified (2 samples) |\n";

    [Fact]
    public void TheBaseIsTheRepositorysNewIssueForm()
    {
        Assert.Equal("https://github.com/canerztk8/NitroLite/issues/new", CompatibilityReportUrl.NewIssueUrl);
        Assert.Equal(CompatibilityReportUrl.NewIssueUrl, UpdateService.NewIssueUrl);
    }

    [Theory]
    [InlineData("AN16-61", "EV9_SKF", "[Compatibility] AN16-61 (EV9_SKF)")]
    [InlineData("AN16-61", null, "[Compatibility] AN16-61")]
    [InlineData("AN16-61", "", "[Compatibility] AN16-61")]
    [InlineData("", "EV9_SKF", "[Compatibility] (EV9_SKF)")]
    [InlineData(null, null, "[Compatibility]")]
    [InlineData("  AN16-61  ", "  EV9_SKF  ", "[Compatibility] AN16-61 (EV9_SKF)")]
    public void TheTitleNamesTheModelAndTheBoard(string? model, string? board, string expected) =>
        Assert.Equal(expected, CompatibilityReportUrl.BuildTitle(model, board));

    [Fact]
    public void AnAbsurdlyLongTitleIsCutRatherThanRefused()
    {
        var title = CompatibilityReportUrl.BuildTitle(new string('M', 400), new string('B', 400));

        Assert.True(title.Length <= CompatibilityReportUrl.MaximumTitleLength);
        Assert.StartsWith("[Compatibility] MMM", title);
    }

    [Fact]
    public void TheUrlCarriesTheTitleAndTheReport()
    {
        var url = CompatibilityReportUrl.Build("AN16-61", "EV9_SKF", SmallReport);

        Assert.StartsWith(CompatibilityReportUrl.NewIssueUrl + "?title=", url);
        Assert.Contains(Uri.EscapeDataString("[Compatibility] AN16-61 (EV9_SKF)"), url);
        Assert.Contains("&body=", url);
        Assert.True(CompatibilityReportUrl.IsCompatibilityUrl(url));
    }

    [Fact]
    public void AShortReportSurvivesTheRoundTripUnchanged()
    {
        var url = CompatibilityReportUrl.Build("AN16-61", "EV9_SKF", SmallReport);
        var body = DecodeBody(url);

        Assert.Equal(SmallReport.Trim(), body);
    }

    [Theory]
    [InlineData("°C")]
    [InlineData("| table | cell |")]
    [InlineData("a & b")]
    [InlineData("line one\nline two")]
    [InlineData("100 % #1 ?x=1")]
    [InlineData("ünïcode · 温度")]
    [InlineData("emoji 😀 pair")]
    public void EverythingThatWouldBreakAUrlIsEscaped(string text)
    {
        var url = CompatibilityReportUrl.Build("M", "B", text);
        var body = DecodeBody(url);

        Assert.Equal(text, body);

        // The characters that would end or split the query are not in the raw URL; only their escapes are.
        Assert.DoesNotContain("&body=a & b", url);
        Assert.DoesNotContain("\n", url);
        Assert.DoesNotContain(" ", url);
        Assert.DoesNotContain("°", url);
        Assert.DoesNotContain("|", url);
    }

    [Fact]
    public void CarriageReturnsAreNormalisedSoTheBodyHasOneNewlinePerLine()
    {
        var url = CompatibilityReportUrl.Build("M", "B", "line one\r\nline two\rline three");

        Assert.Equal("line one\nline two\nline three", DecodeBody(url));
    }

    [Fact]
    public void ALongReportIsCutWithANoteSayingTheClipboardHasTheRest()
    {
        var huge = string.Concat(Enumerable.Repeat(SmallReport, 200));
        var url = CompatibilityReportUrl.Build("AN16-61", "EV9_SKF", huge);

        Assert.True(url.Length <= CompatibilityReportUrl.MaximumUrlLength, $"length {url.Length}");
        Assert.True(CompatibilityReportUrl.IsCompatibilityUrl(url));

        var body = DecodeBody(url);
        Assert.EndsWith(CompatibilityReportUrl.TruncationNote.Trim(), body);
        Assert.Contains("clipboard", body[(body.Length - CompatibilityReportUrl.TruncationNote.Length)..]);

        // The kept part is real report text, not a mangled escape: it still starts at the beginning.
        Assert.StartsWith("### NitroLite hardware report", body);
    }

    [Fact]
    public void TheCutNeverSplitsASurrogatePair()
    {
        // A body of surrogate pairs at the exact edge of the budget is the case that would leave a lone half in the
        // issue and render as a replacement character.
        var emoji = string.Concat(Enumerable.Repeat("😀", 4000));
        var url = CompatibilityReportUrl.Build("M", "B", emoji);
        var body = DecodeBody(url);

        Assert.True(url.Length <= CompatibilityReportUrl.MaximumUrlLength);
        Assert.DoesNotContain('\uFFFD', body);

        var kept = body[..^CompatibilityReportUrl.TruncationNote.Length].TrimEnd();
        Assert.True(kept.Length % 2 == 0, $"a lone surrogate survived the cut: length {kept.Length}");
        for (int i = 0; i < kept.Length; i += 2) Assert.True(char.IsSurrogatePair(kept, i));
    }

    [Fact]
    public void AnEmptyReportStillProducesAUsableTitleOnlyLink()
    {
        var url = CompatibilityReportUrl.Build("AN16-61", "EV9_SKF", null);

        Assert.Equal(CompatibilityReportUrl.NewIssueUrl + "?title="
                     + Uri.EscapeDataString("[Compatibility] AN16-61 (EV9_SKF)") + "&body=", url);
        Assert.True(CompatibilityReportUrl.IsCompatibilityUrl(url));
    }

    [Fact]
    public void NothingPrivateCanReachTheLink()
    {
        var url = CompatibilityReportUrl.Build("AN16-61", "EV9_SKF", SmallReport);

        foreach (var fragment in CompatibilityReportUrl.ForbiddenFragments)
            Assert.DoesNotContain(fragment, url, StringComparison.OrdinalIgnoreCase);

        // The title comes from SMBIOS strings, and SMBIOS is where a serial number sometimes hides. A model code and a
        // board name are kept; a serial-shaped value in the same field is not this builder's job to guess at, so the
        // test pins the one thing it can promise: the link is the title plus the report, nothing else.
        Assert.Equal(
            CompatibilityReportUrl.NewIssueUrl + "?title="
            + Uri.EscapeDataString(CompatibilityReportUrl.BuildTitle("AN16-61", "EV9_SKF"))
            + "&body=" + Uri.EscapeDataString(SmallReport.Trim()),
            url);
    }

    [Fact]
    public void IsCompatibilityUrlRejectsAnythingElse()
    {
        Assert.False(CompatibilityReportUrl.IsCompatibilityUrl(null));
        Assert.False(CompatibilityReportUrl.IsCompatibilityUrl(""));
        Assert.False(CompatibilityReportUrl.IsCompatibilityUrl("https://example.com/issues/new?title=x"));
        Assert.False(CompatibilityReportUrl.IsCompatibilityUrl("javascript:alert(1)"));
        Assert.False(CompatibilityReportUrl.IsCompatibilityUrl(
            CompatibilityReportUrl.NewIssueUrl + "?title=" + new string('x', CompatibilityReportUrl.MaximumUrlLength)));
    }

    [Fact]
    public void DescribeLengthNeverPrintsTheReportItself()
    {
        var url = CompatibilityReportUrl.Build("AN16-61", "EV9_SKF", SmallReport);
        var text = CompatibilityReportUrl.DescribeLength(url);

        Assert.Contains(
            url.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        Assert.Contains(
            CompatibilityReportUrl.MaximumUrlLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("###", text);
    }

    /// <summary>The body of a built URL, decoded the way GitHub decodes it.</summary>
    private static string DecodeBody(string url)
    {
        var at = url.IndexOf("&body=", StringComparison.Ordinal);
        Assert.True(at > 0, "the URL carries no body");
        return Uri.UnescapeDataString(url[(at + "&body=".Length)..]);
    }
}
