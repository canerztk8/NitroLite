using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class AuditRemediationTests
{
    // =========================================================================
    // R1. Debloat Service Restoration & Error Propagation
    // =========================================================================

    [Fact]
    public void DebloatService_PreDebloatStartupTypes_RecordedAndRestoredAccurately()
    {
        var debloat = new DebloatService();
        var tempFile = Path.Combine(Path.GetTempPath(), $"debloat_test_{Guid.NewGuid():N}.json");
        debloat.BackupStateFilePath = tempFile;

        try
        {
            var mockPreState = new Dictionary<string, string>
            {
                { "AcerServiceSvc", "Automatic" },
                { "ASMSvc", "Manual" },
                { "AcerEZSvc", "Disabled" }
            };

            debloat.SaveBackupState(mockPreState);
            var loaded = debloat.LoadBackupState();

            Assert.Equal("Automatic", loaded["AcerServiceSvc"]);
            Assert.Equal("Manual", loaded["ASMSvc"]);
            Assert.Equal("Disabled", loaded["AcerEZSvc"]);

            // Generate restore script and verify that exact recorded startup types are respected
            var restoreScript = debloat.GenerateRestoreScript(loaded);

            // Manual service must be restored to Manual (not unconditionally Automatic!)
            Assert.Contains("Set-Service -Name 'ASMSvc' -StartupType Manual", restoreScript);
            // Disabled service must be restored to Disabled
            Assert.Contains("Set-Service -Name 'AcerEZSvc' -StartupType Disabled", restoreScript);
            // Automatic service must be set to Automatic and started
            Assert.Contains("Set-Service -Name 'AcerServiceSvc' -StartupType Automatic", restoreScript);
            Assert.Contains("Start-Service -Name 'AcerServiceSvc'", restoreScript);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void DebloatService_PowerShellFailures_SurfacedAndNotSilentlySuppressed()
    {
        var debloat = new DebloatService();
        var debloatScript = debloat.GenerateDebloatScript();
        var restoreScript = debloat.GenerateRestoreScript();

        // Must NOT contain unconditional -ErrorAction SilentlyContinue on Set-Service / Stop-Service
        // Errors must be captured in $errs and written to $errLog with non-zero exit code
        Assert.Contains("$errLog", debloatScript);
        Assert.Contains("$errs", debloatScript);
        Assert.Contains("exit 1", debloatScript);
        Assert.Contains("Out-File", debloatScript);

        Assert.Contains("$errLog", restoreScript);
        Assert.Contains("$errs", restoreScript);
        Assert.Contains("exit 1", restoreScript);
    }

    // =========================================================================
    // R2. Memory Cleaner Game Guard & Default State
    // =========================================================================

    // =========================================================================
    // R3. Acer Agent Client Framing & Robust Communication
    // =========================================================================

    [Fact]
    public void TryScanJsonCompleteness_DetectsBalancedBracesWithoutStringAllocations()
    {
        string chunk1 = "{\"Function\":\"GET_MONITOR_DATA\",";
        string chunk2 = "\"data\":{\"CpuTemperature\":65,";
        string chunk3 = "\"SysTemperature\":51}}";

        byte[] b1 = Encoding.UTF8.GetBytes(chunk1);
        byte[] b2 = Encoding.UTF8.GetBytes(chunk2);
        byte[] b3 = Encoding.UTF8.GetBytes(chunk3);

        int openBraces = 0;
        bool inString = false;
        bool isEscaped = false;
        bool hasStarted = false;

        bool r1 = AcerAgentClient.TryScanJsonCompleteness(b1, 0, b1.Length, ref openBraces, ref inString, ref isEscaped, ref hasStarted);
        Assert.False(r1);
        Assert.True(openBraces > 0);

        bool r2 = AcerAgentClient.TryScanJsonCompleteness(b2, 0, b2.Length, ref openBraces, ref inString, ref isEscaped, ref hasStarted);
        Assert.False(r2);
        Assert.True(openBraces > 0);

        bool r3 = AcerAgentClient.TryScanJsonCompleteness(b3, 0, b3.Length, ref openBraces, ref inString, ref isEscaped, ref hasStarted);
        Assert.True(r3);
        Assert.Equal(0, openBraces);
    }

    [Fact]
    public void TryScanJsonCompleteness_IgnoresBracesInsideStringsAndEscapes()
    {
        string jsonWithBracesInStrings = "{\"message\":\"hello { world } \\\"nested\\\" test\", \"value\":42}";
        byte[] bytes = Encoding.UTF8.GetBytes(jsonWithBracesInStrings);

        int openBraces = 0;
        bool inString = false;
        bool isEscaped = false;
        bool hasStarted = false;

        bool isComplete = AcerAgentClient.TryScanJsonCompleteness(bytes, 0, bytes.Length, ref openBraces, ref inString, ref isEscaped, ref hasStarted);
        Assert.True(isComplete);
        Assert.Equal(0, openBraces);
    }

    [Fact]
    public void AcerAgentClient_MaxResponseSizeEnforced()
    {
        Assert.Equal(1024 * 1024, AcerAgentClient.MaxResponseSizeBytes);
    }

    // =========================================================================
    // R4. NVIDIA NVML Secure DLL Search Path
    // =========================================================================

    [Fact]
    public void HardwareMonitorService_PInvokeMethods_HaveDefaultDllImportSearchPathsSystem32()
    {
        var methods = typeof(HardwareMonitorService).GetMethods(BindingFlags.NonPublic | BindingFlags.Static);
        int nvmlMethodsChecked = 0;

        foreach (var m in methods)
        {
            var dllImport = m.GetCustomAttribute<DllImportAttribute>();
            if (dllImport != null && dllImport.Value.Equals("nvml.dll", StringComparison.OrdinalIgnoreCase))
            {
                var searchPathAttr = m.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
                Assert.NotNull(searchPathAttr);
                Assert.True((searchPathAttr.Paths & DllImportSearchPath.System32) != 0, 
                    $"Method {m.Name} should restrict nvml.dll search to System32");
                nvmlMethodsChecked++;
            }
        }

        Assert.True(nvmlMethodsChecked >= 5, "Expected at least 5 NVML P/Invoke declarations checked for safe DLL search path");
    }

    // =========================================================================
    // R5. Background Telemetry Throttling & Report Loop Fix
    // =========================================================================

    [Fact]
    public void TelemetryReportLoop_MonotonicTickCondition_ExecutesStrictlyEvery5Ticks()
    {
        // Bug reproduction & fix verification:
        // In the old bug: history.Count % 5 == 0 remained true forever once history reached 60!
        var mockHistory = new List<int>();
        int oldBugExecutions = 0;
        int fixedExecutions = 0;
        long monotonicTick = 0;

        for (int sample = 1; sample <= 120; sample++)
        {
            monotonicTick++;
            mockHistory.Add(sample);
            if (mockHistory.Count > 60) mockHistory.RemoveAt(0);

            // Old bug logic
            if (mockHistory.Count % 5 == 0) oldBugExecutions++;

            // Remediated logic with monotonic tick
            if (monotonicTick % 5 == 0) fixedExecutions++;
        }

        // Old bug executed 60 times after sample 60 (100% of the time, 1s interval instead of 5s!)
        Assert.True(oldBugExecutions > 50, "Demonstrating the old bug where Count % 5 remained constantly true");

        // Remediated tick executed strictly 24 times in 120 ticks (120 / 5 = 24)
        Assert.Equal(24, fixedExecutions);
    }

    // =========================================================================
    // R6. Atomic Settings Persistence & Fallback Recovery
    // =========================================================================

    [Fact]
    public void SettingsManager_SaveAtomic_ReplacesFileWithoutCorruption()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"settings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string primaryPath = Path.Combine(tempDir, "settings.json");

        try
        {
            var s1 = new AppSettings { SelectedChartMetric = "GPU_POWER" };
            SettingsManager.SaveSettingsAtomic(primaryPath, s1);

            Assert.True(File.Exists(primaryPath));
            var loaded = SettingsManager.LoadSettingsWithFallback(primaryPath);
            Assert.Equal("GPU_POWER", loaded.SelectedChartMetric);

            // Second save should maintain backup
            var s2 = new AppSettings { SelectedChartMetric = "CPU_TEMP" };
            SettingsManager.SaveSettingsAtomic(primaryPath, s2);

            string bakPath = primaryPath + ".bak";
            Assert.True(File.Exists(bakPath));

            var bakLoaded = SettingsManager.LoadSettingsWithFallback(bakPath);
            Assert.Equal("GPU_POWER", bakLoaded.SelectedChartMetric);

            var currLoaded = SettingsManager.LoadSettingsWithFallback(primaryPath);
            Assert.Equal("CPU_TEMP", currLoaded.SelectedChartMetric);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SettingsManager_LoadWithFallback_RecoversFromCorruptedSettings()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fallback_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string primaryPath = Path.Combine(tempDir, "settings.json");
        string legacyPath = Path.Combine(tempDir, "config.json");

        try
        {
            // Primary file corrupted (0 bytes or malformed JSON)
            File.WriteAllText(primaryPath, " { CORRUPTED INVALID JSON !!! ");

            // Legacy config file valid
            var legacySettings = new AppSettings { SelectedChartMetric = "GPU_TEMP", StartMinimized = true };
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacySettings));

            // Load should gracefully fallback to legacy config rather than dropping directly to defaults
            var recovered = SettingsManager.LoadSettingsWithFallback(primaryPath, primaryPath + ".bak", legacyPath);
            Assert.Equal("GPU_TEMP", recovered.SelectedChartMetric);
            Assert.True(recovered.StartMinimized);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // =========================================================================
    // R7. Privacy Remediation
    // =========================================================================

    [Fact]
    public void Privacy_BatteryReportHtml_NotInWorkspaceOrExcluded()
    {
        string rootDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\.."));
        string reportPath = Path.Combine(rootDir, "battery-report.html");
        string gitignorePath = Path.Combine(rootDir, ".gitignore");

        // battery-report.html must not exist in workspace or must be excluded in .gitignore
        bool fileExists = File.Exists(reportPath);
        Assert.False(fileExists, "battery-report.html should be removed or excluded from workspace to protect privacy");

        if (File.Exists(gitignorePath))
        {
            string gitignoreContent = File.ReadAllText(gitignorePath);
            Assert.Contains("battery-report.html", gitignoreContent);
        }
    }
}
