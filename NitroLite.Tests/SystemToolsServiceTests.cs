using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        try { File.Delete(SystemToolsService.OptimizeBackupPath); } catch { }
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>Runs an action with the elevated runner replaced by a capture, and returns the script it built.</summary>
    private static async Task<string> CaptureScriptAsync(Func<Task> action)
    {
        string? captured = null;
        SystemToolsService.ElevatedRunnerSeam = script =>
        {
            captured = script;
            return Task.FromResult(0);
        };
        await action();
        Assert.NotNull(captured);
        return captured!;
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
    public async Task FixKeyboardLightingAsync_PutsThePreviousServiceImageBackWhenTheRepairFails()
    {
        var script = await CaptureScriptAsync(() => SystemToolsService.FixKeyboardLightingAsync());

        Assert.Contains("$oldImage", script);
        Assert.Contains("Set-ItemProperty -LiteralPath $svcKey -Name 'ImagePath' -Value $oldImage", script);
        Assert.Contains("Set-ItemProperty -LiteralPath $svcKey -Name 'Start' -Value ([int]$oldStart)", script);
        // AASSvc is stopped before the change, so it has to be started again on every path.
        Assert.Contains("finally", script);
        Assert.Contains("Start-Service -Name 'AASSvc'", script);
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
    public async Task FixAcerServicesAsync_PutsBothPreviousServiceImagesBackWhenTheRepairFails()
    {
        var script = await CaptureScriptAsync(() => SystemToolsService.FixAcerServicesAsync());

        Assert.Contains("$oldAgentImage", script);
        Assert.Contains("$oldLightingImage", script);
        Assert.Contains("Set-ItemProperty -LiteralPath $agentKey -Name 'ImagePath' -Value $oldAgentImage", script);
        Assert.Contains("Set-ItemProperty -LiteralPath $lightingKey -Name 'ImagePath' -Value $oldLightingImage", script);
        Assert.Contains("finally", script);
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
    public async Task RestoreMuxSwitchAsync_RepairsAnExistingKeyInsteadOfCreatingOne()
    {
        var script = await CaptureScriptAsync(() => SystemToolsService.RestoreMuxSwitchAsync());

        // Creating the key on a machine without a MUX switch would leave a setting nothing reads behind.
        Assert.DoesNotContain("New-Item", script);
        Assert.Contains("Test-Path -LiteralPath $regPath", script);
        Assert.Contains("$old", script);
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
    public async Task OptimizePcAsync_WritesTheUndoRecordBeforeChangingAnything()
    {
        var script = await CaptureScriptAsync(() => SystemToolsService.OptimizePcAsync());

        int save = script.IndexOf("try { Save-Backup }", StringComparison.Ordinal);
        int firstChange = script.IndexOf("Set-Service -Name 'SysMain'", StringComparison.Ordinal);
        Assert.True(save >= 0, "the script never writes the undo record");
        Assert.True(firstChange >= 0, "the script never disables SysMain");
        Assert.True(save < firstChange, "the undo record must be written before the first change");
    }

    [Fact]
    public async Task OptimizePcAsync_KeepsTheFirstUndoRecordWhenItRunsAgain()
    {
        var script = await CaptureScriptAsync(() => SystemToolsService.OptimizePcAsync());

        // A second run must not record the already-optimized state as the machine's original one.
        Assert.Contains("if (Test-Path -LiteralPath $backupPath)", script);
        Assert.Contains("$services.ContainsKey('SysMain')", script);
        Assert.Contains("$services.ContainsKey($s)", script);
    }

    [Fact]
    public async Task OptimizePcAsync_LeavesTheEdgeUpdaterAndTheAudioStackAlone()
    {
        var script = await CaptureScriptAsync(() => SystemToolsService.OptimizePcAsync());

        // Turning the Edge updater off would stop security updates; restarting audiodg only glitches the sound.
        Assert.DoesNotContain("edgeupdate", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audiodg", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AudioEndpointBuilder", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AudioSrv", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreOptimizePcAsync_RemovesTheHagsValueThatDidNotExistBefore()
    {
        await File.WriteAllTextAsync(SystemToolsService.OptimizeBackupPath, "{\"services\":{},\"hags\":-1,\"tasks\":[]}");

        var script = await CaptureScriptAsync(() => SystemToolsService.RestoreOptimizePcAsync());

        Assert.Contains("Remove-ItemProperty", script);
        Assert.Contains("HwSchMode", script);
    }

    [Fact]
    public async Task RestoreOptimizePcAsync_ReportsWhenThereIsNothingToUndo()
    {
        try { File.Delete(SystemToolsService.OptimizeBackupPath); } catch { }

        var result = await SystemToolsService.RestoreOptimizePcAsync();

        Assert.False(result.Success);
        Assert.Contains("yedeği yok", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EveryGeneratedScriptParsesAsPowerShell()
    {
        var scripts = new List<(string Name, string Script)>
        {
            ("fix_lighting", await CaptureScriptAsync(() => SystemToolsService.FixKeyboardLightingAsync())),
            ("fix_services", await CaptureScriptAsync(() => SystemToolsService.FixAcerServicesAsync())),
            ("restore_mux", await CaptureScriptAsync(() => SystemToolsService.RestoreMuxSwitchAsync())),
            ("optimize", await CaptureScriptAsync(() => SystemToolsService.OptimizePcAsync()))
        };

        await File.WriteAllTextAsync(SystemToolsService.OptimizeBackupPath, "{\"services\":{},\"hags\":-1,\"tasks\":[]}");
        scripts.Add(("restore_optimize", await CaptureScriptAsync(() => SystemToolsService.RestoreOptimizePcAsync())));

        foreach (var (name, script) in scripts)
        {
            var path = Path.Combine(_tempDir, name + ".ps1");
            await File.WriteAllTextAsync(path, script);

            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.WindowsPowerShell,
                Arguments = $"-NoProfile -NonInteractive -Command \"$null = [ScriptBlock]::Create((Get-Content -Raw -LiteralPath '{path}'))\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi)!;
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, $"{name} is not valid PowerShell: {stderr}");
        }
    }
}
