using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Regression tests for the second audit round: tolerant telemetry parsing, settings corruption reporting,
/// profile identity, install-folder validation and the thermal sample age.
/// </summary>
public class AuditRound2Tests
{
    // ---------------------------------------------------------------- monitor payload tolerance

    [Fact]
    public void ParseMonitorData_KeepsTheGoodFieldsWhenOneFieldIsMalformed()
    {
        var payload = JsonNode.Parse("""
        {
            "CPU_TEMPERATURE": 61,
            "GPU1_TEMPERATURE": "not a number",
            "CPU_USAGE": 42.5,
            "RAM_TOTAL": 16384,
            "CPU_CORE_AMOUNT": 16
        }
        """);

        var data = AcerAgentClient.ParseMonitorData(payload);

        Assert.NotNull(data);
        Assert.Equal(61, data!.CpuTemperature);
        Assert.Equal(0, data.GpuTemperature);      // malformed field degrades to 0, not a null payload
        Assert.Equal(42.5, data.CpuUsage);
        Assert.Equal(16384, data.RamTotalMB);
        Assert.Equal(16, data.CpuCores);
    }

    [Fact]
    public void ParseMonitorData_AcceptsNumericStrings()
    {
        var payload = JsonNode.Parse("""{ "CPU_TEMPERATURE": "58", "CPU_USAGE": "37.5" }""");

        var data = AcerAgentClient.ParseMonitorData(payload);

        Assert.NotNull(data);
        Assert.Equal(58, data!.CpuTemperature);
        Assert.Equal(37.5, data.CpuUsage);
    }

    [Fact]
    public void ParseMonitorData_ReturnsNullForANonObjectPayload()
    {
        Assert.Null(AcerAgentClient.ParseMonitorData(null));
        Assert.Null(AcerAgentClient.ParseMonitorData(JsonNode.Parse("[1,2,3]")));
    }

    // ---------------------------------------------------------------- settings corruption report

    [Fact]
    public void LoadSettings_ReportsCorruptionAndUsesTheBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string primary = Path.Combine(root, "config.json");
        string backup = primary + ".bak";
        try
        {
            File.WriteAllText(primary, "{ this is not json");
            File.WriteAllText(backup, """{ "StartMinimized": true }""");

            var result = SettingsManager.LoadSettings(primary, backup);

            Assert.True(result.PrimaryCorrupt);
            Assert.Equal(SettingsLoadSource.Backup, result.Source);
            Assert.True(result.Settings.StartMinimized);
            Assert.False(string.IsNullOrWhiteSpace(result.PrimaryError));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void LoadSettings_ReportsCorruptionWhenNothingCanBeRecovered()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string primary = Path.Combine(root, "config.json");
        try
        {
            File.WriteAllText(primary, "");

            var result = SettingsManager.LoadSettings(primary, primary + ".bak");

            Assert.True(result.PrimaryCorrupt);
            Assert.Equal(SettingsLoadSource.Defaults, result.Source);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void LoadSettings_IsCleanWhenThePrimaryFileIsValid()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string primary = Path.Combine(root, "config.json");
        try
        {
            File.WriteAllText(primary, """{ "StartMinimized": true }""");

            var result = SettingsManager.LoadSettings(primary);

            Assert.False(result.PrimaryCorrupt);
            Assert.Equal(SettingsLoadSource.Primary, result.Source);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- profile identity

    [Fact]
    public void TwoGamesWithTheSameExeNameStaySeparateWhenThePathDiffers()
    {
        string file = Path.Combine(Path.GetTempPath(), "nl_profiles_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var service = new GameDetectionService(file);
            string exe = "Game_" + Guid.NewGuid().ToString("N");
            string pathA = Path.Combine(Path.GetTempPath(), "a", exe + ".exe");
            string pathB = Path.Combine(Path.GetTempPath(), "b", exe + ".exe");

            Assert.True(service.SaveProfile(new GameProfile { ExeName = exe, DisplayName = "A", InstallPath = pathA }));
            Assert.True(service.SaveProfile(new GameProfile { ExeName = exe, DisplayName = "B", InstallPath = pathB }));

            Assert.Equal(2, service.GetProfiles().Count(p => p.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal("A", service.GetProfile(exe, pathA)!.DisplayName);
            Assert.Equal("B", service.GetProfile(exe, pathB)!.DisplayName);

            // Deleting one must leave the other alone.
            Assert.True(service.DeleteProfile(exe, pathA));
            Assert.Null(service.GetProfile(exe, pathA));
            Assert.Equal("B", service.GetProfile(exe, pathB)!.DisplayName);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    [Fact]
    public void AProfileWithoutAPathAdoptsThePathInsteadOfDuplicating()
    {
        string file = Path.Combine(Path.GetTempPath(), "nl_profiles_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var service = new GameDetectionService(file);
            string exe = "Game_" + Guid.NewGuid().ToString("N");
            string path = Path.Combine(Path.GetTempPath(), "c", exe + ".exe");

            Assert.True(service.SaveProfile(new GameProfile { ExeName = exe, DisplayName = "First" }));
            Assert.True(service.SaveProfile(new GameProfile { ExeName = exe, DisplayName = "Second", InstallPath = path }));

            Assert.Single(service.GetProfiles().Where(p => p.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal("Second", service.GetProfile(exe, path)!.DisplayName);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    [Fact]
    public void IsInstalledIsFalseOnlyWhenTheRecordedExeIsGone()
    {
        string missing = Path.Combine(Path.GetTempPath(), "nl_missing_" + Guid.NewGuid().ToString("N") + ".exe");
        Assert.False(new GameProfile { ExeName = "x", InstallPath = missing }.IsInstalled);
        Assert.True(new GameProfile { ExeName = "x", InstallPath = "" }.IsInstalled);

        string existing = Path.GetTempFileName();
        try
        {
            Assert.True(new GameProfile { ExeName = "x", InstallPath = existing }.IsInstalled);
        }
        finally { File.Delete(existing); }
    }

    // ---------------------------------------------------------------- install folder validation

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Program Files")]
    public void UninstallScriptRefusesADangerousInstallFolder(string folder)
    {
        var task = new StandbyPurgeTask { InstallDirectory = folder };
        Assert.Throws<ArgumentException>(() => task.GenerateUninstallScript(@"C:\Temp\err.log"));
        Assert.Throws<ArgumentException>(() => task.GenerateInstallScript(
            "caner", new[] { StandbyPurgeTask.HelperExeName }, new[] { new string('A', 64) }, @"C:\Temp\err.log"));
    }

    // ---------------------------------------------------------------- thermal sample age

    [Fact]
    public void ThermalReportExposesTheLastLoadMeasurementDate()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl_thermal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string previous = Environment.GetEnvironmentVariable("NITROLITE_DATA_DIR") ?? "";
        try
        {
            Environment.SetEnvironmentVariable("NITROLITE_DATA_DIR", root);
            var service = new ThermalHealthService();
            service.SetDailyHistoryForTesting(new[]
            {
                new DailyThermalSummary { Date = "2026-10-01", ThermalResistanceRatio = 2.10, AvgLoadCpuTemp = 70 },
                new DailyThermalSummary { Date = "2026-10-05", ThermalResistanceRatio = 2.20, AvgLoadCpuTemp = 72 }
            });

            var report = service.GetReport();

            Assert.True(report.HasLoadMeasurement);
            Assert.Equal("2026-10-05", report.LastLoadMeasurementDate);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NITROLITE_DATA_DIR", previous);
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
