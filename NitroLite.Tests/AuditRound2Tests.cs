using System.IO;
using System.Text.Json.Nodes;
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
}
