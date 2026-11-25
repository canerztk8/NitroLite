using System;
using System.IO;
using System.Linq;
using Xunit;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Tests;

/// <summary>
/// Issue #13: on the Nitro V 16 ANV16-I31 the ENE KB5130 sits on I2C-HID. Its report descriptor stats as 4096 bytes
/// and holds 203, which <c>File.ReadAllBytes</c> refuses, and the udev rule matched USB attributes the I2C parent chain
/// does not have.
/// </summary>
public class Issue13I2cHidKeyboardTests
{
    // The head of the descriptor from the report: vendor page 0xFF5A, report id 0xA1, then report id 0xA4.
    private static readonly byte[] I2cVendorDescriptor =
    [
        0x06, 0x5A, 0xFF,       // Usage Page (vendor 0xFF5A)
        0x09, 0x01,             // Usage 1
        0xA1, 0x01,             // Collection (Application)
        0x85, 0xA1,             //   Report ID 0xA1
        0x09, 0x10,
        0xA1, 0x02,
        0x09, 0x11,
        0x15, 0x00,
        0x26, 0xFF, 0x00,
        0x75, 0x08,
        0x95, 0x01,
        0xB1, 0x03,
        0xC0,
        0x85, 0xA4,             //   Report ID 0xA4
        0x09, 0x40,
        0xA1, 0x02,
        0xC0,
        0xC0
    ];

    [Fact]
    public void AnI2cHidController_IsFoundByVendorProductAndDescriptor()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithDirectory("/sys/class/hidraw/hidraw8");
        fs.WithFile("/sys/class/hidraw/hidraw8/device/uevent",
            "DRIVER=hid-generic\nHID_ID=0018:00000CF2:00005130\nHID_NAME=ENEK5130:00 0CF2:5130\n" +
            "HID_PHYS=i2c-ENEK5130:00\nMODALIAS=hid:b0018g0001v00000CF2p00005130\n");

        Assert.Equal("/dev/hidraw8", LinuxHidrawLocator.FindEneKb5130(fs, _ => I2cVendorDescriptor));
        Assert.True(LinuxHidrawLocator.IsVendorLightingInterface(I2cVendorDescriptor));
    }

    [Fact]
    public void ABinaryAttributeIsReadToItsRealEnd_NotToTheLengthItAdvertises()
    {
        // What sysfs does: a page for the length, 203 bytes in the file.
        var content = new byte[203];
        I2cVendorDescriptor.CopyTo(content, 0);
        using var stream = new LyingLengthStream(content, advertisedLength: 4096);

        var read = SysfsBinary.ReadToEnd(stream);

        Assert.NotNull(read);
        Assert.Equal(203, read!.Length);
        Assert.Equal(content, read);
        Assert.True(LinuxHidrawLocator.IsVendorLightingInterface(read));
    }

    [Fact]
    public void TheDescriptorReaders_UseTheReadToEndReader_NotFileReadAllBytes()
    {
        // The locator and the capture writer each fall back to a default reader; File.ReadAllBytes there is the bug.
        Func<string, byte[]?> expected = SysfsBinary.Read;

        Assert.Equal(expected.Method, LinuxHidrawLocator.DefaultReadBytes.Method);
        Assert.Equal(expected.Method, LinuxCaptureWriter.DefaultReadBytes.Method);
    }

    [Fact]
    public void ABinaryAttributeLargerThanTheCap_IsRefused()
    {
        using var stream = new MemoryStream(new byte[SysfsBinary.MaxBytes + 1]);

        Assert.Null(SysfsBinary.ReadToEnd(stream));
    }

    [Fact]
    public void AMissingAttribute_ReadsAsNull()
    {
        Assert.Null(SysfsBinary.Read(Path.Combine(Path.GetTempPath(), "nitrolite-absent-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void TheUdevRule_MatchesTheControllerOnUsbAndOnI2c_AndAppliesTheAclAtOnce()
    {
        var rule = File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "linux", "99-nitrolite.rules"));

        // udev does not strip a byte-order mark: with one, the first comment line is parsed as a rule and logged.
        var head = File.ReadAllBytes(Path.Combine(RepoRoot(), "packaging", "linux", "99-nitrolite.rules")).AsSpan(0, 3);
        Assert.False(head.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        var hidraw = rule.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#') && l.Contains("SUBSYSTEM==\"hidraw\"", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, hidraw.Count);
        Assert.Contains(hidraw, l => l.Contains("ATTRS{idVendor}==\"0cf2\"") && l.Contains("ATTRS{idProduct}==\"5130\""));

        // The I2C parent chain has no idVendor/idProduct; the HID device's kernel name is 0018:0CF2:5130.<instance>.
        Assert.Contains(hidraw, l => l.Contains("KERNELS==\"0018:0CF2:5130.*\""));

        // Tag for logind, builtin for the event itself; and nothing wider than this one controller.
        Assert.All(hidraw, l =>
        {
            Assert.Contains("TAG+=\"uaccess\"", l);
            Assert.Contains("RUN{builtin}+=\"uaccess\"", l);
            Assert.StartsWith("ACTION!=\"remove\"", l);
            Assert.DoesNotContain("MODE=", l);
            Assert.DoesNotContain("GROUP=", l);
        });
    }

    [Fact]
    public void TheUdevRuleAndTheGrantScript_KeepSysfsNodesReadable()
    {
        // 0660 took the read bit from everyone outside the group: a user who writes through the helper could then not
        // read the operating mode back, and the control disabled itself after a few "failed" writes.
        var rule = File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "linux", "99-nitrolite.rules"));
        var active = string.Join("\n", rule.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

        Assert.DoesNotContain("chmod 0660", active);
        Assert.Contains("chmod 0664 /sys/firmware/acpi/platform_profile", active);
        Assert.DoesNotContain("chmod 0660", LinuxPlatformSetup.GrantScript);
        Assert.Contains("chmod 0664 /sys/firmware/acpi/platform_profile", LinuxPlatformSetup.GrantScript);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("NitroLite.sln was not found above the test folder.");
    }

    /// <summary>A seekable stream that advertises more than it holds, as a sysfs binary attribute does.</summary>
    private sealed class LyingLengthStream(byte[] content, long advertisedLength) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => advertisedLength;
        public override long Position { get => _position; set => _position = (int)value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, content.Length - _position);
            if (n <= 0) return 0;
            Array.Copy(content, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
