using System.Runtime.InteropServices;
using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>One <c>struct input_event</c>, reduced to the three fields the listener reads.</summary>
/// <remarks>
/// The kernel's struct is <c>timeval</c> + <c>type</c> + <c>code</c> + <c>value</c>, and the timeval is two machine
/// words: 16 bytes on a 64-bit build and 8 on a 32-bit one. That is the whole reason the size is computed rather than
/// hard-coded — a fixed 24 would read every event on a 32-bit install at the wrong offset and report nonsense.
/// </remarks>
public readonly record struct EvdevEvent(ushort Type, ushort Code, int Value)
{
    /// <summary><c>EV_KEY</c>: a key or button changed state.</summary>
    public const ushort TypeKey = 0x01;

    /// <summary><c>KEY_PROG1</c>. What <c>acer-wmi</c> reports for the Nitro key on most models.</summary>
    public const int KeyProg1 = 148;

    /// <summary><c>KEY_PROG3</c>. The other code the same driver uses.</summary>
    public const int KeyProg3 = 202;

    /// <summary>The value a press carries; 0 is the release and 2 is an autorepeat.</summary>
    public const int ValuePress = 1;

    /// <summary>True when this event is the Nitro key going down.</summary>
    public bool IsNitroKeyPress =>
        Type == TypeKey && Value == ValuePress && (Code == KeyProg1 || Code == KeyProg3);

    /// <summary>Reads one event out of <paramref name="buffer"/>, or returns false when it is too short.</summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, int eventSize, out EvdevEvent value)
    {
        value = default;
        if (eventSize < 8 || buffer.Length < eventSize) return false;

        // The payload sits at the end of the struct on both word sizes, so the offset is derived rather than assumed.
        int offset = eventSize - 8;
        value = new EvdevEvent(
            BitConverter.ToUInt16(buffer[offset..]),
            BitConverter.ToUInt16(buffer[(offset + 2)..]),
            BitConverter.ToInt32(buffer[(offset + 4)..]));
        return true;
    }
}

/// <summary>One open <c>/dev/input/event*</c> device.</summary>
/// <remarks>
/// An interface rather than a <see cref="FileStream"/> so the listener can be driven from a test: a real evdev device
/// needs a kernel, a driver and a user in the <c>input</c> group, and a test that needed all three would never run.
/// </remarks>
public interface IEvdevDevice : IDisposable
{
    /// <summary>The name the driver reports through <c>EVIOCGNAME</c>, e.g. <c>Acer WMI hotkeys</c>.</summary>
    string Name { get; }

    /// <summary><c>sizeof(struct input_event)</c> for this process.</summary>
    int EventSize { get; }

    /// <summary>True when the device advertises <paramref name="code"/> in its <c>EV_KEY</c> bitmap.</summary>
    bool SupportsKey(int code);

    /// <summary>Reads raw bytes, blocking until at least one event is available. Zero means the device closed.</summary>
    int Read(Span<byte> buffer);
}

/// <summary>Finds and opens the input devices on this machine.</summary>
public interface IEvdevSource
{
    /// <summary>
    /// Every device this process may read. A device that refuses the open is skipped rather than reported: on a stock
    /// install that is all of them, and it is not an error.
    /// </summary>
    IReadOnlyList<IEvdevDevice> OpenAll();
}

/// <summary>
/// The real <c>/dev/input</c> tree, read through libc.
/// </summary>
/// <remarks>
/// <para>
/// <c>ioctl</c> is the only way to ask a device its name and its capabilities: <c>EVIOCGNAME</c> and
/// <c>EVIOCGBIT</c> are not files, so there is nothing to read. The file descriptor comes from the
/// <see cref="FileStream"/> the device is opened with, which keeps the read path on the ordinary .NET API.
/// </para>
/// <para>
/// Nothing here throws out of <see cref="OpenAll"/>: a missing <c>/dev/input</c>, a device that vanishes between the
/// listing and the open, and a permission refusal all end the same way, with a shorter list.
/// </para>
/// </remarks>
public sealed class RealEvdevSource : IEvdevSource
{
    /// <summary>Where the kernel puts one character device per input device.</summary>
    public const string DeviceRoot = "/dev/input";

    /// <summary>The name prefix of the character devices, as opposed to the legacy <c>event</c>-less nodes.</summary>
    public const string DevicePrefix = "event";

    /// <summary><c>_IOC_READ</c>: the direction bits say the kernel writes the answer back to the caller.</summary>
    internal const uint IocRead = 2;

    /// <summary>The <c>type</c> byte every evdev ioctl carries: <c>'E'</c>.</summary>
    internal const byte EvdevMagic = (byte)'E';

    /// <summary>
    /// The <c>_IOC</c> encoding the kernel expects: direction at bit 30, size at 16, type at 8, number at 0.
    /// </summary>
    /// <remarks>
    /// The type and the number used to be the other way round, so every request named a different ioctl —
    /// <c>EVIOCGNAME(256)</c> came out as <c>0x81000645</c> instead of <c>0x81004506</c> — and the kernel answered
    /// <c>ENOTTY</c>. The device name was always empty and no key was ever advertised, which is why the Nitro key
    /// never worked on Linux. It lives here rather than inside the device so the encoding can be asserted against
    /// the kernel's own numbers without a device to open.
    /// </remarks>
    internal static nuint IoctlRequest(uint number, int size) =>
        (nuint)((IocRead << 30) | (((uint)size & 0x3FFF) << 16) | ((uint)EvdevMagic << 8) | (number & 0xFF));

    public IReadOnlyList<IEvdevDevice> OpenAll()
    {
        var devices = new List<IEvdevDevice>();

        string[] paths;
        try
        {
            if (!Directory.Exists(DeviceRoot)) return devices;
            paths = Directory.GetFiles(DeviceRoot, DevicePrefix + "*");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Giriş aygıtları listelenemedi", ex.Message);
            return devices;
        }

        Array.Sort(paths, StringComparer.Ordinal);

        foreach (var path in paths)
        {
            try
            {
                devices.Add(RealEvdevDevice.Open(path));
            }
            catch (UnauthorizedAccessException)
            {
                // The normal answer for a user who is not in the input group. The listener reports the hint once.
            }
            catch (Exception ex)
            {
                AppLog.Info("Giriş aygıtı açılamadı", $"{path}: {ex.Message}");
            }
        }

        return devices;
    }

    private sealed class RealEvdevDevice : IEvdevDevice
    {
        private const uint EvIocGName = 0x06;
        private const uint EvIocGBit = 0x20;
        private const ushort EvKey = 0x01;
        private const int KeyMax = 0x2FF;

        private readonly FileStream _stream;
        private readonly int _fd;

        private RealEvdevDevice(FileStream stream, string name)
        {
            _stream = stream;
            _fd = (int)stream.SafeFileHandle.DangerousGetHandle();
            Name = name;
        }

        public string Name { get; }

        public int EventSize => IntPtr.Size == 8 ? 24 : 16;

        public static RealEvdevDevice Open(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.None);
            try
            {
                return new RealEvdevDevice(stream, ReadName(stream));
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static string ReadName(FileStream stream)
        {
            int fd = (int)stream.SafeFileHandle.DangerousGetHandle();
            const int length = 256;

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (Ioctl(fd, Request(EvIocGName, length), buffer) < 0) return "";

                var bytes = new byte[length];
                Marshal.Copy(buffer, bytes, 0, length);

                int end = Array.IndexOf(bytes, (byte)0);
                if (end < 0) end = bytes.Length;
                return System.Text.Encoding.UTF8.GetString(bytes, 0, end).Trim();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public bool SupportsKey(int code)
        {
            if (code < 0 || code > KeyMax) return false;

            // EV_KEY has to be advertised first: a device with no key events has no key bitmap to ask about.
            if (!HasEventType(EvKey)) return false;

            int length = (KeyMax + 1 + 7) / 8;
            var bits = new byte[length];

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (Ioctl(_fd, Request(EvIocGBit + EvKey, length), buffer) < 0) return false;
                Marshal.Copy(buffer, bits, 0, length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return (bits[code / 8] & (1 << (code % 8))) != 0;
        }

        private bool HasEventType(ushort type)
        {
            const int length = 8;
            var bits = new byte[length];

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (Ioctl(_fd, Request(EvIocGBit, length), buffer) < 0) return false;
                Marshal.Copy(buffer, bits, 0, length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return (bits[type / 8] & (1 << (type % 8))) != 0;
        }

        /// <summary><c>_IOC(_IOC_READ, 'E', nr, size)</c>, which is how every evdev ioctl number is built.</summary>
        private static nuint Request(uint nr, int size) => IoctlRequest(nr, size);

        public int Read(Span<byte> buffer) => _stream.Read(buffer);

        public void Dispose() => _stream.Dispose();

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, nuint request, IntPtr argument);
    }
}
