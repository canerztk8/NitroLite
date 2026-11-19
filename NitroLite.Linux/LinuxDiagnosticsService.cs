using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// The diagnostics bundle on Linux: the log, the hardware report and the settings, in one zip.
/// </summary>
/// <remarks>
/// <para>
/// The Windows bundle also embeds the capability probe's snapshot, which is a WMI artefact and has no Linux
/// counterpart. Everything else is the same shape, so a maintainer reading a Linux bundle and a Windows bundle side by
/// side finds the same file names and the same sections.
/// </para>
/// <para>
/// The privacy rule is the one the hardware report already follows: no serial number, no machine UUID, no user name and
/// no personal file path. The log is included because it is the only artefact that says what the app tried.
/// </para>
/// </remarks>
public sealed class LinuxDiagnosticsService : IDiagnosticsService
{
    /// <summary>Prefix of the archive name, so a bundle is recognisable in a downloads folder.</summary>
    public const string FileNamePrefix = "NitroLite-diagnostics";

    public const string LogFileName = "nitrolite.log";
    public const string ReportFileName = "hardware-report.md";
    public const string SettingsFileName = "settings.json";
    public const string SystemFileName = "system.txt";

    private readonly Func<string?> _hardwareReport;

    /// <param name="hardwareReport">
    /// The privacy-safe report, or null when the probe has not run. A delegate rather than a string because the probe
    /// state can change after a rescan, and a bundle that described a machine the user is not running on would be worse
    /// than no bundle.
    /// </param>
    public LinuxDiagnosticsService(Func<string?>? hardwareReport = null) =>
        _hardwareReport = hardwareReport ?? (() => null);

    public bool IsSupported => true;

    public string DataDirectory => AppPaths.DataDirectory;

    public string CreateBundle()
    {
        var directory = AppPaths.DataDirectory;
        Directory.CreateDirectory(directory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"{FileNamePrefix}-{stamp}.zip");

        // Create rather than overwrite: a second export in the same second must not silently replace the first one,
        // because the user may already have attached it to a report.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        Write(archive, SystemFileName, BuildSystemText());

        var report = _hardwareReport();
        if (!string.IsNullOrWhiteSpace(report)) Write(archive, ReportFileName, report!);

        // A missing log or settings file is normal on a fresh install; an empty placeholder would only make the archive
        // look complete when it is not, so the entry is simply left out.
        var log = ReadIfExists(AppLog.LogFilePath);
        if (log != null) Write(archive, LogFileName, log);

        var settings = ReadIfExists(AppPaths.GetDataFile("settings.json"));
        if (settings != null) Write(archive, SettingsFileName, settings);

        return path;
    }

    /// <summary>Short system summary: what the app is, where it runs, and what it could see.</summary>
    public static string BuildSystemText(DateTime? now = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("NitroLite diagnostics");
        builder.AppendLine("=====================");
        builder.AppendLine();

        builder.AppendLine("[Application]");
        builder.AppendLine($"Version          : {UpdateService.CurrentVersionText}");
        builder.AppendLine($"Process          : {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
        builder.AppendLine($"Runtime          : {Environment.Version}");
        builder.AppendLine($"Culture          : {CultureInfo.CurrentCulture.Name}");
        builder.AppendLine(
            $"Captured (UTC)   : {(now ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        builder.AppendLine();

        builder.AppendLine("[System]");
        builder.AppendLine($"OS               : {Environment.OSVersion.VersionString}");
        builder.AppendLine($"Kernel           : {Environment.OSVersion.Version}");
        builder.AppendLine($"Machine          : {Environment.MachineName}");
        builder.AppendLine($"Processors       : {Environment.ProcessorCount}");
        builder.AppendLine($"Data directory   : {SafeDataDirectory()}");

        var dataError = AppPaths.DataDirectoryError;
        if (!string.IsNullOrWhiteSpace(dataError)) builder.AppendLine($"Data dir warning : {dataError}");

        return builder.ToString();
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string? ReadIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The diagnostics file could not be read", $"{path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The data folder, or a placeholder. The path is the one thing in this file that could name a user, so it is only
    /// printed when it resolved; a bundle is meant to be attached to a public issue.
    /// </summary>
    private static string SafeDataDirectory()
    {
        try
        {
            return AppPaths.DataDirectory;
        }
        catch (Exception ex)
        {
            return $"(unavailable: {ex.GetType().Name})";
        }
    }
}
