using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Periodic work on the telemetry tick is paced by elapsed time, not by counting ticks: the tick is 0.5 s with the
/// overlay, 1 s on screen and 5 s in the tray, and a tick count stretched game detection to 25 s there.
/// </summary>
public class TickScheduleTests
{
    private sealed class CountingScanner : IProcessScanner
    {
        public int Scans { get; private set; }
        public IReadOnlyList<RunningProcess> Scan() { Scans++; return Array.Empty<RunningProcess>(); }
    }

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-tick-" + Guid.NewGuid().ToString("N") + ".json");

    private static MainViewModel Create(CountingScanner scanner, CleanupA1Tests.FakeClock clock, Phase3Fakes.FakeBattery? battery = null)
    {
        var settings = new AppSettings { AutoGameModeEnabled = true };
        var hardware = new Phase3Fakes.StubHardware();
        var service = new GameProfileService(scanner, settings, hardware);
        return new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service, batteryHealth: battery)
        {
            FanClock = clock.Now
        };
    }

    private static async Task TicksAsync(MainViewModel vm, CleanupA1Tests.FakeClock clock, int count, long everyMs)
    {
        for (int i = 0; i < count; i++)
        {
            clock.Advance(everyMs);
            await vm.RefreshTelemetryAsync();
        }
    }

    [Fact]
    public async Task GameDetection_RunsEveryFiveSeconds_WhetherTheTickIsOneSecondOrFive()
    {
        foreach (long tickMs in new long[] { 1_000, 5_000 })
        {
            var clock = new CleanupA1Tests.FakeClock();
            var scanner = new CountingScanner();
            using var vm = Create(scanner, clock);
            await vm.InitializeAsync();
            int before = scanner.Scans;

            await TicksAsync(vm, clock, (int)(60_000 / tickMs), tickMs);

            // 60 s at one poll per 5 s: 11 or 12 depending on whether the first tick only starts the clock.
            Assert.InRange(scanner.Scans - before, 10, 12);
        }
    }

    [Fact]
    public async Task BatteryHealth_KeepsItsFiveMinutePeriod_WhenTheTickIsFiveSeconds()
    {
        var clock = new CleanupA1Tests.FakeClock();
        var battery = new Phase3Fakes.FakeBattery();
        using var vm = Create(new CountingScanner(), clock, battery);
        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync(); // first tick: the replug check reads once
        int before = battery.Reads;

        await TicksAsync(vm, clock, 12 * 10, 5_000); // ten minutes of tray ticks

        Assert.InRange(battery.Reads - before, 1, 3); // ~every 5 min, not every 25 min
    }
}

public class ProcessScannerUnreadableLogTests
{
    [Fact]
    public void UnreadableProcesses_AreSummarisedOncePerChange_NotPerScan()
    {
        var scanner = new NitroLite.Windows.WindowsProcessScanner();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var later = t0 + NitroLite.Windows.WindowsProcessScanner.UnreadableWarningInterval;

        Assert.False(scanner.ReportUnreadable(0, null, t0));
        Assert.True(scanner.ReportUnreadable(7, "Access is denied", t0)); // first line is immediate
        Assert.False(scanner.ReportUnreadable(7, "Access is denied", later)); // same count: silent
        Assert.True(scanner.ReportUnreadable(8, "Access is denied", later));
        Assert.False(scanner.ReportUnreadable(0, null, later)); // recovery is not a warning
    }

    [Fact]
    public void UnreadableSummary_IsRateLimitedToOneLinePerTenMinutes()
    {
        var scanner = new NitroLite.Windows.WindowsProcessScanner();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(scanner.ReportUnreadable(7, "x", t0));
        Assert.False(scanner.ReportUnreadable(8, "x", t0.AddMinutes(1)));  // changed, but too soon
        Assert.False(scanner.ReportUnreadable(9, "x", t0.AddMinutes(9.9)));
        Assert.True(scanner.ReportUnreadable(9, "x", t0.AddMinutes(10)));  // picked up after the interval
        Assert.False(scanner.ReportUnreadable(9, "x", t0.AddMinutes(30))); // unchanged
    }
}
