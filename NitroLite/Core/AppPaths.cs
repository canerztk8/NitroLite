using System;
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

    public static string DataDirectory
    {
        get
        {
            var cached = _dataDirectory;
            if (cached != null) return cached;
            lock (MigrationLock)
            {
                _dataDirectory ??= ResolveDataDirectory();
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
        try { Directory.CreateDirectory(dir); } catch { }
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

    /// <summary>Full path of a tool in System32, so a same-named exe in the working directory or PATH is never run.</summary>
    public static string SystemTool(string exeName) => Path.Combine(Environment.SystemDirectory, exeName);

    public static string WindowsPowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>A temp file name that cannot be guessed in advance (for files written by elevated processes).</summary>
    public static string UniqueTempFile(string prefix, string extension) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}{extension}");
}
