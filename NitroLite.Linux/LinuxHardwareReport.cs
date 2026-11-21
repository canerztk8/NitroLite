using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NitroLite.Core;
using NitroLite.Core.Hardware;

namespace NitroLite.Linux;

/// <summary>
/// Builds the Markdown hardware report a user pastes into a GitHub issue.
/// </summary>
/// <remarks>
/// <para>
/// The report is deliberately English: it is read by maintainers, it is pasted next to English issue templates, and a
/// translated report would make two machines impossible to compare. The button and the status line around it are
/// localised; the payload is not.
/// </para>
/// <para>
/// Privacy is a hard requirement, not a nicety. The report is built from <see cref="LinuxCapabilities"/>, which carries
/// only model-level facts: SMBIOS product and board names, BIOS version and vendor, the processor name, which sysfs
/// attributes exist and what the capability probe concluded. It never reads <c>product_serial</c>,
/// <c>board_serial</c>, <c>product_uuid</c> or <c>chassis_serial</c>, it never touches the user's home directory, and
/// every line is passed through <see cref="Redact"/> before it is emitted so a note that happens to carry a path
/// cannot leak one either.
/// </para>
/// </remarks>
public static class LinuxHardwareReport
{
    /// <summary>Home directories, which identify a person rather than a machine.</summary>
    private static readonly Regex HomePath = new(
        @"(?<root>/home|/Users|/root)(/[^\s""'<>|]+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Anything that names a unit rather than a model. Belt and braces: nothing here reads one.</summary>
    private static readonly Regex UnitIdentifier = new(
        @"\b(serial(?:\s*(?:number|no\.?))?|uuid|machine-id|hostname)\b\s*[:=]\s*\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The report for this build, generated now.</summary>
    public static string Build(LinuxCapabilities capabilities) =>
        Build(capabilities, AppVersion(), DateTime.UtcNow);

    /// <summary>
    /// The report for a specific build and timestamp. The two are parameters so a test can pin the output instead of
    /// matching a moving date.
    /// </summary>
    public static string Build(LinuxCapabilities capabilities, string appVersion, DateTime generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var identity = capabilities.Identity;
        var nodes = capabilities.Nodes;
        var hardware = capabilities.Hardware;
        var text = new StringBuilder();

        text.AppendLine("# NitroLite — Linux hardware report");
        text.AppendLine();
        text.AppendLine($"- NitroLite: {Clean(appVersion)}");
        text.AppendLine($"- Generated (UTC): {generatedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        text.AppendLine($"- Backend: {Clean(hardware.Backend)}");
        text.AppendLine($"- Tier: {Clean(capabilities.Tier)}");
        text.AppendLine($"- udev rule needed: {(capabilities.NeedsUdevRule ? "yes" : "no")}");
        text.AppendLine();

        text.AppendLine("## Device (DMI)");
        text.AppendLine();
        text.AppendLine($"- Manufacturer: {Clean(identity.Manufacturer)}");
        text.AppendLine($"- Model: {Clean(identity.Model)}");
        text.AppendLine($"- Model code: {Clean(identity.ModelCode)}");
        text.AppendLine($"- Family: {Clean(identity.ModelFamily)}");
        text.AppendLine($"- Board: {Clean(identity.BoardProduct)}");
        text.AppendLine($"- BIOS: {Clean(identity.BiosVersion)} ({Clean(identity.BiosVendor)})");
        text.AppendLine($"- CPU: {Clean(identity.CpuName)} [{identity.CpuVendor}]");
        text.AppendLine($"- Acer: {(identity.IsAcer ? "yes" : "no")}");
        text.AppendLine();

        text.AppendLine("## Kernel modules");
        text.AppendLine();
        // The module and the profile are reported apart: on an AMD laptop platform_profile is often registered by
        // amd-pmf rather than acer-wmi, and one line naming both made a present file look like a present Acer driver.
        text.AppendLine($"- acer-wmi: {(capabilities.AcerWmiLoaded ? "loaded" : "not loaded")}");
        var predatorV4 = capabilities.PredatorV4;
        text.AppendLine($"- acer-wmi predator_v4: {(predatorV4 == null ? "not available on this kernel" : Clean(predatorV4))}");
        text.AppendLine($"- platform_profile: {(nodes.PlatformProfile != null ? "present" : "absent")}");
        if (nodes.PlatformProfileChoices != null)
        {
            // The file's contents, not its path: the path is the same on every machine and says nothing.
            text.AppendLine($"- platform_profile_choices: {Clean(capabilities.PlatformProfileChoicesText)}");
        }
        text.AppendLine($"- operating mode written through: {(capabilities.ModeViaPowerProfiles ? "power-profiles-daemon" : nodes.PlatformProfile != null ? "platform_profile" : "nothing")}");
        text.AppendLine($"- Linuwu-Sense: {(nodes.SenseRoot != null ? "present" : "absent")}");
        text.AppendLine($"- ENE KB5130 keyboard (hidraw): {(nodes.HidrawKeyboard != null ? Clean(nodes.HidrawKeyboard) : "absent")}");
        text.AppendLine($"- suggested kernel step: {capabilities.KernelHint}");
        text.AppendLine($"- running kernel: {(capabilities.KernelVersion?.ToString() ?? "unknown")}");
        text.AppendLine(capabilities.KernelModel is { } km
            ? $"- mainline support: since Linux {km.Since}{(km.FanControlSince != null ? $", fan control since {km.FanControlSince}" : ", no fan control")}"
            : "- mainline support: not in the acer-wmi quirk table");
        text.AppendLine($"- kernel fan control (hwmon pwm): {(nodes.HasKernelFanControl ? "present" : "absent")}");
        text.AppendLine();

        text.AppendLine("## hwmon");
        text.AppendLine();
        if (nodes.Hwmon.Count == 0)
        {
            text.AppendLine("- none");
        }
        else
        {
            foreach (var device in nodes.Hwmon)
            {
                text.AppendLine(
                    $"- {Clean(device.Name)}: {device.TemperaturesC.Count} temperature(s), {device.FanRpms.Count} fan(s)");
            }
        }
        text.AppendLine();

        text.AppendLine("## sysfs attributes");
        text.AppendLine();
        AppendNode(text, "platform_profile", nodes.PlatformProfile);
        AppendNode(text, "fan_speed", nodes.FanSpeed);
        AppendNode(text, "backlight_timeout", nodes.BacklightTimeout);
        AppendNode(text, "battery_limiter", nodes.BatteryLimiter);
        AppendNode(text, "charge_control_end_threshold", nodes.ChargeThreshold);
        AppendNode(text, "lcd_override", nodes.LcdOverride);
        AppendNode(text, "cool_boost", nodes.CoolBoost);
        AppendNode(text, "dgpu_mode", nodes.DgpuMode);
        AppendNode(text, "per_zone_mode", nodes.PerZoneMode);
        AppendNode(text, "four_zone_mode", nodes.FourZoneMode);
        text.AppendLine();

        text.AppendLine("## Capabilities");
        text.AppendLine();
        AppendCapability(text, "fan control", hardware.FanControl);
        AppendCapability(text, "operating mode", hardware.OperatingModeControl);
        AppendCapability(text, "MUX switch", hardware.MuxSwitch);
        AppendCapability(text, "battery charge limit", hardware.BatteryChargeLimit);
        AppendCapability(text, "keyboard lighting", hardware.KeyboardLighting);
        AppendCapability(text, "backlight timeout", hardware.BacklightTimeout);
        AppendCapability(text, "CoolBoost", hardware.CoolBoost);
        AppendCapability(text, "LCD overdrive", hardware.LcdOverdrive);
        AppendCapability(text, "WinKey lock", hardware.WinKeyLock);
        text.AppendLine();

        text.AppendLine("## Model profile");
        text.AppendLine();
        text.AppendLine($"- Matched: {Clean(capabilities.Profile.Id)}");
        text.AppendLine($"- Model codes: {Clean(string.Join(", ", capabilities.Profile.ModelCodes))}");
        text.AppendLine($"- CPU fan ceiling: {capabilities.Profile.MaxCpuFanRpm} RPM");
        text.AppendLine($"- GPU fan ceiling: {capabilities.Profile.MaxGpuFanRpm} RPM");
        text.AppendLine();

        text.AppendLine("## Probe notes");
        text.AppendLine();
        if (capabilities.Notes.Count == 0)
        {
            text.AppendLine("- none");
        }
        else
        {
            foreach (var note in capabilities.Notes) text.AppendLine($"- {Clean(note)}");
        }

        return text.ToString();
    }

    /// <summary>
    /// Removes anything that identifies a person or a specific unit: home directories and any <c>serial:</c>,
    /// <c>uuid:</c> or <c>hostname:</c> pair that reached the text through a note.
    /// <para>
    /// The two rules above are the shapes a Linux probe produces. <see cref="Redaction.Scrub"/> runs last as a
    /// safety net, because a note is not always produced by the probe: a path can arrive from a settings file, from
    /// a user's own paste, or from a build running under WSL, and <c>C:\Users\name</c> is not a shape
    /// <see cref="HomePath"/> recognises. The shared redactor already knows that form and the machine name, and
    /// running it after the specific rules means it can only remove more, never put anything back.
    /// </para>
    /// </summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var redacted = HomePath.Replace(value, match => match.Groups["root"].Value + "/<redacted>");
        redacted = UnitIdentifier.Replace(redacted, match => match.Groups[1].Value + ": <redacted>");
        return Redaction.Scrub(redacted);
    }

    /// <summary>One line of the report: redacted, and with newlines flattened so a note cannot break the list.</summary>
    private static string Clean(string? value) =>
        Redact(value).Replace("\r", " ").Replace("\n", " ").Trim();

    private static void AppendNode(StringBuilder text, string name, string? path) =>
        text.AppendLine($"- {name}: {(path != null ? "present" : "absent")}");

    private static void AppendCapability(StringBuilder text, string name, bool supported) =>
        text.AppendLine($"- {name}: {(supported ? "yes" : "no")}");

    private static string AppVersion()
    {
        var version = typeof(LinuxHardwareReport).Assembly.GetName().Version;
        return version == null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
