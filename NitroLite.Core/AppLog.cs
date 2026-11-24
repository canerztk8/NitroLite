using System.IO;
using System.Text;

namespace NitroLite.Core;

/// <summary>
/// Minimal append-only log shared by the core services. Every call is best-effort: logging must never throw,
/// because it is also used from crash and shutdown paths.
/// </summary>
/// <remarks>
/// <para>
/// The writer is kept open for the life of the process. Every line used to be its own <see cref="File.AppendAllText(string, string)"/>
/// call, which opens the file, seeks to the end, writes and closes it again — four system calls per line, on a loop
/// that logs several lines a second. One open handle removes the open and the close; the line itself is still pushed
/// to the file as it is written.
/// </para>
/// <para>
/// The line is not held in a buffer. A log exists to explain a crash, and the lines a crash needs are the last ones
/// before it: a buffer that is only flushed when it fills is a buffer that loses exactly those. The saving that
/// matters is the handle, not the write.
/// </para>
/// <para>
/// The handle is opened with <see cref="FileShare.ReadWrite"/> so the file can be read while the app runs, and
/// <see cref="ReadAllText"/> is the reader that knows it. A plain <see cref="File.ReadAllText(string)"/> asks for
/// <see cref="FileShare.Read"/>, which refuses to open a file another handle has open for writing — the diagnostics
/// export used to come back with no log at all for that reason.
/// </para>
/// <para>
/// Every line is scrubbed of the user's profile folder and the machine name before it is written. The log is the file
/// a user attaches to a public issue, and a path names its owner.
/// </para>
/// <para>
/// The text is English at the call site, never a <c>Loc.T</c> key: a log line is read by whoever triages the report,
/// not by the user, and a title that followed the window came back from a Turkish machine as an English heading over
/// a Turkish detail. A label that is already a catalogue key — the name of the control a write failed on, the title of
/// the dialog that had no owner — goes through <see cref="Loc.English(string)"/> instead, and numbers in a line are
/// formatted with a dot so the same line reads the same in Istanbul and in Berlin.
/// </para>
/// </remarks>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _logFile;
    private const long MaxLogBytes = 1024 * 1024;

    /// <summary>
    /// Roughly how much text has been logged since the file's size was last read. The size is only checked once per
    /// <see cref="RotationCheckCharacters"/>, because reading it on every line was a system call per log line for a
    /// cap that a normal session crosses once an hour at most.
    /// </summary>
    private static long _charactersSinceCheck;

    /// <summary>How much text goes by between two reads of the log's size. See <see cref="_charactersSinceCheck"/>.</summary>
    private const int RotationCheckCharacters = 64 * 1024;

    private static StreamWriter? _writer;

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

    /// <summary>
    /// Where the log is being written. Exposed so the diagnostics export attaches the same file the app appends to,
    /// instead of rebuilding the path and drifting from it.
    /// </summary>
    public static string LogFilePath => LogFile;

    public static void Info(string title, string? details = null) => Write("INFO", title, details);
    public static void Checkpoint(string title, string details) => Write("INFO", title, details, durable: true);
    public static void Warn(string title, string? details = null) => Write("WARN", title, details);
    public static void Error(string title, string? details = null) => Write("ERROR", title, details);

    /// <summary>
    /// Pushes whatever the writer still holds to the file. Every line is already written as it is logged, so this is
    /// only a belt-and-braces call for the shutdown and diagnostics paths.
    /// </summary>
    public static void Flush()
    {
        try
        {
            lock (Gate)
            {
                FlushLocked();
            }
        }
        catch
        {
            // A log that cannot be flushed is not a reason to fail the caller.
        }
    }

    /// <summary>
    /// The log's text, read while the app still holds the file open. Null when there is no log yet.
    /// </summary>
    /// <remarks>
    /// The writer keeps its handle for the life of the process, so a reader has to share it. This is the one place
    /// that knows how, and the diagnostics export and the tests both go through it rather than each opening the file
    /// their own way and failing on the share mode.
    /// </remarks>
    public static string? ReadAllText()
    {
        try
        {
            var path = LogFile;
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static void Write(string level, string title, string? details, bool durable = false)
    {
        try
        {
            // Scrubbed before the lock: the substitution is pure and does not need to hold the writer.
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {Redaction.Scrub(title)}:\n" +
                       $"{Redaction.Scrub(details)}\n\n";

            lock (Gate)
            {
                var path = LogFile;

                // The size is read when the file is opened and then once every 64 KB of text, not on every line.
                // Characters rather than bytes: UTF-8 never uses fewer bytes than the character count, so the check
                // happens at or before the cap rather than after it.
                _charactersSinceCheck += line.Length;
                if (_writer == null || _charactersSinceCheck >= RotationCheckCharacters)
                {
                    _charactersSinceCheck = 0;
                    if (RotateIfNeeded(path)) return;
                }

                var writer = _writer ??= OpenWriter(path);
                writer?.Write(line);
                if (durable && writer?.BaseStream is FileStream stream)
                    stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Logging must never throw. A full disk is the common case here, and the answer to it is to drop the
            // line rather than to fail the operation that was being logged.
            //
            // Taken under the same lock as the write. The writer is shared, and disposing it while another thread is
            // inside the lock writing to it turns one failed line into an ObjectDisposedException on a different
            // thread. The lock is not held here — the catch runs after the lock block has unwound — so this cannot
            // deadlock.
            lock (Gate) DropWriter();
        }
    }

    /// <summary>
    /// Moves the file aside when it has grown past the cap. Returns true when the caller must not write this line,
    /// which is the case only when the rotation itself failed.
    /// </summary>
    private static bool RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxLogBytes) return false;

            // The handle has to be closed before the file can be moved on Windows.
            CloseWriter();
            File.Move(path, path + ".old", overwrite: true);
            return false;
        }
        catch
        {
            // The rotation failed, so the caller must drop this line. Appending instead is what the old code did, and
            // it is the wrong answer: the file is already past the cap, the move fails on every subsequent line, and
            // the log grows without bound — on a full disk that is what stops the settings from saving. A dropped log
            // line costs a diagnosis; an unbounded log costs the user their preferences.
            return true;
        }
    }

    private static StreamWriter? OpenWriter(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

            var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 4096, FileOptions.None);

            // The log carries the machine name and the paths the app touched, so on Linux it is owner-only. The
            // umask would otherwise leave it at 0644.
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }

            // AutoFlush: the line reaches the file as it is logged, so a crash cannot swallow the lines that explain
            // it. The handle is what is being saved here, not the write.
            return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
        }
        catch
        {
            // A read-only or full data folder: the log is simply not written this session.
            return null;
        }
    }

    private static void FlushLocked()
    {
        _writer?.Flush();
    }

    private static void CloseWriter()
    {
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    /// <summary>
    /// Throws the writer away after a failed write. The next line reopens the file, so a disk that was full for a
    /// moment does not silence the log for the rest of the session.
    /// </summary>
    private static void DropWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }
}
