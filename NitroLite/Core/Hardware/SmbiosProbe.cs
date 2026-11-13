using System;
using System.Management;

namespace NitroLite.Core.Hardware;

/// <summary>
/// Reads the machine identity out of SMBIOS through WMI.
/// <para>
/// Every query is isolated: a machine with a broken WMI repository, a locked-down namespace, or a firmware that
/// omits a property still gets a partial identity instead of an exception. Nothing here throws, and nothing here
/// writes. The probe is read-only by construction, which is why it is safe to run on startup for every user.
/// </para>
/// </summary>
public static class SmbiosProbe
{
    /// <summary>
    /// Reads the identity. Returns an empty <see cref="DeviceIdentity"/> when WMI answers nothing at all; callers
    /// check <see cref="DeviceIdentity.IsEmpty"/> rather than null.
    /// </summary>
    public static DeviceIdentity Read()
    {
        string manufacturer = "", model = "", family = "", board = "", biosVersion = "", biosVendor = "";
        string cpuManufacturer = "", cpuName = "";

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
            {
                using (o)
                {
                    manufacturer = ReadString(o, "Manufacturer");
                    model = ReadString(o, "Model");
                    family = ReadString(o, "SystemFamily");
                    break;
                }
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard");
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
            {
                using (o) board = ReadString(o, "Product");
                break;
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS");
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
            {
                using (o)
                {
                    biosVendor = ReadString(o, "Manufacturer");
                    biosVersion = ReadString(o, "SMBIOSBIOSVersion");
                    break;
                }
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Name FROM Win32_Processor");
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
            {
                using (o)
                {
                    cpuManufacturer = ReadString(o, "Manufacturer");
                    cpuName = ReadString(o, "Name");
                    break;
                }
            }
        }
        catch { }

        var modelCode = DeviceIdentity.ExtractModelCode(model);
        // The firmware's own SystemFamily wins; the code table only fills a gap, because it can fall behind new
        // models while the firmware string is always current for the machine it ships on.
        if (string.IsNullOrWhiteSpace(family)) family = DeviceIdentity.DeriveFamily(modelCode);

        return new DeviceIdentity
        {
            Manufacturer = manufacturer,
            Model = model,
            ModelCode = modelCode,
            ModelFamily = family,
            BoardProduct = board,
            BiosVersion = DeviceIdentity.NormalizeBiosVersion(biosVersion),
            BiosVendor = biosVendor,
            CpuVendor = DeviceIdentity.ParseCpuVendor(cpuManufacturer),
            CpuName = cpuName,
            IsAcer = DeviceIdentity.IsAcerManufacturer(manufacturer)
        };
    }

    /// <summary>
    /// Reads one property, tolerating a class that does not carry it. Older Windows builds omit SystemFamily, and
    /// indexing a missing property on a ManagementObject throws rather than returning null.
    /// </summary>
    private static string ReadString(ManagementObject o, string property)
    {
        try
        {
            var value = o[property];
            return value?.ToString()?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
