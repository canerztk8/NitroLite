using System;
using System.Globalization;
using System.Linq;

namespace NitroLite.Core;

/// <summary>What a one-shot hardware command asked for. A null field was not mentioned on the command line.</summary>
public sealed record HardwareCommand(OperatingMode? Mode, FanMode? Fan, int? FanPercent, int? MouseProfile)
{
    public bool IsEmpty => Mode is null && Fan is null && MouseProfile is null;
}

/// <summary>
/// The one-shot commands a script, a launcher or a macro pad can run without opening the window:
/// <c>--mode</c>, <c>--fan</c> and <c>--profile</c>. Parsing lives here, away from the window, so every accepted
/// spelling and every rejection can be tested without a laptop.
/// <para>
/// These do not need the mutex: a running NitroLite sees the change on its next poll as an external change, which
/// is the same path a change made in NitroSense takes.
/// </para>
/// </summary>
public static class CliCommands
{
    public const string ModeFlag = "--mode";
    public const string FanFlag = "--fan";
    public const string ProfileFlag = "--profile";

    /// <summary>True when the command line mentions one of the hardware flags, so the GUI must not start.</summary>
    public static bool IsHardwareCommand(string[]? args) =>
        args != null && args.Any(a => !string.IsNullOrWhiteSpace(a) && (
            a.Trim().Equals(ModeFlag, StringComparison.OrdinalIgnoreCase) ||
            a.Trim().Equals(FanFlag, StringComparison.OrdinalIgnoreCase) ||
            a.Trim().Equals(ProfileFlag, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Reads the flags into a <see cref="HardwareCommand"/>. Returns false with a message the user can act on when a
    /// value is missing or out of range; never throws on a malformed command line.
    /// </summary>
    public static bool TryParse(string[]? args, out HardwareCommand command, out string error)
    {
        command = new HardwareCommand(null, null, null, null);
        error = string.Empty;
        if (args == null) return true;

        OperatingMode? mode = null;
        FanMode? fan = null;
        int? fanPercent = null;
        int? mouseProfile = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i]?.Trim();
            if (string.IsNullOrEmpty(arg)) continue;

            if (arg.Equals(ModeFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(args, ref i, out var value))
                {
                    error = Loc.T($"{ModeFlag} bir değer bekliyor.");
                    return false;
                }
                if (!TryParseMode(value, out var parsed))
                {
                    error = Loc.T($"{ModeFlag} için geçerli bir değer yok: '{value}'. Kullanın: turbo, performance, balanced, quiet.");
                    return false;
                }
                mode = parsed;
            }
            else if (arg.Equals(FanFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(args, ref i, out var value))
                {
                    error = Loc.T($"{FanFlag} bir değer bekliyor.");
                    return false;
                }

                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                {
                    if (percent < FanCurve.MinPercent || percent > FanCurve.MaxPercent)
                    {
                        error = Loc.T($"{FanFlag} yüzdesi {FanCurve.MinPercent}-{FanCurve.MaxPercent} arasında olmalı: '{value}'.");
                        return false;
                    }
                    fan = FanMode.Custom;
                    fanPercent = percent;
                }
                else if (TryParseFan(value, out var parsedFan))
                {
                    fan = parsedFan;
                }
                else
                {
                    error = Loc.T($"{FanFlag} için geçerli bir değer yok: '{value}'. Kullanın: auto, max, custom veya {FanCurve.MinPercent}-{FanCurve.MaxPercent} arası bir yüzde.");
                    return false;
                }
            }
            else if (arg.Equals(ProfileFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(args, ref i, out var value))
                {
                    error = Loc.T($"{ProfileFlag} bir değer bekliyor.");
                    return false;
                }
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var profile) || profile < 1 || profile > 4)
                {
                    error = Loc.T($"{ProfileFlag} 1-4 arasında olmalı: '{value}'.");
                    return false;
                }
                mouseProfile = profile;
            }
        }

        command = new HardwareCommand(mode, fan, fanPercent, mouseProfile);
        return true;
    }

    /// <summary>The value that follows a flag, or false when the flag was the last thing on the line.</summary>
    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Length) return false;
        var next = args[index + 1]?.Trim();
        if (string.IsNullOrEmpty(next) || next.StartsWith("--", StringComparison.Ordinal)) return false;
        value = next;
        index++;
        return true;
    }

    private static bool TryParseMode(string value, out OperatingMode mode)
    {
        mode = OperatingMode.Default;
        switch (value.Trim().ToLowerInvariant())
        {
            case "turbo": mode = OperatingMode.Turbo; return true;
            case "performance":
            case "extreme": mode = OperatingMode.Extreme; return true;
            case "balanced":
            case "default": mode = OperatingMode.Default; return true;
            case "quiet": mode = OperatingMode.Quiet; return true;
            case "eco": mode = OperatingMode.Eco; return true;
            default: return false;
        }
    }

    private static bool TryParseFan(string value, out FanMode mode)
    {
        mode = FanMode.Auto;
        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
            case "automatic": mode = FanMode.Auto; return true;
            case "max":
            case "maximum": mode = FanMode.Max; return true;
            case "custom": mode = FanMode.Custom; return true;
            default: return false;
        }
    }
}
