using System.Globalization;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// The adapter and battery state from <c>/sys/class/power_supply</c>.
/// </summary>
/// <remarks>
/// Game profiles need it for one decision: a Turbo target on battery is refused by the firmware, so the profile is
/// clamped to Balanced instead of being written and silently ignored. The class names differ between drivers
/// (<c>AC</c>, <c>ADP1</c>, <c>ACAD</c>), which is why the adapter is found by prefix rather than by a fixed path.
/// </remarks>
public static class LinuxPowerSupply
{
    /// <summary>
    /// The adapter state and the battery percentage. <c>OnAc</c> is null when no adapter node answered, which the
    /// caller must treat as "unknown" rather than as "on battery": downgrading a game profile on a guess would take
    /// Turbo away from every desktop-replacement laptop whose adapter driver uses a name this build does not know.
    /// </summary>
    public static (bool? OnAc, int? BatteryPercent) Read(ISysfsFileSystem fs, string root = LinuxPaths.PowerSupplyRoot)
    {
        ArgumentNullException.ThrowIfNull(fs);

        bool? onAc = null;
        int? percent = null;

        foreach (var directory in fs.EnumerateDirectories(root))
        {
            var name = directory.TrimEnd('/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];

            if (onAc == null && (IsAdapter(name) || IsMains(fs, directory)))
            {
                var online = ReadInt(fs, LinuxPaths.Combine(directory, "online"));
                if (online.HasValue) onAc = online.Value != 0;
                continue;
            }

            if (percent == null && name.StartsWith("BAT", StringComparison.OrdinalIgnoreCase))
            {
                var capacity = ReadInt(fs, LinuxPaths.Combine(directory, "capacity"));
                if (capacity is >= 0 and <= 100) percent = capacity;
            }
        }

        return (onAc, percent);
    }

    /// <summary>True for the directory names the kernel's adapter drivers use.</summary>
    public static bool IsAdapter(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.StartsWith("AC", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("ADP", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("ACAD", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the supply says it is the mains adapter, whatever its directory is called. Some drivers use names the
    /// prefix list does not know (<c>ACPI0003:00</c>, a vendor's own); <c>type</c> is the attribute the class defines
    /// for exactly this. USB-C sources say <c>USB</c> and are not counted: one can be a phone being charged.
    /// </summary>
    private static bool IsMains(ISysfsFileSystem fs, string directory) =>
        string.Equals(fs.ReadAllText(LinuxPaths.Combine(directory, "type"))?.Trim(), "Mains", StringComparison.Ordinal);

    private static int? ReadInt(ISysfsFileSystem fs, string path)
    {
        var text = fs.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
