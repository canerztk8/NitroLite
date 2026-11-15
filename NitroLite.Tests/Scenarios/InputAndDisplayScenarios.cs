using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests.Scenarios;

/// <summary>
/// Category 4: the wireless ATK mouse and the window placement rules.
/// </summary>
/// <remarks>
/// The single-method tests already cover "a retry happens when the receiver starts answering" and "a retry
/// happens when the user touches something". What is missing is the gate itself: which moments must NOT
/// produce an attempt, and what happens to the pending request once the mouse gives a final answer. Those are
/// the cases that turn a working retry into a HID hammer or into a badge stuck on "unknown" forever.
/// </remarks>
public class InputAndDisplayScenarios
{
    private static MainWindow BuildMouseWindow(AppSettings settings, Func<int, bool> switcher) =>
        new(new ScenarioAgentClient(), new ScenarioCpuPower(), new GameDetectionService(), new AtkMouseService(),
            settings: settings, mouseProfileSwitcher: switcher, isTestHarness: true);

    // ---------------------------------------------------------------- retry schedule

    [Fact]
    public void RetrySchedule_StartsFastAndSettlesToASlowPoll()
    {
        // The first attempts have to land inside the second or two the user keeps the mouse awake after
        // noticing nothing happened. After a minute of silence the mouse is more likely switched off, so the
        // schedule drops to a slow poll instead of hammering the receiver forever.
        Assert.Equal(400, MainWindow.MouseRetryDelayMs(1));
        Assert.Equal(800, MainWindow.MouseRetryDelayMs(2));
        Assert.Equal(3000, MainWindow.MouseRetryDelayMs(10));
        Assert.Equal(10000, MainWindow.MouseRetryDelayMs(31));
        Assert.Equal(10000, MainWindow.MouseRetryDelayMs(5000));
    }

    [Fact]
    public void RetrySchedule_NeverGetsShorterAsAttemptsPileUp()
    {
        // A schedule that shortens would make a mouse that stays off produce more and more traffic.
        long previous = 0;
        for (int attempts = 0; attempts <= 60; attempts++)
        {
            long delay = MainWindow.MouseRetryDelayMs(attempts);
            Assert.True(delay >= previous,
                $"attempt {attempts} waits {delay} ms, less than the {previous} ms before it");
            previous = delay;
        }
    }

    // ---------------------------------------------------------------- what must not trigger an attempt

    [Fact]
    public Task StaleInput_DoesNotWakeTheRetry() => Sta.RunAsync(async () =>
    {
        // The wake shortcut compares the last input tick against the tick recorded at the last attempt. Input
        // that happened before that attempt is already accounted for; treating it as fresh would make every
        // poll look like the user just touched the mouse.
        var attempts = new List<int>();
        long inputTick = 5_000;
        var window = BuildMouseWindow(new AppSettings { AtkMouseIntegrationEnabled = true }, profile =>
        {
            attempts.Add(profile);
            return false;
        });
        window.UserActivityProvider = () => inputTick;

        Assert.Equal(AtkMouseResult.NoResponse, await window.ApplyDesktopMouseProfileAsync(1));
        Assert.Single(attempts);

        // Well inside the 400 ms backoff, and the input tick has not moved since the attempt.
        await window.RetryPendingDesktopMouseProfileIfDueAsync(Environment.TickCount64 + 100);
        Assert.Single(attempts);
    });

    [Fact]
    public Task InputBurst_IsThrottledByTheMinimumGap() => Sta.RunAsync(async () =>
    {
        // A mouse that is being moved generates input continuously. Without the floor every poll would turn
        // into an attempt and the receiver would be flooded.
        var attempts = new List<int>();
        long inputTick = 1;
        var window = BuildMouseWindow(new AppSettings { AtkMouseIntegrationEnabled = true }, profile =>
        {
            attempts.Add(profile);
            return false;
        });
        window.UserActivityProvider = () => inputTick;

        await window.ApplyDesktopMouseProfileAsync(1);
        Assert.Single(attempts);

        // Fresh input, but the previous attempt was microseconds ago.
        inputTick = Environment.TickCount64;
        await window.RetryPendingDesktopMouseProfileIfDueAsync(Environment.TickCount64);
        Assert.Single(attempts);
    });

    [Fact]
    public Task ClockGoingBackwards_DoesNotStorm() => Sta.RunAsync(async () =>
    {
        // TickCount64 is monotonic, but a caller can still hand in a stale value. A negative elapsed time must
        // read as "not due yet", never as "overdue".
        var attempts = new List<int>();
        long inputTick = 1;
        var window = BuildMouseWindow(new AppSettings { AtkMouseIntegrationEnabled = true }, profile =>
        {
            attempts.Add(profile);
            return false;
        });
        window.UserActivityProvider = () => inputTick;

        await window.ApplyDesktopMouseProfileAsync(1);
        Assert.Single(attempts);

        inputTick = Environment.TickCount64;
        await window.RetryPendingDesktopMouseProfileIfDueAsync(0);
        Assert.Single(attempts);
    });

    [Fact]
    public Task IntegrationTurnedOff_StopsTheRetries() => Sta.RunAsync(async () =>
    {
        // The user switched the feature off while a profile was still unconfirmed. The pending request must
        // go quiet instead of writing to the mouse behind their back.
        var attempts = new List<int>();
        var settings = new AppSettings { AtkMouseIntegrationEnabled = true };
        var window = BuildMouseWindow(settings, profile =>
        {
            attempts.Add(profile);
            return false;
        });
        window.UserActivityProvider = () => 1;

        await window.ApplyDesktopMouseProfileAsync(1);
        Assert.Single(attempts);

        settings.AtkMouseIntegrationEnabled = false;
        await window.RetryPendingDesktopMouseProfileIfDueAsync(Environment.TickCount64 + 60_000);
        Assert.Single(attempts);
    });

    // ---------------------------------------------------------------- terminal answers

    [Fact]
    public Task Confirmed_EndsTheRetryChain() => Sta.RunAsync(async () =>
    {
        // Once the mouse confirms, there is nothing left to retry. A pending request that survives success
        // would keep re-applying the same profile every few seconds for the rest of the session.
        var attempts = new List<int>();
        var window = BuildMouseWindow(new AppSettings { AtkMouseIntegrationEnabled = true }, profile =>
        {
            attempts.Add(profile);
            return true;
        });
        window.UserActivityProvider = () => 1;

        Assert.Equal(AtkMouseResult.Confirmed, await window.ApplyDesktopMouseProfileAsync(2));
        Assert.Single(attempts);

        await window.RetryPendingDesktopMouseProfileIfDueAsync(Environment.TickCount64 + 60_000);
        Assert.Single(attempts);
    });

    [Fact]
    public Task NewerSelection_ReplacesThePendingRequest() => Sta.RunAsync(async () =>
    {
        // The user picks profile 1, it fails, then picks profile 3. The retry must chase 3 only; re-applying 1
        // would silently undo the newer choice.
        var attempts = new List<int>();
        var window = BuildMouseWindow(new AppSettings { AtkMouseIntegrationEnabled = true }, profile =>
        {
            attempts.Add(profile);
            return false;
        });
        window.UserActivityProvider = () => 1;

        await window.ApplyDesktopMouseProfileAsync(1);
        await window.ApplyDesktopMouseProfileAsync(3);
        Assert.Equal(new[] { 1, 3 }, attempts);

        await window.RetryPendingDesktopMouseProfileIfDueAsync(Environment.TickCount64 + 60_000);
        Assert.Equal(new[] { 1, 3, 3 }, attempts);
    });

    [Fact]
    public Task RetryAfterSuccess_DoesNotReapplyTheOldProfile() => Sta.RunAsync(async () =>
    {
        // Success on the second try: the chain has to stop there even though the first attempt failed.
        var attempts = new List<int>();
        int call = 0;
        var window = BuildMouseWindow(new AppSettings { AtkMouseIntegrationEnabled = true }, profile =>
        {
            attempts.Add(profile);
            return ++call >= 2;
        });
        window.UserActivityProvider = () => 1;

        Assert.Equal(AtkMouseResult.NoResponse, await window.ApplyDesktopMouseProfileAsync(4));
        await window.RetryPendingDesktopMouseProfileIfDueAsync(Environment.TickCount64 + 60_000);
        Assert.Equal(2, attempts.Count);

        await window.RetryPendingDesktopMouseProfileIfDueAsync(Environment.TickCount64 + 120_000);
        Assert.Equal(2, attempts.Count);
    });

    // ---------------------------------------------------------------- sidebar wording

    [Fact]
    public void ShortFailureText_SaysClickOnlyWhileARetryIsStillComing()
    {
        // "No response" read as a dead end, so the sidebar now asks the user to click. That wording is only
        // honest while a retry is actually pending; once the chain ends it must go back to the plain failure.
        string pending = MainWindow.DescribeMouseFailureShort(AtkMouseResult.NoResponse, retryPending: true);
        string settled = MainWindow.DescribeMouseFailureShort(AtkMouseResult.NoResponse, retryPending: false);
        Assert.NotEqual(settled, pending);

        // Every other outcome is final, so the flag must not change its wording.
        foreach (var result in new[]
                 {
                     AtkMouseResult.ReceiverNotFound, AtkMouseResult.Rejected,
                     AtkMouseResult.ProfileNotConfirmed, AtkMouseResult.CommunicationError
                 })
        {
            Assert.Equal(MainWindow.DescribeMouseFailureShort(result, retryPending: false),
                MainWindow.DescribeMouseFailureShort(result, retryPending: true));
        }
    }
}
