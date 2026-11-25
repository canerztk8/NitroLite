using System.IO;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// A backup is the one file a user carries to a new machine, so these tests are about what it must not carry:
/// the window position of another screen, and a school mode whose undo record stayed behind.
/// </summary>
public class BackupServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settingsPath;
    private readonly string _gameProfilesPath;
    private readonly string _rgbPresetsPath;
    private readonly string _backupPath;

    public BackupServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NitroLiteBackup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settingsPath = Path.Combine(_dir, "settings.json");
        _gameProfilesPath = Path.Combine(_dir, "game_profiles.json");
        _rgbPresetsPath = Path.Combine(_dir, "rgb_presets.json");
        _backupPath = Path.Combine(_dir, "backup.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }

        // Nothing here owns a finalizer; the suppression is the shape the analyzer requires.
        GC.SuppressFinalize(this);
    }

    private static AppSettings SampleSettings() => new()
    {
        Language = "de",
        Theme = AppTheme.Dark,
        OsdEnabled = true,
        CustomFanCpuPercent = 65,
        CustomFanGpuPercent = 70,
        FanCurveEnabled = true,
        FanCurvePoints = new List<FanCurvePoint>
        {
            new(40, 30),
            new(90, 100)
        },
        WindowLeft = 120,
        WindowTop = 80,
        WindowWidth = 1400,
        WindowHeight = 900,
        WindowMaximized = true
    };

    private void WriteGameProfiles(string json = "[{\"exe_name\":\"game.exe\",\"display_name\":\"Game\"}]") =>
        File.WriteAllText(_gameProfilesPath, json);

    private void WriteRgbPresets(string json = "[{\"Name\":\"Mine\",\"Effect\":\"STATIC\"}]") =>
        File.WriteAllText(_rgbPresetsPath, json);

    private BackupResult Create(AppSettings? settings = null) =>
        BackupService.Create(_backupPath, settings ?? SampleSettings(), _gameProfilesPath, _rgbPresetsPath, "v1.2.0");

    private BackupResult Restore() =>
        BackupService.Restore(_backupPath, _settingsPath, _gameProfilesPath, _rgbPresetsPath);

    private AppSettings LoadRestored() => SettingsManager.LoadSettings(_settingsPath).Settings;

    // ---------------------------------------------------------------- create

    [Fact]
    public void CreateWritesAMarkedFile()
    {
        var result = Create();
        Assert.True(result.Success);
        Assert.True(File.Exists(_backupPath));

        using var doc = JsonDocument.Parse(File.ReadAllText(_backupPath));
        Assert.Equal("NitroLite", doc.RootElement.GetProperty("app").GetString());
        Assert.Equal(BackupService.SchemaVersion, doc.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal("v1.2.0", doc.RootElement.GetProperty("app_version").GetString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("settings").ValueKind);
    }

    [Fact]
    public void CreateCarriesTheLiveSettings()
    {
        Create();
        using var doc = JsonDocument.Parse(File.ReadAllText(_backupPath));
        var settings = doc.RootElement.GetProperty("settings");
        Assert.Equal("de", settings.GetProperty("language").GetString());
        Assert.Equal(65, settings.GetProperty("custom_fan_cpu_percent").GetInt32());
        Assert.Equal(2, settings.GetProperty("fan_curve_points").GetArrayLength());
    }

    [Fact]
    public void CreateIncludesGameProfilesAndRgbPresets()
    {
        WriteGameProfiles();
        WriteRgbPresets();

        var result = Create();
        Assert.True(result.Success);
        Assert.Contains("Oyun profilleri", result.Written);
        Assert.Contains("RGB profilleri", result.Written);

        using var doc = JsonDocument.Parse(File.ReadAllText(_backupPath));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("game_profiles").ValueKind);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("rgb_presets").ValueKind);
    }

    [Fact]
    public void CreateSkipsFilesThatAreNotThere()
    {
        var result = Create();
        Assert.True(result.Success);
        Assert.Contains("Oyun profilleri", result.Skipped);
        Assert.Contains("RGB profilleri", result.Skipped);

        using var doc = JsonDocument.Parse(File.ReadAllText(_backupPath));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("game_profiles").ValueKind);
    }

    [Fact]
    public void CreateSkipsAFileThatIsNotAnArray()
    {
        File.WriteAllText(_gameProfilesPath, "{\"not\":\"an array\"}");
        var result = Create();
        Assert.True(result.Success);
        Assert.Contains("Oyun profilleri", result.Skipped);
    }

    [Fact]
    public void CreateFailsOnAnEmptyPath()
    {
        var result = BackupService.Create("", SampleSettings(), null, null, "v1.2.0");
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // ---------------------------------------------------------------- restore

    [Fact]
    public void RestoreWritesTheSettingsBack()
    {
        Create();
        var result = Restore();

        Assert.True(result.Success);
        var restored = LoadRestored();
        Assert.Equal("de", restored.Language);
        Assert.Equal(AppTheme.Dark, restored.Theme);
        Assert.True(restored.OsdEnabled);
        Assert.Equal(65, restored.CustomFanCpuPercent);
        Assert.True(restored.FanCurveEnabled);
        Assert.Equal(2, restored.FanCurvePoints!.Count);
    }

    [Fact]
    public void RestoreDropsTheWindowPosition()
    {
        // The rectangle belongs to the screen it was saved on; on a smaller display it would open off-screen.
        Create();
        Restore();

        var restored = LoadRestored();
        Assert.Null(restored.WindowLeft);
        Assert.Null(restored.WindowTop);
        Assert.Null(restored.WindowWidth);
        Assert.Null(restored.WindowHeight);
        Assert.False(restored.WindowMaximized);
    }

    [Fact]
    public void RestoreForcesSchoolModeOffAndSaysSo()
    {
        // School mode's undo record stays on the machine that enabled it, so a restored "on" would be a switch the
        // user cannot turn off cleanly.
        var settings = SampleSettings();
        settings.SchoolModeEnabled = true;
        Create(settings);

        var result = Restore();
        Assert.True(result.Success);
        Assert.False(LoadRestored().SchoolModeEnabled);
        Assert.Contains("Okul modu kapalı olarak geri yüklendi", result.Skipped);
    }

    [Fact]
    public void RestoreLeavesSchoolModeOffAlone()
    {
        Create();
        var result = Restore();
        Assert.DoesNotContain("Okul modu kapalı olarak geri yüklendi", result.Skipped);
    }

    [Fact]
    public void RestoreWritesGameProfilesAndRgbPresets()
    {
        WriteGameProfiles("[{\"exe_name\":\"old.exe\"}]");
        WriteRgbPresets("[{\"Name\":\"Old\"}]");
        Create();

        File.WriteAllText(_gameProfilesPath, "[{\"exe_name\":\"changed.exe\"}]");
        File.WriteAllText(_rgbPresetsPath, "[{\"Name\":\"Changed\"}]");

        var result = Restore();
        Assert.True(result.Success);
        // The game rules come back inside the settings file, which is where the app reads them; game_profiles.json
        // is read by nothing and is no longer written.
        Assert.Contains("Oyun profilleri", result.Written);
        Assert.Contains("RGB profilleri", result.Written);
        Assert.Contains("changed.exe", File.ReadAllText(_gameProfilesPath));
        Assert.Contains("Old", File.ReadAllText(_rgbPresetsPath));
    }

    [Fact]
    public void RestoreNeverWritesTheUnreadGameProfilesFile()
    {
        Create();
        File.WriteAllText(_gameProfilesPath, "[{\"exe_name\":\"keep.exe\"}]");

        var result = Restore();
        Assert.True(result.Success);
        Assert.Contains("keep.exe", File.ReadAllText(_gameProfilesPath));
    }

    [Fact]
    public void RestoreNormalizesOutOfRangeValues()
    {
        // A hand-edited backup must not put a fan speed of 400 or an unknown enum into the running app.
        WriteBackupWithSettings(new
        {
            custom_fan_cpu_percent = 400,
            custom_fan_gpu_percent = 1,
            theme = 99,
            dc_power_mode = 42,
            osd_corner = 17
        });

        var result = Restore();
        Assert.True(result.Success);

        var restored = LoadRestored();
        Assert.Equal(100, restored.CustomFanCpuPercent);
        Assert.Equal(AppSettings.MinCustomFanPercent, restored.CustomFanGpuPercent);
        Assert.Equal(AppTheme.System, restored.Theme);
        Assert.Equal(DcPowerProfilePreference.Eco, restored.DcPowerMode);
        Assert.InRange(restored.OsdCorner, 0, 3);
    }

    [Fact]
    public void RestoreRejectsAFileThatIsNotABackup()
    {
        File.WriteAllText(_backupPath, "{\"app\":\"SomethingElse\",\"schema\":1,\"settings\":{}}");
        var result = Restore();
        Assert.False(result.Success);
        Assert.False(File.Exists(_settingsPath));
    }

    [Fact]
    public void RestoreRejectsAFutureSchema()
    {
        File.WriteAllText(_backupPath,
            $"{{\"app\":\"NitroLite\",\"schema\":{BackupService.SchemaVersion + 1},\"settings\":{{}}}}");
        var result = Restore();
        Assert.False(result.Success);
        Assert.Contains("şema", result.Error);
    }

    [Fact]
    public void RestoreRejectsABackupWithoutSettings()
    {
        File.WriteAllText(_backupPath, "{\"app\":\"NitroLite\",\"schema\":1}");
        var result = Restore();
        Assert.False(result.Success);
        Assert.False(File.Exists(_settingsPath));
    }

    [Fact]
    public void RestoreRejectsAMissingFile()
    {
        var result = BackupService.Restore(Path.Combine(_dir, "nope.json"), _settingsPath, null, null);
        Assert.False(result.Success);
    }

    [Fact]
    public void RestoreRejectsCorruptJson()
    {
        File.WriteAllText(_backupPath, "{ not json at all");
        var result = Restore();
        Assert.False(result.Success);
        Assert.False(File.Exists(_settingsPath));
    }

    [Fact]
    public void RestoreRejectsAFileTooLargeToBeABackup()
    {
        // The dialog offers "all files", so a video can be picked by mistake. The length is checked before the
        // file is read: reading it whole would allocate its size in memory first.
        using (var stream = new FileStream(_backupPath, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(BackupService.MaxBackupBytes + 1);
        }

        var result = Restore();

        Assert.False(result.Success);
        Assert.Equal(Loc.T("Bu dosya bir NitroLite yedeği için çok büyük."), result.Error);
        Assert.False(File.Exists(_settingsPath));
    }

    [Fact]
    public void RestoreAcceptsAFileAtTheSizeLimit()
    {
        // The cap is a ceiling, not a target: a file exactly at the limit still goes through the normal checks.
        using (var stream = new FileStream(_backupPath, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(BackupService.MaxBackupBytes);
        }

        var result = Restore();

        // Rejected for being empty JSON, not for its size.
        Assert.False(result.Success);
        Assert.NotEqual(Loc.T("Bu dosya bir NitroLite yedeği için çok büyük."), result.Error);
    }

    [Fact]
    public void RestoreDoesNotTouchTheSettingsWhenTheFileIsRejected()
    {
        SettingsManager.SaveSettingsAtomic(_settingsPath, new AppSettings { Language = "es" });
        File.WriteAllText(_backupPath, "{\"app\":\"Other\",\"schema\":1,\"settings\":{}}");

        Restore();

        Assert.Equal("es", LoadRestored().Language);
    }

    [Fact]
    public void ARoundTripKeepsTheFanCurve()
    {
        var settings = SampleSettings();
        settings.FanCurvePoints = new List<FanCurvePoint>
        {
            new(45, 35),
            new(60, 55),
            new(85, 95)
        };
        Create(settings);
        Restore();

        var restored = LoadRestored();
        Assert.Equal(3, restored.FanCurvePoints!.Count);
        Assert.Equal(45, restored.FanCurvePoints[0].TemperatureC);
        Assert.Equal(95, restored.FanCurvePoints[2].Percent);
    }

    [Fact]
    public void TheSuggestedFileNameCarriesTheDate()
    {
        var name = BackupService.SuggestedFileName(new DateTime(2026, 3, 12));
        Assert.Equal("NitroLite-backup-2026-03-12.json", name);
    }

    /// <summary>Writes a backup whose settings block is exactly <paramref name="settings"/>.</summary>
    private void WriteBackupWithSettings(object settings)
    {
        var document = new
        {
            app = "NitroLite",
            schema = BackupService.SchemaVersion,
            created_utc = DateTime.UtcNow,
            app_version = "v1.2.0",
            settings
        };
        File.WriteAllText(_backupPath, JsonSerializer.Serialize(document));
    }
}
