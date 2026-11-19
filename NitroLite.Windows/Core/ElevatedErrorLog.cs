namespace NitroLite.Core;

/// <summary>
/// The PowerShell that writes an elevated script's error message to a file without following a link planted there.
/// </summary>
/// <remarks>
/// <para>
/// The error-log path is inside the base64 command line, and a command line is readable by every process on the
/// machine. A same-user process can therefore learn the path and create an NTFS hardlink at it pointing at a file it
/// wants overwritten — and the elevated script's <c>Out-File</c> would write through that link with administrator
/// rights. The path being unguessable does not help: it is not guessed, it is read.
/// </para>
/// <para>
/// <c>FileMode.CreateNew</c> is what closes it. A hardlink means the path already exists, so the open fails and
/// nothing is written; a fresh path is created and written normally. The <c>Remove-Item</c> the scripts already do is
/// kept because it clears a stale file from a previous run, but it is not what makes this safe — the attacker can
/// recreate the link after it, and only the <c>CreateNew</c> open refuses.
/// </para>
/// <para>
/// The write is best effort on purpose: a script that cannot record its error must still exit non-zero, or the
/// caller would read a failed operation as a successful one.
/// </para>
/// </remarks>
public static class ElevatedErrorLog
{
    /// <summary>
    /// The function definition. Every elevated script that writes <c>$errLog</c> must carry it, because the scripts
    /// are separate PowerShell processes and share no session state.
    /// </summary>
    public const string Function =
        "function Write-NitroLiteError { param([string]$Path, [string]$Message) " +
        "try { " +
        "$fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None); " +
        "try { " +
        "$sw = [System.IO.StreamWriter]::new($fs, [System.Text.UTF8Encoding]::new($false)); " +
        "try { $sw.Write($Message) } finally { $sw.Dispose() } " +
        "} finally { $fs.Dispose() } " +
        "} catch { } }";

    /// <summary>
    /// The tail every generated script ends with: write the collected errors, then exit 1, or exit 0 when there were
    /// none. Kept here so the four builders cannot drift apart.
    /// </summary>
    public const string WriteStatement =
        "if ($errs.Count -gt 0) { Write-NitroLiteError $errLog ($errs -join \"`n\"); exit 1 } else { exit 0 }";

    /// <summary>A single-message write, for the wrapper that catches an unhandled exception.</summary>
    public const string WriteException = "Write-NitroLiteError $errLog $_.Exception.Message";
}
