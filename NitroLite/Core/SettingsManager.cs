using System;
using System.IO;
using System.Text.Json;

namespace NitroLite.Core;

public static class SettingsManager
{
    public static AppSettings LoadSettingsWithFallback(string primaryPath, string? backupPath = null, string? legacyPath = null)
    {
        backupPath ??= primaryPath + ".bak";

        // 1. Try Primary settings file
        if (File.Exists(primaryPath))
        {
            try
            {
                var json = File.ReadAllText(primaryPath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) return s;
                }
            }
            catch { }
        }

        // 2. Try Backup settings file (.bak)
        if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
        {
            try
            {
                var json = File.ReadAllText(backupPath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) return s;
                }
            }
            catch { }
        }

        // 3. Try Legacy settings file (e.g. config.json)
        if (!string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath))
        {
            try
            {
                var json = File.ReadAllText(legacyPath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) return s;
                }
            }
            catch { }
        }

        // 4. Default settings
        return new AppSettings();
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
