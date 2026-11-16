using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// The ACPI platform profile strings, and how they map onto NitroLite's operating modes.
/// </summary>
/// <remarks>
/// The kernel's <c>acer-wmi</c> driver registers <c>/sys/firmware/acpi/platform_profile</c> with the standard
/// <c>platform_profile</c> class, so the values are the ACPI ones and not Acer's own enum. The mapping is not
/// one-to-one: Acer has five modes and the kernel offers four or five depending on the model, so
/// <c>balanced-performance</c> becomes Performance and <c>performance</c> becomes Turbo. A machine that does not
/// advertise a profile simply does not get that mode, which is what <see cref="ParseChoices"/> is for.
/// </remarks>
public static class LinuxPlatformProfile
{
    /// <summary>Quiet: the fans stay low and the CPU is capped.</summary>
    public const string Quiet = "quiet";

    /// <summary>Balanced: the firmware's own default.</summary>
    public const string Balanced = "balanced";

    /// <summary>Balanced-performance: more headroom, still quiet-ish.</summary>
    public const string BalancedPerformance = "balanced-performance";

    /// <summary>Performance: the fans are allowed to be loud.</summary>
    public const string Performance = "performance";

    /// <summary>Low-power: the most economical profile, for battery.</summary>
    public const string LowPower = "low-power";

    /// <summary>
    /// The modes a machine advertises, in NitroLite's order. An empty or unreadable answer returns an empty list,
    /// which the caller must treat as "not asked", never as "no modes work".
    /// </summary>
    public static IReadOnlyList<OperatingMode> ParseChoices(string? choices)
    {
        if (string.IsNullOrWhiteSpace(choices)) return Array.Empty<OperatingMode>();

        var modes = new List<OperatingMode>();
        foreach (var token in choices.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var mode = Parse(token);
            if (mode.HasValue && !modes.Contains(mode.Value)) modes.Add(mode.Value);
        }

        // Stable, predictable order for the UI: the same order the Windows build lists them in.
        return modes
            .OrderBy(m => m switch
            {
                OperatingMode.Quiet => 0,
                OperatingMode.Default => 1,
                OperatingMode.Extreme => 2,
                OperatingMode.Turbo => 3,
                OperatingMode.Eco => 4,
                _ => 5
            })
            .ToArray();
    }

    /// <summary>One profile string to a mode, or null when it is not one this build knows.</summary>
    public static OperatingMode? Parse(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return null;
        return profile.Trim().ToLowerInvariant() switch
        {
            Quiet => OperatingMode.Quiet,
            Balanced => OperatingMode.Default,
            BalancedPerformance => OperatingMode.Extreme,
            Performance => OperatingMode.Turbo,
            LowPower => OperatingMode.Eco,
            _ => null
        };
    }

    /// <summary>
    /// The profile string to write for a mode. <see cref="OperatingMode.Extreme"/> and
    /// <see cref="OperatingMode.Turbo"/> are distinct on Windows but adjacent here, so a machine that advertises
    /// only one of them still gets the closest match rather than a rejected write.
    /// </summary>
    public static string ToProfile(OperatingMode mode, IReadOnlyList<OperatingMode>? supported = null)
    {
        var has = supported == null || supported.Count == 0;

        return mode switch
        {
            OperatingMode.Quiet => Quiet,
            OperatingMode.Default => Balanced,
            OperatingMode.Extreme => has || supported!.Contains(OperatingMode.Extreme) ? BalancedPerformance : Performance,
            OperatingMode.Turbo => has || supported!.Contains(OperatingMode.Turbo) ? Performance : BalancedPerformance,
            OperatingMode.Eco => LowPower,
            _ => Balanced
        };
    }
}
