using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

public class InternalPanelTests
{
    [Theory]
    [InlineData(11u, true)]          // DisplayPort embedded (eDP) – the AN16-41 panel
    [InlineData(6u, true)]           // LVDS
    [InlineData(0x80000000u, true)]  // INTERNAL
    [InlineData(10u, false)]         // external DisplayPort – the Lenovo monitor on this machine
    [InlineData(5u, false)]          // HDMI
    public void OutputTechnology_DistinguishesBuiltInPanel(uint technology, bool expected)
    {
        Assert.Equal(expected, DisplayRefreshRateService.IsInternalOutputTechnology(technology));
    }

    [Fact]
    public void LiveQuery_FindsAGdiDeviceNameOrNothing()
    {
        // Read-only QueryDisplayConfig; on a laptop with the lid open it returns e.g. "\\.\DISPLAY1".
        var name = DisplayRefreshRateService.FindInternalPanelDeviceName();
        Assert.True(name == null || name.StartsWith(@"\\.\DISPLAY", StringComparison.Ordinal), $"Unexpected device name '{name}'");
    }
}

public class BatteryIdentityTests
{
    [Theory]
    [InlineData("00000LGCAP21A8T", "AP21A8T", "LGC")]
    [InlineData("AP21A8T", "AP21A8T", "")]        // nothing besides the name
    [InlineData("12345SMPXYZ", "ABC", "")]        // name not at the end
    [InlineData(null, "AP21A8T", "")]
    public void ManufacturerIsParsedFromDeviceId(string? deviceId, string name, string expected)
    {
        Assert.Equal(expected, BatteryWatchdogService.ParseBatteryManufacturer(deviceId, name));
    }

    [Theory]
    [InlineData(6, "Lityum-iyon")]
    [InlineData(8, "Lityum polimer")]
    [InlineData(0, "")]
    public void ChemistryCodesMapToNames(int code, string expected)
    {
        Assert.Equal(expected, BatteryWatchdogService.BatteryChemistryName(code));
    }
}

/// <summary>
/// Phase 3 of the WPF parity pass: the battery and thermal page.
/// </summary>
/// <remarks>
/// The page is a reading, not a control surface, so the assertions are about what it prints from a given snapshot and
/// about the two settings it owns: the DC profile and the watchdog. Both are written to the settings file, which is
/// where a restart reads them from.
/// </remarks>
public class BatteryThermalPageTests
{
    private static MainViewModel Create(
        AppSettings? settings = null,
        Phase3Fakes.FakeBattery? battery = null,
        Phase3Fakes.FakeStorage? storage = null,
        Phase3Fakes.StubHardware? hardware = null,
        string? path = null) =>
        new(settings ?? new AppSettings(),
            hardware,
            null,
            path ?? Phase3Fakes.TempSettingsPath(),
            batteryHealth: battery,
            storageHealth: storage);

    [Fact]
    public async Task HealthDetails_Rendered()
    {
        var battery = new Phase3Fakes.FakeBattery();
        using var vm = Create(battery: battery, storage: new Phase3Fakes.FakeStorage());

        await vm.InitializeAsync();

        Assert.True(vm.BatteryHealthAvailable);
        Assert.True(vm.BatteryCapacityAvailable);

        // The four figures on the capacity card, each formatted the way the page formats it.
        Assert.Contains("92", vm.BatteryHealthText);
        Assert.Contains("8", vm.BatteryWearText);
        Assert.Contains("82", vm.BatteryFullChargeText);
        Assert.Contains("15", vm.BatteryVoltageText);

        // The cycle count is only printed when the firmware reported one.
        Assert.True(vm.HasBatteryCycle);
        Assert.Contains("143", vm.BatteryCycleText);

        // The drive list is the storage card, and both entries carry a letter tag.
        Assert.True(vm.StorageAvailable);
        Assert.Equal(2, vm.StorageDrives.Count);
        Assert.Equal("[C:]", vm.StorageDrives[0].LetterTag);
        Assert.Equal("[D:]", vm.StorageDrives[1].LetterTag);
        Assert.False(vm.HasNoStorageDrives);
    }

    [Fact]
    public async Task WearTrend_Records()
    {
        var battery = new Phase3Fakes.FakeBattery();
        battery.SeedWear(6.0, 7.0);

        using var vm = Create(battery: battery);
        await vm.InitializeAsync();

        // Two seeded readings plus the one the first read records: three a week apart is a trend, so the chart is drawn
        // and the summary names the change.
        Assert.True(vm.ShowWearTrendChart);
        Assert.True(vm.HasWearHistory);
        Assert.Equal(3, vm.WearTrendPoints.Count);
        Assert.Equal(8.0, vm.WearTrendPoints[^1]);
        Assert.NotEmpty(vm.WearTrendSummary);
        Assert.NotEmpty(vm.WearTrendFirstDate);
        Assert.NotEmpty(vm.WearTrendLastDate);

        // Clearing the history empties the chart and hides it again. The command is fire-and-forget by design, so the
        // assertion waits for the effect rather than for the call.
        vm.ClearWearHistoryCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => battery.Clears == 1);
        Assert.Equal(1, battery.Clears);
        Assert.False(vm.ShowWearTrendChart);
        Assert.False(vm.HasWearHistory);
        Assert.Empty(vm.WearTrendPoints);
    }

    [Fact]
    public async Task WearTrend_IsNotDrawnFromASingleAfternoon()
    {
        // The bug: the reported capacity drifts by more than a point between two reads minutes apart, the drop rule
        // let the second read in, and the page drew a wear curve on the first day. Two readings an hour apart are a
        // trend only in the arithmetic sense, so the chart waits for the same week the per-year rate does.
        var battery = new Phase3Fakes.FakeBattery();
        battery.SeedWearAt(TimeSpan.FromHours(1), 6.0, 7.0);

        using var vm = Create(battery: battery);
        await vm.InitializeAsync();

        Assert.True(vm.HasWearHistory);
        Assert.False(vm.ShowWearTrendChart);
        Assert.Empty(vm.WearTrendPoints);
        Assert.Equal(vm.Labels.WearNeedsAWeek, vm.WearTrendNote);
    }

    [Fact]
    public async Task Overcharge_Warns()
    {
        var battery = new Phase3Fakes.FakeBattery();
        battery.Snapshot = new BatteryHealthSnapshot
        {
            Status = new BatteryStatusInfo
            {
                BatteryPercent = 84,
                IsPluggedIn = true,
                IsCharging = false,
                IsOvercharged = true
            },
            Details = battery.Snapshot.Details
        };

        using var vm = Create(battery: battery);
        await vm.InitializeAsync();

        Assert.True(vm.ShowOverchargeBanner);
        Assert.Contains("84", vm.OverchargeMessage);

        // Below the threshold the banner is gone: the watchdog only speaks when the pack is above 80 %. The battery is
        // read once a minute rather than on every tick, so the test runs the ticks that reach the next read.
        battery.Snapshot = new BatteryHealthSnapshot
        {
            Status = new BatteryStatusInfo { BatteryPercent = 79, IsPluggedIn = true, IsOvercharged = false },
            Details = battery.Snapshot.Details
        };
        for (int i = 0; i < 30; i++) await vm.RefreshTelemetryAsync();
        Assert.False(vm.ShowOverchargeBanner);
    }

    [Fact]
    public async Task Watchdog_Toggles()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            var battery = new Phase3Fakes.FakeBattery();
            using var vm = Create(battery: battery, path: path);
            await vm.InitializeAsync();

            // On by default, and the service is told so on the first read.
            Assert.True(vm.WatchdogOn);
            Assert.True(vm.WatchdogAvailable);
            Assert.True(battery.WatchdogEnabled);

            vm.WatchdogOn = false;

            Assert.False(battery.WatchdogEnabled);
            Assert.False(SettingsManager.LoadSettingsWithFallback(path).BatteryWatchdogEnabled);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Watchdog_AnnouncesItsSavedStateOnTheFirstRead()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            var settings = new AppSettings { BatteryWatchdogEnabled = true };
            using var vm = Create(settings: settings, battery: new Phase3Fakes.FakeBattery(), path: path);

            // The switch is bound before InitializeAsync finishes, so a state that is only written to the field never
            // reaches the page. It showed "off" on every launch while the watchdog was armed, and the only way to find
            // out was to flip it off and on again.
            var announced = new List<string?>();
            vm.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

            await vm.InitializeAsync();

            Assert.Contains(nameof(vm.WatchdogOn), announced);
            Assert.True(vm.WatchdogOn);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
