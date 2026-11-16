namespace NitroLite.Linux.Sysfs;

/// <summary>How a write to a sysfs attribute ended.</summary>
/// <remarks>
/// The distinction matters more than the failure itself. <see cref="PermissionDenied"/> is the normal state on a
/// machine without the udev rule installed, and the answer is a one-line instruction to the user;
/// <see cref="Missing"/> means the kernel module does not expose that attribute at all, and the answer is to hide
/// the control. Collapsing the two into a bool would make the app tell a user to install a udev rule for a fan
/// control their laptop does not have.
/// </remarks>
public enum SysfsWriteStatus
{
    Ok = 0,
    Missing = 1,
    PermissionDenied = 2,
    Failed = 3
}

/// <summary>The outcome of one sysfs write, with the path that failed.</summary>
public readonly record struct SysfsWriteResult(SysfsWriteStatus Status, string? Detail)
{
    public bool Ok => Status == SysfsWriteStatus.Ok;

    /// <summary>True when the write failed because the user may not write the node, not because it is absent.</summary>
    public bool NeedsUdevRule => Status == SysfsWriteStatus.PermissionDenied;

    public static SysfsWriteResult Success { get; } = new(SysfsWriteStatus.Ok, null);

    public static SysfsWriteResult Missing(string path) => new(SysfsWriteStatus.Missing, path);

    public static SysfsWriteResult Denied(string path) => new(SysfsWriteStatus.PermissionDenied, path);

    public static SysfsWriteResult Failed(string detail) => new(SysfsWriteStatus.Failed, detail);
}

/// <summary>
/// Every <c>/sys</c> read and write the Linux backend performs, behind one interface.
/// </summary>
/// <remarks>
/// sysfs is a file system, so the obvious implementation is <c>File.ReadAllText</c> at each call site — and that is
/// exactly what makes a hardware backend untestable: the tests would need a real Acer laptop with the right kernel
/// module loaded. Routing every access through this interface means the whole backend, including its read-back
/// verification and its permission handling, runs against an in-memory tree in CI.
/// <para>
/// Reads return null rather than throwing. A sysfs attribute can disappear between the probe and the read (the
/// module is unloaded, the battery is hot-swapped), and a telemetry loop must not die because of it.
/// </para>
/// </remarks>
public interface ISysfsFileSystem
{
    /// <summary>True when the path exists and is a regular file.</summary>
    bool FileExists(string path);

    /// <summary>True when the path exists and is a directory.</summary>
    bool DirectoryExists(string path);

    /// <summary>The file's contents, trimmed of the trailing newline sysfs always adds. Null when unreadable.</summary>
    string? ReadAllText(string path);

    /// <summary>Writes <paramref name="value"/> to the attribute. Never throws.</summary>
    SysfsWriteResult WriteAllText(string path, string value);

    /// <summary>Immediate subdirectories, sorted. Empty when the directory is missing or unreadable.</summary>
    IReadOnlyList<string> EnumerateDirectories(string path);

    /// <summary>File names (not paths) directly inside <paramref name="path"/>, sorted. Empty when unreadable.</summary>
    IReadOnlyList<string> EnumerateFiles(string path);
}
