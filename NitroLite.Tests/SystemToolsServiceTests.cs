using System;
using System.IO;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class SystemToolsServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nl_tools_test_" + Guid.NewGuid().ToString("N"));

    public SystemToolsServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SystemToolsService.ElevatedRunnerSeam = null;
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task FixKeyboardLightingAsync_SuccessWhenRunnerReturnsZero()
    {
        SystemToolsService.ElevatedRunnerSeam = script =>
        {
            Assert.Contains("AcerLightingService", script);
            return Task.FromResult(0);
        };

        var result = await SystemToolsService.FixKeyboardLightingAsync();
        Assert.True(result.Success);
        Assert.Contains("başarıyla", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FixKeyboardLightingAsync_FailsWhenRunnerReturnsNonZero()
    {
        SystemToolsService.ElevatedRunnerSeam = _ => Task.FromResult(1);

        var result = await SystemToolsService.FixKeyboardLightingAsync();
        Assert.False(result.Success);
        Assert.Contains("onarılamadı", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FixAcerServicesAsync_SuccessWhenRunnerReturnsZero()
    {
        SystemToolsService.ElevatedRunnerSeam = script =>
        {
            Assert.Contains("AcerCentralService.exe", script);
            Assert.Contains("AASSvc", script);
            return Task.FromResult(0);
        };

        var result = await SystemToolsService.FixAcerServicesAsync();
        Assert.True(result.Success);
        Assert.Contains("başarıyla", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreMuxSwitchAsync_SetsRegistryKeyAndCleansLegacyTask()
    {
        SystemToolsService.ElevatedRunnerSeam = script =>
        {
            Assert.Contains("discrete_gpu_support", script);
            Assert.Contains("AcerNitroSenseMuxRestore", script);
            return Task.FromResult(0);
        };

        var result = await SystemToolsService.RestoreMuxSwitchAsync();
        Assert.True(result.Success);
        Assert.Contains("etkinleştirildi", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OptimizePcAsync_AppliesAllDebloatAndHagsOptimizations()
    {
        SystemToolsService.ElevatedRunnerSeam = script =>
        {
            Assert.Contains("SysMain", script);
            Assert.Contains("HwSchMode", script);
            Assert.Contains("ReTrim", script);
            return Task.FromResult(0);
        };

        var result = await SystemToolsService.OptimizePcAsync();
        Assert.True(result.Success);
        Assert.Contains("başarıyla", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PurgeStandbyDirect_ReturnsValidExitCode()
    {
        // PurgeStandbyDirect should run safely and return 0 (if elevated), 2 (if privilege missing), or 3/4
        int code = MemoryCleanerService.PurgeStandbyDirect();
        Assert.Contains(code, new[] { 0, 2, 3, 4 });
    }
}
