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
            AppLog.Warn("The settings file could not be read", $"{primaryPath}: {error}");
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
            var json = BoundedTextFile.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return (null, Loc.T("dosya boş"));
            var parsed = JsonSerializer.Deserialize<AppSettings>(json);
            if (parsed == null) return (null, Loc.T("içerik okunamadı"));
            // A number that is a valid JSON value is a valid enum value to System.Text.Json even when the enum has
            // no such member, so the range check has to happen here rather than at every use.
            parsed.Normalize();
            return (parsed, null);
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

        var json = JsonSerializer.Serialize(settings, JsonDefaults.Indented);

        // Validate what is about to be written, not what is already on disk. A serialisation bug would otherwise
        // replace a good settings file with an unreadable one and the next launch would start from defaults.
        if (JsonSerializer.Deserialize<AppSettings>(json) == null)
        {
            throw new InvalidOperationException("Ayarlar serileştirilemedi; dosya yazılmadı.");
        }

        // A previous good file becomes the backup by being renamed, and only when it is a file worth falling back to:
        // an empty or corrupt primary must not push the last good copy out of the way. The read-and-write it used to
        // be was a second durable write of a file already on the platter, on every single save.
        bool keepPrevious = File.Exists(filePath) && IsKnownGoodOrValid(filePath);

        AtomicFile.WriteAllText(filePath, json, keepPrevious ? filePath + ".bak" : null);
        RememberKnownGood(filePath);
    }

    // Remembers the file this process last wrote successfully. Settings are saved on every change, and re-reading
    // and re-parsing the file on each save just to decide whether it is safe to back up was pure overhead: a file
    // this process wrote a moment ago is known to be valid.
    private static readonly object KnownGoodLock = new();
    private static string? _knownGoodPath;
    private static long _knownGoodLength;
    private static DateTime _knownGoodWriteUtc;

    private static bool IsKnownGoodOrValid(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return false;
            lock (KnownGoodLock)
            {
                if (_knownGoodPath != null
                    && string.Equals(_knownGoodPath, path, StringComparison.OrdinalIgnoreCase)
                    && _knownGoodLength == info.Length
                    && _knownGoodWriteUtc == info.LastWriteTimeUtc)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to the full check.
        }
        return IsValidSettingsFile(path);
    }

    private static void RememberKnownGood(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            lock (KnownGoodLock)
            {
                _knownGoodPath = path;
                _knownGoodLength = info.Length;
                _knownGoodWriteUtc = info.LastWriteTimeUtc;
            }
        }
        catch
        {
            // The cache is an optimisation only.
        }
    }

    public static bool IsValidSettingsFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var fi = new FileInfo(path);
            if (fi.Length == 0) return false;
            var text = BoundedTextFile.ReadAllText(path);
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
