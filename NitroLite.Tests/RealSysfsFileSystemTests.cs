using System.IO;
using System.Text;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The real <c>/sys</c> reader, exercised against files on disk.
/// </summary>
/// <remarks>
/// The reader rents its buffer from the shared pool, so the sizes that matter are the ones that decide which buffer it
/// uses: an attribute that fits the small one, a file that does not, and a file past the cap. The cap is the only thing
/// between a mis-resolved path and the telemetry thread reading a multi-gigabyte file into memory, so it is pinned here
/// rather than assumed.
/// </remarks>
public class RealSysfsFileSystemTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NitroLiteSysfs_" + Guid.NewGuid().ToString("N"));

    public RealSysfsFileSystemTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }

        // Nothing here owns a finalizer; the suppression is the shape the analyzer requires.
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    [Fact]
    public void ReadAllText_TrimsTheNewlineAnAttributeEndsWith()
    {
        var path = Write("temp1_input", "55000\n");

        Assert.Equal("55000", RealSysfsFileSystem.Instance.ReadAllText(path));
    }

    [Fact]
    public void ReadAllText_ReturnsAFileThatOutgrewTheSmallBufferInFull()
    {
        // Past the 256-byte first buffer, well under the cap: the grow path must not truncate.
        var content = new string('7', 4096);
        var path = Write("big", content);

        Assert.Equal(content, RealSysfsFileSystem.Instance.ReadAllText(path));
    }

    [Fact]
    public void ReadAllText_StopsAtTheCapInsteadOfReadingTheWholeFile()
    {
        var path = Write("huge", new string('9', 200 * 1024));

        var text = RealSysfsFileSystem.Instance.ReadAllText(path);

        Assert.NotNull(text);
        Assert.Equal(64 * 1024, text!.Length);
    }

    [Fact]
    public void ReadAllText_ReturnsNullForAPathThatIsNotThere()
    {
        Assert.Null(RealSysfsFileSystem.Instance.ReadAllText(Path.Combine(_dir, "missing")));
    }
}
