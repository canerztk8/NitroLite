using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The sidebar mouse card is the only place a user learns why the ATK profile did not apply. A silent
/// wireless mouse is retried every few seconds, so the retrying state must not read like a dead end.
/// </summary>
public class MouseProfileStatusTextTests
{
    [Fact]
    public void NoResponse_WhileRetrying_ReportsThatTheMouseIsWaking()
    {
        Assert.Equal("Uyanıyor…", MainWindow.DescribeMouseFailureShort(AtkMouseResult.NoResponse, retryPending: true));
    }

    [Fact]
    public void NoResponse_WithoutARetry_StaysTerminal()
    {
        Assert.Equal("Yanıt yok", MainWindow.DescribeMouseFailureShort(AtkMouseResult.NoResponse));
    }

    [Theory]
    [InlineData((int)AtkMouseResult.ReceiverNotFound, "Alıcı yok")]
    [InlineData((int)AtkMouseResult.Rejected, "Reddedildi")]
    [InlineData((int)AtkMouseResult.ProfileNotConfirmed, "Doğrulanamadı")]
    [InlineData((int)AtkMouseResult.CommunicationError, "İletişim hatası")]
    public void OtherFailures_IgnoreTheRetryFlag(int result, string expected)
    {
        var failure = (AtkMouseResult)result;
        Assert.Equal(expected, MainWindow.DescribeMouseFailureShort(failure, retryPending: false));
        Assert.Equal(expected, MainWindow.DescribeMouseFailureShort(failure, retryPending: true));
    }

    [Fact]
    public void SidebarFailureTexts_FitTheValueColumn()
    {
        // Sidebar 210 - border padding 24 - card margin 8 - card padding 24 = 154 for the row.
        // "Fare Profili:" measures 53.4 and the value keeps a 6 px left margin, leaving ~95 px.
        const double valueColumnWidth = 95.0;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold,
            FontStretches.Normal);

        foreach (AtkMouseResult result in Enum.GetValues(typeof(AtkMouseResult)))
        {
            foreach (bool retryPending in new[] { false, true })
            {
                string text = MainWindow.DescribeMouseFailureShort(result, retryPending);
                var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, 11, Brushes.Black, 96.0);
                Assert.True(formatted.WidthIncludingTrailingWhitespace <= valueColumnWidth,
                    $"'{text}' measures {formatted.WidthIncludingTrailingWhitespace:F1} px, over the {valueColumnWidth} px budget");
            }
        }
    }
}
