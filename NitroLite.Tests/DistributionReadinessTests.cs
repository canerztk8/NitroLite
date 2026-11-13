using System;
using System.Globalization;
using System.Threading;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class DistributionReadinessTests
{
    [Fact]
    public void OccasionalUiExceptions_AreSurvived_ABurstIsNot()
    {
        App.ResetUiExceptionHistoryForTesting();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < App.MaxUiExceptionsPerWindow; i++)
            Assert.True(App.ShouldSurviveUiException(t0.AddSeconds(i)));

        // One more inside the window: a handler is failing over and over.
        Assert.False(App.ShouldSurviveUiException(t0.AddSeconds(App.MaxUiExceptionsPerWindow)));

        // After a quiet period single errors are survived again.
        Assert.True(App.ShouldSurviveUiException(t0.AddMinutes(5)));
        App.ResetUiExceptionHistoryForTesting();
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\Temp1_NitroLite.zip\NitroLite\NitroLite.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\NitroLite.exe", true)]
    [InlineData(@"c:\users\x\appdata\local\temp\a\NitroLite.exe", true)]
    [InlineData(@"C:\Users\x\Programs\NitroLite\NitroLite.exe", false)]
    [InlineData(@"C:\Users\x\AppData\Local\Temporary\NitroLite.exe", false)]   // not the temp folder itself
    [InlineData(null, false)]
    public void ExeInATempFolder_IsNotRegisteredForAutostart(string? exe, bool expected)
    {
        var roots = new[] { @"C:\Users\x\AppData\Local\Temp\" };
        Assert.Equal(expected, DebloatService.IsTemporaryLocation(exe, roots));
    }

    [Fact]
    public void ThermalDayKeys_DoNotDependOnTheUsersCalendar()
    {
        // th-TH uses the Buddhist calendar (year + 543): a culture-formatted key would be "2569-…" and
        // could never be parsed back or matched against days recorded under another locale.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");
            var summary = new DailyThermalSummary();
            Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), summary.Date);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
