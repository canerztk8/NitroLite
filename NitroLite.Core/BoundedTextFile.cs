using System.IO;

namespace NitroLite.Core;

/// <summary>
/// Reads a text file the app did not necessarily write itself, refusing one that is too large to be what it claims
/// to be.
/// </summary>
/// <remarks>
/// Every settings, preset, history and backup file used to be read with <see cref="File.ReadAllText(string)"/>, which
/// allocates whatever the file happens to be. A file that grew to gigabytes — a runaway log written under the wrong
/// name, a corrupted copy, or a file something else replaced in the data folder — became an
/// <see cref="OutOfMemoryException"/> on the startup path, and the app could not start again until the file was
/// deleted by hand. The cap is generous: the real settings file is a few kilobytes.
/// </remarks>
public static class BoundedTextFile
{
    /// <summary>Largest file the app will read into memory.</summary>
    public const long MaxBytes = 10L * 1024 * 1024;

    /// <summary>
    /// Reads <paramref name="path"/>. Throws <see cref="InvalidDataException"/> when the file is larger than
    /// <see cref="MaxBytes"/>, so a caller that already catches exceptions treats it like any other unreadable file.
    /// </summary>
    public static string ReadAllText(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxBytes)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} is {length} bytes, over the {MaxBytes} byte limit.");
        }

        return File.ReadAllText(path);
    }

    /// <summary>Reads <paramref name="path"/>, or returns null when it is missing, unreadable or too large.</summary>
    public static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
