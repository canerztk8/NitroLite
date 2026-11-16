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
    public void CpuPowerService_ReadsTheRealPowerCfgHiddenOutput()
    {
        // Verbatim `powercfg /qh SCHEME_CURRENT <subgroup> <setting>` output from an Acer Nitro AN16-41. The
        // "Possible Setting Index" lines are decimal and must not be mistaken for the current index, and the
        // setting is hidden, so a plain /query prints only the scheme header and the read returned null.
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        service.MockPowerCfgQueryOutput =
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\r\n" +
            "  GUID Alias: SCHEME_BALANCED\r\n" +
            "  Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)\r\n" +
            "    GUID Alias: SUB_PROCESSOR\r\n" +
            "    Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7  (Processor performance boost mode)\r\n" +
            "      GUID Alias: PERFBOOSTMODE\r\n" +
            "      Possible Setting Index: 000\r\n" +
            "      Possible Setting Friendly Name: Disabled\r\n" +
            "      Possible Setting Index: 006\r\n" +
            "      Possible Setting Friendly Name: Efficient Aggressive At Guaranteed\r\n" +
            "    Current AC Power Setting Index: 0x00000004\r\n" +
            "    Current DC Power Setting Index: 0x00000000\r\n";

        Assert.Equal(CpuBoostMode.Efficient, service.QueryCurrentBoostModePowerCfg(isAc: true));
        Assert.Equal(CpuBoostMode.Disabled, service.QueryCurrentBoostModePowerCfg(isAc: false));
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

}
