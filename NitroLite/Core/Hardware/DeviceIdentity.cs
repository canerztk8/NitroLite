using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace NitroLite.Core.Hardware;

/// <summary>Who made the processor. NitroLite does not change behaviour by vendor today; the report records it.</summary>
public enum CpuVendor
{
    Unknown = 0,
    Amd = 1,
    Intel = 2
}

/// <summary>
/// What machine NitroLite is running on, read once from SMBIOS through WMI.
/// <para>
/// This is the identity half of hardware detection: it answers "which laptop is this" without touching the Acer
/// service. Capability questions ("does this keyboard have four zones") belong to the probe layer, because they
/// need the service to answer and the identity does not.
/// </para>
/// <para>
/// Serial numbers are deliberately absent. They identify a specific unit rather than a model, they are not needed
/// to pick a capability profile, and the hardware report is meant to be pasted into a public GitHub issue.
/// </para>
/// </summary>
public sealed record DeviceIdentity
{
    /// <summary>SMBIOS system manufacturer, e.g. "Acer".</summary>
    public string Manufacturer { get; init; } = "";

    /// <summary>The model string exactly as SMBIOS reports it, e.g. "Nitro AN16-41".</summary>
    public string Model { get; init; } = "";

    /// <summary>The model code pulled out of <see cref="Model"/>, e.g. "AN16-41". Empty when the string has none.</summary>
    public string ModelCode { get; init; } = "";

    /// <summary>Marketing family, e.g. "Nitro 16". Prefers SMBIOS SystemFamily, falls back to the model code.</summary>
    public string ModelFamily { get; init; } = "";

    /// <summary>Baseboard product. Usually an internal codename ("Sierra_PEH"), so it is reported but never matched on.</summary>
    public string BoardProduct { get; init; } = "";

    /// <summary>BIOS version with the vendor prefix stripped, e.g. "1.27".</summary>
    public string BiosVersion { get; init; } = "";

    /// <summary>BIOS vendor, e.g. "INSYDE Corp.".</summary>
    public string BiosVendor { get; init; } = "";

    public CpuVendor CpuVendor { get; init; }

    /// <summary>Processor name as Windows reports it, e.g. "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics".</summary>
    public string CpuName { get; init; } = "";

    /// <summary>True when SMBIOS names Acer as the system manufacturer.</summary>
    public bool IsAcer { get; init; }

    /// <summary>True when nothing at all could be read; the caller shows a neutral state instead of an empty label.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Model) && string.IsNullOrWhiteSpace(ModelCode) &&
        string.IsNullOrWhiteSpace(CpuName) && string.IsNullOrWhiteSpace(BiosVersion);

    /// <summary>
    /// One line for the window and the report: "Acer Nitro 16 (AN16-41)". Falls back through the pieces that were
    /// actually read, so a machine with a sparse SMBIOS still gets a label instead of "()".
    /// </summary>
    public string DisplayName
    {
        get
        {
            var family = string.IsNullOrWhiteSpace(ModelFamily) ? Model : ModelFamily;
            // SystemFamily often already carries the brand ("Acer Nitro 16"), so prepending the manufacturer would
            // read "Acer Acer Nitro 16". Only add it when the family does not already start with it.
            var needsBrand = !string.IsNullOrWhiteSpace(Manufacturer) &&
                             !family.StartsWith(Manufacturer, StringComparison.OrdinalIgnoreCase);
            var parts = new[] { needsBrand ? Manufacturer : "", family }.Where(p => !string.IsNullOrWhiteSpace(p));
            var name = string.Join(" ", parts);
            if (string.IsNullOrWhiteSpace(name)) name = Model;
            if (!string.IsNullOrWhiteSpace(ModelCode) && !name.Contains(ModelCode, StringComparison.OrdinalIgnoreCase))
                name = $"{name} ({ModelCode})";
            return name.Trim();
        }
    }

    // "AN16-41", "AN515-58", "ANV15-51", "PH16-71", and the longer "AN515-58-79QL" shape. Two or three leading
    // letters, two or three digits, then dash-separated suffixes. A single leading letter ("N50-620") is a desktop
    // and is not matched, which is what we want: NitroLite targets laptops.
    private static readonly Regex ModelCodePattern = new(
        @"\b([A-Z]{2,3}\d{2,3}(?:-[A-Z0-9]{2,4})*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Pulls the model code out of a free-form SMBIOS model string. Returns "" when there is none, so callers can
    /// tell "no code" from "code we did not recognise".
    /// </summary>
    public static string ExtractModelCode(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "";
        var match = ModelCodePattern.Match(model.ToUpperInvariant());
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>
    /// The marketing family for a model code. Only used when SMBIOS SystemFamily is empty: the firmware's own
    /// answer is always preferred, because this table can fall behind new models.
    /// </summary>
    public static string DeriveFamily(string? modelCode)
    {
        if (string.IsNullOrWhiteSpace(modelCode)) return "";
        var code = modelCode.Trim().ToUpperInvariant();
        int dash = code.IndexOf('-');
        var series = dash > 0 ? code[..dash] : code;
        return series switch
        {
            "AN16" => "Nitro 16",
            "AN17" => "Nitro 17",
            "AN515" => "Nitro 5",
            "AN517" => "Nitro 5",
            "ANV15" => "Nitro V 15",
            "ANV16" => "Nitro V 16",
            "PH16" => "Predator Helios 16",
            "PH18" => "Predator Helios 18",
            "PT16" => "Predator Triton 16",
            _ => ""
        };
    }

    /// <summary>
    /// Reads the processor vendor from Win32_Processor.Manufacturer. Anything that is not one of the two known
    /// strings is Unknown rather than a guess: a wrong vendor would later pick the wrong power path.
    /// </summary>
    public static CpuVendor ParseCpuVendor(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return CpuVendor.Unknown;
        var value = manufacturer.Trim();
        if (value.Equals("AuthenticAMD", StringComparison.OrdinalIgnoreCase)) return CpuVendor.Amd;
        if (value.Equals("GenuineIntel", StringComparison.OrdinalIgnoreCase)) return CpuVendor.Intel;
        return CpuVendor.Unknown;
    }

    /// <summary>
    /// Strips the vendor prefix from a BIOS version so two machines can be compared: "V1.27" and "1.27" are the
    /// same firmware. Only a single leading letter followed by a digit is removed, so "ACRSYS - 2" is left alone.
    /// </summary>
    public static string NormalizeBiosVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var value = raw.Trim();
        if (value.Length > 1 && char.IsLetter(value[0]) && (char.IsDigit(value[1]) || value[1] == '.'))
            value = value[1..];
        return value.Trim();
    }

    /// <summary>True when the manufacturer string names Acer. Used only to label the report, never to block a feature.</summary>
    public static bool IsAcerManufacturer(string? manufacturer) =>
        !string.IsNullOrWhiteSpace(manufacturer) &&
        manufacturer.Contains("Acer", StringComparison.OrdinalIgnoreCase);
}
