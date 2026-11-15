using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// A wireless ATK mouse answers vendor HID commands only while it is awake, and it wakes on user input. The
/// retry schedule therefore has to be dense right after a failure and has to react to input, otherwise every
/// attempt lands while the mouse is asleep again and the profile never applies.
/// </summary>
public class MouseProfileRetryTests
{
    [Fact]
    public void FirstAttemptsAreCloseTogether()
    {
        // The user usually touches the mouse a second or two after the switch fails. Three attempts inside the
        // first two seconds is what catches that window.
        Assert.True(MainWindow.MouseRetryDelayMs(1) <= 500);
        Assert.True(MainWindow.MouseRetryDelayMs(2) <= 900);
        Assert.True(MainWindow.MouseRetryDelayMs(3) <= 1300);
    }

    [Fact]
    public void ScheduleNeverGetsFasterAsAttemptsPileUp()
    {
        long previous = 0;
        for (int attempts = 1; attempts <= 40; attempts++)
        {
            long delay = MainWindow.MouseRetryDelayMs(attempts);
            Assert.True(delay >= previous, $"attempt {attempts} waits {delay} ms, less than the {previous} ms before it");
            previous = delay;
        }
    }

    [Fact]
    public void ScheduleSettlesInsteadOfGrowingWithoutBound()
    {
        // A mouse that is switched off must not turn into a HID hammer, and must not back off so far that the
        // user has to wait minutes after switching it back on.
        Assert.InRange(MainWindow.MouseRetryDelayMs(50), 1000, 15000);
        Assert.Equal(MainWindow.MouseRetryDelayMs(50), MainWindow.MouseRetryDelayMs(500));
    }

    [Fact]
    public void ASilentMouseBacksOffToASlowPoll()
    {
        // Past a minute of silence the mouse is more likely off than asleep. User input still bypasses the
        // schedule, so this only bounds the cost of a mouse that is not there.
        Assert.True(MainWindow.MouseRetryDelayMs(40) >= 5000);
    }

    [Fact]
    public void EveryDelayIsPositive()
    {
        for (int attempts = 0; attempts <= 10; attempts++)
            Assert.True(MainWindow.MouseRetryDelayMs(attempts) > 0);
    }

    [Fact]
    public void LastInputTickIsInThePastAndNotZero()
    {
        long tick = UserActivity.LastInputTick;
        Assert.True(tick > 0, "GetLastInputInfo returned nothing; the activity-triggered retry would never fire");
        Assert.True(tick <= Environment.TickCount64, "the last input tick is in the future");
    }

    [Fact]
    public void LastInputTickIsStableWhenNothingHappens()
    {
        // The retry compares this value against the one recorded at the last attempt. A value that jitters on
        // every read would fire a retry on every poll tick.
        long first = UserActivity.LastInputTick;
        long second = UserActivity.LastInputTick;
        Assert.InRange(second - first, 0, 50);
    }

    [Fact]
    public void DeferredRowTextFitsTheValueColumn()
    {
        // Same 95 px budget the failure texts are held to.
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold,
            FontStretches.Normal);
        foreach (var text in new[] { "Tıklayın", "Oyun bitince" })
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 11, Brushes.Black, 96.0);
            Assert.True(formatted.WidthIncludingTrailingWhitespace <= 95.0,
                $"'{text}' measures {formatted.WidthIncludingTrailingWhitespace:F1} px, over the 95 px budget");
        }
    }

    [Fact]
    public void DeferredRowTextIsTranslatedInEveryCatalogue()
    {
        foreach (var code in new[] { "en", "de", "es", "ru", "pt", "hi", "id" })
        {
            var catalogue = Loc.LoadCatalog(code);
            foreach (var key in new[] { "Oyun bitince", "Tıklayın" })
            {
                Assert.True(catalogue.TryGetValue(key, out var value), $"{code} is missing '{key}'");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{code} has an empty '{key}'");
            }
        }
    }
}
