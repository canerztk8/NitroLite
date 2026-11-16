using System.IO;

namespace NitroLite.Core;

/// <summary>
/// Central location for NitroLite's writable data files and for the system tools it launches.
/// </summary>
/// <remarks>
/// Data used to live next to the executable (AppDomain.BaseDirectory). That breaks when the app is installed
/// under Program Files (not writable) and lets anyone who can write to the exe folder tamper with state that
/// is later acted on. Data now lives in %LOCALAPPDATA%\NitroLite; existing files are copied over once
/// (the originals are left in place so an older build can still read them).
/// Set NITROLITE_DATA_DIR to redirect all data (used by the test suite so it never touches real user data).
/// </remarks>
public static class AppPaths
{
    public const string DataDirOverrideVariable = "NITROLITE_DATA_DIR";

    private static readonly object MigrationLock = new();
    private static string? _dataDirectory;

    /// <summary>
    /// Why <see cref="DataDirectory"/> could not be created, or null when it is usable. The app keeps running
    /// (reads fall back to defaults) but the user has to be told: otherwise every setting silently fails to save
    /// and the next launch starts from defaults again with no explanation.
    /// </summary>
    public static string? DataDirectoryError { get; private set; }

    public static string DataDirectory
    {
        get
        {
            var cached = _dataDirectory;
            if (cached != null) return cached;
            lock (MigrationLock)
            {
                if (_dataDirectory == null)
                {
                    _dataDirectory = ResolveDataDirectory();
                    // Logged only after the field is assigned: AppLog resolves its own file through this property,
                    // so logging from inside ResolveDataDirectory would re-enter it while the field is still null.
                    if (DataDirectoryError != null)
                    {
                        AppLog.Error("Veri klasörü oluşturulamadı", DataDirectoryError);
                    }
                }
                return _dataDirectory;
            }
        }
    }

    public static string LegacyDirectory => AppDomain.CurrentDomain.BaseDirectory;

    private static bool IsOverridden =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DataDirOverrideVariable));

    private static string ResolveDataDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable(DataDirOverrideVariable);
        string dir = !string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NitroLite");
        try
        {
            Directory.CreateDirectory(dir);
            DataDirectoryError = null;
        }
        catch (Exception ex)
        {
            // Swallowing this used to leave the app writing into a directory that does not exist, so every save
            // failed with a different, unrelated-looking error. The reason is kept here instead.
            DataDirectoryError = $"{dir}: {ex.Message}";
        }
        return dir;
    }

    /// <summary>
    /// Returns the full path of a data file in <see cref="DataDirectory"/>, copying a legacy copy
    /// (and its ".bak") from the executable folder the first time if the new location has none.
    /// </summary>
    public static string GetDataFile(string fileName)
    {
        var target = Path.Combine(DataDirectory, fileName);
        if (!IsOverridden)
        {
            MigrateLegacyFile(fileName);
            MigrateLegacyFile(fileName + ".bak");
        }
        return target;
    }

    private static void MigrateLegacyFile(string fileName)
    {
        try
        {
            var target = Path.Combine(DataDirectory, fileName);
            var legacy = Path.Combine(LegacyDirectory, fileName);
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(legacy), StringComparison.OrdinalIgnoreCase)) return;

            lock (MigrationLock)
            {
                if (!File.Exists(target) && File.Exists(legacy))
                {
                    File.Copy(legacy, target, overwrite: false);
                }
            }
        }
        catch
        {
            // Migration is best effort; the app falls back to defaults if the copy fails.
        }
    }

    /// <summary>A temp file name that cannot be guessed in advance (for files written by elevated processes).</summary>
    public static string UniqueTempFile(string prefix, string extension) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}{extension}");
}
