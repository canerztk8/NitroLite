using System.Buffers;
using System.IO;
using System.Text;

namespace NitroLite.Linux.Sysfs;

/// <summary>
/// The real <c>/sys</c> tree.
/// </summary>
/// <remarks>
/// Every method swallows its own failure. sysfs is not a normal file system: a read can fail with
/// <c>EIO</c> because the embedded controller did not answer, a write can fail with <c>EINVAL</c> because the
/// firmware rejected the value, and a directory can vanish because the module was unloaded. None of those are
/// reasons to take the application down, and all of them are reasons to report a status instead of throwing.
/// </remarks>
public sealed class RealSysfsFileSystem : ISysfsFileSystem
{
    public static RealSysfsFileSystem Instance { get; } = new();

    public bool FileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    public bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    /// <summary>
    /// The most a sysfs attribute may yield. Every real one is a handful of bytes; the cap exists because the path is
    /// not always a sysfs node — a caller that resolved one from a config file could point at anything, and
    /// <see cref="File.ReadAllText(string)"/> would read all of it into memory on the telemetry thread.
    /// </summary>
    private const int MaxAttributeBytes = 64 * 1024;

    /// <summary>
    /// What a read starts with. A real attribute is a few bytes, so this is the only buffer the telemetry thread
    /// ever touches; the <see cref="MaxAttributeBytes"/> cap is only paid when the path turns out not to be sysfs.
    /// </summary>
    private const int InitialAttributeBytes = 256;

    public string? ReadAllText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            // sysfs attributes are tiny and always end with a newline; trimming keeps every caller from having to.
            //
            // bufferSize: 1 is deliberate. The default 4096-byte buffer is allocated per stream, and a sysfs attribute
            // is read in a single call into the pooled buffer below, so that 4 KB would be rented and thrown away nine
            // times per telemetry tick. One byte means the stream keeps no buffer of its own and every Read goes
            // straight to the file descriptor, which is exactly one syscall for the shape of file this reads.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1, FileOptions.None);

            var buffer = ArrayPool<byte>.Shared.Rent(InitialAttributeBytes);
            try
            {
                int total = ReadInto(stream, buffer);
                if (total < buffer.Length || buffer.Length >= MaxAttributeBytes)
                    return Encoding.UTF8.GetString(buffer, 0, total).Trim();

                // Filled the small buffer: not a sysfs attribute. Start over with the full cap.
                stream.Seek(0, SeekOrigin.Begin);
                var large = ArrayPool<byte>.Shared.Rent(MaxAttributeBytes);
                try
                {
                    return Encoding.UTF8.GetString(large, 0, ReadInto(stream, large)).Trim();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(large);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    private static int ReadInto(Stream stream, byte[] buffer)
    {
        int total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += read;

        return total;
    }

    public SysfsWriteResult WriteAllText(string path, string value)
    {
        try
        {
            if (!File.Exists(path)) return SysfsWriteResult.Missing(path);
            File.WriteAllText(path, value);
            return SysfsWriteResult.Success;
        }
        catch (UnauthorizedAccessException)
        {
            // The normal state without packaging/linux/99-nitrolite.rules installed.
            return SysfsWriteResult.Denied(path);
        }
        catch (DirectoryNotFoundException)
        {
            return SysfsWriteResult.Missing(path);
        }
        catch (FileNotFoundException)
        {
            return SysfsWriteResult.Missing(path);
        }
        catch (IOException ex)
        {
            // EINVAL / EIO from the firmware: the node exists and refused the value.
            return SysfsWriteResult.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            return SysfsWriteResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the attribute for writing and closes it again. sysfs applies a value on <c>write()</c>, not on
    /// <c>open()</c>, so this reports the permission without touching the hardware.
    /// </summary>
    public bool CanWrite(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<string> EnumerateDirectories(string path)
    {        try
        {
            if (!Directory.Exists(path)) return Array.Empty<string>();
            var entries = Directory.GetDirectories(path);
            Array.Sort(entries, StringComparer.Ordinal);
            return entries;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> EnumerateFiles(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return Array.Empty<string>();

            // The names are what the caller wants, and the LINQ form this replaces built three enumerators to get
            // them. The telemetry loop calls this once per hwmon device per tick, so the loop is worth writing out.
            var entries = Directory.GetFiles(path);
            var names = new string[entries.Length];
            for (var i = 0; i < entries.Length; i++) names[i] = Path.GetFileName(entries[i]);
            Array.Sort(names, StringComparer.Ordinal);
            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
