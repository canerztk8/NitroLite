using NitroLite;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins how long a toast stays on screen. The banner used to be a fixed three seconds and a single unwrapped line,
/// so a message that explained a failure was both cut off at the window edge and gone before it could be read.
/// </summary>
public class ToastDurationTests
{
    [Fact]
    public void ShortMessage_KeepsTheFloor()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), MainWindow.ToastDurationFor("Ayarlar kaydedildi"));
    }

    [Fact]
    public void EmptyMessage_KeepsTheFloor()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), MainWindow.ToastDurationFor(""));
        Assert.Equal(TimeSpan.FromSeconds(3), MainWindow.ToastDurationFor(null));
    }

    [Fact]
    public void LongerMessage_StaysUpLonger()
    {
        var shortMessage = new string('a', 22);
        var longMessage = new string('a', 110);

        Assert.True(MainWindow.ToastDurationFor(longMessage) > MainWindow.ToastDurationFor(shortMessage));
    }

    [Fact]
    public void VeryLongMessage_IsCappedSoItCannotSitOnTheScreen()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), MainWindow.ToastDurationFor(new string('a', 4000)));
    }

    [Fact]
    public void Duration_NeverFallsBelowTheFloor()
    {
        for (int length = 0; length <= 400; length += 7)
        {
            var duration = MainWindow.ToastDurationFor(new string('a', length));
            Assert.InRange(duration.TotalSeconds, 3, 10);
        }
    }
}
