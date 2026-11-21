namespace NitroLite.Core.Hardware;

/// <summary>
/// Turns a profile's <c>unsupportedMethods</c> into capabilities that are off, so a firmware method a model is known
/// to accept and ignore is not offered as a control.
/// </summary>
/// <remarks>
/// <para>
/// The field existed and was parsed, but nothing read it, which is why every profile left it empty: listing a method
/// would have looked like enforcement and enforced nothing. Each method name maps to the capability it drives; a name
/// this table does not know is reported, never guessed at.
/// </para>
/// <para>
/// It can only take a capability away. A profile cannot use this to switch on something the backend did not find.
/// </para>
/// </remarks>
public static class UnsupportedMethodGate
{
    /// <summary>The firmware methods this gate understands, and the capability each one drives.</summary>
    public static IReadOnlyDictionary<string, string> Methods { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SetGamingFanSpeed"] = nameof(HardwareCapabilities.FanControl),
        ["SetGamingFanBehavior"] = nameof(HardwareCapabilities.FanControl),
        ["SetGamingRgbKb"] = nameof(HardwareCapabilities.KeyboardLighting),
        ["SetGamingKBBacklight"] = nameof(HardwareCapabilities.KeyboardLighting),
        ["SetBatteryHealthControl"] = nameof(HardwareCapabilities.BatteryChargeLimit),
    };

    /// <summary>
    /// <paramref name="capabilities"/> with every capability the profile lists a method for switched off.
    /// <paramref name="disabled"/> names what was switched off and why, for the log and the report.
    /// </summary>
    public static HardwareCapabilities Apply(HardwareCapabilities capabilities, ModelProfile? profile, out IReadOnlyList<string> disabled)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var notes = new List<string>();
        disabled = notes;
        if (profile == null || profile.UnsupportedMethods.Count == 0) return capabilities;

        var result = capabilities;
        foreach (var method in profile.UnsupportedMethods)
        {
            if (!Methods.TryGetValue(method, out var capability))
            {
                notes.Add($"{method}: not a method this build maps to a control; ignored");
                continue;
            }

            var before = result;
            result = capability switch
            {
                nameof(HardwareCapabilities.FanControl) => result with { FanControl = false },
                nameof(HardwareCapabilities.KeyboardLighting) => result with { KeyboardLighting = false },
                nameof(HardwareCapabilities.BatteryChargeLimit) => result with { BatteryChargeLimit = false },
                _ => result
            };

            if (!Equals(before, result)) notes.Add($"{capability} off: profile {profile.Id} lists {method} as unsupported");
        }

        return result;
    }
}
