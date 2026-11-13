using System;
using System.IO;

namespace NitroLite.Core;

/// <summary>
/// Minimal append-only log shared by the core services. Every call is best-effort: logging must never throw,
/// because it is also used from crash and shutdown paths.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _logFile;
    private const long MaxLogBytes = 1024 * 1024;

    private static string LogFile
    {
        get
        {
            if (_logFile != null) return _logFile;
            try
            {
                _logFile = Path.Combine(AppPaths.DataDirectory, "nitrolite.log");
            }
            catch
            {
                // A broken data directory must not turn a log call into a second exception.
                _logFile = Path.Combine(Path.GetTempPath(), "nitrolite.log");
            }
            return _logFile;
        }
    }

    public static void Info(string title, string? details = null) => Write("INFO", title, details);
    public static void Warn(string title, string? details = null) => Write("WARN", title, details);
    public static void Error(string title, string? details = null) => Write("ERROR", title, details);

    private static void Write(string level, string title, string? details)
    {
        try
        {
            lock (Gate)
            {
                var path = LogFile;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {title}:\n{details}\n\n");
            }
        }
        catch { }
    }
}
