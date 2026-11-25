namespace NitroLite.Linux.Sysfs;

/// <summary>
/// Reads a binary sysfs attribute (a HID report descriptor, for one) to its real end.
/// </summary>
/// <remarks>
/// sysfs reports a page (4096) as the size of a binary attribute whatever it holds: an I2C-HID report descriptor of
/// 203 bytes stats as 4096. <see cref="File.ReadAllBytes(string)"/> trusts a non-zero length and throws when the
/// stream ends before it, so the attribute has to be read until the kernel says there is no more, never to the
/// length it advertised.
/// </remarks>
public static class SysfsBinary
{
    /// <summary>The most an attribute may yield; a report descriptor is a few hundred bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The attribute's bytes, or null when it is absent, unreadable or larger than the cap.</summary>
    public static byte[]? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1, FileOptions.None);
            return ReadToEnd(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Everything the stream yields before it reports the end. <see cref="Stream.Length"/> is never consulted. Null
    /// when the stream holds more than <see cref="MaxBytes"/>.
    /// </summary>
    public static byte[]? ReadToEnd(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var collected = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (collected.Length + read > MaxBytes) return null;
            collected.Write(buffer, 0, read);
        }

        return collected.ToArray();
    }
}
