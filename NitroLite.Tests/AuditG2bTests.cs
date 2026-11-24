using System;
using NitroLite.Core;
using NitroLite.Core.Hardware.Lighting;
using NitroLite.Windows;
using NitroLite.Windows.Core.Hardware.Lighting;
using Xunit;

namespace NitroLite.Tests;

/// <summary>Audit group G2b: Windows hardware-service fixes. No test here touches real hardware.</summary>
[Collection("AuditG2b-HidLightingStatics")]
public class AuditG2bHidLightingCacheTests : IDisposable
{
    private static readonly KeyboardLightingFrame Red =
        KeyboardLightingFrame.Create("STATIC", 100, 4, 1, "#FF0000", null);

    private sealed class FakeWriter : IHidLightingWriter
    {
        public bool Accepts { get; set; } = true;
        public bool Disposed { get; private set; }
        public string Description => "fake";
        public string Protocol => "fake";
        public bool CanWrite => true;
        public string? Refusal => null;

        public bool TryWrite(KeyboardLightingFrame frame, out string? error)
        {
            error = Accepts ? null : "dead handle";
            return Accepts;
        }

        public void Dispose() => Disposed = true;
    }

    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public AuditG2bHidLightingCacheTests()
    {
        HidLightingDevices.ResetCache();
        HidLightingDevices.UtcNow = () => _now;
    }

    public void Dispose()
    {
        HidLightingDevices.ProbeOverride = null;
        HidLightingDevices.UtcNow = static () => DateTime.UtcNow;
        GC.SuppressFinalize(this);
        HidLightingDevices.ResetCache();
    }

    [Fact]
    public void ANullProbeIsRetriedAfterItsLifetime()
    {
        int probes = 0;
        FakeWriter? available = null;
        HidLightingDevices.ProbeOverride = () => { probes++; return available; };

        Assert.Null(HidLightingDevices.FindWriter());
        Assert.Null(HidLightingDevices.FindWriter());
        Assert.Equal(1, probes);

        _now += HidLightingDevices.NullProbeLifetime + TimeSpan.FromSeconds(1);
        available = new FakeWriter();

        Assert.NotNull(HidLightingDevices.FindWriter());
        Assert.Equal(2, probes);
    }

    [Fact]
    public void AFoundWriterIsCachedUntilAWriteFails()
    {
        int probes = 0;
        var inner = new FakeWriter();
        HidLightingDevices.ProbeOverride = () => { probes++; return inner; };

        var first = HidLightingDevices.FindWriter()!;
        Assert.Same(first, HidLightingDevices.FindWriter());
        Assert.Equal(1, probes);

        inner.Accepts = false;
        Assert.False(first.TryWrite(Red, out _));

        Assert.True(inner.Disposed);
        Assert.NotNull(HidLightingDevices.FindWriter());
        Assert.Equal(2, probes);
    }

    [Fact]
    public void InvalidateDropsTheCachedWriterSoTheNextRequestProbesAgain()
    {
        int probes = 0;
        var inner = new FakeWriter();
        HidLightingDevices.ProbeOverride = () => { probes++; return inner; };

        _ = HidLightingDevices.FindWriter();
        HidLightingDevices.Invalidate();
        _ = HidLightingDevices.FindWriter();

        Assert.True(inner.Disposed);
        Assert.Equal(2, probes);
    }
}

public class AuditG2bMonitorTemperatureTests
{
    [Fact]
    public void ACpuTemperatureFromTheThermalZoneIsShownButNotTrusted()
    {
        using var monitor = new HardwareMonitorService(initNvml: false)
        {
            ThermalZoneProvider = () => 28.4
        };
        var data = new MonitorData { CpuTemperature = 0, CpuUsage = 5, CpuCores = 4 };

        monitor.EnrichMonitorData(data, gpuAsleepPerService: true);

        Assert.Equal(28, data.CpuTemperature);
        Assert.False(data.IsTemperatureMeasured);
        Assert.Equal("PDH thermal zone", data.Provenance["cpu-temp"]);
    }

    [Fact]
    public void AFirmwareCpuTemperatureStaysMeasured()
    {
        using var monitor = new HardwareMonitorService(initNvml: false)
        {
            ThermalZoneProvider = () => 28.4
        };
        var data = new MonitorData { CpuTemperature = 66, CpuUsage = 5, CpuCores = 4 };

        monitor.EnrichMonitorData(data, gpuAsleepPerService: true);

        Assert.Equal(66, data.CpuTemperature);
        Assert.True(data.IsTemperatureMeasured);
    }
}

public class AuditG2bBatteryTests
{
    [Fact]
    public void PluggingOrUnpluggingForcesAWmiRefreshAndRestartsTheSmoothing()
    {
        using var watchdog = new BatteryWatchdogService(attachSystemEvents: false);
        watchdog.MarkSmoothingWarmForTest(25000);
        Assert.False(watchdog.ForceRefreshPending);

        watchdog.ObserveAdapterState(isPluggedIn: true, previousIsPluggedIn: true);
        Assert.False(watchdog.ForceRefreshPending);
        Assert.True(watchdog.SmoothingInitialized);

        watchdog.ObserveAdapterState(isPluggedIn: false, previousIsPluggedIn: true);

        Assert.True(watchdog.ForceRefreshPending);
        Assert.False(watchdog.SmoothingInitialized);
    }
}

public class AuditG2bWatcherAndKeyTests
{
    [Fact]
    public void ADialogThatGetsItsTitleLaterIsReClassified()
    {
        using var watcher = new NitroSenseWatcher();
        var handle = new IntPtr(0x4321);
        int reads = 0;

        ForegroundWindowInfo Read(IntPtr h)
        {
            reads++;
            return reads == 1
                ? new ForegroundWindowInfo(77, "ApplicationFrameHost", "ApplicationFrameWindow", "", "", h)
                : new ForegroundWindowInfo(77, "ApplicationFrameHost", "ApplicationFrameWindow", "NitroSense", "", h);
        }

        var first = watcher.CachedForegroundWindow(handle, Read);
        var second = watcher.CachedForegroundWindow(handle, Read);

        Assert.Equal(NitroSenseWindowKind.None, NitroSenseWatcher.Classify(first, false));
        Assert.Equal(NitroSenseWindowKind.ApplicationFrameHost, NitroSenseWatcher.Classify(second, false));

        // Once it classifies as NitroSense the cache holds again.
        _ = watcher.CachedForegroundWindow(handle, Read);
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(0x0C, 0x046D, true)]
    [InlineData(0xFF00, 0x046D, false)]
    [InlineData(0xFF00, 0x1532, false)]
    [InlineData(0xFF00, 0x0CF2, true)]
    [InlineData(0xFF00, 0, true)]
    public void VendorPageReportsFromThirdPartyPeripheralsAreNotScanned(int page, int vendor, bool scanned)
    {
        Assert.Equal(scanned, WindowsNitroKeyListener.ShouldScanForNitroUsage((ushort)page, (uint)vendor));
    }
}
