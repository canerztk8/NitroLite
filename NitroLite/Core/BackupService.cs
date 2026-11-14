using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NitroLite.Core;

/// <summary>The single file a backup writes: settings, game profiles and RGB presets, nothing else.</summary>
public sealed class BackupDocument
{
    [JsonPropertyName("app")]
    public string App { get; set; } = BackupService.AppMarker;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = BackupService.SchemaVersion;

    [JsonPropertyName("created_utc")]
    public DateTime CreatedUtc { get; set; }

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonPropertyName("settings")]
    public JsonElement? Settings { get; set; }

    [JsonPropertyName("game_profiles")]
    public JsonElement? GameProfiles { get; set; }

    [JsonPropertyName("rgb_presets")]
    public JsonElement? RgbPresets { get; set; }
}

/// <summary>What a backup or a restore did, in the words the user will read.</summary>
public sealed class BackupResult
{
    public bool Success { get; init; }

    /// <summary>Why it failed, already translated. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Parts that were written, as catalogue keys.</summary>
    public List<string> Written { get; } = new();

    /// <summary>Parts the file did not carry, or that were deliberately left out.</summary>
    public List<string> Skipped { get; } = new();

    public static BackupResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Writes and reads a single portable file holding everything a user would want back after reinstalling Windows or
/// moving to another Nitro: settings (including fan curves and the keyboard lighting), game profiles and RGB presets.
/// <para>
/// Deliberately not included: the undo records (debloat, optimize, school mode) and the runtime state files. Those
/// describe what this particular machine changed, and replaying them on another machine would restore services that
/// were never removed, or leave school mode on with nothing to undo it with.
/// </para>
/// <para>
/// A restore never trusts the file blindly: the marker and schema are checked, every value goes through
/// <see cref="AppSettings.Normalize"/>, the window position is dropped (it belongs to the screen it was saved on) and
/// school mode is forced off (its undo record does not travel with the backup).
/// </para>
/// </summary>
public static class BackupService
{
    public const string AppMarker = "NitroLite";
    public const int SchemaVersion = 1;

    /// <summary>Extension offered in the save dialog, so a backup is recognisable in a downloads folder.</summary>
    public const string FileExtension = ".json";

    public static string SuggestedFileName(DateTime now) => $"NitroLite-backup-{now:yyyy-MM-dd}.json";

    /// <summary>
    /// Writes <paramref name="targetPath"/>. <paramref name="settings"/> is the live settings object, so a change the
    /// user made a second ago is in the file even if it has not been flushed yet.
    /// </summary>
    public static BackupResult Create(
        string targetPath,
        AppSettings settings,
        string? gameProfilesPath,
        string? rgbPresetsPath,
        string appVersion,
        DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return BackupResult.Fail(Loc.T("Yedek dosyası yolu boş."));
        if (settings == null) return BackupResult.Fail(Loc.T("Ayarlar okunamadı."));

        var result = new BackupResult { Success = true };

        var document = new BackupDocument
        {
            CreatedUtc = nowUtc ?? DateTime.UtcNow,
            AppVersion = appVersion ?? string.Empty,
            Settings = JsonSerializer.SerializeToElement(settings)
        };
        result.Written.Add("Ayarlar");

        document.GameProfiles = ReadJsonFile(gameProfilesPath, JsonValueKind.Array, "Oyun profilleri", result);
        document.RgbPresets = ReadJsonFile(rgbPresetsPath, JsonValueKind.Array, "RGB profilleri", result);

        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(targetPath, json);
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Yedek yazılamadı", $"{targetPath}: {ex.Message}");
            return BackupResult.Fail(Loc.T($"Yedek yazılamadı: {ex.Message}"));
        }
    }

    /// <summary>
    /// Reads <paramref name="sourcePath"/> and writes its parts over the live files. The caller restarts the app
    /// afterwards: settings are read once at startup.
    /// </summary>
    public static BackupResult Restore(
        string sourcePath,
        string settingsPath,
        string? gameProfilesPath,
        string? rgbPresetsPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return BackupResult.Fail(Loc.T("Yedek dosyası bulunamadı."));

        BackupDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<BackupDocument>(File.ReadAllText(sourcePath));
        }
        catch (Exception ex)
        {
            return BackupResult.Fail(Loc.T($"Yedek dosyası okunamadı: {ex.Message}"));
        }

        if (document == null)
            return BackupResult.Fail(Loc.T("Yedek dosyası boş."));

        if (!string.Equals(document.App, AppMarker, StringComparison.Ordinal))
            return BackupResult.Fail(Loc.T("Bu dosya bir NitroLite yedeği değil."));

        if (document.Schema < 1 || document.Schema > SchemaVersion)
            return BackupResult.Fail(Loc.T($"Bu yedek farklı bir NitroLite sürümünden (şema {document.Schema}); bu sürüm şema {SchemaVersion} okuyor."));

        if (document.Settings is not JsonElement settingsElement || settingsElement.ValueKind != JsonValueKind.Object)
            return BackupResult.Fail(Loc.T("Yedekte ayar bölümü yok."));

        AppSettings? restored;
        try
        {
            restored = settingsElement.Deserialize<AppSettings>();
        }
        catch (Exception ex)
        {
            return BackupResult.Fail(Loc.T($"Yedekteki ayarlar okunamadı: {ex.Message}"));
        }

        if (restored == null)
            return BackupResult.Fail(Loc.T("Yedekteki ayarlar okunamadı."));

        restored.Normalize();

        // The window rectangle belongs to the screen it was saved on; restoring it on a smaller display would put
        // the window off-screen. The app falls back to its default size and centres itself.
        restored.WindowLeft = null;
        restored.WindowTop = null;
        restored.WindowWidth = null;
        restored.WindowHeight = null;
        restored.WindowMaximized = false;

        // School mode's undo record stays on the machine that enabled it, so a restored "on" would be a switch the
        // user cannot turn off cleanly. It comes back off, and the result says so.
        bool schoolModeDropped = restored.SchoolModeEnabled;
        restored.SchoolModeEnabled = false;

        var result = new BackupResult { Success = true };

        try
        {
            SettingsManager.SaveSettingsAtomic(settingsPath, restored);
            result.Written.Add("Ayarlar");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Yedekten ayarlar yazılamadı", $"{settingsPath}: {ex.Message}");
            return BackupResult.Fail(Loc.T($"Ayarlar yazılamadı: {ex.Message}"));
        }

        if (schoolModeDropped) result.Skipped.Add("Okul modu kapalı olarak geri yüklendi");

        WriteJsonFile(document.GameProfiles, gameProfilesPath, JsonValueKind.Array, "Oyun profilleri", result);
        WriteJsonFile(document.RgbPresets, rgbPresetsPath, JsonValueKind.Array, "RGB profilleri", result);

        return result;
    }

    /// <summary>Reads a JSON file into the document, or records why it was left out.</summary>
    private static JsonElement? ReadJsonFile(string? path, JsonValueKind expected, string label, BackupResult result)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            result.Skipped.Add(label);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != expected)
            {
                result.Skipped.Add(label);
                return null;
            }
            result.Written.Add(label);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Yedeğe {label} eklenemedi", $"{path}: {ex.Message}");
            result.Skipped.Add(label);
            return null;
        }
    }

    /// <summary>Writes one part of a backup over the live file, or records that the backup did not carry it.</summary>
    private static void WriteJsonFile(JsonElement? element, string? path, JsonValueKind expected, string label, BackupResult result)
    {
        if (element is not JsonElement value || value.ValueKind != expected || string.IsNullOrWhiteSpace(path))
        {
            result.Skipped.Add(label);
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            result.Written.Add(label);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Yedekten {label} yazılamadı", $"{path}: {ex.Message}");
            result.Skipped.Add(label);
        }
    }
}
