using System;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The standing "Acer service unreachable" note. Two things matter: it must survive the test harness, where the
/// XAML was never loaded and every named element is null, and it must not appear on a single dropped poll.
/// </summary>
public class ServiceBannerTests
{
    private static async Task RunInStaAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource<object?>();
        var thread = new Thread(async () =>
        {
            try
            {
                await action();
                tcs.SetResult(null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await tcs.Task;
    }

    private static MainWindow CreateWindow() => new(
        new MockAcerAgentClient(),
        new MockCpuPowerService(),
        new GameDetectionService(),
        new AtkMouseService(),
        settings: null,
        isTestHarness: true);

    [Fact]
    public async Task ReportingAvailability_DoesNotThrowWhenTheXamlWasNeverLoaded()
    {
        await RunInStaAsync(() =>
        {
            var window = CreateWindow();
            try
            {
                // The harness has no ServiceBanner element; the update must be a no-op, not a crash.
                for (int i = 0; i < 5; i++) window.ReportServiceAvailabilityForTesting(false);
                window.ReportServiceAvailabilityForTesting(true);
                Assert.False(window.ServiceBannerShownForTesting);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ASingleDroppedPoll_IsNotReportedAsLoss()
    {
        await RunInStaAsync(() =>
        {
            var window = CreateWindow();
            try
            {
                // Nothing has been reported yet, so the state is still unknown rather than "lost".
                Assert.Null(window.LastServiceTelemetryAvailableForTesting);

                window.ReportServiceAvailabilityForTesting(false);
                Assert.Equal(1, window.TelemetryFailureCountForTesting);
                Assert.Null(window.LastServiceTelemetryAvailableForTesting);

                window.ReportServiceAvailabilityForTesting(false);
                Assert.Equal(2, window.TelemetryFailureCountForTesting);
                Assert.Null(window.LastServiceTelemetryAvailableForTesting);

                // The third consecutive failure is the one that counts.
                window.ReportServiceAvailabilityForTesting(false);
                Assert.False(window.LastServiceTelemetryAvailableForTesting);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Recovery_ClearsTheFailureCount()
    {
        await RunInStaAsync(() =>
        {
            var window = CreateWindow();
            try
            {
                window.ReportServiceAvailabilityForTesting(false);
                window.ReportServiceAvailabilityForTesting(false);
                window.ReportServiceAvailabilityForTesting(true);

                Assert.Equal(0, window.TelemetryFailureCountForTesting);
                Assert.True(window.LastServiceTelemetryAvailableForTesting);

                // The counter restarted, so two more failures must not be enough to report loss.
                window.ReportServiceAvailabilityForTesting(false);
                window.ReportServiceAvailabilityForTesting(false);
                Assert.True(window.LastServiceTelemetryAvailableForTesting);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }
}
