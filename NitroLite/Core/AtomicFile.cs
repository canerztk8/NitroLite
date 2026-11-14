using System;
using System.IO;

namespace NitroLite.Core;

/// <summary>
/// Writes a text file so a reader never sees a half-written one: the content goes to a temporary file next to the
/// target and is then moved over it.
/// </summary>
/// <remarks>
/// The temporary file is removed when anything fails. Every caller used to write its own version of this and none of
/// them cleaned up, so a save that kept failing (a full disk, a locked file) left one <c>.tmp.&lt;guid&gt;</c> behind
/// per attempt, and they accumulated in the data folder with nothing to remove them.
/// </remarks>
public static class AtomicFile
{
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

            File.WriteAllText(tmp, contents);
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
}
