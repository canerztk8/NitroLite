using System.Text.Json;

namespace NitroLite.Core;

/// <summary>
/// The last lighting configuration the keyboard actually accepted, kept in its own file beside the settings.
/// </summary>
/// <remarks>
/// <para>
/// A second copy of a value the settings file already holds, and deliberately so. The settings file is written on
/// every preference change, including the ones that never reach the hardware; this file is written only after a
/// lighting write succeeded. When the app is killed mid-session the two can disagree, and the one that describes the
/// keyboard rather than the intent is the one a re-assert after a resume has to put back.
/// </para>
/// <para>
/// The name is the one the WPF front end used, so a machine that upgrades keeps the state it had.
/// </para>
/// </remarks>
public static class ActiveRgbStateStore
{
    public const string FileName = "active_rgb_state.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Where the file lives for a given settings path: the same folder, so a portable install stays portable.</summary>
    public static string PathFor(string settingsPath)
    {
        var folder = Path.GetDirectoryName(settingsPath);
        return Path.Combine(string.IsNullOrEmpty(folder) ? "." : folder, FileName);
    }

    /// <summary>The stored configuration, or null when the file is missing or unreadable.</summary>
    public static LightingConfig? Load(string settingsPath)
    {
        try
        {
            var path = PathFor(settingsPath);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return LightingPolicy.Sanitize(JsonSerializer.Deserialize<LightingConfig>(json, Options));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Aktif RGB durumu okunamadı", ex.Message);
            return null;
        }
    }

    /// <summary>Writes the configuration atomically. A failure is logged, never thrown: this is a recovery copy.</summary>
    public static void Save(string settingsPath, LightingConfig? config)
    {
        if (config == null) return;
        try
        {
            var sanitized = LightingPolicy.Sanitize(config);
            if (sanitized == null) return;
            AtomicFile.WriteAllText(PathFor(settingsPath), JsonSerializer.Serialize(sanitized, Options));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Aktif RGB durumu yazılamadı", ex.Message);
        }
    }
}

/// <summary>
/// A backend that can say whether the OpenRGB SDK answered, so the lighting page can show the third path's state.
/// </summary>
/// <remarks>
/// Optional on purpose. Only the Linux backend speaks the SDK today, and a Windows machine that does not implement
/// this simply hides the line rather than claiming OpenRGB is unavailable.
/// </remarks>
public interface IOpenRgbStatusProvider
{
    /// <summary>A one-line description of the SDK connection, or null when nothing is known.</summary>
    string? OpenRgbStatus { get; }
}
