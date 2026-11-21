using System.Globalization;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// A machine recorded by <c>tools/linux-capture/nitrolite-capture.sh</c>, ready to be replayed.
/// </summary>
/// <remarks>
/// <para>
/// The capture is the bridge that lets the Linux build be worked on without booting Linux: the script runs once on the
/// laptop, and this class turns its file back into the three things the backend reads — an in-memory <c>/sys</c> tree
/// with the same writability, the HID report descriptors, and the answers of <c>busctl</c>, <c>powerprofilesctl</c> and
/// <c>nvidia-smi</c>. The same object drives a unit test and the <c>--linux-capture</c> window.
/// </para>
/// <para>
/// The format is line based. A marker line starts with <c>@@ </c>; every content line starts with <c>| </c>, so no
/// attribute value can be mistaken for a marker.
/// </para>
/// </remarks>
public sealed class LinuxCapture
{
    public const string Header = "NITROLITE-CAPTURE 1";

    private readonly Dictionary<string, byte[]> _binary = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommandResult> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _info = new(StringComparer.Ordinal);

    private LinuxCapture(InMemorySysfsFileSystem fs) => FileSystem = fs;

    /// <summary>The recorded <c>/sys</c> and <c>/proc</c> files, with the ones this user could not write denied.</summary>
    public InMemorySysfsFileSystem FileSystem { get; }

    /// <summary>When the capture was taken, as the script wrote it.</summary>
    public string Captured { get; private set; } = "";

    /// <summary>The human-readable sections (lsmod, lsusb, Secure Boot, groups), keyed by label.</summary>
    public IReadOnlyDictionary<string, string> Info => _info;

    /// <summary>A recorded HID report descriptor, or null. Matches the probe's <c>readBytes</c> parameter.</summary>
    public byte[]? ReadBytes(string path) => _binary.TryGetValue(path, out var bytes) ? bytes : null;

    /// <summary>A runner that answers with what the tools printed on the captured machine.</summary>
    public ICommandRunner Runner => new ReplayRunner(this);

    /// <summary>The recorded daemon, replayed: writes change its state in memory, like the real one.</summary>
    public IPowerProfiles PowerProfiles => new PowerProfilesDaemon(new MutableReplayRunner(this));

    public static LinuxCapture LoadFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses a capture. Throws <see cref="FormatException"/> for a file that is not one.</summary>
    public static LinuxCapture Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Header)
        {
            throw new FormatException("not a NitroLite capture: the first line must be '" + Header + "'");
        }

        var capture = new LinuxCapture(new InMemorySysfsFileSystem());
        var denied = new List<string>();

        string? kind = null;
        string? key = null;
        int exit = 0;
        var body = new List<string>();

        void Flush()
        {
            if (kind == null || key == null) return;
            var content = string.Join("\n", body);

            switch (kind)
            {
                case "file":
                    capture.FileSystem.WithFile(key, content);
                    break;
                case "hex":
                    capture._binary[key] = FromHex(content);
                    break;
                case "cmd":
                    capture._commands[key] = new CommandResult(exit, content.Trim(), "");
                    break;
                case "info":
                    capture._info[key] = content;
                    break;
            }

            kind = null;
            key = null;
            exit = 0;
            body.Clear();
        }

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith("| ", StringComparison.Ordinal) || line == "|")
            {
                if (kind != null) body.Add(line.Length > 2 ? line[2..] : "");
                continue;
            }

            if (!line.StartsWith("@@ ", StringComparison.Ordinal)) continue;

            var rest = line[3..];
            var space = rest.IndexOf(' ');
            var marker = space < 0 ? rest : rest[..space];
            var argument = space < 0 ? "" : rest[(space + 1)..].Trim();

            switch (marker)
            {
                case "file":
                case "hex":
                case "cmd":
                case "info":
                    Flush();
                    kind = marker;
                    key = argument;
                    break;
                case "exit":
                    exit = int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : -1;
                    break;
                case "dir":
                    Flush();
                    capture.FileSystem.WithDirectory(argument);
                    break;
                case "ro":
                    Flush();
                    denied.Add(argument);
                    break;
                case "which":
                    Flush();
                    var parts = argument.Split(' ', 2);
                    if (parts.Length == 2) capture._tools[parts[0]] = parts[1] == "present";
                    break;
                case "meta":
                    Flush();
                    if (argument.StartsWith("captured ", StringComparison.Ordinal)) capture.Captured = argument[9..];
                    break;
                case "end":
                    Flush();
                    break;
            }
        }

        Flush();

        // Applied last: a denied path must already exist in the tree for the deny to mean "exists, not writable".
        foreach (var path in denied) capture.FileSystem.DenyWrite(path);
        return capture;
    }

    private static byte[] FromHex(string text)
    {
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    /// <summary>The key a command is recorded under: the program's name and its arguments, space-separated.</summary>
    public static string CommandKey(string fileName, IReadOnlyList<string> arguments) =>
        Path.GetFileName(fileName) + (arguments.Count == 0 ? "" : " " + string.Join(" ", arguments));

    /// <summary>Answers from the recording; a command that was not recorded did not exist on the machine.</summary>
    private class ReplayRunner : ICommandRunner
    {
        protected readonly LinuxCapture Capture;

        public ReplayRunner(LinuxCapture capture) => Capture = capture;

        public string? Find(string name) =>
            Capture._tools.TryGetValue(name, out var present) && present ? "/usr/bin/" + name : null;

        public virtual CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout) =>
            Capture._commands.TryGetValue(CommandKey(fileName, arguments), out var result)
                ? result
                : CommandResult.NotRun($"{Path.GetFileName(fileName)}: not in the capture");
    }

    /// <summary>
    /// The replay plus a memory for the daemon's active profile: a <c>set-property</c> or <c>set</c> succeeds and the
    /// next read answers with it, so the replayed window can switch modes the way the real one does.
    /// </summary>
    private sealed class MutableReplayRunner : ReplayRunner
    {
        private string? _active;

        public MutableReplayRunner(LinuxCapture capture) : base(capture)
        {
        }

        public override CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            var tool = Path.GetFileName(fileName);

            if (tool == "busctl" && arguments.Contains("set-property") && arguments.Count > 0)
            {
                _active = arguments[^1];
                return new CommandResult(0, "", "");
            }

            if (tool == "powerprofilesctl" && arguments.Count == 2 && arguments[0] == "set")
            {
                _active = arguments[1];
                return new CommandResult(0, "", "");
            }

            var recorded = base.Run(fileName, arguments, timeout);
            if (_active == null || !recorded.Ok) return recorded;

            if (tool == "busctl" && arguments.Contains("ActiveProfile"))
            {
                return arguments.Contains("--json=short")
                    ? recorded with { Output = $"{{\"type\":\"s\",\"data\":\"{_active}\"}}" }
                    : recorded with { Output = $"s \"{_active}\"" };
            }

            if (tool == "powerprofilesctl" && arguments.Count == 1 && arguments[0] == "get")
            {
                return recorded with { Output = _active };
            }

            return recorded;
        }
    }
}
