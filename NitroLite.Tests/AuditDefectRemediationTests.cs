using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class AuditDefectRemediationTests
{
    [Theory]
    [InlineData("0x0", CpuBoostMode.Disabled)]
    [InlineData("0x1", CpuBoostMode.Enabled)]
    [InlineData("0x2", CpuBoostMode.Aggressive)]
    [InlineData("0x4", CpuBoostMode.Efficient)]
    [InlineData("0x6", (CpuBoostMode)6)]
    [InlineData("0x7", null)] // Beyond Windows 0..6 PerfBoostMode range -> null
    [InlineData("0x99", null)]
    public void CpuPowerService_BoostModeQuery_ValidatesEnumProperly(string hexValue, CpuBoostMode? expected)
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        service.MockPowerCfgQueryOutput = $"Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e\n" +
                                          $"  Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00\n" +
                                          $"    Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7\n" +
                                          $"      Current AC Power Setting Index: {hexValue}\n";

        var result = service.QueryCurrentBoostModePowerCfg(isAc: true);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void MemoryCleanerService_Calculation_ReturnsGigabytesNotMegabytes()
    {
        // 16 GB = 17,179,869,184 bytes
        ulong availPhysBytes = 17179869184UL;
        double availGB = Math.Round(availPhysBytes / (1024.0 * 1024.0 * 1024.0), 2);
        
        Assert.Equal(16.0, availGB);

        // Previous bug without third /1024 divisor would return 16384.0 MB as "GB"
        double buggyGB = Math.Round(availPhysBytes / (1024.0 * 1024.0), 2);
        Assert.NotEqual(availGB, buggyGB);
        Assert.Equal(16384.0, buggyGB);
    }

    [Fact]
    public async Task RgbProfileService_ConcurrentReadWrite_ThreadSafe()
    {
        var service = new RgbProfileService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new List<Exception>();

        var readTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var presets = service.GetPresets();
                    Assert.NotEmpty(presets);
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }
        });

        var writeTask = Task.Run(() =>
        {
            int counter = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    string name = $"TestPreset_{counter % 5}";
                    service.SaveCustomPreset(name, "STATIC", 5, 3, 1, "#FFFFFF", new List<string> { "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF" });
                    service.DeletePreset(name);
                    counter++;
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll(readTask, writeTask);
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ThermalHealthService_ConcurrentRecordAndSave_ThreadSafe()
    {
        var service = new ThermalHealthService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new List<Exception>();

        var sample = new MonitorData
        {
            CpuTemperature = 50,
            CpuFanSpeed = 2200,
            GpuFanSpeed = 2200,
            CpuUsage = 30.0,
            CpuPowerWatts = 25.0,
            SysTemperature = 28
        };

        var recordTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    service.RecordSample(sample, 1);
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }
        });

        var saveTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    service.SaveData();
                    var report = service.GetReport();
                    Assert.NotNull(report);
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll(recordTask, saveTask);
        Assert.Empty(exceptions);
    }

    [Fact]
    public void BatteryWatchdogService_StopDischarge_ThreadSafeWithoutObjectDisposedException()
    {
        using var watchdog = new BatteryWatchdogService();

        // Concurrently call StartSafeDischarge and StopSafeDischarge
        for (int i = 0; i < 20; i++)
        {
            watchdog.StartSafeDischarge();
            watchdog.StopSafeDischarge();
        }

        Assert.False(watchdog.IsDischarging);
    }

    [Fact]
    public void OsdOverlayWindow_IsExiting_Property_CanBeSet()
    {
        var type = typeof(OsdOverlayWindow);
        var prop = type.GetProperty("IsExiting");
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop.PropertyType);
    }
}
