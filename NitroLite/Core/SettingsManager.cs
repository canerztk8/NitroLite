using System;
using System.IO;
using System.Text.Json;

namespace NitroLite.Core;

/// <summary>Where the settings that are now in use actually came from.</summary>
public enum SettingsLoadSource
{
    Defaults,
    Primary,
    Backup,
    Legacy
}

/// <summary>Result of a settings load, including whether the primary file had to be discarded.</summary>
public sealed class SettingsLoadResult
{
    public AppSettings Settings { get; init; } = new();
    public SettingsLoadSource Source { get; init; } = SettingsLoadSource.Defaults;

    /// <summary>True when the primary file existed but could not be read or parsed.</summary>
    public bool PrimaryCorrupt { get; init; }

    /// <summary>Why the primary file was rejected, for the log and the user-facing warning.</summary>
    public string? PrimaryError { get; init; }
}

public static class SettingsManager
{
    public static AppSettings LoadSettingsWithFallback(string primaryPath, string? backupPath = null, string? legacyPath = null)
        => LoadSettings(primaryPath, backupPath, legacyPath).Settings;

    /// <summary>
    /// Loads settings from the primary file, then the backup, then the legacy file, then defaults. The returned
    /// result says which one won and whether the primary file was corrupt, so the caller can tell the user
    /// instead of silently starting from defaults.
    /// </summary>
    public static SettingsLoadResult LoadSettings(string primaryPath, string? backupPath = null, string? legacyPath = null)
    {
        backupPath ??= primaryPath + ".bak";

        bool primaryCorrupt = false;
        string? primaryError = null;

        // 1. Try Primary settings file
        if (File.Exists(primaryPath))
        {
            var (settings, error) = TryRead(primaryPath);
            if (settings != null)
                return new SettingsLoadResult { Settings = settings, Source = SettingsLoadSource.Primary };

            primaryCorrupt = true;
            primaryError = error;
            AppLog.Warn("Ayarlar dosyası okunamadı", $"{primaryPath}: {error}");
        }

        // 2. Try Backup settings file (.bak)
        if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
        {
            var (settings, _) = TryRead(backupPath);
            if (settings != null)
                return new SettingsLoadResult
                {
                    Settings = settings,
                    Source = SettingsLoadSource.Backup,
                    PrimaryCorrupt = primaryCorrupt,
                    PrimaryError = primaryError
                };
        }

        // 3. Try Legacy settings file (e.g. config.json)
        if (!string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath))
        {
            var (settings, _) = TryRead(legacyPath);
            if (settings != null)
                return new SettingsLoadResult
                {
                    Settings = settings,
                    Source = SettingsLoadSource.Legacy,
                    PrimaryCorrupt = primaryCorrupt,
                    PrimaryError = primaryError
                };
        }

        // 4. Default settings
        return new SettingsLoadResult
        {
            Settings = new AppSettings(),
            Source = SettingsLoadSource.Defaults,
            PrimaryCorrupt = primaryCorrupt,
            PrimaryError = primaryError
        };
    }

    /// <summary>Reads one settings file; returns null settings plus the reason when it cannot be used.</summary>
    private static (AppSettings? Settings, string? Error) TryRead(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return (null, Loc.T("dosya boş"));
            var parsed = JsonSerializer.Deserialize<AppSettings>(json);
            return parsed != null ? (parsed, null) : (null, Loc.T("içerik okunamadı"));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public static void SaveSettingsAtomic(string filePath, AppSettings settings)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        string tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
        string backupPath = filePath + ".bak";

        File.WriteAllText(tempPath, json);

        // Maintain backup of previous valid settings (only copy if filePath contains valid deserializable settings)
        if (File.Exists(filePath) && IsValidSettingsFile(filePath))
        {
            try
            {
                File.Copy(filePath, backupPath, overwrite: true);
            }
            catch { }
        }

        File.Move(tempPath, filePath, overwrite: true);
    }

    public static bool IsValidSettingsFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var fi = new FileInfo(path);
            if (fi.Length == 0) return false;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parsed = JsonSerializer.Deserialize<AppSettings>(text);
            return parsed != null;
        }
        catch
        {
            return false;
        }
    }
}
