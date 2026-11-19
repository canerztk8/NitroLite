using System.IO;

namespace NitroLite.Core;

/// <summary>
/// A file that exists only while NitroLite is running. Finding it at startup means the previous run never reached
/// a clean exit — a crash, a kill from Task Manager, or a power cut — so the hardware may still be in whatever
/// state that run left it in.
/// <para>
/// The fan mode is the one that matters. A custom speed written to the firmware outlives the process: a run that
/// died while the curve was on leaves the fans pinned at the last speed it asked for, and nothing in the next
/// process would know to take them back. The next start reads this marker and hands the fans to the firmware
/// before anything else touches them.
/// </para>
/// <para>
/// The marker is written after the single-instance mutex is held, so a second launch that exits immediately never
/// creates one, and a clean exit deletes it. A read-only data folder makes both calls no-ops rather than failures:
/// losing the marker costs one unnecessary fan reset, not the app.
/// </para>
/// </summary>
public static class CleanExitMarker
{
    public const string FileName = "running.marker";

    public static string FilePath => AppPaths.GetDataFile(FileName);

    /// <summary>True when a previous run left the marker behind, i.e. it did not exit cleanly.</summary>
    public static bool PreviousRunWasUnclean()
    {
        try
        {
            return File.Exists(FilePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Records that this run is alive. Best effort: a read-only data folder must not stop the app.</summary>
    public static void MarkRunning()
    {
        try
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Work marker could not be written", ex.Message);
        }
    }

    /// <summary>Removes the marker on a clean exit, so the next start does not think this run crashed.</summary>
    public static void MarkCleanExit()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Work marker could not be deleted", ex.Message);
        }
    }
}
