using System.Globalization;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// Panel backlight brightness over <c>/sys/class/backlight</c>, the Linux half of
/// <see cref="IScreenBrightnessService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The kernel exposes a raw level and the maximum that level can reach, not a percentage, so both directions are a
/// conversion. The device is resolved once at construction: a laptop has one internal panel, and re-scanning the class
/// on every read would make the school-mode state machine do file-system work inside its undo record.
/// </para>
/// <para>
/// A write is read back before it is reported as applied. Some panels clamp the value to their own table, and a
/// brightness the app claims to have set but did not is worse than a refusal: school mode records the previous value
/// and would restore a number that was never in effect.
/// </para>
/// </remarks>
public sealed class LinuxScreenBrightnessService : IScreenBrightnessService
{
    private readonly ISysfsFileSystem _fs;
    private readonly string? _brightnessNode;
    private readonly string? _maxNode;

    /// <summary>Resolves the first backlight device that exposes both a level and a maximum.</summary>
    public LinuxScreenBrightnessService(ISysfsFileSystem fs, string root = LinuxPaths.BacklightRoot)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));

        foreach (var directory in fs.EnumerateDirectories(root))
        {
            var brightness = LinuxPaths.Combine(directory, LinuxPaths.Brightness);
            var max = LinuxPaths.Combine(directory, LinuxPaths.MaxBrightness);
            if (!fs.FileExists(brightness) || !fs.FileExists(max)) continue;

            _brightnessNode = brightness;
            _maxNode = max;
            break;
        }
    }

    /// <summary>True when a panel answered the probe. False means the machine has no dimmable backlight.</summary>
    public bool IsAvailable => _brightnessNode != null && _maxNode != null;

    /// <summary>The node this service writes, for the hardware report. Null when there is none.</summary>
    public string? Node => _brightnessNode;

    public int? GetBrightness()
    {
        int? max = ReadInt(_maxNode);
        int? raw = ReadInt(_brightnessNode);
        if (max is not > 0 || raw is not int level) return null;

        return ToPercent(level, max.Value);
    }

    /// <summary>
    /// Sets the brightness in percent. The level is clamped to at least one step: zero turns the panel off, and a
    /// school mode that blacks the screen is not one the user can switch back off.
    /// </summary>
    public bool SetBrightness(int percent)
    {
        if (!IsAvailable) return false;

        int max = ReadInt(_maxNode) ?? 0;
        if (max <= 0) return false;

        int target = ToRaw(Math.Clamp(percent, 1, 100), max);
        var result = _fs.WriteAllText(_brightnessNode!, target.ToString(CultureInfo.InvariantCulture) + "\n");
        if (!result.Ok)
        {
            AppLog.Warn("Ekran parlaklığı yazılamadı", $"{_brightnessNode}: {result.Status} {result.Detail}");
            return false;
        }

        // Read back: a panel that clamps to its own table still reports the level it settled on, and that is what the
        // undo record has to hold.
        return ReadInt(_brightnessNode) is int observed && Math.Abs(observed - target) <= 1;
    }

    /// <summary>A raw level as a percentage, rounded to the nearest whole percent.</summary>
    public static int ToPercent(int raw, int max) =>
        max <= 0 ? 0 : (int)Math.Round(Math.Clamp(raw, 0, max) / (double)max * 100, MidpointRounding.AwayFromZero);

    /// <summary>A percentage as a raw level, never below one step.</summary>
    public static int ToRaw(int percent, int max) =>
        max <= 0 ? 0 : Math.Clamp((int)Math.Round(Math.Clamp(percent, 1, 100) / 100.0 * max, MidpointRounding.AwayFromZero), 1, max);

    private int? ReadInt(string? path)
    {
        if (path == null) return null;
        var text = _fs.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
