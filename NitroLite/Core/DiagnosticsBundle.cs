using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using NitroLite.Core.Hardware;

namespace NitroLite.Core;

/// <summary>One file inside the diagnostics archive.</summary>
public sealed record DiagnosticsEntry(string Name, string Content);

/// <summary>
/// Collects everything a bug report needs into a single zip: the log, the hardware report, the capability snapshot,
/// the settings file and a short system summary.
/// <para>
/// The point is that a user who says "it does not work" can hand over one file instead of being walked through five
/// separate exports. Nothing here is written to the data directory except the archive itself, and the settings copy is
/// the file on disk rather than the in-memory object, so what the report shows is what the app actually loaded.
/// </para>
/// </summary>
public static class DiagnosticsBundle
{
    /// <summary>Prefix of the produced archive; the timestamp is appended so two exports never collide.</summary>
    public const string FileNamePrefix = "NitroLite-diagnostics";

    /// <summary>Name of the log file inside the archive and on disk.</summary>
    public const string LogFileName = "nitrolite.log";

    /// <summary>Name of the settings file inside the archive and on disk.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// Builds the text entries of the archive. Pure: no disk access, so the contents can be asserted in a unit test
    /// without creating a zip.
    /// </summary>
    /// <param name="snapshot">Last capability probe, or null when the probe has not run yet.</param>
    /// <param name="capabilities">Live device capabilities, or null when the agent has not answered yet.</param>
    /// <param name="settingsJson">Raw settings file contents, or null when the file does not exist.</param>
    /// <param name="logText">Raw log contents, or null when the file does not exist.</param>
    /// <param name="now">Timestamp for the system summary; defaults to the current UTC time.</param>
    public static IReadOnlyList<DiagnosticsEntry> BuildEntries(
        CapabilitySnapshot? snapshot = null,
        DeviceCapabilities? capabilities = null,
        string? settingsJson = null,
        string? logText = null,
        DateTime? now = null)
    {
        var entries = new List<DiagnosticsEntry>
        {
            new("system.txt", BuildSystemText(snapshot, now)),
            new("hardware-report.txt", HardwareReport.BuildText(capabilities)),
            new("capabilities.json", BuildCapabilitiesJson(snapshot, capabilities))
        };

        // A missing log or settings file is normal on a fresh install; an empty placeholder would only make the
        // archive look complete when it is not, so the entry is simply left out.
        if (!string.IsNullOrWhiteSpace(logText)) entries.Add(new(LogFileName, logText!));
        if (!string.IsNullOrWhiteSpace(settingsJson)) entries.Add(new(SettingsFileName, settingsJson!));

        return entries;
    }

    /// <summary>
    /// Writes the archive into <paramref name="directory"/> and returns its full path. The caller is responsible for
    /// telling the user where it went; this method never opens a window or a shell.
    /// </summary>
    public static string Create(
        string directory,
        CapabilitySnapshot? snapshot = null,
        DeviceCapabilities? capabilities = null,
        string? settingsPath = null,
        string? logPath = null,
        DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A directory is required.", nameof(directory));
        Directory.CreateDirectory(directory);

        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"{FileNamePrefix}-{stamp}.zip");

        var entries = BuildEntries(
            snapshot,
            capabilities,
            ReadIfExists(settingsPath),
            ReadIfExists(logPath),
            now);

        // Create rather than CreateDirectory-overwrite: a second export in the same second must not silently replace
        // the first one, because the user may already have attached it to a report.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            var zipEntry = archive.CreateEntry(entry.Name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(zipEntry.Open(), new UTF8Encoding(false));
            writer.Write(entry.Content);
        }

        return path;
    }

    /// <summary>Short system summary: what the app is, where it runs, and what it could see.</summary>
    public static string BuildSystemText(CapabilitySnapshot? snapshot = null, DateTime? now = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("NitroLite diagnostics");
        builder.AppendLine("=====================");
        builder.AppendLine();

        builder.AppendLine("[Application]");
        builder.AppendLine($"Version          : {AppVersion()}");
        builder.AppendLine($"Process          : {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
        builder.AppendLine($"Elevated         : {(IsElevated() ? "yes" : "no")}");
        builder.AppendLine($"Runtime          : {Environment.Version}");
        builder.AppendLine($"Culture          : {CultureInfo.CurrentCulture.Name}");
        builder.AppendLine($"Captured (UTC)   : {(now ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        builder.AppendLine();

        builder.AppendLine("[System]");
        builder.AppendLine($"OS               : {HardwareReport.WindowsVersionText()}");
        builder.AppendLine($"Machine          : {Environment.MachineName}");
        builder.AppendLine($"Processors       : {Environment.ProcessorCount}");
        builder.AppendLine($"Data directory   : {SafeDataDirectory()}");
        var dataError = AppPaths.DataDirectoryError;
        if (!string.IsNullOrWhiteSpace(dataError)) builder.AppendLine($"Data dir warning : {dataError}");
        builder.AppendLine();

        builder.AppendLine("[Capabilities]");
        if (snapshot == null)
        {
            builder.AppendLine("No capability snapshot has been captured yet.");
        }
        else
        {
            builder.AppendLine($"Profile          : {snapshot.ProfileId} ({snapshot.ProfileSource})");
            builder.AppendLine($"Cache key        : {snapshot.CacheKey}");
            builder.AppendLine($"From cache       : {(snapshot.FromCache ? "yes" : "no")}");
            builder.AppendLine($"Captured (UTC)   : {snapshot.CapturedUtc:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"CPU fan ceiling  : {snapshot.MaxCpuFanRpm} rpm");
            builder.AppendLine($"GPU fan ceiling  : {snapshot.MaxGpuFanRpm} rpm");
            builder.AppendLine();
            foreach (var capability in snapshot.Capabilities)
                builder.AppendLine($"  {capability.Summary}");
            if (snapshot.Rejections.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Rejected profile records:");
                foreach (var rejection in snapshot.Rejections) builder.AppendLine($"  {rejection}");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The capability snapshot as JSON. Kept separate from <see cref="HardwareReport.BuildJson"/> because that one
    /// describes the live agent, while this one describes what the probe decided before the agent was ever asked.
    /// </summary>
    public static string BuildCapabilitiesJson(CapabilitySnapshot? snapshot, DeviceCapabilities? capabilities)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"snapshot\": " + (snapshot == null ? "null" : SnapshotJson(snapshot)) + ",");
        builder.AppendLine("  \"live\": " + (capabilities == null ? "null" : LiveJson(capabilities)));
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string SnapshotJson(CapabilitySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine($"    \"profileId\": {Json(snapshot.ProfileId)},");
        builder.AppendLine($"    \"profileSource\": {Json(snapshot.ProfileSource)},");
        builder.AppendLine($"    \"cacheKey\": {Json(snapshot.CacheKey)},");
        builder.AppendLine($"    \"fromCache\": {(snapshot.FromCache ? "true" : "false")},");
        builder.AppendLine($"    \"capturedUtc\": {Json(snapshot.CapturedUtc.ToString("o", CultureInfo.InvariantCulture))},");
        builder.AppendLine($"    \"maxCpuFanRpm\": {snapshot.MaxCpuFanRpm},");
        builder.AppendLine($"    \"maxGpuFanRpm\": {snapshot.MaxGpuFanRpm},");
        builder.AppendLine("    \"capabilities\": [");
        for (var i = 0; i < snapshot.Capabilities.Count; i++)
        {
            var capability = snapshot.Capabilities[i];
            var comma = i == snapshot.Capabilities.Count - 1 ? "" : ",";
            builder.AppendLine(
                $"      {{ \"capability\": {Json(capability.Capability)}, \"outcome\": {Json(capability.Outcome.ToString())}, " +
                $"\"detail\": {Json(capability.Detail)} }}{comma}");
        }
        builder.AppendLine("    ],");
        builder.AppendLine("    \"rejections\": [");
        for (var i = 0; i < snapshot.Rejections.Count; i++)
        {
            var comma = i == snapshot.Rejections.Count - 1 ? "" : ",";
            builder.AppendLine($"      {Json(snapshot.Rejections[i])}{comma}");
        }
        builder.AppendLine("    ]");
        builder.Append("  }");
        return builder.ToString();
    }

    private static string LiveJson(DeviceCapabilities capabilities)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine($"    \"keyboard\": {Json(capabilities.Keyboard?.ToString())},");
        builder.AppendLine($"    \"fanMode\": {Json(capabilities.FanMode?.ToString())},");
        builder.AppendLine($"    \"gpuModeCapability\": {(capabilities.GpuModeCapability?.ToString(CultureInfo.InvariantCulture) ?? "null")},");
        builder.AppendLine($"    \"lightingCapability\": {(capabilities.LightingCapability.HasValue ? (capabilities.LightingCapability.Value ? "true" : "false") : "null")},");
        builder.AppendLine("    \"operatingModes\": [");
        var modes = capabilities.OperatingModes ?? Array.Empty<OperatingMode>();
        for (var i = 0; i < modes.Count; i++)
        {
            var comma = i == modes.Count - 1 ? "" : ",";
            builder.AppendLine($"      {Json(modes[i].ToString())}{comma}");
        }
        builder.AppendLine("    ]");
        builder.Append("  }");
        return builder.ToString();
    }

    private static string Json(string? value)
    {
        if (value == null) return "null";
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }

    private static string AppVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string SafeDataDirectory()
    {
        try { return AppPaths.DataDirectory; }
        catch (Exception ex) { return $"unavailable ({ex.GetType().Name})"; }
    }

    private static string? ReadIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            // A locked or unreadable file must not abort the whole export.
            return null;
        }
    }
}
