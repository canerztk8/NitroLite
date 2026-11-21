using System.Globalization;
using System.Runtime.InteropServices;
using NitroLite.Core.Hardware.Lighting;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// Finds the ENE KB5130 keyboard controller among the <c>hidraw</c> devices, with no kernel module.
/// </summary>
/// <remarks>
/// <para>
/// The 2024-and-later Nitro V, Nitro 16S and Helios Neo boards drive the keyboard colour from a separate USB controller
/// (<c>0x0CF2:0x5130</c>) that takes an eleven-byte HID feature report. The kernel already exposes every HID interface
/// as <c>/dev/hidrawN</c>, so this path needs no out-of-tree driver at all: only write access to that one node, which
/// the udev rule grants to the seat user.
/// </para>
/// <para>
/// A controller has several interfaces and only one carries the vendor report. The right one is the interface whose
/// report descriptor opens a vendor usage page (<c>0xFF00</c> and up) and declares report id <c>0xA4</c>; the keyboard's
/// own input interface is never opened, which is the same rule the Windows build follows.
/// </para>
/// </remarks>
public static class LinuxHidrawLocator
{
    public const string HidrawClassRoot = "/sys/class/hidraw";

    public const int EneVendorId = 0x0CF2;
    public const int EneKb5130ProductId = 0x5130;

    /// <summary>The <c>/dev/hidrawN</c> node of the KB5130 vendor interface, or null when there is none.</summary>
    public static string? FindEneKb5130(ISysfsFileSystem fs, Func<string, byte[]?>? readBytes = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        readBytes ??= ReadBytes;

        foreach (var directory in fs.EnumerateDirectories(HidrawClassRoot))
        {
            var uevent = fs.ReadAllText(LinuxPaths.Combine(directory, "device/uevent"));
            if (!TryParseHidId(uevent, out var vendor, out var product)) continue;
            if (vendor != EneVendorId || product != EneKb5130ProductId) continue;

            var descriptor = readBytes(LinuxPaths.Combine(directory, "device/report_descriptor"));
            if (descriptor == null || !IsVendorLightingInterface(descriptor)) continue;

            return "/dev/" + Path.GetFileName(directory);
        }

        return null;
    }

    /// <summary>
    /// <c>HID_ID=0003:00000CF2:00005130</c> out of a <c>uevent</c> file. Bus, vendor and product are hexadecimal.
    /// </summary>
    public static bool TryParseHidId(string? uevent, out int vendor, out int product)
    {
        vendor = 0;
        product = 0;
        if (string.IsNullOrWhiteSpace(uevent)) return false;

        foreach (var raw in uevent.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("HID_ID=", StringComparison.Ordinal)) continue;

            var parts = line["HID_ID=".Length..].Split(':');
            if (parts.Length != 3) return false;

            return int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vendor) &&
                   int.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out product);
        }

        return false;
    }

    /// <summary>
    /// True when the report descriptor opens a vendor usage page and declares the lighting report id. Walks the
    /// descriptor item by item rather than searching for byte pairs, so a data byte that happens to equal a tag cannot
    /// match.
    /// </summary>
    public static bool IsVendorLightingInterface(byte[] descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var vendorPage = false;
        var reportId = false;

        for (int i = 0; i < descriptor.Length;)
        {
            var prefix = descriptor[i];

            // A long item: 0xFE, size, tag, data. Not used by these controllers, skipped whole.
            if (prefix == 0xFE)
            {
                if (i + 1 >= descriptor.Length) break;
                i += 3 + descriptor[i + 1];
                continue;
            }

            var size = (prefix & 0x03) == 3 ? 4 : prefix & 0x03;
            if (i + 1 + size > descriptor.Length) break;

            uint value = 0;
            for (int b = 0; b < size; b++) value |= (uint)descriptor[i + 1 + b] << (8 * b);

            switch (prefix & 0xFC)
            {
                case 0x04: // Usage Page (global)
                    if (value >= 0xFF00 && value <= 0xFFFF) vendorPage = true;
                    break;
                case 0x84: // Report ID (global)
                    if (value == EneKb5130Protocol.ReportId) reportId = true;
                    break;
            }

            i += 1 + size;
        }

        return vendorPage && reportId;
    }

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Sends KB5130 frames through <c>HIDIOCSFEATURE</c> on a <c>/dev/hidrawN</c> node: the Linux twin of the Windows
/// <c>HidD_SetFeature</c> writer, building its bytes with the same <see cref="EneKb5130Protocol"/>.
/// </summary>
public sealed class LinuxHidrawKeyboardWriter : IHidLightingWriter
{
    private const int O_RDWR = 2;
    private const int O_CLOEXEC = 0x80000;

    private readonly string _node;
    private bool _disposed;

    public LinuxHidrawKeyboardWriter(string node)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
    }

    public string Description => $"ENE KB5130 0x0CF2:0x5130 ({_node})";

    public string Protocol => "ene-kb5130";

    public bool CanWrite => !_disposed;

    public string? Refusal => _disposed ? "the controller was closed" : null;

    /// <summary>The ioctl request for a feature report of <paramref name="length"/> bytes: <c>_IOC(RW, 'H', 6, len)</c>.</summary>
    public static ulong FeatureRequest(int length) => 0xC0000000UL | ((ulong)(uint)length << 16) | 0x4806UL;

    public bool TryWrite(KeyboardLightingFrame frame, out string? error)
    {
        error = null;
        if (_disposed)
        {
            error = Refusal;
            return false;
        }

        if (!EneKb5130Protocol.TryBuild(frame, out var reports, out error)) return false;

        // Opened per write rather than held: the node is recreated on resume and on a USB reset, and a held
        // descriptor to the old one writes into nothing.
        int fd;
        try
        {
            fd = open(_node, O_RDWR | O_CLOEXEC);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            error = "libc is not available";
            return false;
        }

        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            error = errno == 13 ? "permission denied (the udev rule is not installed)" : $"open failed (errno {errno})";
            return false;
        }

        try
        {
            foreach (var report in reports)
            {
                if (ioctl(fd, FeatureRequest(report.Length), report) < 0)
                {
                    error = $"HIDIOCSFEATURE failed (errno {Marshal.GetLastPInvokeError()})";
                    return false;
                }
            }

            return true;
        }
        finally
        {
            _ = close(fd);
        }
    }

    public void Dispose() => _disposed = true;

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, byte[] data);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
