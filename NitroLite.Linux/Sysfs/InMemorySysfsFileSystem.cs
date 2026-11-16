using System.IO;

namespace NitroLite.Linux.Sysfs;

/// <summary>
/// An in-memory <c>/sys</c> tree, so the whole Linux backend can be tested without a laptop.
/// </summary>
/// <remarks>
/// Two things make this more than a dictionary. <see cref="DenyWrite"/> reproduces the state every user is in
/// before they install the udev rule, which is the case the backend has to survive without crashing. And
/// <see cref="OnWrite"/> lets a test make the firmware accept a write and then report the old value — the
/// "written but silent" behaviour that <c>ReadBackPolicy</c> exists to catch, and the one thing a plain fake
/// cannot reproduce.
/// </remarks>
public sealed class InMemorySysfsFileSystem : ISysfsFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);
    private readonly List<(string Path, string Value)> _writes = new();

    /// <summary>
    /// Called before a write is applied. Return false to make the write succeed at the file-system level while
    /// leaving the stored value untouched, which is how a firmware that ignores a command behaves.
    /// </summary>
    public Func<string, string, bool>? OnWrite { get; set; }

    /// <summary>Every write that reached the file system, in order, for assertions.</summary>
    public IReadOnlyList<(string Path, string Value)> Writes => _writes;

    /// <summary>Adds or replaces a file, creating its parent directories.</summary>
    public InMemorySysfsFileSystem WithFile(string path, string content)
    {
        _files[Normalize(path)] = content;
        AddParents(path);
        return this;
    }

    /// <summary>Adds a directory and its parents.</summary>
    public InMemorySysfsFileSystem WithDirectory(string path)
    {
        AddParents(path);
        _directories.Add(Normalize(path));
        return this;
    }

    /// <summary>Makes writes to <paramref name="path"/> fail with a permission error, like a missing udev rule.</summary>
    public InMemorySysfsFileSystem DenyWrite(string path)
    {
        _denied.Add(Normalize(path));
        return this;
    }

    /// <summary>Removes a file, so a test can model a module that does not expose an attribute.</summary>
    public InMemorySysfsFileSystem WithoutFile(string path)
    {
        _files.Remove(Normalize(path));
        return this;
    }

    /// <summary>The current contents of a file, or null. Used by tests to assert what a write left behind.</summary>
    public string? Peek(string path) => _files.TryGetValue(Normalize(path), out var value) ? value : null;

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public bool DirectoryExists(string path) => _directories.Contains(Normalize(path));

    public string? ReadAllText(string path) => _files.TryGetValue(Normalize(path), out var value) ? value : null;

    public SysfsWriteResult WriteAllText(string path, string value)
    {
        var key = Normalize(path);
        if (!_files.ContainsKey(key)) return SysfsWriteResult.Missing(path);
        if (_denied.Contains(key)) return SysfsWriteResult.Denied(path);

        _writes.Add((key, value));

        // A firmware that accepts the command and drops it: the write "succeeds" and the value does not change.
        if (OnWrite != null && !OnWrite(key, value)) return SysfsWriteResult.Success;

        _files[key] = value;
        return SysfsWriteResult.Success;
    }

    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        var prefix = Normalize(path) + "/";
        return _directories
            .Where(d => d.StartsWith(prefix, StringComparison.Ordinal) &&
                        d.IndexOf('/', prefix.Length) < 0)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> EnumerateFiles(string path)
    {
        var prefix = Normalize(path) + "/";
        return _files.Keys
            .Where(f => f.StartsWith(prefix, StringComparison.Ordinal) &&
                        f.IndexOf('/', prefix.Length) < 0)
            .Select(f => f[prefix.Length..])
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    private void AddParents(string path)
    {
        var normalized = Normalize(path);
        var index = normalized.LastIndexOf('/');
        while (index > 0)
        {
            var parent = normalized[..index];
            if (!_directories.Add(parent)) break;
            index = parent.LastIndexOf('/');
        }
    }
}
