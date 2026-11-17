using System.IO;
using System.Text;

namespace NitroLite.Core;

/// <summary>
/// Writes a text file so a reader never sees a half-written one: the content goes to a temporary file next to the
/// target and is then moved over it.
/// </summary>
/// <remarks>
/// <para>
/// The temporary file is removed when anything fails. Every caller used to write its own version of this and none of
/// them cleaned up, so a save that kept failing (a full disk, a locked file) left one <c>.tmp.&lt;guid&gt;</c> behind
/// per attempt, and they accumulated in the data folder with nothing to remove them.
/// </para>
/// <para>
/// The rename is atomic, but the bytes behind it are not on the platter until they are flushed. A power cut between
/// the write and the move used to leave a zero-length file where the settings had been: the file system had the
/// directory entry but not the data. <see cref="FileOptions.WriteThrough"/> bypasses the operating system's write
/// cache and the explicit <see cref="FileStream.Flush(bool)"/> pushes what is left out of the file system's own
/// buffer, so by the time the move happens the content is durable.
/// </para>
/// </remarks>
public static class AtomicFile
{
    /// <summary>
    /// The encoding every file this class writes uses: UTF-8 without a byte order mark, which is what
    /// <see cref="File.WriteAllText(string, string)"/> produced before and what the readers expect.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents)
    {
        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var stream = new FileStream(
                       tmp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                // Set before anything is written, so the content is never readable by another local user even for
                // the moment the write takes. The move below keeps the mode.
                RestrictFilePermissions(tmp);

                using (var writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 4096, leaveOpen: true))
                {
                    writer.Write(contents);
                    writer.Flush();
                }

                // After the writer has handed everything to the stream, and before the move: this is the call that
                // makes the content survive a power cut.
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
                // The original failure is the one worth reporting.
            }
            throw;
        }
    }

    /// <summary>
    /// Makes a written file owner-only on Linux. The umask leaves a new file at 0644, which is world-readable: the
    /// settings file holds the user's game library paths and the log holds their machine name.
    /// </summary>
    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // A file system without Unix permissions cannot express this.
        }
    }
}
