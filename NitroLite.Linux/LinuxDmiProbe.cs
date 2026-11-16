using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// Reads the machine's identity from <c>/sys/class/dmi/id</c>, the Linux equivalent of the SMBIOS read the Windows
/// build does through WMI.
/// </summary>
/// <remarks>
/// The kernel exposes the same SMBIOS strings as plain files, world readable, so this needs no privilege and no
/// module. The model-code extraction and the family table are shared with the Windows probe
/// (<see cref="DeviceIdentity.ExtractModelCode"/>), which is what lets one <c>profiles.json</c> serve both platforms.
/// </remarks>
public static class LinuxDmiProbe
{
    /// <summary>
    /// Builds the identity. Never throws and never returns null: a machine whose DMI files are unreadable gets an
    /// empty identity, which the caller renders as a neutral state instead of an empty label.
    /// </summary>
    public static DeviceIdentity Read(ISysfsFileSystem fs, string dmiRoot = LinuxPaths.DmiRoot)
    {
        ArgumentNullException.ThrowIfNull(fs);

        var vendor = Read(fs, dmiRoot, "sys_vendor");
        var product = Read(fs, dmiRoot, "product_name");
        var board = Read(fs, dmiRoot, "board_name");
        var family = Read(fs, dmiRoot, "product_family");
        var biosVersion = Read(fs, dmiRoot, "bios_version");
        var biosVendor = Read(fs, dmiRoot, "bios_vendor");

        // Some Acer boards leave product_name empty and only fill board_name. The model code is what the profile
        // table matches on, so falling back is worth it: without it the machine drops to the catch-all profile.
        var model = !string.IsNullOrWhiteSpace(product) ? product : board;
        var modelCode = DeviceIdentity.ExtractModelCode(model);
        if (string.IsNullOrWhiteSpace(modelCode)) modelCode = DeviceIdentity.ExtractModelCode(board);

        var modelFamily = !string.IsNullOrWhiteSpace(family) ? family : DeviceIdentity.DeriveFamily(modelCode);

        return new DeviceIdentity
        {
            Manufacturer = vendor,
            Model = model,
            ModelCode = modelCode,
            ModelFamily = modelFamily,
            BoardProduct = board,
            BiosVersion = biosVersion,
            BiosVendor = biosVendor,
            CpuVendor = ReadCpuVendor(fs),
            CpuName = ReadCpuName(fs),
            IsAcer = vendor.Contains("Acer", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string Read(ISysfsFileSystem fs, string root, string name) =>
        fs.ReadAllText(LinuxPaths.Combine(root, name)) ?? "";

    /// <summary>
    /// The vendor from <c>/proc/cpuinfo</c>. Not under <c>/sys</c>, but it is the same kind of read and the report
    /// wants it; a missing file simply leaves the vendor unknown.
    /// </summary>
    private static CpuVendor ReadCpuVendor(ISysfsFileSystem fs)
    {
        var cpuinfo = fs.ReadAllText("/proc/cpuinfo");
        if (string.IsNullOrEmpty(cpuinfo)) return CpuVendor.Unknown;
        if (cpuinfo.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase)) return CpuVendor.Amd;
        if (cpuinfo.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase)) return CpuVendor.Intel;
        return CpuVendor.Unknown;
    }

    /// <summary>The first <c>model name</c> line of <c>/proc/cpuinfo</c>, or empty.</summary>
    private static string ReadCpuName(ISysfsFileSystem fs)
    {
        var cpuinfo = fs.ReadAllText("/proc/cpuinfo");
        if (string.IsNullOrEmpty(cpuinfo)) return "";

        foreach (var line in cpuinfo.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (!line[..colon].Trim().Equals("model name", StringComparison.OrdinalIgnoreCase)) continue;
            return line[(colon + 1)..].Trim();
        }

        return "";
    }
}
