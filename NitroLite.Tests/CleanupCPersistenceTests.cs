using System.IO;
using System.Text;
using System.Text.Json;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Persistence defects: a downloaded profile table that outranked a newer build's own fixes for ever, a profile fetch
/// that buffered whatever the server sent, a restore that reported the game rules as restored into a file nothing reads,
/// and a preset service that wrote its stale list back over restored presets.
/// </summary>
public class CleanupCPersistenceTests : IDisposable
{
    private readonly string _dir;

    public CleanupCPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NitroLite-cleanupc-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    private const string RemoteTable = """
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "remote-an16-61",
              "match": { "modelCodes": [ "AN16-61" ] },
              "maxFanRpm": { "cpu": 6200, "gpu": 6000 }
            }
          ]
        }
        """;

    // ---------------------------------------------------------------- remote profile cache

    [Fact]
    public void ACacheWrittenByThisBuild_IsCurrent()
    {
        var cache = PathOf("profiles.remote.json");

        Assert.True(RemoteProfiles.Install(RemoteTable, cache).Saved);

        Assert.Equal(ProfileCatalog.CurrentAppVersionText,
            File.ReadAllText(ProfileCatalog.RemoteStampPathFor(cache)));
        Assert.True(ProfileCatalog.IsRemoteCacheCurrent(cache, UpdateService.CurrentVersion));
    }

    [Fact]
    public void ACacheWithoutAStamp_IsNotTrusted()
    {
        // Every cache written before the stamp existed: it may predate this build's embedded table.
        var cache = PathOf("profiles.remote.json");
        File.WriteAllText(cache, RemoteTable);

        Assert.False(ProfileCatalog.IsRemoteCacheCurrent(cache, UpdateService.CurrentVersion));
    }

    [Theory]
    [InlineData("1.3.4", "1.3.5", false)]
    [InlineData("1.3.5", "1.3.5", true)]
    [InlineData("1.3.5.0", "1.3.5", true)]
    [InlineData("1.4.0", "1.3.5", true)]
    [InlineData("garbage", "1.3.5", false)]
    [InlineData("", "1.3.5", false)]
    public void ACacheIsTrustedOnlyWhenAnEqualOrNewerBuildWroteIt(string stamp, string app, bool expected)
    {
        var cache = PathOf("profiles.remote.json");
        File.WriteAllText(cache, RemoteTable);
        File.WriteAllText(ProfileCatalog.RemoteStampPathFor(cache), stamp);

        Assert.Equal(expected, ProfileCatalog.IsRemoteCacheCurrent(cache, Version.Parse(app)));
    }

    [Fact]
    public void ARejectedDownload_DoesNotRefreshTheStamp()
    {
        var cache = PathOf("profiles.remote.json");
        File.WriteAllText(cache, RemoteTable);
        File.WriteAllText(ProfileCatalog.RemoteStampPathFor(cache), "0.1.0");

        Assert.False(RemoteProfiles.Install("{ not json", cache).Saved);

        Assert.Equal("0.1.0", File.ReadAllText(ProfileCatalog.RemoteStampPathFor(cache)));
    }

    [Fact]
    public void ARemoteTableMayStillCarryTheCatchAll_AndAUserFileMayNot()
    {
        // The published table is the embedded table's own file, catch-all included; refusing it would reject every
        // real download. Only a hand-edited user file is refused a catch-all.
        const string catchAll = """
            { "schemaVersion": 1, "profiles": [ { "id": "any-acer", "match": { "anyAcer": true } } ] }
            """;

        Assert.True(ProfileCatalog.Validate(catchAll, isUserFile: false, out _, out _));
        Assert.False(ProfileCatalog.Validate(catchAll, isUserFile: true, out _, out _));
    }

    [Fact]
    public async Task TheBoundedRead_ReturnsTheTextUnderTheCap()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("çağrı")).ToArray();
        using var stream = new MemoryStream(bytes);

        Assert.Equal("çağrı", await RemoteProfiles.ReadBoundedAsync(stream, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task TheBoundedRead_StopsAtTheCap()
    {
        using var stream = new MemoryStream(new byte[RemoteProfiles.MaximumBytes + 1]);

        Assert.Null(await RemoteProfiles.ReadBoundedAsync(stream, RemoteProfiles.MaximumBytes, CancellationToken.None));
    }

    [Fact]
    public async Task TheBoundedRead_HonoursCancellation()
    {
        using var stream = new MemoryStream(new byte[1024]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RemoteProfiles.ReadBoundedAsync(stream, 4096, cancelled.Token));
    }

    // ---------------------------------------------------------------- backup restore

    private string SettingsPath => PathOf("settings.json");

    private string GameProfilesPath => PathOf("game_profiles.json");

    private void WriteBackup(string settingsJson, string? gameProfilesJson)
    {
        var text = "{\"app\":\"NitroLite\",\"schema\":1,\"created_utc\":\"2026-01-01T00:00:00Z\",\"app_version\":\"v1.0.0\","
                   + $"\"settings\":{settingsJson}"
                   + (gameProfilesJson == null ? "" : $",\"game_profiles\":{gameProfilesJson}")
                   + "}";
        File.WriteAllText(PathOf("backup.json"), text);
    }

    private BackupResult Restore() =>
        BackupService.Restore(PathOf("backup.json"), SettingsPath, GameProfilesPath, PathOf("rgb_presets.json"));

    [Fact]
    public void AnOldBackupsGameProfileSection_IsRestoredIntoTheSettingsFile()
    {
        // The section is the old Windows game_profiles.json: default serializer, so C# property names.
        WriteBackup("{\"language\":\"de\"}",
            "[{\"ExeName\":\"legacy.exe\",\"DisplayName\":\"Legacy\",\"TargetMode\":2}]");

        var result = Restore();

        Assert.True(result.Success, result.Error);
        Assert.Contains("Oyun profilleri", result.Written);
        var restored = SettingsManager.LoadSettings(SettingsPath).Settings;
        var profile = Assert.Single(restored.GameProfiles);
        Assert.Equal("legacy.exe", profile.ExeName);
        Assert.False(File.Exists(GameProfilesPath));
    }

    [Fact]
    public void ACurrentBackup_RestoresTheGameRulesFromItsSettingsBlock()
    {
        var settings = new AppSettings { Language = "es" };
        settings.GameProfiles = new List<GameProfile> { new() { ExeName = "mine.exe", DisplayName = "Mine" } };
        Assert.True(BackupService.Create(PathOf("backup.json"), settings, null, null, "v1.0.0").Success);

        var result = Restore();

        Assert.True(result.Success, result.Error);
        Assert.Contains("Oyun profilleri", result.Written);
        Assert.Equal("mine.exe", Assert.Single(SettingsManager.LoadSettings(SettingsPath).Settings.GameProfiles).ExeName);
        Assert.False(File.Exists(GameProfilesPath));
    }

    [Fact]
    public void AnUnreadableGameProfileSection_IsAFailedPart_NotARestoredOne()
    {
        WriteBackup("{\"language\":\"de\"}", "[{\"ExeName\":{\"not\":\"a string\"}}]");

        var result = Restore();

        Assert.False(result.Success);
        Assert.Contains("Oyun profilleri", result.Failed);
        Assert.DoesNotContain("Oyun profilleri", result.Written);
    }

    [Fact]
    public void ABackupWithNoGameRulesAnywhere_SaysSo()
    {
        WriteBackup("{\"language\":\"de\"}", null);

        var result = Restore();

        Assert.True(result.Success, result.Error);
        Assert.Contains("Oyun profilleri", result.Skipped);
    }

    // ---------------------------------------------------------------- RGB presets after a restore

    [Fact]
    public void Reload_PicksUpPresetsARestoreWrote_SoTheNextSaveDoesNotOverwriteThem()
    {
        var presetsPath = PathOf("rgb_presets.json");
        var service = new RgbProfileService(presetsPath);
        service.SaveCustomPreset("Before", "STATIC", 5, 3, 1, "#112233", new List<string> { "#112233", "#112233", "#112233", "#112233" });

        // A restore rewrites the file under the running service.
        File.WriteAllText(presetsPath, JsonSerializer.Serialize(new[]
        {
            new RgbPreset { Name = "Restored", Color = "#445566", LEDs = new List<string> { "#445566", "#445566", "#445566", "#445566" } }
        }));

        service.Reload();
        service.SaveCustomPreset("After", "STATIC", 5, 3, 1, "#778899", new List<string> { "#778899", "#778899", "#778899", "#778899" });

        var names = service.GetPresets().Where(p => !p.IsBuiltIn).Select(p => p.Name).ToList();
        Assert.Contains("Restored", names);
        Assert.Contains("After", names);
        Assert.DoesNotContain("Before", names);

        var onDisk = File.ReadAllText(presetsPath);
        Assert.Contains("Restored", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("Before", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_ClearsAnEarlierLoadError()
    {
        var presetsPath = PathOf("rgb_presets.json");
        File.WriteAllText(presetsPath, "{ broken");
        var service = new RgbProfileService(presetsPath);
        Assert.NotNull(service.LoadError);

        File.WriteAllText(presetsPath, "[]");
        service.Reload();

        Assert.Null(service.LoadError);
    }
}
